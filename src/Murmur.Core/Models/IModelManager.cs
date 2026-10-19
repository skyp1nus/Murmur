namespace Murmur.Core.Models;

public enum ModelKind
{
    /// <summary>Speech recognition (Parakeet). Required for transcription.</summary>
    Asr,

    /// <summary>Voice activity detection (Silero). Required for transcription.</summary>
    Vad,

    /// <summary>The local LLM runtime (llama.cpp <c>llama-server</c>). Required for AI.</summary>
    LlmRuntime,

    /// <summary>A chat model (Gemma). One is used at a time.</summary>
    Chat,

    /// <summary>The embedding model (EmbeddingGemma). Required for AI search.</summary>
    Embedding,
}

/// <summary>One installable item of the model catalog.</summary>
/// <param name="Id">Stable id, e.g. <c>parakeet-tdt-0.6b-v3-int8</c>; used in settings.</param>
/// <param name="SizeBytes">Approximate download size, for the UI before the download starts.</param>
/// <param name="Required">Needed for the core app (transcription); AI items are optional.</param>
public sealed record ModelInfo(string Id, ModelKind Kind, string DisplayName, long SizeBytes, bool Required);

public enum ModelInstallState
{
    NotInstalled,
    Downloading,
    Installed,
    Failed,
}

/// <param name="BytesDone">Bytes on disk so far (including a resumed partial download).</param>
/// <param name="BytesTotal">Total bytes, or <c>null</c> while unknown.</param>
/// <param name="Error">Last failure, English, for logs; the UI shows its own text.</param>
public sealed record ModelStatus(ModelInfo Model, ModelInstallState State, long BytesDone, long? BytesTotal, string? Error);

/// <summary>
/// Downloads, verifies and installs the models the app needs into the models directory, in the layout the rest of
/// the app already reads (<c>scripts/fetch-models.*</c> layout). Thread-safe; installs of different items may run in
/// parallel, a second install of the same item joins the first. Events are raised off the UI thread.
/// </summary>
public interface IModelManager
{
    string ModelsDirectory { get; }

    /// <summary>Items for this OS in display order (required first).</summary>
    IReadOnlyList<ModelInfo> Catalog { get; }

    /// <summary>The chat model suggested for this machine (by memory); one of the <see cref="ModelKind.Chat"/> items.</summary>
    string RecommendedChatModelId { get; }

    /// <summary>Free space on the drive of <see cref="ModelsDirectory"/>.</summary>
    long AvailableFreeSpaceBytes { get; }

    ModelStatus GetStatus(string id);

    /// <summary>
    /// Downloads (resuming a partial download), verifies (size, and SHA-256 when the catalog knows it) and installs
    /// the item atomically: until it completes, the previous state stays on disk. Cancelling keeps the partial
    /// download for the next attempt. Installing a chat or embedding model also makes it the active one.
    /// </summary>
    Task InstallAsync(string id, CancellationToken cancellationToken);

    /// <summary>Deletes the item's files. Removing the active chat model leaves AI without a chat model.</summary>
    Task RemoveAsync(string id, CancellationToken cancellationToken);

    /// <summary>Raised on state changes and, throttled to at most ~10 times a second, on download progress.</summary>
    event Action<ModelStatus>? StatusChanged;
}
