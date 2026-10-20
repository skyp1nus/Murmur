using System.Text.Json;
using Murmur.Core.Models;
using Murmur.Models.Tests.Support;

namespace Murmur.Models.Tests;

/// <summary>Which source served a file stays known: across a crash in the download folder and across switching the active model.</summary>
public sealed class ProvenanceTests
{
    private static string Gguf(Harness h, string name) => Path.Combine(h.Models, "gguf", name);

    private static string Dl(Harness h, string id) => Path.Combine(h.Models, ".staging", id, "dl");

    private static async Task<JsonElement> ManifestAsync(Harness h) =>
        JsonDocument.Parse(await File.ReadAllTextAsync(Gguf(h, "models.json"), TestContext.Current.CancellationToken)).RootElement;

    private static ModelCatalogItem Chat(Harness h, ModelMirror first, params ModelMirror[] fallbacks) =>
        new(
            new ModelInfo("chat", ModelKind.Chat, "chat", 1000, Required: false),
            "gguf/chat.gguf",
            IsDirectory: false,
            [new ModelFileSpec(first.Url, "chat.gguf", first.Sha256, first.SizeBytes, first.Repo, first.FileName, fallbacks)],
            Repo: first.Repo);

    private static ModelMirror One(Harness h, string? sha = null, long? size = null) => new(h.Server.Url("/one/chat.gguf"), "org/one", "chat.gguf", sha, size);

    private static ModelMirror Two(Harness h, string? sha = null, long? size = null) => new(h.Server.Url("/two/google_chat.gguf"), "org/two", "google_chat.gguf", sha, size);

    [Fact]
    public async Task AFinishedFileWithARecord_IsCheckedAgainstTheSourceTheRecordNames()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(2500, 3);
        var dl = Dl(h, "chat");
        Directory.CreateDirectory(dl);
        await File.WriteAllBytesAsync(Path.Combine(dl, "chat.gguf"), content, TestContext.Current.CancellationToken); // renamed before the crash
        await File.WriteAllTextAsync(Path.Combine(dl, "chat.gguf.source"), Two(h).Url, TestContext.Current.CancellationToken);
        var item = Chat(h, One(h, sha: new string('a', 64)), Two(h, sha: Harness.Sha256(content))); // the first pin would reject the file
        using var manager = h.Manager([item]);

        await manager.InstallAsync("chat", TestContext.Current.CancellationToken);

