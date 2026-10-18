namespace Murmur.Core.Recording;

/// <summary>Tuning of <see cref="Recorder"/>.</summary>
public sealed record RecorderSettings
{
    public static RecorderSettings Default { get; } = new();

    /// <summary>How often the WAV headers are rewritten and the files forced to disk (bounds the loss on a power cut).</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How often the writer thread drains the capture rings.</summary>
    public TimeSpan WriterPeriod { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>Ring length per audio source, in seconds of source audio (at least 4).</summary>
    public double RingSeconds { get; init; } = 4;
}
