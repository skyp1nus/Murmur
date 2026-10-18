namespace Murmur.Capture.Windows.Screen;

/// <summary>
/// Tells a burst of back-pressure from a sample pool that stopped delivering: frames dropped because every encoder
/// sample was still in use (MF_E_SAMPLEALLOCATOR_EMPTY) are normal for a moment (the GPU is busy, the encoder lags),
/// but when allocations keep failing for at least <see cref="Duration"/> and <see cref="MinimumDrops"/> frames in a row
/// the frame path is broken (nothing returns the samples) and the recorder reports it once per segment. Any successful
/// allocation starts over. Pure and allocation-free; single-threaded (owned by the FrameArrived handler).
/// </summary>
internal struct PoolExhaustionTracker
{
    /// <summary>How long allocations must keep failing (100 ns units).</summary>
    public const long Duration = 2 * MediaTime.TicksPerSecond;

    /// <summary>How many frames in a row must be dropped (a static screen delivers few frames: two drops 2 s apart prove nothing).</summary>
    public const int MinimumDrops = 10;

    private long _since;
    private bool _reported;

    /// <summary>Frames dropped in a row since the last successful allocation.</summary>
    public int Consecutive { get; private set; }

    /// <summary>How long the current run of drops lasts at <paramref name="now"/> (0 when there is none).</summary>
    public readonly long Elapsed(long now) => Consecutive == 0 ? 0 : now - _since;

    /// <summary>A sample was allocated: the pool delivers again.</summary>
    public void OnAllocated()
    {
        Consecutive = 0;
        _since = 0;
    }

    /// <summary>
    /// A frame was dropped at <paramref name="now"/> (100 ns) because the pool was empty. True the first time a run of
    /// drops becomes persistent, and never again for this tracker (one tracker per segment: reported once).
    /// </summary>
    public bool OnExhausted(long now)
    {
        if (Consecutive++ == 0)
        {
            _since = now;
        }

        if (_reported || Consecutive < MinimumDrops || now - _since < Duration)
        {
            return false;
        }

        _reported = true;
        return true;
    }
}

/// <summary>Texts of a persistent pool exhaustion (pure).</summary>
internal static class PoolExhaustionNaming
{
    /// <summary>
    /// E.g. "GPU zero-copy: all 12 encoder samples stayed in use for 2.0 s (61 frames dropped in a row); the encoder
    /// does not hand frames back; the next segments use CPU readback".
    /// </summary>
    public static string Fault(EncoderInput input, uint samples, int drops, long elapsed)
    {
        var text = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{FrameCopyNaming.Path(input)}: all {samples} encoder samples stayed in use for {(double)elapsed / MediaTime.TicksPerSecond:F1} s ({drops} frames dropped in a row); the encoder does not hand frames back");
        return input == EncoderInput.Gpu ? text + "; the next segments use CPU readback" : text;
    }

    /// <summary>The <c>FramePath</c> of the segments after a GPU-path exhaustion.</summary>
    public static string FallbackReason(EncoderInput input, uint samples) =>
        $"CPU readback: the {FrameCopyNaming.Path(input)} sample pool ({samples} samples) ran dry in an earlier segment";
}
