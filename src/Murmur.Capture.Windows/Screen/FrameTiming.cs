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
/// When the encoder thread may write repeated frames (static screens) and where a segment's video ends. The rule:
/// repeats only extend the timeline while the segment is accepting frames and has not faulted, and the last frame never
/// lasts past the moment of a fatal fault (display disconnected, frame pool or GPU copy failure). Otherwise a display
/// unplugged 5 min into a 1 h meeting would leave 55 min of a frozen last frame that looks like real screen content.
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
    /// The capture time (QPC, 100 ns) at which the segment's video ends: the stop time, or the fault time when the
    /// segment faulted earlier; <paramref name="now"/> when the encoder thread ends without a stop request (it failed).
    /// </summary>
    public static long EndTicks(bool stopRequested, long stopTicks, long faultTicks, long now) =>
        Math.Min(stopRequested ? stopTicks : now, faultTicks);
}

/// <summary>
/// Variable-frame-rate sample timing of the fragmented MP4. Media Foundation's FMPEG4 sink writes each sample's duration
/// into the fragment's <c>trun</c> box and writes no <c>tfdt</c> (base decode time), so players lay the samples end to
/// end: the file's timeline is the sum of the sample durations, the sample times are not stored. A fixed 1/fps duration
/// therefore collapses every static stretch (WGC sends no frames while nothing changes) to a single frame.
/// <para>
/// So the encoder thread holds the newest frame back until the next one arrives (or the segment ends) and writes it
/// with the real gap as its duration: the durations add up to the wall-clock time from the segment start to its end.
/// The first frame starts at sample time 0 (it stands for the picture since the segment start) and the last one ends at
/// the end of the segment. Every duration is at least 1 (100 ns). Pure and allocation-free; single-threaded (owned by
/// the encoder thread).
/// </para>
/// </summary>
internal sealed class HeldFrameTiming
{
    /// <summary><see cref="HeldTime"/> while no frame is held.</summary>
    public const long None = -1;

    private long _held = None;

    /// <summary>The sample time of the held frame, or <see cref="None"/>.</summary>
    public long HeldTime => _held;

    public bool HasHeld => _held != None;

    /// <summary>
    /// Holds a new frame with sample time <paramref name="sampleTime"/> (relative to the segment start). True when a
    /// frame was held before: that frame must now be written with <paramref name="heldTime"/> and
    /// <paramref name="heldDuration"/> (it lasts until the new frame).
    /// </summary>
    public bool Advance(long sampleTime, out long heldTime, out long heldDuration)
    {
        if (_held == None)
        {
            heldTime = 0;
            heldDuration = 0;
            _held = 0; // the first frame stands for the picture since the segment start
            return false;
        }

        heldTime = _held;
        heldDuration = Math.Max(sampleTime - _held, 1);
        _held = _held + heldDuration;
        return true;
    }

    /// <summary>
    /// Ends the timeline at <paramref name="endTime"/> (relative to the segment start). True when a frame was held: it
    /// must be written with <paramref name="heldTime"/> and <paramref name="heldDuration"/> (it lasts until the end).
    /// </summary>
    public bool Close(long endTime, out long heldTime, out long heldDuration)
    {
        if (_held == None)
        {
            heldTime = 0;
            heldDuration = 0;
            return false;
        }

        heldTime = _held;
        heldDuration = Math.Max(endTime - _held, 1);
        _held = None;
        return true;
    }

    /// <summary>Forgets the held frame (its sample was released without being written).</summary>
    public void Discard() => _held = None;
}

/// <summary>
/// The placeholder frame of a segment in which no sample reached the encoder (WGC delivered no frame: headless or
/// non-composited session, locked workstation, every frame dropped). Media Foundation refuses to finalize an MP4 without
/// samples (MF_E_SINK_NO_SAMPLES_PROCESSED), so the encoder thread writes one opaque black frame at sample time 0 that
/// lasts until the end of the segment, and the segment becomes a valid one-frame file of the segment's length (if
/// Finalize still fails, the segment ends empty: see <see cref="SegmentCompletion"/>). It is not counted as captured or encoded, so
/// <see cref="Murmur.Core.Capture.ScreenRecorderStats.FramesCaptured"/> == 0 still tells callers nothing was captured.
/// Pure and allocation-free.
/// </summary>
internal static class PlaceholderFrame
{
    /// <summary>One B8G8R8A8 pixel of opaque black, read as a little-endian uint (B, G, R = 0, A = 255).</summary>
    public const uint OpaqueBlackBgra = 0xFF000000;

    /// <summary>
    /// True when the placeholder must be written before Finalize: no sample reached the timeline and the writer
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
