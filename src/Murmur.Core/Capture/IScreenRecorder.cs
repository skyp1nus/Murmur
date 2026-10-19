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
    /// <remarks>
    /// Media time 0 of the file is the moment this call starts, not the first captured frame: start-up (capture
    /// session, encoder) and the wait for the first frame are covered by the first frame (or a placeholder), so the
    /// caller can place the segment on its own timeline at the time it made the call.
    /// </remarks>
    ValueTask StartSegmentAsync(string filePath, CancellationToken cancellationToken);

    /// <summary>
    /// Finalizes the current segment. No-op when not recording. The file ends at the moment this call starts, so its
    /// media duration is the time between the start and stop calls.
    /// </summary>
    ValueTask StopSegmentAsync(CancellationToken cancellationToken);

    event Action<CaptureFault>? Faulted;
}
