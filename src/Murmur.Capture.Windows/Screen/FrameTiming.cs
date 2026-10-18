namespace Murmur.Capture.Windows.Screen;

/// <summary>Media Foundation time math: 100 ns units (REFERENCE_TIME / MFTIME / WinRT TimeSpan ticks).</summary>
internal static class MediaTime
{
    public const long TicksPerSecond = 10_000_000;

    /// <summary>Converts a QueryPerformanceCounter value to 100 ns units without overflow (the basis of WGC SystemRelativeTime).</summary>
    public static long FromQpc(long counter, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        var seconds = counter / frequency;
        var remainder = counter % frequency;
        return (seconds * TicksPerSecond) + (remainder * TicksPerSecond / frequency);
    }

    /// <summary>The duration of one frame at <paramref name="frameRate"/> fps, rounded to the nearest 100 ns.</summary>
    public static long FrameDuration(int frameRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);
        return (TicksPerSecond + (frameRate / 2)) / frameRate;
    }

    /// <summary>"Now" on the QPC timeline in 100 ns units (<see cref="System.Diagnostics.Stopwatch"/> is QPC on Windows).</summary>
    public static long Now() => FromQpc(System.Diagnostics.Stopwatch.GetTimestamp(), System.Diagnostics.Stopwatch.Frequency);
}

/// <summary>
/// Rate-limits capture frames to a target frame rate. Accepted frames sit on a grid
/// <c>anchor + n / fps</c>; a frame is accepted once it reaches its grid slot minus a quarter interval of tolerance
/// (timestamp jitter), so a 60 Hz desktop yields exactly every second frame at 30 fps instead of aliasing down to
/// 20 fps. After a gap longer than one interval (static screen: WGC sends frames only when something changes) the grid
/// re-anchors at the next frame, so a burst after a pause is not "caught up". Timestamps going backwards are rejected.
/// Pure and allocation-free; single-threaded (owned by the FrameArrived handler).
/// </summary>
internal sealed class FrameRateLimiter
{
    private readonly int _frameRate;
    private readonly long _tolerance;
    private long _anchor;
    private long _count;
    private bool _started;

    public FrameRateLimiter(int frameRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);
        _frameRate = frameRate;
        _tolerance = MediaTime.TicksPerSecond / frameRate / 4;
    }

    public int FrameRate => _frameRate;

    public void Reset()
    {
        _started = false;
        _anchor = 0;
        _count = 0;
    }

    /// <summary>True when the frame captured at <paramref name="ticks"/> (100 ns) should be encoded.</summary>
    public bool TryAccept(long ticks)
    {
        if (!_started)
        {
            _started = true;
            _anchor = ticks;
            _count = 1;
            return true;
        }

        if (ticks < Due(_count) - _tolerance)
        {
            return false;
        }

        _count++;
        if (ticks >= Due(_count) - _tolerance)
        {
            // More than one interval late (a gap): restart the grid here.
            _anchor = ticks;
            _count = 1;
        }

        return true;
    }

    private long Due(long n) => _anchor + (n * MediaTime.TicksPerSecond / _frameRate);
}

/// <summary>
/// Maps capture timestamps (QPC timeline, 100 ns) to sample times of one segment: relative to the segment start,
/// never negative, strictly increasing (Media Foundation rejects or reorders non-monotonic input).
/// Single-threaded (owned by the encoder thread).
/// </summary>
internal sealed class SegmentTimeline
{
    private long _start;
    private long _last = -1;

    public long Start => _start;

    /// <summary>The last sample time handed out, or -1 when none.</summary>
    public long LastSampleTime => _last;

    public void Reset(long startTicks)
    {
        _start = startTicks;
        _last = -1;
    }

    public long ToSampleTime(long ticks)
    {
        var time = ticks - _start;
        if (time < 0)
        {
            time = 0;
        }

        if (time <= _last)
        {
            time = _last + 1;
        }

        _last = time;
        return time;
    }
}

/// <summary>
/// When the encoder thread may write repeated frames (static screens, segment tail). The rule: repeats only extend the
/// timeline while the segment is accepting frames and has not faulted, and the tail never reaches past the moment of a
/// fatal fault (display disconnected, frame pool or GPU copy failure). Otherwise a display unplugged 5 min into a 1 h
/// meeting would leave 55 min of a frozen last frame that looks like real screen content.
/// Pure and allocation-free.
/// </summary>
internal static class RepeatPolicy
{
    /// <summary>Fault time of a segment that has not faulted.</summary>
    public const long NoFault = long.MaxValue;

    /// <summary>True when the last frame should be repeated at <paramref name="now"/> because nothing new arrived for <paramref name="interval"/>.</summary>
    public static bool ShouldRepeat(bool hasLastFrame, bool writerHealthy, bool accepting, long now, long lastWriteTicks, long faultTicks, long interval) =>
        hasLastFrame && writerHealthy && accepting && now < faultTicks && now - lastWriteTicks >= interval;

    /// <summary>
    /// Computes the capture time (QPC, 100 ns) of the tail frame that extends the file to the end of the segment: one
    /// frame before the stop time, or before the fault time when the segment faulted earlier. False when no tail is
    /// needed (the last sample already reaches that point) or allowed (no frame yet, writer failed).
    /// </summary>
    public static bool TryGetTail(
        bool hasLastFrame,
        bool writerHealthy,
        long stopTicks,
        long faultTicks,
        long frameDuration,
        long timelineStart,
        long lastSampleTime,
        out long tailTicks)
    {
        tailTicks = Math.Min(stopTicks, faultTicks) - frameDuration;
        return hasLastFrame && writerHealthy && tailTicks - timelineStart > lastSampleTime + (frameDuration / 2);
    }
}

/// <summary>
/// The placeholder frame of a segment in which no sample reached the encoder (WGC delivered no frame: headless or
/// non-composited session, locked workstation, every frame dropped). Media Foundation refuses to finalize an MP4 without
/// samples (MF_E_SINK_NO_SAMPLES_PROCESSED), so the encoder thread writes one opaque black frame at sample time 0 and
/// the segment becomes a valid one-frame file. It is not counted as captured or encoded, so
/// <see cref="Murmur.Core.Capture.ScreenRecorderStats.FramesCaptured"/> == 0 still tells callers nothing was captured.
/// Pure and allocation-free.
/// </summary>
internal static class PlaceholderFrame
{
    /// <summary>One B8G8R8A8 pixel of opaque black, read as a little-endian uint (B, G, R = 0, A = 255).</summary>
    public const uint OpaqueBlackBgra = 0xFF000000;

    /// <summary>
    /// True when the placeholder must be written before Finalize: nothing was written to the sink writer and the writer
    /// is still healthy (a failed writer would reject the placeholder as well; its fault has already been raised).
    /// </summary>
    public static bool ShouldWrite(bool anySampleWritten, bool writerHealthy) => !anySampleWritten && writerHealthy;

    /// <summary>Capture time (QPC, 100 ns) of the placeholder: the segment start, i.e. sample time 0.</summary>
    public static long CaptureTicks(SegmentTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        return timeline.Start;
    }

    /// <summary>Bytes per row of a B8G8R8A8 picture <paramref name="width"/> pixels wide.</summary>
    public static uint RowPitch(int width)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        return checked((uint)width * 4);
    }

    /// <summary>Pixels of a picture of <paramref name="size"/>.</summary>
    public static long PixelCount(PixelSize size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size.Width, nameof(size));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size.Height, nameof(size));
        return (long)size.Width * size.Height;
    }
}
