using System.Formats.Tar;
using Murmur.Core.Models;
using Murmur.Models.Tests.Support;

namespace Murmur.Models.Tests;

public sealed class ArchiveTests
{
    private static string Llama(Harness h, string relative = "") => Path.Combine(h.Models, "llama.cpp", relative);

    private static Task InstallAsync(ModelManager manager) => manager.InstallAsync("llama", TestContext.Current.CancellationToken);

    [Fact]
    public async Task Zip_WithAFlatLayout_InstallsEverything()
    {
        await using var h = new Harness();
        var server = Harness.Bytes(3000, 1);
        h.Server.Add("/llama.zip", Harness.Zip(("llama-server.exe", server), ("ggml.dll", Harness.Bytes(100, 2)), ("sub/extra.dll", Harness.Bytes(100, 3))));
        using var manager = h.Manager([Harness.ArchiveItem("llama", h.Server, "/llama.zip", ModelPackaging.Zip, "llama-server.exe")]);

        await InstallAsync(manager);

        Assert.Equal(server, await File.ReadAllBytesAsync(Llama(h, "llama-server.exe"), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Llama(h, "ggml.dll")));
        Assert.True(File.Exists(Llama(h, Path.Combine("sub", "extra.dll"))));
        Assert.Equal(ModelInstallState.Installed, manager.GetStatus("llama").State);
        Assert.False(Directory.Exists(Path.Combine(h.Models, ".staging")));
    }

    [Fact]
    public async Task Zip_WithANestedLayout_InstallsOnlyTheServersFolder()
    {
        await using var h = new Harness();
        h.Server.Add(
            "/llama.zip",
            Harness.Zip(
                ("README.md", Harness.Text("readme")),
                ("llama-b1/build/bin/llama-server.exe", Harness.Bytes(3000, 1)),
                ("llama-b1/build/bin/ggml.dll", Harness.Bytes(100, 2)),
                ("llama-b1/docs/guide.txt", Harness.Text("docs"))));
        using var manager = h.Manager([Harness.ArchiveItem("llama", h.Server, "/llama.zip", ModelPackaging.Zip, "llama-server.exe")]);

        await InstallAsync(manager);

        Assert.True(File.Exists(Llama(h, "llama-server.exe")));
        Assert.True(File.Exists(Llama(h, "ggml.dll")));
        Assert.False(File.Exists(Llama(h, "README.md")));
        Assert.False(Directory.Exists(Llama(h, "docs")));
    }

    [Fact]
    public async Task Zip_PicksTheShallowestServerWhenThereAreSeveral()
    {
        await using var h = new Harness();
        h.Server.Add(
            "/llama.zip",
            Harness.Zip(
                ("deep/er/llama-server.exe", Harness.Bytes(10, 1)),
                ("top/llama-server.exe", Harness.Bytes(20, 2)),
                ("top/lib.dll", Harness.Bytes(5, 3))));
        using var manager = h.Manager([Harness.ArchiveItem("llama", h.Server, "/llama.zip", ModelPackaging.Zip, "llama-server.exe")]);

        await InstallAsync(manager);

        Assert.Equal(20, new FileInfo(Llama(h, "llama-server.exe")).Length);
        Assert.True(File.Exists(Llama(h, "lib.dll")));
    }

    [Fact]
    public async Task TarGz_InstallsTheServerFolderAndMarksTheServerExecutable()
    {
        await using var h = new Harness();
        var server = Harness.Bytes(3000, 1);
        h.Server.Add(
            "/llama.tar.gz",
            Harness.TarGz(("llama-b1/llama-server", server), ("llama-b1/libggml.so", Harness.Bytes(100, 2)), ("other/notes.txt", Harness.Text("x"))));
        using var manager = h.Manager([Harness.ArchiveItem("llama", h.Server, "/llama.tar.gz", ModelPackaging.TarGz, "llama-server")]);

        await InstallAsync(manager);

        Assert.Equal(server, await File.ReadAllBytesAsync(Llama(h, "llama-server"), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Llama(h, "libggml.so")));
        Assert.False(Directory.Exists(Llama(h, "other")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(Llama(h, "llama-server")).HasFlag(UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute));
        }
    }

