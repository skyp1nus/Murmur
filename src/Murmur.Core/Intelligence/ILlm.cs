using Microsoft.Extensions.AI;

namespace Murmur.Core.Intelligence;

public enum LlmStatus
{
    /// <summary>Model files are missing (AI was skipped or not downloaded yet).</summary>
    NotInstalled,
    Unloaded,
    Loading,
    Ready,
}

/// <summary>
/// Local language and embedding models. Models run in a separate worker process, start lazily on the first
/// request and are unloaded after use; they are never kept loaded while a recording is in progress.
/// </summary>
public interface ILlm : IAsyncDisposable
{
    LlmStatus Status { get; }

    ValueTask<IChatClient> GetChatClientAsync(CancellationToken cancellationToken);

    ValueTask<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingGeneratorAsync(CancellationToken cancellationToken);

    /// <summary>Stops the worker processes and frees model memory.</summary>
    ValueTask UnloadAsync(CancellationToken cancellationToken);
}
