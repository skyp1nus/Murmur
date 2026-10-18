namespace Murmur.Core.Transcription;

/// <param name="AudioPath">16 kHz mono WAV.</param>
/// <param name="Language">ISO 639-1 code, or <c>null</c> for auto-detection.</param>
public sealed record AsrRequest(string AudioPath, Speaker Speaker, string? Language = null);

public readonly record struct AsrProgress(TimeSpan Processed, TimeSpan Total);

public enum AsrStatus
{
    /// <summary>Model files are missing; nothing can be transcribed yet.</summary>
    ModelMissing,
    Idle,
    Busy,
}

/// <summary>
/// Speech-to-text. Implementations run the model in a separate worker process, load it lazily and
/// unload it when idle; nothing model-related lives in the app process.
/// </summary>
public interface IAsr
{
    AsrStatus Status { get; }

    /// <summary>Transcribes one track; segments are streamed in time order as they are decoded.</summary>
    IAsyncEnumerable<TranscriptSegment> TranscribeAsync(AsrRequest request, IProgress<AsrProgress>? progress, CancellationToken cancellationToken);
}