    [Fact]
    public async Task Archive_WithoutTheServer_FailsAndIsDownloadedAgainNextTime()
    {
        await using var h = new Harness();
        var route = h.Server.Add("/llama.zip", Harness.Zip(("readme.txt", Harness.Text("no server here"))));
        using var manager = h.Manager([Harness.ArchiveItem("llama", h.Server, "/llama.zip", ModelPackaging.Zip, "llama-server.exe")]);

        var ex = await Assert.ThrowsAsync<ModelInstallException>(() => InstallAsync(manager));

        Assert.Contains("llama-server.exe", ex.Message);
        Assert.False(Directory.Exists(Llama(h)));
        Assert.Equal(ModelInstallState.Failed, manager.GetStatus("llama").State);
        Assert.Equal(0, manager.GetStatus("llama").BytesDone); // the suspect archive was deleted
        Assert.True(route.Hits >= 1);
    }

    [Fact]
    public async Task Archive_ThatIsNotAnArchive_Fails()
    {
        await using var h = new Harness();
        h.Server.Add("/llama.zip", Harness.Bytes(5000));
        using var manager = h.Manager([Harness.ArchiveItem("llama", h.Server, "/llama.zip", ModelPackaging.Zip, "llama-server.exe")]);

        await Assert.ThrowsAsync<ModelInstallException>(() => InstallAsync(manager));

        Assert.False(Directory.Exists(Llama(h)));
    }

    [Fact]
    public async Task Zip_WithAnEntryEscapingTheFolder_FailsAndWritesNothingOutside()
    {
        await using var h = new Harness();
        h.Server.Add("/llama.zip", Harness.Zip(("../../evil.txt", Harness.Text("pwned")), ("llama-server.exe", Harness.Bytes(10, 1))));
        using var manager = h.Manager([Harness.ArchiveItem("llama", h.Server, "/llama.zip", ModelPackaging.Zip, "llama-server.exe")]);

        await Assert.ThrowsAsync<ModelInstallException>(() => InstallAsync(manager));

        Assert.False(File.Exists(Path.Combine(h.Models, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(h.Models, ".staging", "evil.txt")));
        Assert.False(File.Exists(Path.Combine(h.Root, "evil.txt")));
        Assert.False(Directory.Exists(Llama(h)));
    }

    [Fact]
    public async Task Reinstall_AfterRemoval_ReplacesTheFolder()
    {
        await using var h = new Harness();
        h.Server.Add("/llama.zip", Harness.Zip(("llama-server.exe", Harness.Bytes(100, 1)), ("a.dll", Harness.Bytes(10, 2))));
        using var manager = h.Manager([Harness.ArchiveItem("llama", h.Server, "/llama.zip", ModelPackaging.Zip, "llama-server.exe")]);
        await InstallAsync(manager);
        File.WriteAllText(Llama(h, "stale.txt"), "left over from the previous version");
        await manager.RemoveAsync("llama", TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(Llama(h)));

        await InstallAsync(manager);

        Assert.False(File.Exists(Llama(h, "stale.txt")));
        Assert.True(File.Exists(Llama(h, "a.dll")));
    }

    /// <summary>Hostile tarballs: each must fail, write nothing outside the staging folder and leave no llama.cpp folder.</summary>
    [Theory]
    [InlineData("escaping-file")]
    [InlineData("symlink-out")]
    [InlineData("hardlink-out")]
    public async Task TarGz_WithEntriesThatEscapeTheFolder_FailsAndWritesNothingOutside(string attack)
    {
        await using var h = new Harness();
        var outside = Path.Combine(h.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(h.Root, "x"), "precious");
        var server = new TarSpec("llama-server", Content: Harness.Bytes(10, 1));
        var entries = attack switch
        {
            "escaping-file" => new[] { new TarSpec("../../evil.txt", Content: Harness.Text("pwned")), server },
            // A link pointing at a directory outside, then a file written "through" it.
            "symlink-out" => [new TarSpec("link", TarEntryType.SymbolicLink, outside), new TarSpec("link/pwned", Content: Harness.Text("pwned")), server],
            _ => [new TarSpec("hard", TarEntryType.HardLink, "../x"), server],
        };
        h.Server.Add("/llama.tar.gz", Harness.TarGz(entries));
        using var manager = h.Manager([Harness.ArchiveItem("llama", h.Server, "/llama.tar.gz", ModelPackaging.TarGz, "llama-server")]);

        await Assert.ThrowsAsync<ModelInstallException>(() => InstallAsync(manager));

        Assert.False(File.Exists(Path.Combine(h.Root, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(h.Models, "evil.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside)); // nothing written through the link
        Assert.Equal("precious", await File.ReadAllTextAsync(Path.Combine(h.Root, "x"), TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Llama(h)));
        Assert.Equal(ModelInstallState.Failed, manager.GetStatus("llama").State);
    }
}
