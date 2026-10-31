namespace Murmur.Capture.Windows.Playback;

/// <summary>Sample-wise mixing of PCM16 tracks: plain sum, then a soft knee instead of hard clipping.</summary>
internal static class PlaybackMixer
{
    /// <summary>Samples up to this magnitude pass unchanged (0.75 of full scale).</summary>
    internal const int Knee = 24_576;

    private const float Headroom = short.MaxValue - Knee;

    /// <summary>
    /// Identity up to <see cref="Knee"/>; above it <c>Knee + (32767 - Knee) * tanh((|x| - Knee) / (32767 - Knee))</c>,
    /// which is continuous with slope 1 at the knee and approaches (never exceeds) full scale. Odd-symmetric.
    /// </summary>
    public static short SoftClip(int sum)
    {
        if (sum is >= -Knee and <= Knee)
        {
            return (short)sum;
        }

        var magnitude = Math.Abs(sum);
        var shaped = Knee + (Headroom * MathF.Tanh((magnitude - Knee) / Headroom));
        var rounded = Math.Min((int)(shaped + 0.5f), short.MaxValue);
        return (short)(sum < 0 ? -rounded : rounded);
    }

    /// <summary>Adds <paramref name="samples"/> to <paramref name="accumulator"/> (same length or shorter).</summary>
    public static void Accumulate(ReadOnlySpan<short> samples, Span<int> accumulator)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            accumulator[i] += samples[i];
        }
    }

    /// <summary>Soft-clips <paramref name="accumulator"/> into <paramref name="destination"/> (same length).</summary>
    public static void Clip(ReadOnlySpan<int> accumulator, Span<short> destination)
    {
        for (var i = 0; i < accumulator.Length; i++)
        {
            destination[i] = SoftClip(accumulator[i]);
        }
    }
}

/// <summary>
/// The tracks of one session on a common timeline: <see cref="Render"/> mixes the next frames of every track
/// (shorter tracks contribute silence after their end) until the longest track ends. One track is copied as is (no
/// clipping stage). All buffers are preallocated for <see cref="MaxFramesPerRender"/>; larger requests are served in
/// several passes, so <see cref="Render"/> never allocates. Not thread-safe: owned by the render thread.
/// </summary>
internal sealed class TrackMixer : IPcmSource, IDisposable
{
    /// <summary>Frames mixed per pass: 100 ms at 16 kHz, larger than any shared-mode endpoint buffer we request.</summary>
    internal const int MaxFramesPerRender = 1_600;

    private readonly Pcm16TrackReader[] _tracks;
    private readonly short[] _scratch;
    private readonly int[] _accumulator;
    private long _position;
    private int _mutedMask;

    // The gain each track has now (1 = heard, 0 = silenced). It moves towards its target over RampFrames, so a switch is not a click.
    private readonly float[] _gains;
    private int _snapPending;

    // The gains when the mask last changed and where, so that a reader that went back (Rewind) finds the fades as they were there.
    private readonly float[] _gainsAtChange;
    private long _changePosition;
    private int _seenMask;
    private int _changeVersion;

    /// <summary>Takes ownership of <paramref name="tracks"/> (1..n readers of the same sample rate).</summary>
    /// <exception cref="InvalidDataException">The tracks have different sample rates.</exception>
    public TrackMixer(IReadOnlyList<Pcm16TrackReader> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0)
        {
            throw new ArgumentException("At least one track is required.", nameof(tracks));
        }

        _tracks = new Pcm16TrackReader[tracks.Count];
        long total = 0;
        for (var i = 0; i < _tracks.Length; i++)
        {
            _tracks[i] = tracks[i];
            if (tracks[i].SampleRate != tracks[0].SampleRate)
            {
                throw new InvalidDataException($"All tracks must share one sample rate ({tracks[0].SampleRate} Hz vs {tracks[i].SampleRate} Hz).");
            }

            total = Math.Max(total, tracks[i].FrameCount);
        }

