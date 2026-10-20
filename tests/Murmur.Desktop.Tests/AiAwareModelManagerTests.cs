using Microsoft.Extensions.AI;
using Murmur.Core.Intelligence;
using Murmur.Core.Models;
using Murmur.Desktop;

namespace Murmur.Desktop.Tests;

public sealed class AiAwareModelManagerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Path(string id) => "/models/gguf/" + id + ".gguf";

    private static AiAwareModelManager Create(List<string> log, params string[] loaded) =>
        new(new FakeManager(log), new FakeLlm(log), id => id == "unknown" ? null : Path(id), path => loaded.Contains(path));

    [Theory]
    [InlineData("runtime", true)]  // the executable: always released
    [InlineData("runtime-vulkan", true)] // a GPU build is not listed in the catalog but holds its executable the same way
    [InlineData("chat", true)]     // loaded
    [InlineData("chat-big", false)] // not loaded
    [InlineData("embed", true)]    // loaded
    [InlineData("embed-old", false)]
    [InlineData("asr", false)]
    [InlineData("vad", false)]
    [InlineData("unknown", false)]
    public async Task Remove_UnloadsTheAiBeforeDeletingWhatIsHeldOnly(string id, bool unloads)
    {
        var log = new List<string>();
        var manager = Create(log, Path("chat"), Path("embed"));

        await manager.RemoveAsync(id, Ct);

        Assert.Equal(unloads ? ["unload", "remove " + id] : ["remove " + id], log);
    }

    [Fact]
    public async Task Remove_OfAModelNoLongerActiveButStillLoaded_Unloads()
    {
        // The user switched to another chat model while the server still holds the old one.
        var log = new List<string>();
        var manager = Create(log, Path("chat-big"));

        await manager.RemoveAsync("chat-big", Ct);

        Assert.Equal(["unload", "remove chat-big"], log);
    }

    [Fact]
    public async Task Remove_WithNothingLoaded_DoesNotUnloadForChatOrEmbedding()
    {
        var log = new List<string>();
        var manager = Create(log);

        await manager.RemoveAsync("chat", Ct);
        await manager.RemoveAsync("embed", Ct);

        Assert.Equal(["remove chat", "remove embed"], log);
    }

    [Fact]
    public void Everything_ElseIsDelegatedAndEventsAreForwarded()
    {
        var log = new List<string>();
        var inner = new FakeManager(log);
        var manager = new AiAwareModelManager(inner, new FakeLlm(log), _ => null, _ => false);
        ModelStatus? seen = null;
        manager.StatusChanged += status => seen = status;

        inner.Raise(inner.GetStatus("chat"));

        Assert.Equal("/models", manager.ModelsDirectory);
        Assert.Same(inner.Catalog, manager.Catalog);
        Assert.Equal("chat", manager.RecommendedChatModelId);
        Assert.Equal("chat", seen?.Model.Id);
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

        /// <summary>Installable by id but not listed (<c>ModelCatalogItem.Listed = false</c>): the GPU builds of the runtime.</summary>
        private static ModelInfo Unlisted { get; } = new("runtime-vulkan", ModelKind.LlmRuntime, "Runtime (Vulkan)", 1, false);

        public ModelStatus GetStatus(string id) =>
            new(
                Catalog.FirstOrDefault(m => m.Id == id) ?? (id == Unlisted.Id ? Unlisted : throw new ArgumentException($"Unknown model id '{id}'", nameof(id))),
                ModelInstallState.Installed,
                1,
                1,
                null);

        public Task InstallAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveAsync(string id, CancellationToken cancellationToken)
        {
            log.Add("remove " + id);
            return Task.CompletedTask;
        }

        public void Raise(ModelStatus status) => StatusChanged?.Invoke(status);
    }
}
