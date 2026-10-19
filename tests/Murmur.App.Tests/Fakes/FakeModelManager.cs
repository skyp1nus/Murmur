using Murmur.Core.Models;

namespace Murmur.App.Tests.Fakes;

/// <summary>
/// A scriptable <see cref="IModelManager"/>: installs stay pending until the test completes, fails or progresses them.
/// Nothing touches the disk or the network.
/// </summary>
internal sealed class FakeModelManager : IModelManager
{
    public const string AsrId = "asr";
    public const string VadId = "vad";
    public const string RuntimeId = "runtime";
    public const string EmbeddingId = "embedding";
    public const string ChatSmallId = "chat-small";
    public const string ChatLargeId = "chat-large";

    private readonly object _gate = new();
    private readonly Dictionary<string, ModelStatus> _status = [];
    private readonly Dictionary<string, TaskCompletionSource> _pending = [];
    private readonly Dictionary<string, CancellationToken> _tokens = [];

    public FakeModelManager(IEnumerable<ModelInfo>? catalog = null)
    {
        Catalog =
        [
            .. catalog ??
            [
                new ModelInfo(AsrId, ModelKind.Asr, "Parakeet TDT 0.6B v3 (int8)", 670_478_772, Required: true),
                new ModelInfo(VadId, ModelKind.Vad, "Silero VAD", 643_854, Required: true),
                new ModelInfo(RuntimeId, ModelKind.LlmRuntime, "llama.cpp b11716 (CPU server)", 30_000_000, Required: false),
                new ModelInfo(EmbeddingId, ModelKind.Embedding, "EmbeddingGemma 300M (Q8_0)", 333_590_944, Required: false),
                new ModelInfo(ChatSmallId, ModelKind.Chat, "Gemma 4 E4B (Q4_K_M)", 5_000_000_000, Required: false),
                new ModelInfo(ChatLargeId, ModelKind.Chat, "Gemma 4 26B A4B (Q4_K_M)", 17_000_000_000, Required: false),
            ],
        ];
        foreach (var info in Catalog)
        {
            _status[info.Id] = new ModelStatus(info, ModelInstallState.NotInstalled, 0, null, null);
        }
    }

    public string ModelsDirectory { get; set; } = "/models";

    public IReadOnlyList<ModelInfo> Catalog { get; }

    public string RecommendedChatModelId { get; set; } = ChatSmallId;

    public long AvailableFreeSpaceBytes { get; set; } = 123_456_000_000;

    /// <summary>Install calls in the order they were made.</summary>
    public List<string> InstallCalls { get; } = [];

    public List<string> RemoveCalls { get; } = [];

    public event Action<ModelStatus>? StatusChanged;

    public ModelStatus GetStatus(string id)
    {
        lock (_gate)
        {
            return _status[id];
        }
    }

    public Task InstallAsync(string id, CancellationToken cancellationToken)
    {
        TaskCompletionSource tcs;
        lock (_gate)
        {
            InstallCalls.Add(id);
            if (_status[id].State == ModelInstallState.Installed)
            {
                return Task.CompletedTask;
            }

            tcs = _pending[id] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _tokens[id] = cancellationToken;
        }

        cancellationToken.Register(() =>
        {
            Set(id, ModelInstallState.NotInstalled, 0, null, null);
            tcs.TrySetCanceled(cancellationToken);
        });
        Set(id, ModelInstallState.Downloading, 0, null, null);
        return tcs.Task;
    }

    public Task RemoveAsync(string id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            RemoveCalls.Add(id);
        }

        Set(id, ModelInstallState.NotInstalled, 0, null, null);
        return Task.CompletedTask;
    }

    /// <summary>Marks an item as installed from the start (no event).</summary>
    public void SetInstalled(string id)
    {
        lock (_gate)
        {
            var info = _status[id].Model;
            _status[id] = new ModelStatus(info, ModelInstallState.Installed, info.SizeBytes, info.SizeBytes, null);
        }
    }

    public bool WasInstallCalled(string id)
    {
        lock (_gate)
        {
            return InstallCalls.Contains(id);
        }
    }

    public CancellationToken TokenOf(string id)
    {
        lock (_gate)
        {
            return _tokens[id];
        }
    }

    public void Progress(string id, long done, long? total) => Set(id, ModelInstallState.Downloading, done, total, null);

    public void Complete(string id)
    {
        TaskCompletionSource tcs;
        lock (_gate)
        {
            var info = _status[id].Model;
            _status[id] = new ModelStatus(info, ModelInstallState.Installed, info.SizeBytes, info.SizeBytes, null);
            tcs = _pending[id];
        }

        StatusChanged?.Invoke(GetStatus(id));
        tcs.TrySetResult();
    }

    public void Fail(string id, string message)
    {
        TaskCompletionSource tcs;
        lock (_gate)
        {
            tcs = _pending[id];
        }

        Set(id, ModelInstallState.Failed, 0, null, message);
        tcs.TrySetException(new InvalidOperationException(message));
    }

    /// <summary>Raises a status the way the real manager does for work started elsewhere.</summary>
    public void Raise(ModelStatus status)
    {
        lock (_gate)
        {
            _status[status.Model.Id] = status;
        }

        StatusChanged?.Invoke(status);
    }

    private void Set(string id, ModelInstallState state, long done, long? total, string? error)
    {
        ModelStatus status;
        lock (_gate)
        {
            status = _status[id] = new ModelStatus(_status[id].Model, state, done, total, error);
        }

        StatusChanged?.Invoke(status);
    }
}
