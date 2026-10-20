using Murmur.Core.Models;
using Murmur.Models.Tests.Support;

namespace Murmur.Models.Tests;

public sealed class MirrorTests
{
    private static string Gguf(Harness h, string name) => Path.Combine(h.Models, "gguf", name);

    private static ModelCatalogItem Chat(Harness h, params ModelMirror[] fallbacks) =>
        new(
            new ModelInfo("chat", ModelKind.Chat, "chat", 1000, Required: false),
            "gguf/chat.gguf",
            IsDirectory: false,
            [new ModelFileSpec(h.Server.Url("/one/chat.gguf"), "chat.gguf", Repo: "org/one", RemoteName: "chat.gguf", Fallbacks: fallbacks)],
            Repo: "org/one");

    private static ModelMirror Two(Harness h, string? sha = null, long? size = null) =>
        new(h.Server.Url("/two/google_chat.gguf"), "org/two", "google_chat.gguf", sha, size);

    private static ModelMirror Three(Harness h) => new(h.Server.Url("/three/chat.gguf"), "org/three", "chat.gguf");

    [Fact]
    public async Task TheFirstSourceThatAnswersIsUsed_AndRecordedInTheManifest()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(30_000);
        h.Server.Add("/one/chat.gguf", Harness.Bytes(10)).NotFound = true;
        h.Server.Add("/two/google_chat.gguf", content);
        using var manager = h.Manager([Chat(h, Two(h), Three(h))]);

        await manager.InstallAsync("chat", TestContext.Current.CancellationToken);

