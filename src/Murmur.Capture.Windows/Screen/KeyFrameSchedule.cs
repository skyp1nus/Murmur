namespace Murmur.Capture.Windows.Screen;

/// <summary>
/// When the encoder must be told to start a new IDR frame, so that the time between keyframes is bounded by media time
/// whatever the content. The GOP counts frames, and the file has a variable frame rate: on a static screen the recorder
/// writes about one repeat a second, so a 2 s GOP of 120 frames at 60 fps would put two minutes between keyframes. A
/// file cut off by a crash still keeps every complete fragment (Media Foundation's FMPEG4 sink writes one every ~0.3 s
/// of media time or every sample, keyframe or not), but a player decodes, seeks and recovers from damage only from a
/// keyframe on. So a keyframe is forced (CODECAPI_AVEncVideoForceKeyFrame) on the first sample at least
/// <see cref="Interval"/> after the previous one.
/// <para>
/// Natural GOP keyframes are not tracked: the hardware encoder restarts its GOP at a forced IDR frame
/// (KeyFrameIntervalTests), so on a steady 60 fps stream it never adds one of its own, because the forced one comes
/// first. The threshold is half a frame below the interval, so on a steady stream the forced keyframe lands on the
/// frame the GOP would have picked instead of one frame after it. The request only lands on the next frame while the
/// encoder keeps up (an asynchronous hardware MFT with a backlog applies it later and merges requests); the recorder
/// writes in real time. Pure and allocation-free; single-threaded (owned by the encoder thread).
/// </para>
/// </summary>
internal struct KeyFrameSchedule
{
    /// <summary>The longest media time between keyframes: 2 s.</summary>
    public const long DefaultInterval = 2 * MediaTime.TicksPerSecond;

    private const long None = long.MinValue;

    private readonly long _threshold;
    private long _lastKey;

    /// <param name="interval">The longest media time (100 ns) between keyframes.</param>
    /// <param name="frameRate">The encoder frame rate (half a frame of slack, see the remarks).</param>
    public KeyFrameSchedule(long interval, int frameRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(interval);
        Interval = interval;
        _threshold = Math.Max(1, interval - (MediaTime.FrameDuration(frameRate) / 2));
        _lastKey = None;
    }

    public long Interval { get; }

    /// <summary>The sample time of the last keyframe, or null before the first sample.</summary>
    public readonly long? LastKeyFrame => _lastKey == None ? null : _lastKey;

    /// <summary>
    /// True when the sample at <paramref name="time"/> (100 ns, the sample time) must be an IDR frame; call
    /// <see cref="Forced"/> once the encoder was told. The first sample is a keyframe anyway (every stream starts with
    /// one) and only starts the schedule.
    /// </summary>
    public bool ShouldForce(long time)
    {
        if (_lastKey == None)
        {
            _lastKey = time;
            return false;
        }

        return time - _lastKey >= _threshold;
    }

    /// <summary>The encoder accepted the request: the sample at <paramref name="time"/> is the new keyframe.</summary>
    public void Forced(long time) => _lastKey = time;
}
