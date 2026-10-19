using System.Text;
using System.Text.Json;
using Murmur.Core.Models;
using Murmur.Models.Tests.Support;

namespace Murmur.Models.Tests;

public sealed class ManifestTests
{
    private static string Gguf(Harness h, string name = "") => Path.Combine(h.Models, "gguf", name);

    private static string ManifestPath(Harness h) => Gguf(h, "models.json");

    private static (ModelCatalogItem Chat1, ModelCatalogItem Chat2, ModelCatalogItem Embed) Items(Harness h)
    {
        h.Server.Add("/chat1", Harness.Bytes(4000, 1));
        h.Server.Add("/chat2", Harness.Bytes(5000, 2));
        h.Server.Add("/embed", Harness.Bytes(3000, 3));
        return (
            Harness.FileItem("chat1", h.Server, "/chat1", "gguf/chat-one.gguf", ModelKind.Chat, repo: "org/chat1"),
            Harness.FileItem("chat2", h.Server, "/chat2", "gguf/chat-two.gguf", ModelKind.Chat, repo: "org/chat2"),
            Harness.FileItem("embed", h.Server, "/embed", "gguf/embed.gguf", ModelKind.Embedding, repo: "org/embed"));
    }

    private static JsonElement Json(Harness h) => JsonDocument.Parse(File.ReadAllText(ManifestPath(h))).RootElement;

    [Fact]
    public async Task InstallingAChatModel_WritesTheFetchModelsFormat()
    {
        await using var h = new Harness();
        var (chat1, _, embed) = Items(h);
        using var manager = h.Manager([chat1, embed]);

        await manager.InstallAsync("chat1", TestContext.Current.CancellationToken);
        await manager.InstallAsync("embed", TestContext.Current.CancellationToken);

        var json = Json(h);
        Assert.Equal(Gguf(h, "chat-one.gguf"), json.GetProperty("chat").GetString());
        Assert.Equal(Gguf(h, "embed.gguf"), json.GetProperty("embed").GetString());
        Assert.Equal("org/chat1", json.GetProperty("repo").GetProperty("chat").GetString());
        Assert.Equal("org/embed", json.GetProperty("repo").GetProperty("embed").GetString());
        Assert.Equal("chat-one.gguf", json.GetProperty("file").GetProperty("chat").GetString());
        Assert.Equal("embed.gguf", json.GetProperty("file").GetProperty("embed").GetString());
        Assert.False(File.Exists(ManifestPath(h) + ".part"));
    }

    [Fact]
    public async Task TheManifestParsesTheWayAiModelPathsParsesIt()
    {
        await using var h = new Harness();
        var (chat1, _, embed) = Items(h);
        using var manager = h.Manager([chat1, embed]);
        await manager.InstallAsync("chat1", TestContext.Current.CancellationToken);
        await manager.InstallAsync("embed", TestContext.Current.CancellationToken);

        var manifest = AiModelPathsReader.Read(ManifestPath(h));

        Assert.NotNull(manifest);
        Assert.Equal(Gguf(h, "chat-one.gguf"), manifest.Chat);
        Assert.Equal(Gguf(h, "embed.gguf"), manifest.Embed);
    }

    [Fact]
    public async Task InstallingAnotherChatModel_SwitchesTheActiveOneAndKeepsTheEmbedding()
    {
        await using var h = new Harness();
        var (chat1, chat2, embed) = Items(h);
        using var manager = h.Manager([chat1, chat2, embed]);
        await manager.InstallAsync("embed", TestContext.Current.CancellationToken);
        await manager.InstallAsync("chat1", TestContext.Current.CancellationToken);

        await manager.InstallAsync("chat2", TestContext.Current.CancellationToken);

        var json = Json(h);
        Assert.Equal(Gguf(h, "chat-two.gguf"), json.GetProperty("chat").GetString());
        Assert.Equal(Gguf(h, "embed.gguf"), json.GetProperty("embed").GetString());
        Assert.Equal("org/chat2", json.GetProperty("repo").GetProperty("chat").GetString());
        Assert.Equal("chat2", manager.ActiveModelId(ModelKind.Chat));
        Assert.True(File.Exists(Gguf(h, "chat-one.gguf"))); // the old file stays
        Assert.Equal(ModelInstallState.Installed, manager.GetStatus("chat1").State);
    }

    [Fact]
    public async Task InstallingAnAlreadyInstalledModel_JustMakesItActive()
    {
        await using var h = new Harness();
        var (chat1, chat2, _) = Items(h);
        using var manager = h.Manager([chat1, chat2]);
        await manager.InstallAsync("chat1", TestContext.Current.CancellationToken);
        await manager.InstallAsync("chat2", TestContext.Current.CancellationToken);
        var requests = h.Server.Requests.Count;

        await manager.InstallAsync("chat1", TestContext.Current.CancellationToken);

        Assert.Equal(requests, h.Server.Requests.Count);
        Assert.Equal("chat1", manager.ActiveModelId(ModelKind.Chat));
    }

