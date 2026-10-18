namespace Murmur.Capture.Windows.Audio;

/// <summary>Where a device packet goes on the wall-clock timeline.</summary>
/// <param name="SilenceFrames">Silence to emit before the packet (a gap: idle loopback, glitch, slow device clock).</param>
/// <param name="SkipFrames">Leading packet frames to drop because they overlap frames already emitted.</param>
internal readonly record struct PacketPlacement(long SilenceFrames, int SkipFrames);

/// <summary>
/// Gap math of the wall-clock continuity contract (see <see cref="Murmur.Core.Capture.IAudioSink"/>): the stream
/// emitted since <see cref="Reset"/> must cover elapsed wall-clock time frame for frame. Time is in 100 ns units,
/// the unit of WASAPI's <c>pu64QPCPosition</c> (QPC converted to 100 ns).
/// Rules:
/// <list type="bullet">
/// <item>A packet is placed at the frame its QPC position maps to. In steady state a drift of up to
/// <see cref="ToleranceFrames"/> is accepted (timestamp jitter, device-clock drift); beyond it the stream is
/// re-aligned with silence (behind) or by dropping overlapping frames (ahead).</item>
/// <item>The first packet, the first packet after an idle fill and a packet flagged DATA_DISCONTINUITY are placed
/// exactly (resync).</item>
/// <item>When packets stop (idle loopback delivers nothing while nothing plays) and the lag exceeds
/// 2 x tolerance, silence is emitted up to <c>now - tolerance</c>, so a packet that is already in flight still
/// lands behind the emitted position and the stream never runs ahead of wall clock.</item>
/// <item>Silence is never emitted past the frame that corresponds to "now".</item>
/// </list>
/// Pure, allocation-free, single-threaded (owned by the capture thread).
/// </summary>
internal sealed class CaptureClock
{
    public const long TicksPerSecond = 10_000_000;

    private readonly int _sampleRate;
    private readonly long _toleranceFrames;
    private long _startTicks;
    private long _emittedFrames;
    private bool _resync = true;

    public CaptureClock(int sampleRate, int toleranceFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toleranceFrames);
        _sampleRate = sampleRate;
        _toleranceFrames = toleranceFrames;
    }

    public int SampleRate => _sampleRate;

    public long ToleranceFrames => _toleranceFrames;

    public long StartTicks => _startTicks;

    /// <summary>Frames emitted (real + silence) since <see cref="Reset"/>.</summary>
    public long EmittedFrames => _emittedFrames;

    /// <summary>Starts a new timeline at <paramref name="startTicks"/> (100 ns).</summary>
    public void Reset(long startTicks)
    {
        _startTicks = startTicks;
        _emittedFrames = 0;
        _resync = true;
    }

    /// <summary>Frames of wall-clock time between the start and <paramref name="ticks"/> (0 before the start).</summary>
    public long FramesAt(long ticks)
    {
        var frames = FrameOffset(ticks);
        return frames < 0 ? 0 : frames;
    }

    /// <summary>
    /// Places a packet of <paramref name="packetFrames"/> frames whose first frame was captured at
    /// <paramref name="packetTicks"/> (ignored when <paramref name="timestampValid"/> is false) and advances the
    /// emitted position by the returned silence plus the frames kept.
    /// </summary>
    public PacketPlacement Place(int packetFrames, long packetTicks, bool timestampValid, bool discontinuity, long nowTicks)
    {
        if (discontinuity)
        {
            _resync = true;
        }

        var exact = _resync;
        _resync = false;

        var nowFrames = FramesAt(nowTicks);
        long target;
        if (timestampValid)
        {
            target = FrameOffset(packetTicks);
        }
        else if (exact)
        {
            target = nowFrames - packetFrames; // best estimate: the packet ends about now
        }
        else
        {
            target = _emittedFrames; // no timestamp: assume continuity
        }

        if (target > nowFrames)
        {
            target = nowFrames; // a bogus timestamp must not push silence past wall clock
        }

        var drift = target - _emittedFrames;
        long silence = 0;
        var skip = 0;
        if (drift > 0 && (exact || drift > _toleranceFrames))
        {
            silence = drift;
        }
        else if (drift < 0 && (exact || -drift > _toleranceFrames))
        {
            skip = -drift >= packetFrames ? packetFrames : (int)-drift;
        }

        _emittedFrames += silence + (packetFrames - skip);
        return new PacketPlacement(silence, skip);
    }

    /// <summary>
    /// Called on every wake-up after draining the device. Returns the silence to emit when packets are missing
    /// (0 while the stream is flowing) and advances the emitted position by it.
    /// </summary>
    public long IdleSilence(long nowTicks)
    {
        var lag = FramesAt(nowTicks) - _emittedFrames;
        if (lag <= 2 * _toleranceFrames)
        {
            return 0;
        }

        var fill = lag - _toleranceFrames;
        _emittedFrames += fill;
        _resync = true; // the next real packet is placed exactly at its QPC position
        return fill;
    }

    /// <summary>On stop: silence that completes the stream up to <paramref name="nowTicks"/> exactly.</summary>
    public long TailSilence(long nowTicks)
    {
        var lag = FramesAt(nowTicks) - _emittedFrames;
        if (lag <= 0)
        {
            return 0;
        }

        _emittedFrames += lag;
        return lag;
    }

    /// <summary>Signed frame offset of <paramref name="ticks"/> from the start, rounded down (towards -infinity).</summary>
    private long FrameOffset(long ticks)
    {
        var scaled = (Int128)(ticks - _startTicks) * _sampleRate;
        var frames = scaled / TicksPerSecond;
        if (scaled < 0 && frames * TicksPerSecond != scaled)
        {
            frames--;
        }

        return (long)frames;
    }
}
