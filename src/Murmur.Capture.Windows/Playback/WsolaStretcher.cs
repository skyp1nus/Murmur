namespace Murmur.Capture.Windows.Playback;

/// <summary>
/// A sequential, seekable source of mono PCM16 frames on one timeline (<see cref="TrackMixer"/> in the player). Not
/// thread-safe: the render thread owns it.
/// </summary>
internal interface IPcmSource
{
    /// <summary>Frames of the whole source.</summary>
    long TotalFrames { get; }

    /// <summary>The next frame <see cref="Render"/> returns, in [0, <see cref="TotalFrames"/>].</summary>
    long Position { get; }

    /// <summary>Fills up to <c>destination.Length</c> frames; fewer only at the end of the source. Returns the frames written (0 at the end).</summary>
    int Render(Span<short> destination);

    /// <summary>Repositions to <paramref name="frame"/>, clamped to [0, <see cref="TotalFrames"/>].</summary>
    void Seek(long frame);

    /// <summary>
    /// Goes back to <paramref name="frame"/> (not after <see cref="Position"/>) to read again what was read ahead, as if it had not been
    /// read: state that changed with the frames read since (the fades of a mute) is put back as it was at <paramref name="frame"/>.
    /// </summary>
    void Rewind(long frame);

    /// <summary>Counts the changes (a track muted or restored) that make frames read ahead obsolete; read it before using the read-ahead.</summary>
    int ChangeVersion { get; }
}

/// <summary>
/// Pitch-preserving time stretch of an <see cref="IPcmSource"/> for 0.5-2x playback of speech: WSOLA (waveform-similarity
/// overlap-add) with a sum-of-absolute-differences search, implemented here (no dependency).
/// <list type="bullet">
/// <item><b>Algorithm.</b> Output is made of hops of 20 ms. Hop <c>k</c> is a stretch of the input that starts near the
/// nominal analysis position <c>N_k = N_(k-1) + hop * speed</c>; within +-10 ms of it the start is chosen where the input best
/// matches (smallest sum of absolute differences over 8 ms) the natural continuation of the previous hop, and the first 8 ms
/// of the hop are cross-faded (linear, constant gain for equal signals) from that continuation. So every join is
/// waveform-aligned (no clicks, the pitch periods stay intact) and the nominal grid does not accumulate the search offsets:
/// the output is exactly <c>ceil(frames / speed)</c> frames long (the last hop is cut).</item>
/// <item><b>Speed 1 is a bit-exact passthrough</b> (the source is read straight into the caller's span). Switching between
/// 1 and another speed happens at a hop boundary and is seamless: a hop starts where the previous one naturally goes on, so
/// no cross-fade is needed; a change takes effect within one hop (20 ms) plus what is already queued.</item>
/// <item><b>Timeline.</b> <see cref="MediaPosition"/> is the nominal analysis position (in input frames) of the next output
/// frame: it advances by <c>speed</c> per output frame, so the player's position / clock runs in media time, and it never
/// jumps (<see cref="MediaAtOutput"/> answers for the frame being heard from a short history of the output). The audible
/// content is off it by up to <c>hop x |speed - 1|</c> + search (~30 ms at 2x): a hop is a stretch of input played at its own
/// speed 1, so it lags the nominal position by up to a hop at speeds above 1 and runs ahead of it at speeds below 1; after a
/// return to speed 1 this offset stays until the next seek. At speeds above 1 the last <c>hop x (speed - 1)</c> of input
/// (~20 ms at 2x) is not played.</item>
/// <item><b>Seek</b> repositions the source and drops all state. At a speed other than 1 the next output fades in over 4 ms
/// (the device buffer is flushed by the caller, so the stream restarts from silence); at speed 1 the passthrough is untouched
/// and starts with the source's first frame as it is.</item>
/// <item><b>Mute</b> (<see cref="IPcmSource.ChangeVersion"/>): what was read ahead was mixed before the change, so it is dropped
/// and read again from where the output goes on (the source puts its fades back), instead of letting later hops play the old mix.</item>
/// <item><b>Hot path.</b> <see cref="Render"/> and <see cref="SetSpeed"/> allocate nothing and take no lock; all buffers are
/// allocated by the constructor. <see cref="SetSpeed"/> may be called from any thread; everything else belongs to the render thread.</item>
/// </list>
/// </summary>
internal sealed class WsolaStretcher
{
    /// <summary>Slowest and fastest supported speeds.</summary>
    public const double MinSpeed = 0.5;

    public const double MaxSpeed = 2.0;