    [Fact]
    public async Task RemovingTheActiveModel_ClearsItsKeysOnly()
    {
        await using var h = new Harness();
        var (chat1, _, embed) = Items(h);
        using var manager = h.Manager([chat1, embed]);
        await manager.InstallAsync("chat1", TestContext.Current.CancellationToken);
        await manager.InstallAsync("embed", TestContext.Current.CancellationToken);

        await manager.RemoveAsync("chat1", TestContext.Current.CancellationToken);

        var json = Json(h);
        Assert.False(json.TryGetProperty("chat", out _));
        Assert.Equal(Gguf(h, "embed.gguf"), json.GetProperty("embed").GetString());
        Assert.False(json.GetProperty("repo").TryGetProperty("chat", out _));
        Assert.False(json.GetProperty("file").TryGetProperty("chat", out _));
        Assert.Null(AiModelPathsReader.Read(ManifestPath(h))!.Chat);
        Assert.Null(manager.ActiveModelId(ModelKind.Chat));
        Assert.Equal("embed", manager.ActiveModelId(ModelKind.Embedding));
    }

    [Fact]
    public async Task RemovingAnInactiveModel_LeavesTheManifestAlone()
    {
        await using var h = new Harness();
        var (chat1, chat2, _) = Items(h);
        using var manager = h.Manager([chat1, chat2]);
        await manager.InstallAsync("chat1", TestContext.Current.CancellationToken);
        await manager.InstallAsync("chat2", TestContext.Current.CancellationToken);
        var before = await File.ReadAllTextAsync(ManifestPath(h), TestContext.Current.CancellationToken);

        await manager.RemoveAsync("chat1", TestContext.Current.CancellationToken);

        Assert.Equal(before, await File.ReadAllTextAsync(ManifestPath(h), TestContext.Current.CancellationToken));
        Assert.Equal("chat2", manager.ActiveModelId(ModelKind.Chat));
    }

    [Fact]
    public async Task AManifestFromFetchModels_IsUpdatedNotLost()
    {
        await using var h = new Harness();
        var (chat1, _, embed) = Items(h);
        Directory.CreateDirectory(Gguf(h));
        // As Windows PowerShell 5.1 writes it: UTF-8 with BOM.
        var legacy = $$"""
            {
              "chat": "{{Gguf(h, "old-chat.gguf").Replace("\\", "\\\\")}}",
              "embed": "{{Gguf(h, "embed.gguf").Replace("\\", "\\\\")}}",
              "repo": { "chat": "ggml-org/old", "embed": "org/embed" },
              "file": { "chat": "old-chat.gguf", "embed": "embed.gguf" }
            }
            """;
        await File.WriteAllTextAsync(ManifestPath(h), legacy, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Gguf(h, "embed.gguf"), Harness.Bytes(100), TestContext.Current.CancellationToken);
        using var manager = h.Manager([chat1, embed]);
        Assert.Equal("embed", manager.ActiveModelId(ModelKind.Embedding)); // the legacy file is understood
        Assert.Equal(ModelInstallState.Installed, manager.GetStatus("embed").State);

        await manager.InstallAsync("chat1", TestContext.Current.CancellationToken);

        var manifest = AiModelPathsReader.Read(ManifestPath(h));
        Assert.Equal(Gguf(h, "chat-one.gguf"), manifest!.Chat);
        Assert.Equal(Gguf(h, "embed.gguf"), manifest.Embed);
        Assert.Equal("org/embed", Json(h).GetProperty("repo").GetProperty("embed").GetString());
    }

    [Fact]
    public async Task AnUnreadableManifest_IsReplaced()
    {
        await using var h = new Harness();
        var (chat1, _, _) = Items(h);
        Directory.CreateDirectory(Gguf(h));
        await File.WriteAllTextAsync(ManifestPath(h), "{ this is not json", TestContext.Current.CancellationToken);
        using var manager = h.Manager([chat1]);

        await manager.InstallAsync("chat1", TestContext.Current.CancellationToken);

        Assert.Equal(Gguf(h, "chat-one.gguf"), AiModelPathsReader.Read(ManifestPath(h))!.Chat);
    }

    [Fact]
    public async Task NonAiItems_NeverTouchTheManifest()
    {
        await using var h = new Harness();
        h.Server.Add("/vad", Harness.Bytes(100));
        using var manager = h.Manager([Harness.FileItem("vad", h.Server, "/vad", "silero_vad.onnx", ModelKind.Vad)]);

        await manager.InstallAsync("vad", TestContext.Current.CancellationToken);

        Assert.False(File.Exists(ManifestPath(h)));
    }
}
