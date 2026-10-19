using Murmur.Core.Intelligence;
using Murmur.Core.Models;

namespace Murmur.Desktop;

/// <summary>
/// <see cref="IModelManager"/> that releases the local AI before the files it holds are deleted: removing the
/// llama.cpp runtime or the active chat/embedding model while a <c>llama-server</c> process still has it open fails on
/// Windows. Removing an inactive model touches nothing that is loaded, so a running answer is not interrupted.
/// Everything else is delegated unchanged.
/// </summary>
/// <remarks>
/// ASR and VAD items need no release: the ASR worker process only runs while sessions are being processed, and the
/// processor stops it for the whole recording.
/// </remarks>
internal sealed class AiAwareModelManager : IModelManager
{
    private readonly IModelManager _inner;
    private readonly ILlm _llm;
    private readonly Func<ModelKind, string?> _activeModelId;

    /// <param name="activeModelId">The id of the active chat/embedding model (<c>ModelManager.ActiveModelId</c>), or <c>null</c>.</param>
    public AiAwareModelManager(IModelManager inner, ILlm llm, Func<ModelKind, string?> activeModelId)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(llm);
        ArgumentNullException.ThrowIfNull(activeModelId);
        _inner = inner;
        _llm = llm;
        _activeModelId = activeModelId;
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
                return model.Kind switch
                {
                    ModelKind.LlmRuntime => true,
                    ModelKind.Chat or ModelKind.Embedding => string.Equals(_activeModelId(model.Kind), id, StringComparison.Ordinal),
                    _ => false,
                };
            }
        }

        return false;
    }
}