        _gains = new float[_tracks.Length];
        Array.Fill(_gains, 1f);
        _gainsAtChange = new float[_tracks.Length];
        Array.Fill(_gainsAtChange, 1f);
        SampleRate = tracks[0].SampleRate;
        TotalFrames = total;
        _scratch = new short[MaxFramesPerRender];
        _accumulator = new int[MaxFramesPerRender];
    }

    public int SampleRate { get; }

    /// <summary>Frames of the longest track.</summary>
    public long TotalFrames { get; }

    /// <summary>The next frame <see cref="Render"/> produces, in [0, <see cref="TotalFrames"/>].</summary>
    public long Position => _position;

    public bool AtEnd => _position >= TotalFrames;

    /// <summary>Tracks that are silenced (bit <c>i</c> = track <c>i</c>); they are still read, so every track stays at the same position.</summary>
    /// <summary>Setting the whole mask (a new mixer) takes effect at once; <see cref="SetMuted"/> fades over <see cref="RampFrames"/>.</summary>
    public int MutedMask
    {
        get => Volatile.Read(ref _mutedMask);
        set
        {
            Volatile.Write(ref _mutedMask, value);
            Volatile.Write(ref _snapPending, 1);
            Interlocked.Increment(ref _changeVersion);
        }
    }

    /// <summary>The fade of a mute or an unmute: 10 ms at 16 kHz.</summary>
    internal const int RampFrames = 160;

    /// <summary>Silences or restores one track (indexes 0..31). Callable from any thread while the render thread mixes.</summary>
    public void SetMuted(int track, bool muted)
    {
        if (track is < 0 or >= 32)
        {
            return;
        }

        if (muted)
        {
            Interlocked.Or(ref _mutedMask, 1 << track);
        }
        else
        {
            Interlocked.And(ref _mutedMask, ~(1 << track));
        }

        Interlocked.Increment(ref _changeVersion);
    }

    /// <summary>Incremented by every change of the mask; <see cref="WsolaStretcher"/> drops its read-ahead when it moves.</summary>
    public int ChangeVersion => Volatile.Read(ref _changeVersion);

    /// <summary>
    /// Goes back to <paramref name="frame"/> to read again what was read ahead: like <see cref="Seek"/>, and the gains are put back
    /// to what they were at that frame (a fade that started after it starts again there, one that had finished before it stays
    /// finished), so a mute is not turned into a step.
    /// </summary>
    public void Rewind(long frame)
    {
        Seek(frame);
        var elapsed = _position - _changePosition;
        for (var t = 0; t < _gains.Length; t++)
        {
            var target = t < 32 && (_seenMask & (1 << t)) != 0 ? 0f : 1f;
            var start = _gainsAtChange[t];
            var moved = elapsed <= 0 ? 0f : Math.Min(elapsed * (1f / RampFrames), Math.Abs(target - start));
            _gains[t] = target > start ? start + moved : start - moved;
        }

        _changePosition = _position;
        Array.Copy(_gains, _gainsAtChange, _gains.Length);
    }

    /// <summary>Repositions every track to <paramref name="frame"/>, clamped to [0, <see cref="TotalFrames"/>].</summary>
    public void Seek(long frame)
    {
        _position = Math.Clamp(frame, 0, TotalFrames);
        foreach (var track in _tracks)
        {
            track.Seek(_position);
        }
    }

    /// <summary>Mixes up to <c>destination.Length</c> frames; fewer only at the end of the longest track.</summary>
    /// <returns>Frames written to the start of <paramref name="destination"/>; 0 at the end.</returns>
    public int Render(Span<short> destination)
    {
        var frames = (int)Math.Min(destination.Length, TotalFrames - _position);
        if (frames <= 0)
        {
            return 0;
        }

        for (var done = 0; done < frames;)
        {
            var pass = Math.Min(frames - done, MaxFramesPerRender);
            var output = destination.Slice(done, pass);
            var muted = MutedMask;
            SnapGains(muted);
            if (muted != _seenMask)
            {
                _seenMask = muted;
                _changePosition = _position + done;
                Array.Copy(_gains, _gainsAtChange, _gains.Length);
            }

            if (_tracks.Length == 1)
            {
                var read = _tracks[0].Read(output);
                output[read..].Clear();
                ApplyGain(output, ref _gains[0], (muted & 1) != 0 ? 0f : 1f);
            }
            else
            {
                var accumulator = _accumulator.AsSpan(0, pass);
                accumulator.Clear();
                for (var t = 0; t < _tracks.Length; t++)
                {
                    var read = _tracks[t].Read(_scratch.AsSpan(0, pass));
                    var target = t < 32 && (muted & (1 << t)) != 0 ? 0f : 1f;
                    ApplyGain(_scratch.AsSpan(0, read), ref _gains[t], target);
                    PlaybackMixer.Accumulate(_scratch.AsSpan(0, read), accumulator);
                }

                PlaybackMixer.Clip(accumulator, output);
            }

            done += pass;
        }

        _position += frames;
        return frames;
    }

    /// <summary>A mask that was set as a whole (not by <see cref="SetMuted"/>) puts every gain at its target without a fade.</summary>
    private void SnapGains(int muted)
    {
        if (Interlocked.Exchange(ref _snapPending, 0) == 0)
        {
            return;
        }

        for (var t = 0; t < _gains.Length; t++)
        {
            _gains[t] = t < 32 && (muted & (1 << t)) != 0 ? 0f : 1f;
        }
    }

    /// <summary>
    /// Scales <paramref name="samples"/> in place by a gain that moves from <paramref name="gain"/> towards <paramref name="target"/> by
    /// 1 / <see cref="RampFrames"/> per frame. No allocation; a track at gain 1 or 0 for good costs a compare per call.
    /// </summary>
    private static void ApplyGain(Span<short> samples, ref float gain, float target)
    {
        if (gain == target)
        {
            if (target == 0f)
            {
                samples.Clear();
            }

            return;
        }

        const float step = 1f / RampFrames;
        var g = gain;
        for (var i = 0; i < samples.Length; i++)
        {
            g = target > g ? Math.Min(target, g + step) : Math.Max(target, g - step);
            samples[i] = (short)(samples[i] * g);
        }

        gain = g;
    }

    public void Dispose()
    {
        foreach (var track in _tracks)
        {
            track.Dispose();
        }
    }
}
