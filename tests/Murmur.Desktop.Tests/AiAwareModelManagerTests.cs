using Microsoft.Extensions.AI;
using Murmur.Core.Intelligence;
using Murmur.Core.Models;
using Murmur.Desktop;

namespace Murmur.Desktop.Tests;

public sealed class AiAwareModelManagerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("runtime", true)]
    [InlineData("chat", true)]
    [InlineData("chat-big", false)] // inactive chat model
    [InlineData("embed", true)]
    [InlineData("embed-old", false)]
    [InlineData("asr", false)]
    [InlineData("vad", false)]
    [InlineData("unknown", false)]
    public async Task Remove_UnloadsTheAiBeforeDeletingAiItemsOnly(string id, bool unloads)
    {
        var log = new List<string>();
        var manager = new FakeManager(log);
        var manager2 = new AiAwareModelManager(manager, new FakeLlm(log), ActiveId);

        await manager2.RemoveAsync(id, Ct);

        Assert.Equal(unloads ? ["unload", "remove " + id] : ["remove " + id], log);
    }

    [Fact]
    public void Everything_ElseIsDelegatedAndEventsAreForwarded()
    {
        var log = new List<string>();
        var inner = new FakeManager(log);
        var manager = new AiAwareModelManager(inner, new FakeLlm(log), ActiveId);
        ModelStatus? seen = null;
        manager.StatusChanged += status => seen = status;

        inner.Raise(inner.GetStatus("chat"));

        Assert.Equal("/models", manager.ModelsDirectory);
        Assert.Same(inner.Catalog, manager.Catalog);
        Assert.Equal("chat", manager.RecommendedChatModelId);
        Assert.Equal("chat", seen?.Model.Id);
    }

    private static string? ActiveId(ModelKind kind) => kind switch
    {
        ModelKind.Chat => "chat",
        ModelKind.Embedding => "embed",
        _ => null,
    };

    [Fact]
    public async Task Remove_WithNoActiveModel_DoesNotUnloadForChatOrEmbedding()
    {
        var log = new List<string>();
        var manager = new AiAwareModelManager(new FakeManager(log), new FakeLlm(log), _ => null);

        await manager.RemoveAsync("chat", Ct);

        Assert.Equal(["remove chat"], log);
    }

    private sealed class FakeLlm(List<string> log) : ILlm
    {
        public LlmStatus Status => LlmStatus.Ready;

        public ValueTask<IChatClient> GetChatClientAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingGeneratorAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask UnloadAsync(CancellationToken cancellationToken)
        {
            log.Add("unload");
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeManager(List<string> log) : IModelManager
    {
        public string ModelsDirectory => "/models";

        public IReadOnlyList<ModelInfo> Catalog { get; } =
        [
            new("asr", ModelKind.Asr, "ASR", 1, true),
            new("vad", ModelKind.Vad, "VAD", 1, true),
            new("runtime", ModelKind.LlmRuntime, "Runtime", 1, false),
            new("chat", ModelKind.Chat, "Chat", 1, false),
            new("chat-big", ModelKind.Chat, "Chat big", 1, false),
            new("embed", ModelKind.Embedding, "Embed", 1, false),
            new("embed-old", ModelKind.Embedding, "Embed old", 1, false),
        ];

        public string RecommendedChatModelId => "chat";

        public long AvailableFreeSpaceBytes => 0;

        public event Action<ModelStatus>? StatusChanged;

        public ModelStatus GetStatus(string id) => new(Catalog.Single(m => m.Id == id), ModelInstallState.Installed, 1, 1, null);

        public Task InstallAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveAsync(string id, CancellationToken cancellationToken)
        {
            log.Add("remove " + id);
            return Task.CompletedTask;
        }

        public void Raise(ModelStatus status) => StatusChanged?.Invoke(status);
    }
}