        Assert.Equal(content, await File.ReadAllBytesAsync(Gguf(h, "chat.gguf"), TestContext.Current.CancellationToken)); // canonical local name
        Assert.False(File.Exists(Gguf(h, "google_chat.gguf")));
        var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Gguf(h, "models.json"), TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal("org/two", json.GetProperty("repo").GetProperty("chat").GetString());
        Assert.Equal("google_chat.gguf", json.GetProperty("file").GetProperty("chat").GetString());
        Assert.Equal(Gguf(h, "chat.gguf"), json.GetProperty("chat").GetString());
        Assert.DoesNotContain(h.Server.Requests, r => r.Path == "/three/chat.gguf"); // never asked
        Assert.Equal(h.Server.Url("/two/google_chat.gguf"), manager.GetLastInstallReport("chat").Single().Url);
    }

    [Fact]
    public async Task APreferredSourceWins_WhenItAnswers()
    {
        await using var h = new Harness();
        var first = Harness.Bytes(2000, 1);
        h.Server.Add("/one/chat.gguf", first);
        h.Server.Add("/two/google_chat.gguf", Harness.Bytes(2000, 2));
        using var manager = h.Manager([Chat(h, Two(h))]);

        await manager.InstallAsync("chat", TestContext.Current.CancellationToken);

        Assert.Equal(first, await File.ReadAllBytesAsync(Gguf(h, "chat.gguf"), TestContext.Current.CancellationToken));
        Assert.DoesNotContain(h.Server.Requests, r => r.Path == "/two/google_chat.gguf");
    }

    [Fact]
    public async Task ASourceThatKeepsFailing_IsSkippedAfterItsRetries()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(3000);
        h.Server.Add("/one/chat.gguf", Harness.Bytes(10)).StatusFor = _ => 503;
        h.Server.Add("/two/google_chat.gguf", content);
        using var manager = h.Manager([Chat(h, Two(h))]);

        await manager.InstallAsync("chat", TestContext.Current.CancellationToken);

        Assert.Equal(content, await File.ReadAllBytesAsync(Gguf(h, "chat.gguf"), TestContext.Current.CancellationToken));
        Assert.Equal(4, h.Server.Requests.Count(r => r.Path == "/one/chat.gguf")); // the try plus three retries
    }

    [Fact]
    public async Task ASourceWithTheWrongPinnedSize_IsSkipped()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(3000);
        h.Server.Add("/one/chat.gguf", Harness.Bytes(2999));
        h.Server.Add("/two/google_chat.gguf", content);
        var item = Chat(h, Two(h, size: 3000)) with
        {
            Files = [new ModelFileSpec(h.Server.Url("/one/chat.gguf"), "chat.gguf", SizeBytes: 3000, Fallbacks: [Two(h, size: 3000)])],
        };
        using var manager = h.Manager([item]);

        await manager.InstallAsync("chat", TestContext.Current.CancellationToken);

        Assert.Equal(content, await File.ReadAllBytesAsync(Gguf(h, "chat.gguf"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EachSourceIsVerifiedAgainstItsOwnHash()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(3000);
        h.Server.Add("/one/chat.gguf", Harness.Bytes(10)).NotFound = true;
        h.Server.Add("/two/google_chat.gguf", content);
        using var good = h.Manager([Chat(h, Two(h, sha: Harness.Sha256(content)))]);
        await good.InstallAsync("chat", TestContext.Current.CancellationToken);
        Assert.Equal(ModelInstallState.Installed, good.GetStatus("chat").State);
        await good.RemoveAsync("chat", TestContext.Current.CancellationToken);

        using var bad = h.Manager([Chat(h, Two(h, sha: new string('1', 64)))]);
        var ex = await Assert.ThrowsAsync<ModelInstallException>(() => bad.InstallAsync("chat", TestContext.Current.CancellationToken));
        Assert.Contains("SHA-256 mismatch", ex.Message);
    }

    [Fact]
    public async Task WhenNoSourceAnswers_TheErrorListsEveryUrlTried()
    {
        await using var h = new Harness();
        h.Server.Add("/one/chat.gguf", Harness.Bytes(10)).NotFound = true;
        h.Server.Add("/two/google_chat.gguf", Harness.Bytes(10)).NotFound = true;
        h.Server.Add("/three/chat.gguf", Harness.Bytes(10)).NotFound = true;
        using var manager = h.Manager([Chat(h, Two(h), Three(h))]);

        var ex = await Assert.ThrowsAsync<ModelInstallException>(() => manager.InstallAsync("chat", TestContext.Current.CancellationToken));

        Assert.Contains(h.Server.Url("/one/chat.gguf"), ex.Message);
        Assert.Contains(h.Server.Url("/two/google_chat.gguf"), ex.Message);
        Assert.Contains(h.Server.Url("/three/chat.gguf"), ex.Message);
        Assert.Contains("404", ex.Message);
        var status = manager.GetStatus("chat");
        Assert.Equal(ModelInstallState.Failed, status.State);
        Assert.Contains("/three/chat.gguf", status.Error);
        Assert.False(File.Exists(Gguf(h, "chat.gguf")));
    }

    [Fact]
    public async Task ResumingAfterSwitchingSource_RestartsThePart()
    {
        await using var h = new Harness();
        var firstContent = Harness.Bytes(400_000, 1);
        var one = h.Server.Add("/one/chat.gguf", firstContent);
        one.ChunkSize = 16 * 1024;
        one.ChunkDelay = TimeSpan.FromMilliseconds(15);
        using (var first = h.Manager([Chat(h, Two(h))], progressInterval: TimeSpan.Zero))
        {
            using var cts = new CancellationTokenSource();
            first.StatusChanged += status =>
            {
                if (status.BytesDone > 100_000)
                {
                    cts.Cancel();
                }
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.InstallAsync("chat", cts.Token));
        }

        Assert.True(File.Exists(Path.Combine(h.Models, ".staging", "chat", "dl", "chat.gguf.part")));
        one.NotFound = true; // the first source went away; the second serves a different file
        var secondContent = Harness.Bytes(400_000, 2);
        h.Server.Add("/two/google_chat.gguf", secondContent);

        using var manager = h.Manager([Chat(h, Two(h))]);
        await manager.InstallAsync("chat", TestContext.Current.CancellationToken);

        Assert.Equal(secondContent, await File.ReadAllBytesAsync(Gguf(h, "chat.gguf"), TestContext.Current.CancellationToken));
        // Not a single resume request went to the second source: the part of the first one was thrown away.
        Assert.All(h.Server.Requests.Where(r => r.Path == "/two/google_chat.gguf"), r => Assert.True(r.Range is null or "bytes=0-0"));
    }

    [Fact]
    public async Task ResumingFromTheSameSource_StillContinues()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(400_000, 1);
        var one = h.Server.Add("/one/chat.gguf", content);
        one.ChunkSize = 16 * 1024;
        one.ChunkDelay = TimeSpan.FromMilliseconds(15);
        using (var first = h.Manager([Chat(h, Two(h))], progressInterval: TimeSpan.Zero))
        {
            using var cts = new CancellationTokenSource();
            first.StatusChanged += status =>
            {
                if (status.BytesDone > 100_000)
                {
                    cts.Cancel();
                }
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.InstallAsync("chat", cts.Token));
        }

        var kept = new FileInfo(Path.Combine(h.Models, ".staging", "chat", "dl", "chat.gguf.part")).Length;
        one.ChunkDelay = TimeSpan.Zero;
        using var manager = h.Manager([Chat(h, Two(h))]);
        await manager.InstallAsync("chat", TestContext.Current.CancellationToken);

        Assert.Equal(content, await File.ReadAllBytesAsync(Gguf(h, "chat.gguf"), TestContext.Current.CancellationToken));
        Assert.Contains(h.Server.Requests, r => r.Range == $"bytes={kept}-");
    }

    [Fact]
    public async Task ReactivatingAnInstalledModel_KeepsTheRecordedMirror()
    {
        await using var h = new Harness();
        h.Server.Add("/one/chat.gguf", Harness.Bytes(10)).NotFound = true;
        h.Server.Add("/two/google_chat.gguf", Harness.Bytes(3000));
        using var manager = h.Manager([Chat(h, Two(h))]);
        await manager.InstallAsync("chat", TestContext.Current.CancellationToken);

        await manager.InstallAsync("chat", TestContext.Current.CancellationToken); // no-op re-activation

        var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Gguf(h, "models.json"), TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal("org/two", json.GetProperty("repo").GetProperty("chat").GetString());
        Assert.Equal("google_chat.gguf", json.GetProperty("file").GetProperty("chat").GetString());
    }

    [Fact]
    public async Task ProbeAsync_ReportsTheSkippedSources()
    {
        await using var h = new Harness();
        h.Server.Add("/one/chat.gguf", Harness.Bytes(10)).NotFound = true;
        h.Server.Add("/two/google_chat.gguf", Harness.Bytes(4321));
        using var manager = h.Manager([Chat(h, Two(h))]);

        var probe = Assert.Single(await manager.ProbeAsync("chat", TestContext.Current.CancellationToken));

        Assert.Equal(h.Server.Url("/two/google_chat.gguf"), probe.Url);
        Assert.Equal(4321, probe.ContentLength);
        Assert.Equal(2, probe.Tried.Count);
        Assert.Contains("404", probe.Tried[0].Error);
        Assert.Null(probe.Tried[1].Error);
    }

    [Fact]
    public async Task TheManifest_HasExactlyTheExpectedKeys()
    {
        await using var h = new Harness();
        h.Server.Add("/one/chat.gguf", Harness.Bytes(1000));
        h.Server.Add("/e", Harness.Bytes(1000));
        var embed = Harness.FileItem("embed", h.Server, "/e", "gguf/embed.gguf", ModelKind.Embedding, repo: "org/e");
        using var manager = h.Manager([Chat(h), embed]);
        await manager.InstallAsync("chat", TestContext.Current.CancellationToken);
        await manager.InstallAsync("embed", TestContext.Current.CancellationToken);

        var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Gguf(h, "models.json"), TestContext.Current.CancellationToken)).RootElement;

        Assert.Equal(["chat", "embed", "file", "repo"], json.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(["chat", "embed"], json.GetProperty("repo").EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(["chat", "embed"], json.GetProperty("file").EnumerateObject().Select(p => p.Name).Order());
        Assert.DoesNotContain("IsEmpty", await File.ReadAllTextAsync(Gguf(h, "models.json"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AManifestWithOnlyAChat_HasNoEmbedKeysAtAll()
    {
        await using var h = new Harness();
        h.Server.Add("/one/chat.gguf", Harness.Bytes(1000));
        using var manager = h.Manager([Chat(h)]);
        await manager.InstallAsync("chat", TestContext.Current.CancellationToken);

        var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Gguf(h, "models.json"), TestContext.Current.CancellationToken)).RootElement;

        Assert.Equal(["chat", "file", "repo"], json.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(["chat"], json.GetProperty("repo").EnumerateObject().Select(p => p.Name));
    }
}