        Assert.Empty(h.Server.Requests); // nothing downloaded or probed
        Assert.Equal(content, await File.ReadAllBytesAsync(Gguf(h, "chat.gguf"), TestContext.Current.CancellationToken));
        var json = await ManifestAsync(h);
        Assert.Equal("org/two", json.GetProperty("repo").GetProperty("chat").GetString());
        Assert.Equal("google_chat.gguf", json.GetProperty("file").GetProperty("chat").GetString());
    }

    [Fact]
    public async Task AFinishedFileWithoutARecord_IsMatchedBySourceWithTheSameSize()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(2500, 4);
        var dl = Dl(h, "chat");
        Directory.CreateDirectory(dl);
        await File.WriteAllBytesAsync(Path.Combine(dl, "chat.gguf"), content, TestContext.Current.CancellationToken);
        using var manager = h.Manager([Chat(h, One(h, sha: new string('a', 64), size: 99_999), Two(h, sha: Harness.Sha256(content), size: content.Length))]);

        await manager.InstallAsync("chat", TestContext.Current.CancellationToken);

        Assert.Equal("org/two", (await ManifestAsync(h)).GetProperty("repo").GetProperty("chat").GetString());
    }

    [Fact]
    public async Task AFinishedFileWithoutARecord_PicksTheFirstUnpinnedSourceWhenNoSizeMatches()
    {
        await using var h = new Harness();
        var dl = Dl(h, "chat");
        Directory.CreateDirectory(dl);
        await File.WriteAllBytesAsync(Path.Combine(dl, "chat.gguf"), Harness.Bytes(2500, 5), TestContext.Current.CancellationToken);
        using var manager = h.Manager([Chat(h, One(h, size: 99_999), Two(h))]);

        await manager.InstallAsync("chat", TestContext.Current.CancellationToken);

        Assert.Equal("org/two", (await ManifestAsync(h)).GetProperty("repo").GetProperty("chat").GetString());
    }

    [Fact]
    public async Task AFinishedFileWithoutARecord_FallsBackToTheFirstSource()
    {
        await using var h = new Harness();
        var dl = Dl(h, "chat");
        Directory.CreateDirectory(dl);
        await File.WriteAllBytesAsync(Path.Combine(dl, "chat.gguf"), Harness.Bytes(2500, 6), TestContext.Current.CancellationToken);
        using var manager = h.Manager([Chat(h, One(h, size: 99_999), Two(h, size: 88_888))]);

        // Both sources are pinned to a size the file does not have: the first source rejects it, as before.
        await Assert.ThrowsAsync<ModelInstallException>(() => manager.InstallAsync("chat", TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(dl, "chat.gguf")));
        Assert.False(File.Exists(Path.Combine(dl, "chat.gguf.source")));
    }

    [Fact]
    public async Task ADownload_LeavesNoRecordInTheInstalledFolder()
    {
        await using var h = new Harness();
        h.Server.Add("/a", Harness.Bytes(100, 1));
        h.Server.Add("/b", Harness.Bytes(100, 2));
        using var manager = h.Manager([Harness.DirectoryItem("multi", h.Server, "multi-dir", "/a", "/b")]);

        await manager.InstallAsync("multi", TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b"], Directory.EnumerateFiles(Path.Combine(h.Models, "multi-dir")).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task AStrayRecordFromACrash_DoesNotTravelIntoTheInstalledFolder()
    {
        await using var h = new Harness();
        var a = Harness.Bytes(100, 1);
        h.Server.Add("/a", a);
        h.Server.Add("/b", Harness.Bytes(100, 2));
        var dl = Dl(h, "multi");
        Directory.CreateDirectory(dl);
        await File.WriteAllBytesAsync(Path.Combine(dl, "a"), a, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dl, "a.source"), h.Server.Url("/a"), TestContext.Current.CancellationToken);
        using var manager = h.Manager([Harness.DirectoryItem("multi", h.Server, "multi-dir", "/a", "/b")]);

        await manager.InstallAsync("multi", TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b"], Directory.EnumerateFiles(Path.Combine(h.Models, "multi-dir")).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task AFailedPlacement_KeepsTheRecordsSoThatTheRetryKnowsTheSource_AndASuccessfulOneRemovesThem()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(2500, 7);
        h.Server.Add("/one/chat.gguf", Harness.Bytes(10)).NotFound = true;
        h.Server.Add("/two/google_chat.gguf", content);
        var blocker = Gguf(h, "chat.gguf");
        Directory.CreateDirectory(blocker); // a directory where the file belongs: moving the download there fails
        await File.WriteAllTextAsync(Path.Combine(blocker, "keep"), "x", TestContext.Current.CancellationToken);
        using var manager = h.Manager([Chat(h, One(h), Two(h))]);

        await Assert.ThrowsAnyAsync<Exception>(() => manager.InstallAsync("chat", TestContext.Current.CancellationToken));

        var dl = Dl(h, "chat");
        Assert.Equal(Two(h).Url, await File.ReadAllTextAsync(Path.Combine(dl, "chat.gguf.source"), TestContext.Current.CancellationToken));

        Directory.Delete(blocker, recursive: true);
        await manager.InstallAsync("chat", TestContext.Current.CancellationToken);

        Assert.Equal(content, await File.ReadAllBytesAsync(blocker, TestContext.Current.CancellationToken));
        Assert.Equal("org/two", (await ManifestAsync(h)).GetProperty("repo").GetProperty("chat").GetString());
        Assert.False(Directory.Exists(Path.Combine(h.Models, ".staging")));
    }

    [Fact]
    public async Task SwitchingBackToAnInstalledModel_RecordsTheSourceThatServedIt()
    {
        await using var h = new Harness();
        h.Server.Add("/one/chat.gguf", Harness.Bytes(10)).NotFound = true;
        h.Server.Add("/two/google_chat.gguf", Harness.Bytes(3000));
        h.Server.Add("/o", Harness.Bytes(2000, 2));
        var other = Harness.FileItem("other", h.Server, "/o", "gguf/other.gguf", repo: "org/other");
        using var manager = h.Manager([Chat(h, One(h), Two(h)), other]);
        await manager.InstallAsync("chat", TestContext.Current.CancellationToken);
        await manager.InstallAsync("other", TestContext.Current.CancellationToken);
        Assert.Equal("org/other", (await ManifestAsync(h)).GetProperty("repo").GetProperty("chat").GetString());

        await manager.InstallAsync("chat", TestContext.Current.CancellationToken); // installed already: only switches back

        var json = await ManifestAsync(h);
        Assert.Equal(Gguf(h, "chat.gguf"), json.GetProperty("chat").GetString());
        Assert.Equal("org/two", json.GetProperty("repo").GetProperty("chat").GetString());
        Assert.Equal("google_chat.gguf", json.GetProperty("file").GetProperty("chat").GetString());
        Assert.Equal(["chat", "file", "repo"], json.EnumerateObject().Select(p => p.Name).Order()); // keys unchanged
    }

    [Fact]
    public async Task TheProvenanceRecord_SitsNextToTheModelAndGoesWhenItIsRemoved()
    {
        await using var h = new Harness();
        h.Server.Add("/one/chat.gguf", Harness.Bytes(3000));
        using var manager = h.Manager([Chat(h, One(h))]);
        await manager.InstallAsync("chat", TestContext.Current.CancellationToken);

        Assert.Equal("org/one\nchat.gguf\n", await File.ReadAllTextAsync(Gguf(h, "chat.gguf.source"), TestContext.Current.CancellationToken));

        await manager.RemoveAsync("chat", TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Gguf(h, "chat.gguf.source")));
        Assert.False(File.Exists(Gguf(h, "chat.gguf")));
    }

    [Fact]
    public async Task AnInstalledModelWithoutARecord_KeepsTheOldBehaviour()
    {
        await using var h = new Harness();
        Directory.CreateDirectory(Path.Combine(h.Models, "gguf"));
        await File.WriteAllBytesAsync(Gguf(h, "chat.gguf"), Harness.Bytes(500), TestContext.Current.CancellationToken); // installed by an older version
        using var manager = h.Manager([Chat(h, One(h), Two(h))]);

        await manager.InstallAsync("chat", TestContext.Current.CancellationToken);

        var json = await ManifestAsync(h);
        Assert.Equal("org/one", json.GetProperty("repo").GetProperty("chat").GetString());
        Assert.Equal("chat.gguf", json.GetProperty("file").GetProperty("chat").GetString());
        Assert.Empty(h.Server.Requests);
    }

    [Fact]
    public async Task ProbeAsync_WhenNoSourceReportsALength_ListsOnlyTheSourcesTriedUpToTheChosenOne()
    {
        await using var h = new Harness();
        var first = h.Server.Add("/one/chat.gguf", Harness.Bytes(3000));
        first.SupportsRange = false;
        first.Chunked = _ => true; // answers, but without a Content-Length
        h.Server.Add("/two/google_chat.gguf", Harness.Bytes(10)).NotFound = true;
        using var manager = h.Manager([Chat(h, One(h), Two(h))]);

        var probe = Assert.Single(await manager.ProbeAsync("chat", TestContext.Current.CancellationToken));

        Assert.Equal(h.Server.Url("/one/chat.gguf"), probe.Url);
        Assert.Null(probe.ContentLength);
        var tried = Assert.Single(probe.Tried);
        Assert.Equal(h.Server.Url("/one/chat.gguf"), tried.Url);
        Assert.Null(tried.Error);
    }
}
