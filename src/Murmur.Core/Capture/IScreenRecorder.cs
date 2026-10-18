namespace Murmur.Core.Capture;

public sealed record DisplayInfo(string Id, string Name, int Width, int Height, bool IsPrimary);

public sealed record ScreenCaptureOptions(string? DisplayId = null, int FrameRate = 60, int MaxHeight = 1080, int BitrateKbps = 12_000);

public readonly record struct ScreenRecorderStats(long FramesCaptured, long FramesEncoded, long FramesDropped, string Encoder);

/// <summary>
/// Captures a display and encodes it to MP4 on the GPU. Recording is split into segments so the screen
/// can be toggled on and off during a session; each segment is a self-contained, crash-tolerant file.
/// </summary>
public interface IScreenRecorder : IAsyncDisposable
{
    bool IsRecording { get; }

    ScreenRecorderStats Stats { get; }

    /// <summary>Starts a new segment written to <paramref name="filePath"/>.</summary>
    ValueTask StartSegmentAsync(string filePath, CancellationToken cancellationToken);

    /// <summary>Finalizes the current segment. No-op when not recording.</summary>
    ValueTask StopSegmentAsync(CancellationToken cancellationToken);

    event Action<CaptureFault>? Faulted;
}
