using Murmur.Core.Intelligence;
using Murmur.Core.Models;

namespace Murmur.Desktop;

/// <summary>
/// <see cref="IModelManager"/> that releases the local AI before the files it holds are deleted: removing the
/// llama.cpp runtime or a GGUF model while a <c>llama-server</c> process still has it open fails on Windows. Everything
/// else is delegated unchanged.
/// </summary>
/// <remarks>
/// ASR and VAD items need no release: the ASR worker process only runs while sessions are being processed, and the
/// processor stops it for the whole recording.
/// </remarks>
internal sealed class AiAwareModelManager : IModelManager
{
    private readonly IModelManager _inner;
    private readonly ILlm _llm;

    public AiAwareModelManager(IModelManager inner, ILlm llm)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(llm);
        _inner = inner;
        _llm = llm;
    }

    public string ModelsDirectory => _inner.ModelsDirectory;

    public IReadOnlyList<ModelInfo> Catalog => _inner.Catalog;

    public string RecommendedChatModelId => _inner.RecommendedChatModelId;

    public long AvailableFreeSpaceBytes => _inner.AvailableFreeSpaceBytes;

    public event Action<ModelStatus>? StatusChanged
    {
        add => _inner.StatusChanged += value;
        remove => _inner.StatusChanged -= value;
    }

    public ModelStatus GetStatus(string id) => _inner.GetStatus(id);

    public Task InstallAsync(string id, CancellationToken cancellationToken) => _inner.InstallAsync(id, cancellationToken);

    public async Task RemoveAsync(string id, CancellationToken cancellationToken)
    {
        if (IsAiItem(id))
        {
            await _llm.UnloadAsync(cancellationToken).ConfigureAwait(false);
        }

        await _inner.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private bool IsAiItem(string id)
    {
        foreach (var model in _inner.Catalog)
        {
            if (string.Equals(model.Id, id, StringComparison.Ordinal))
            {
                return model.Kind is ModelKind.LlmRuntime or ModelKind.Chat or ModelKind.Embedding;
            }
        }

        return false;
    }
}
