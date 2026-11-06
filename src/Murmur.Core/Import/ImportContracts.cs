using Murmur.Core.Sessions;

namespace Murmur.Core.Import;

public enum MediaKind
{
    Audio,
    Video,
}

/// <summary>What a decoder found in a media file.</summary>
public sealed record MediaProbe(string Path, MediaKind Kind, long SizeBytes, TimeSpan Duration);

public interface IMediaDecoder
{
    /// <summary>Lower-case extensions with the dot (".wav") this decoder can try.</summary>
    IReadOnlySet<string> Extensions { get; }

    /// <summary>Throws NotSupportedException (format) or InvalidDataException (damaged file).</summary>
    ValueTask<MediaProbe> ProbeAsync(string path, CancellationToken cancellationToken);

    /// <summary>Decodes the first audio stream into a WAV file in the recorder's track format at <paramref name="destinationWav"/>.</summary>
    ValueTask DecodeAudioAsync(string path, string destinationWav, IProgress<double>? progress, CancellationToken cancellationToken);
}

public enum OriginalFileMode
{
    CopyToLibrary,
    LeaveInPlace,
}

/// <param name="Language">A Parakeet language code, or null = auto-detect.</param>
public sealed record ImportOptions(string? Language, OriginalFileMode OriginalFile);

public sealed record ImportRequest(string Path, ImportOptions Options);

public enum ImportState
{
    Queued,
    Decoding,
    Done,
    Failed,
    Cancelled,
}

public sealed record ImportItem(long Id, string Path, ImportState State, double Progress, SessionId? Session, string? Error);

/// <summary>Imports files one by one; persisted, so unfinished imports restart after an app restart. Changed is raised off the UI thread.</summary>
public interface IImportQueue
{
    /// <summary>All file extensions the import accepts on this platform (the union of the decoders).</summary>
    IReadOnlySet<string> SupportedExtensions { get; }

    IReadOnlyList<ImportItem> Items { get; }

    /// <summary>Unsupported extensions are rejected here (Failed item with the "unsupported" code, nothing written).</summary>
    ValueTask<IReadOnlyList<ImportItem>> EnqueueAsync(IReadOnlyList<ImportRequest> requests, CancellationToken cancellationToken);

    /// <summary>Stops the running decode or removes a queued item; a half-made session is deleted.</summary>
    ValueTask CancelAsync(long id, CancellationToken cancellationToken);

    event Action? Changed;
}