    private const int HopMilliseconds = 20;
    private const int OverlapMilliseconds = 8;
    private const int SearchMilliseconds = 10;
    private const int FadeInMilliseconds = 4;

    // Rounding slack of the double media position at the end of the source.
    private const double Epsilon = 1e-6;

    private readonly IPcmSource _source;
    private readonly long _total;
    private readonly int _hop;
    private readonly int _overlap;
    private readonly int _search;
    private readonly int _fadeIn;

    // Weight of the incoming segment in the cross-fade, per overlap frame: (i + 1) / (overlap + 1).
    private readonly float[] _fade;

    // Input window: frames [_inBase, _inBase + _inLen) of the source timeline, read ahead of what is output.
    private readonly short[] _in;
    private long _inBase;
    private int _inLen;
    private bool _eof;

    // The hop being handed out.
    private readonly short[] _out;
    private int _outPos;
    private int _outLen;
    private double _hopSpeed = 1;

    private double _requested = 1;
    private double _media;
    private double _outputSpeed = 1;
    private int _version;

    // History of the output: runs of constant speed, so the media position of any recent output frame is known exactly.
    private const int HistorySize = 64;
    private readonly long[] _historyOut = new long[HistorySize];
    private readonly double[] _historyMedia = new double[HistorySize];
    private readonly double[] _historySpeed = new double[HistorySize];
    private int _historyHead;
    private int _historyCount;
    private long _outputFrames;
    private long _continuation; // input frame where the previous hop goes on naturally (the cursor while passing through)
    private bool _warm;         // a previous hop exists to be matched
    private bool _fadePending;
    private bool _ended;

    /// <summary>Starts at the current position of <paramref name="source"/> (no seek).</summary>
    public WsolaStretcher(IPcmSource source, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1_000);
        _source = source;
        _total = source.TotalFrames;
        _hop = sampleRate * HopMilliseconds / 1_000;
        _overlap = sampleRate * OverlapMilliseconds / 1_000;
        _search = sampleRate * SearchMilliseconds / 1_000;
        _fadeIn = sampleRate * FadeInMilliseconds / 1_000;
        if (_hop * MaxSpeed > (2 * _search) + _hop + _overlap)
        {
            throw new InvalidOperationException("The analysis hop at the fastest speed would skip past the search window.");
        }

        _fade = new float[_overlap];
        for (var i = 0; i < _overlap; i++)
        {
            _fade[i] = (i + 1f) / (_overlap + 1f);
        }

