using Murmur.Core.Intelligence;
using Murmur.Core.Models;

namespace Murmur.Desktop;

/// <summary>
/// <see cref="IModelManager"/> that releases the local AI before the files it holds are deleted: removing the
/// llama.cpp runtime or a GGUF model while a <c>llama-server</c> process still has it open fails on Windows. The runtime
/// is always released; a chat/embedding model only when a server actually holds its file (whether or not it is still the
/// active one), so removing an unused model does not interrupt a running answer. Everything else is delegated unchanged.
/// </summary>
/// <remarks>
/// ASR and VAD items need no release: the ASR worker process only runs while sessions are being processed, and the
/// processor stops it for the whole recording.
/// </remarks>
internal sealed class AiAwareModelManager : IModelManager
{
    private readonly IModelManager _inner;
    private readonly ILlm _llm;
    private readonly Func<string, string?> _itemPath;
    private readonly Func<string, bool> _isModelInUse;

    /// <param name="itemPath">The full path of an item's file (chat/embedding) by id, or <c>null</c> when unknown.</param>
    /// <param name="isModelInUse">Whether a running or stopping server holds the file (<c>LocalLlm.IsUsingModel</c>).</param>
    public AiAwareModelManager(IModelManager inner, ILlm llm, Func<string, string?> itemPath, Func<string, bool> isModelInUse)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(llm);
        ArgumentNullException.ThrowIfNull(itemPath);
        ArgumentNullException.ThrowIfNull(isModelInUse);
        _inner = inner;
        _llm = llm;
        _itemPath = itemPath;
        _isModelInUse = isModelInUse;
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
        var model = Find(id);
        return model?.Kind switch
        {
            ModelKind.LlmRuntime => true,
            ModelKind.Chat or ModelKind.Embedding => _itemPath(id) is { } path && _isModelInUse(path),
            _ => false,
        };
    }

    /// <summary>The listed item, else an unlisted one (a GPU build of the runtime) that the inner manager still knows by id.</summary>
    private ModelInfo? Find(string id)
    {
        foreach (var model in _inner.Catalog)
        {
            if (string.Equals(model.Id, id, StringComparison.Ordinal))
            {
                return model;
            }
        }

        try
        {
            return _inner.GetStatus(id).Model;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