        _in = new short[16 * _hop];
        _out = new short[_hop];
        Reset(source.Position);
    }

    /// <summary>The speed asked for (clamped; <c>1</c> = passthrough).</summary>
    public double Speed => Volatile.Read(ref _requested);

    /// <summary>The speed of the output handed out last (1 before any); what the frames still queued in a device buffer were made at.</summary>
    public double OutputSpeed => _outputSpeed;

    /// <summary>The nominal input frame of the next output frame (see the class remarks); <c>Total</c> at the end.</summary>
    public double MediaPosition => _media;

    /// <summary>Output frames handed out since construction / the last seek.</summary>
    public long OutputFrames => _outputFrames;

    /// <summary>
    /// The media position of output frame <paramref name="outputFrame"/> (counted like <see cref="OutputFrames"/>), from the history of
    /// the last output: exact within the runs it keeps (64 changes of speed or mode), the oldest run extended beyond them.
    /// Allocation-free.
    /// </summary>
    public double MediaAtOutput(long outputFrame)
    {
        outputFrame = Math.Min(outputFrame, _outputFrames);
        var index = _historyHead;
        for (var i = 0; i < _historyCount; i++)
        {
            if (_historyOut[index] <= outputFrame || i == _historyCount - 1)
            {
                return _historyMedia[index] + ((outputFrame - _historyOut[index]) * _historySpeed[index]);
            }

            index = (index + HistorySize - 1) % HistorySize;
        }

        return _media;
    }

    /// <summary>All output was handed out.</summary>
    public bool AtEnd => _outPos >= _outLen && (_ended || (_warm ? _media >= _total - Epsilon : _continuation >= _total));

    /// <summary>Clamps to [<see cref="MinSpeed"/>, <see cref="MaxSpeed"/>]; NaN is 1 and so is anything within 0.5 % of it.</summary>
    public static double ClampSpeed(double speed)
    {
        if (double.IsNaN(speed))
        {
            return 1;
        }

        speed = Math.Clamp(speed, MinSpeed, MaxSpeed);
        return Math.Abs(speed - 1) < 0.005 ? 1 : speed;
    }

    /// <summary>Takes effect at the next hop boundary. Any thread.</summary>
    /// <returns>The speed in effect afterwards.</returns>
    public double SetSpeed(double speed)
    {
        speed = ClampSpeed(speed);
        Volatile.Write(ref _requested, speed);
        return speed;
    }

    /// <summary>Repositions the source and drops all stretcher state; the next output fades in.</summary>
    public void Seek(long frame)
    {
        _source.Seek(frame);
        Reset(_source.Position);
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with output frames, fewer only at the end of the source. Allocation-free.
    /// </summary>
    /// <returns>Frames written; 0 at the end.</returns>
    public int Render(Span<short> destination)
    {
        var done = 0;
        while (done < destination.Length)
        {
            if (_outPos < _outLen)
            {
                var count = Math.Min(destination.Length - done, _outLen - _outPos);
                _out.AsSpan(_outPos, count).CopyTo(destination[done..]);
                _outPos += count;
                done += count;
                Record(count, _hopSpeed);
                _outputSpeed = _hopSpeed;
                continue;
            }

            var version = _source.ChangeVersion;
            if (version != _version)
            {
                _version = version;
                DropReadAhead();
            }

            var speed = Volatile.Read(ref _requested);
            if (speed == 1)
            {
                var passed = PassThrough(destination[done..]);
                if (passed <= 0)
                {
                    _ended = true;
                    break;
                }

                done += passed;
            }
            else if (MakeHop(speed) <= 0)
            {
                _ended = true;
                break;
            }
        }

        return done;
    }

    private void Reset(long frame)
    {
        _inBase = frame;
        _inLen = 0;
        _eof = false;
        _outPos = 0;
        _outLen = 0;
        _hopSpeed = 1;
        _media = frame;
        _continuation = frame;
        _warm = false;
        _fadePending = true;
        _ended = false;
        _version = _source.ChangeVersion;
        _outputFrames = 0;
        _historyHead = 0;
        _historyCount = 1;
        _historyOut[0] = 0;
        _historyMedia[0] = frame;
        _historySpeed[0] = 1;
    }

    /// <summary>Counts <paramref name="count"/> output frames made at <paramref name="speed"/> into the media position and the history.</summary>
    private void Record(int count, double speed)
    {
        var last = _historyHead;
        var expected = _historyMedia[last] + ((_outputFrames - _historyOut[last]) * _historySpeed[last]);
        if (_historySpeed[last] != speed || Math.Abs(expected - _media) > Epsilon)
        {
            last = (last + 1) % HistorySize;
            _historyHead = last;
            _historyCount = Math.Min(_historyCount + 1, HistorySize);
            _historyOut[last] = _outputFrames;
            _historyMedia[last] = _media;
            _historySpeed[last] = speed;
        }

        _outputFrames += count;
        _media += count * speed;
    }

    /// <summary>
    /// The mix changed (a mute): what was read ahead is stale. Go back to where the output goes on and read it again, cold; the
    /// frames after it are then mixed with the new setting.
    /// </summary>
    private void DropReadAhead()
    {
        if (_continuation >= _inBase + _inLen && !_warm)
        {
            return; // nothing was read ahead
        }

        _source.Rewind(_continuation);
        _inBase = _source.Position;
        _continuation = _inBase;
        _inLen = 0;
        _eof = false;
        _warm = false;
    }

    /// <summary>Speed 1: first what was read ahead of the cursor, then the source itself, untouched.</summary>
    private int PassThrough(Span<short> destination)
    {
        _warm = false;
        _fadePending = false;
        _outputSpeed = 1;
        var inputEnd = _inBase + _inLen;
        int count;
        if (_continuation < inputEnd)
        {
            count = (int)Math.Min(Math.Min(destination.Length, inputEnd - _continuation), Math.Max(0, _total - _continuation));
            if (count > 0)
            {
                _in.AsSpan((int)(_continuation - _inBase), count).CopyTo(destination);
            }
        }
        else
        {
            count = _source.Render(destination);
        }

        if (count > 0)
        {
            Record(count, 1);
            _continuation += count;
        }

        if (_continuation >= inputEnd)
        {
            _inBase = _continuation; // the read-ahead is used up: the source is at the cursor again
            _inLen = 0;
        }

        return count;
    }

    /// <summary>Builds the next hop into the output buffer. Returns its length, 0 at the end.</summary>
    private int MakeHop(double speed)
    {
        var remaining = _total - _media;
        if (remaining <= Epsilon)
        {
            return 0;
        }

        var length = (int)Math.Min(_hop, Math.Max(1, Math.Ceiling((remaining / speed) - 1e-9)));
        long start;
        if (!_warm)
        {
            // Cold start (first hop, after a seek, or after passing through): nothing to match, the source simply goes on.
            start = _continuation;
            if (start >= _total)
            {
                return 0;
            }

            Window(start, start + _hop + _overlap);
            _in.AsSpan((int)(start - _inBase), length).CopyTo(_out);
            if (_fadePending)
            {
                _fadePending = false;
                var fade = Math.Min(_fadeIn, length);
                for (var i = 0; i < fade; i++)
                {
                    _out[i] = (short)(_out[i] * ((i + 1f) / (_fadeIn + 1f)));
                }
            }

            _warm = true;
        }
        else
        {
            var center = (long)Math.Round(_media);
            var low = Math.Max(0, center - _search);
            var high = Math.Min(center + _search, _total);
            Window(Math.Min(low, _continuation), Math.Max(high, _continuation) + _hop + _overlap);

            // After a read-ahead drop the window starts at the continuation: candidates before it are not available.
            low = Math.Max(low, _inBase);
            high = Math.Max(high, low);
            start = BestStart(center, low, high);

            var reference = _in.AsSpan((int)(_continuation - _inBase), _overlap);
            var incoming = _in.AsSpan((int)(start - _inBase), _hop);
            var blend = Math.Min(_overlap, length);
            for (var i = 0; i < blend; i++)
            {
                var a = reference[i];
                _out[i] = (short)MathF.Round(a + ((incoming[i] - a) * _fade[i]));
            }

            if (length > blend)
            {
                incoming.Slice(blend, length - blend).CopyTo(_out.AsSpan(blend));
            }
        }

        _continuation = start + _hop;
        _hopSpeed = speed;
        _outPos = 0;
        _outLen = length;
        return length;
    }

    /// <summary>The start in [low, high] whose first overlap frames are closest to the natural continuation; ties go to the nearest to <paramref name="center"/>.</summary>
    private long BestStart(long center, long low, long high)
    {
        var reference = _in.AsSpan((int)(_continuation - _inBase), _overlap);
        var best = long.MaxValue;
        var bestStart = Math.Clamp(center, low, high);
        for (var distance = 0; distance <= _search; distance++)
        {
            var up = center + distance;
            if (up >= low && up <= high)
            {
                var sad = Difference(_in.AsSpan((int)(up - _inBase), _overlap), reference, best);
                if (sad < best)
                {
                    best = sad;
                    bestStart = up;
                }
            }

            var down = center - distance;
            if (distance > 0 && down >= low && down <= high)
            {
                var sad = Difference(_in.AsSpan((int)(down - _inBase), _overlap), reference, best);
                if (sad < best)
                {
                    best = sad;
                    bestStart = down;
                }
            }
        }

        return bestStart;
    }

    /// <summary>Sum of absolute differences; stops early once it cannot beat <paramref name="limit"/>.</summary>
    private static long Difference(ReadOnlySpan<short> a, ReadOnlySpan<short> b, long limit)
    {
        var sum = 0;
        var i = 0;
        for (; i + 16 <= a.Length; i += 16)
        {
            for (var j = i; j < i + 16; j++)
            {
                sum += Math.Abs(a[j] - b[j]);
            }

            if (sum >= limit)
            {
                return sum;
            }
        }

        for (; i < a.Length; i++)
        {
            sum += Math.Abs(a[i] - b[i]);
        }

        return sum;
    }

    /// <summary>Makes frames [<paramref name="from"/>, <paramref name="to"/>) available in the window; reads the source ahead as needed, zeros past its end.</summary>
    private void Window(long from, long to)
    {
        // Drop what is behind `from` (never beyond what was read).
        var drop = (int)Math.Clamp(from - _inBase, 0, _inLen);
        if (drop > 0)
        {
            _in.AsSpan(drop, _inLen - drop).CopyTo(_in);
            _inBase += drop;
            _inLen -= drop;
        }

        var need = (int)Math.Min(to - (_inBase + _inLen), _in.Length - _inLen);
        while (need > 0)
        {
            var read = 0;
            if (!_eof)
            {
                read = _source.Render(_in.AsSpan(_inLen, need));
                if (read < need)
                {
                    _eof = true;
                }
            }

            if (read < need)
            {
                _in.AsSpan(_inLen + read, need - read).Clear();
            }

            _inLen += need;
            need = 0;
        }
    }
}
