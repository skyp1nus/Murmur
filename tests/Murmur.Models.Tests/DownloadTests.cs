using Murmur.Core.Models;
using Murmur.Models.Tests.Support;

namespace Murmur.Models.Tests;

public sealed class DownloadTests
{
    private const string Path1 = "/files/model.gguf";

    private static string Target(Harness h, string relative) => Path.Combine(h.Models, relative);

    private static string Staging(Harness h, string id) => Path.Combine(h.Models, ".staging", id);

    [Fact]
    public async Task Install_SingleFile_WritesTheFileAndLeavesNoStagingBehind()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(300_000);
        h.Server.Add(Path1, content);
        using var manager = h.Manager([Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf")]);

        await manager.InstallAsync("m", TestContext.Current.CancellationToken);

        Assert.Equal(content, await File.ReadAllBytesAsync(Target(h, "gguf/model.gguf"), TestContext.Current.CancellationToken));
        var status = manager.GetStatus("m");
        Assert.Equal(ModelInstallState.Installed, status.State);
        Assert.Equal(content.Length, status.BytesDone);
        Assert.Equal(content.Length, status.BytesTotal);
        Assert.Null(status.Error);
        Assert.False(Directory.Exists(Path.Combine(h.Models, ".staging")));
        Assert.Empty(Directory.EnumerateFiles(h.Models, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Install_WhenAlreadyInstalled_DoesNotTouchTheNetwork()
    {
        await using var h = new Harness();
        h.Server.Add(Path1, Harness.Bytes(10_000));
        using var manager = h.Manager([Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf")]);
        await manager.InstallAsync("m", TestContext.Current.CancellationToken);
        var before = h.Server.Requests.Count;

        await manager.InstallAsync("m", TestContext.Current.CancellationToken);

        Assert.Equal(before, h.Server.Requests.Count);
    }

    [Fact]
    public async Task Install_ResumesAPartialDownloadWithARangeRequest()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(400_000);
        var route = h.Server.Add(Path1, content);
        using var manager = h.Manager([Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf")]);
        var part = Path.Combine(Staging(h, "m"), "dl", "model.gguf.part");
        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
        await File.WriteAllBytesAsync(part, content.AsMemory(0, 150_000).ToArray(), TestContext.Current.CancellationToken);

        var before = manager.GetStatus("m");
        await manager.InstallAsync("m", TestContext.Current.CancellationToken);

        Assert.Equal(ModelInstallState.NotInstalled, before.State);
        Assert.Equal(150_000, before.BytesDone);
        Assert.Equal(content, await File.ReadAllBytesAsync(Target(h, "gguf/model.gguf"), TestContext.Current.CancellationToken));
        Assert.Contains(h.Server.Requests, r => r.Range == "bytes=150000-");
        await Harness.ServedSettledAsync(route, content.Length - 150_000);
        Assert.Equal(content.Length - 150_000, route.BytesServed);
    }

    [Fact]
    public async Task Install_RestartsWhenTheServerIgnoresTheRange()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(250_000);
        var route = h.Server.Add(Path1, content);
        route.SupportsRange = false;
        using var manager = h.Manager([Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf")]);
        var part = Path.Combine(Staging(h, "m"), "dl", "model.gguf.part");
        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
        await File.WriteAllBytesAsync(part, content.AsMemory(0, 100_000).ToArray(), TestContext.Current.CancellationToken);

        await manager.InstallAsync("m", TestContext.Current.CancellationToken);

        Assert.Equal(content, await File.ReadAllBytesAsync(Target(h, "gguf/model.gguf"), TestContext.Current.CancellationToken));
        await Harness.ServedSettledAsync(route, content.Length);
        Assert.Equal(content.Length, route.BytesServed); // the whole body, from byte 0
    }

    [Fact]
    public async Task Install_DiscardsAPartLongerThanTheFile()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(50_000);
        h.Server.Add(Path1, content);
        using var manager = h.Manager([Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf")]);
        var part = Path.Combine(Staging(h, "m"), "dl", "model.gguf.part");
        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
        await File.WriteAllBytesAsync(part, Harness.Bytes(90_000, seed: 9), TestContext.Current.CancellationToken);

        await manager.InstallAsync("m", TestContext.Current.CancellationToken);

        Assert.Equal(content, await File.ReadAllBytesAsync(Target(h, "gguf/model.gguf"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Install_WithACompletePartFileOnlyVerifiesAndMoves()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(50_000);
        var route = h.Server.Add(Path1, content);
        using var manager = h.Manager([Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf", sha256: Harness.Sha256(content))]);
        var part = Path.Combine(Staging(h, "m"), "dl", "model.gguf.part");
        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
        await File.WriteAllBytesAsync(part, content, TestContext.Current.CancellationToken); // a crash happened right before the rename

        await manager.InstallAsync("m", TestContext.Current.CancellationToken);

        Assert.Equal(content, await File.ReadAllBytesAsync(Target(h, "gguf/model.gguf"), TestContext.Current.CancellationToken));
        Assert.Equal(0, route.BytesServed);
    }

    [Fact]
    public async Task Install_RetriesTransientServerErrors()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(60_000);
        var route = h.Server.Add(Path1, content);
        route.StatusFor = hit => hit is >= 2 and <= 4 ? 500 : null; // hit 1 is the size probe; three failed downloads = all three retries used
        using var manager = h.Manager([Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf")]);

        await manager.InstallAsync("m", TestContext.Current.CancellationToken);

        Assert.Equal(content, await File.ReadAllBytesAsync(Target(h, "gguf/model.gguf"), TestContext.Current.CancellationToken));
        Assert.Equal(ModelInstallState.Installed, manager.GetStatus("m").State);
        Assert.Equal(4, h.Server.Requests.Count(r => r.Range != "bytes=0-0")); // three failures and the success
    }

    [Fact]
    public async Task Install_GivesUpAfterThreeRetriesAndReportsFailed()
    {
        await using var h = new Harness();
        var route = h.Server.Add(Path1, Harness.Bytes(1000));
        route.StatusFor = hit => hit >= 2 ? 503 : null; // the size probe passes, every download fails
        using var manager = h.Manager([Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf")]);

        var ex = await Assert.ThrowsAsync<ModelInstallException>(() => manager.InstallAsync("m", TestContext.Current.CancellationToken));

        Assert.Equal(4, h.Server.Requests.Count(r => r.Range != "bytes=0-0")); // the first try plus three retries
        Assert.Equal(5, route.Hits);
        Assert.Contains("503", ex.Message);
        var status = manager.GetStatus("m");
        Assert.Equal(ModelInstallState.Failed, status.State);
        Assert.Contains("503", status.Error);
        Assert.False(File.Exists(Target(h, "gguf/model.gguf")));
    }

    [Fact]
    public async Task Install_ClientErrorsAreNotRetried()
    {
        await using var h = new Harness();
        var route = h.Server.Add(Path1, Harness.Bytes(1000));
        route.NotFound = true;
        using var manager = h.Manager([Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf")]);

        var ex = await Assert.ThrowsAsync<ModelInstallException>(() => manager.InstallAsync("m", TestContext.Current.CancellationToken));

        Assert.Contains("404", ex.Message);
        Assert.Single(h.Server.Requests);
        Assert.Equal(ModelInstallState.Failed, manager.GetStatus("m").State);
    }

    [Fact]
    public async Task Install_ResumesAfterAMidStreamDisconnect()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(500_000);
        var route = h.Server.Add(Path1, content);
        route.DisconnectAfter = hit => hit == 2 ? 160_000 : null; // hit 1 is the size probe
        using var manager = h.Manager([Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf", sha256: Harness.Sha256(content))]);

        await manager.InstallAsync("m", TestContext.Current.CancellationToken);

        Assert.Equal(content, await File.ReadAllBytesAsync(Target(h, "gguf/model.gguf"), TestContext.Current.CancellationToken));
        Assert.Contains(h.Server.Requests, r => r.Range is { } range && range.StartsWith("bytes=", StringComparison.Ordinal) && range != "bytes=0-0" && !range.StartsWith("bytes=0-", StringComparison.Ordinal));
        Assert.True(route.BytesServed < content.Length * 2L);
    }

    [Fact]
    public async Task Install_KeepsRetryingWhileEveryAttemptMakesProgress()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(400_000);
        var route = h.Server.Add(Path1, content);
        route.DisconnectAfter = hit => hit is >= 2 and <= 6 ? 64_000 : null; // five drops, more than the three retries
        using var manager = h.Manager([Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf")]);

        await manager.InstallAsync("m", TestContext.Current.CancellationToken);

        Assert.Equal(content, await File.ReadAllBytesAsync(Target(h, "gguf/model.gguf"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Install_FailsWhenTheServerStallsWithoutData()
    {
        await using var h = new Harness();
        var route = h.Server.Add(Path1, Harness.Bytes(1000));
        route.Hang = hit => hit >= 2; // the probe answers, the download never does
        using var manager = h.Manager([Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf")], retries: 1, stall: TimeSpan.FromMilliseconds(200));

        var ex = await Assert.ThrowsAsync<ModelInstallException>(() => manager.InstallAsync("m", TestContext.Current.CancellationToken));

        Assert.Contains("no data", ex.Message);
        Assert.Equal(ModelInstallState.Failed, manager.GetStatus("m").State);
    }

    [Fact]
    public async Task Install_RejectsAFileWhoseSizeDiffersFromTheCatalog()
    {
        await using var h = new Harness();
        h.Server.Add(Path1, Harness.Bytes(1000));
        using var manager = h.Manager([Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf", sizeBytes: 999)]);

        var ex = await Assert.ThrowsAsync<ModelInstallException>(() => manager.InstallAsync("m", TestContext.Current.CancellationToken));

        Assert.Contains("expected 999", ex.Message);
        Assert.False(File.Exists(Target(h, "gguf/model.gguf")));
    }

    [Fact]
    public async Task Install_VerifiesAPinnedSha256()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(80_000);
        h.Server.Add(Path1, content);
        using var good = h.Manager([Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf", sha256: Harness.Sha256(content).ToUpperInvariant())]);

        await good.InstallAsync("m", TestContext.Current.CancellationToken);

        Assert.Equal(ModelInstallState.Installed, good.GetStatus("m").State);
    }

    [Fact]
    public async Task Install_WithTheWrongSha256_FailsAndDiscardsTheDownload()
    {
        await using var h = new Harness();
        h.Server.Add(Path1, Harness.Bytes(80_000));
        using var manager = h.Manager([Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf", sha256: new string('0', 64))]);

        var ex = await Assert.ThrowsAsync<ModelInstallException>(() => manager.InstallAsync("m", TestContext.Current.CancellationToken));

        Assert.Contains("SHA-256 mismatch", ex.Message);
        Assert.False(File.Exists(Target(h, "gguf/model.gguf")));
        var leftovers = Directory.EnumerateFiles(h.Models, "*", SearchOption.AllDirectories).ToList();
        Assert.Empty(leftovers);
        var status = manager.GetStatus("m");
        Assert.Equal(ModelInstallState.Failed, status.State);
        Assert.Equal(0, status.BytesDone);

        // A new attempt starts from scratch (and fails the same way, since the catalog hash is wrong).
        await Assert.ThrowsAsync<ModelInstallException>(() => manager.InstallAsync("m", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Install_ReportsHashesOnRequest()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(20_000);
        h.Server.Add(Path1, content);
        using var manager = h.Manager([Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf")], reportHashes: true);

        Assert.Empty(manager.GetLastInstallReport("m"));
        await manager.InstallAsync("m", TestContext.Current.CancellationToken);

        var report = Assert.Single(manager.GetLastInstallReport("m"));
        Assert.Equal(Harness.Sha256(content), report.Sha256);
        Assert.Equal(20_000, report.Bytes);
        Assert.Equal(h.Server.Url(Path1), report.Url);
    }

    [Fact]
    public async Task Install_WithoutHashReporting_DoesNotHash()
    {
        await using var h = new Harness();
        h.Server.Add(Path1, Harness.Bytes(20_000));
        using var manager = h.Manager([Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf")]);

        await manager.InstallAsync("m", TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(manager.GetLastInstallReport("m")).Sha256);
    }

    [Fact]
    public async Task Install_FailsFastWhenThereIsNotEnoughDiskSpace()
    {
        await using var h = new Harness();
        var route = h.Server.Add(Path1, Harness.Bytes(100_000));
        using var manager = h.Manager([Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf")], freeSpace: _ => 50_000);

        var ex = await Assert.ThrowsAsync<ModelInstallException>(() => manager.InstallAsync("m", TestContext.Current.CancellationToken));

        Assert.Contains("disk space", ex.Message);
        Assert.Equal(50_000, manager.AvailableFreeSpaceBytes);
        Assert.All(h.Server.Requests, r => Assert.Equal("bytes=0-0", r.Range)); // only the size probe went out
        Assert.Equal(0, route.BytesServed);
        Assert.Equal(ModelInstallState.Failed, manager.GetStatus("m").State);
    }

    [Fact]
    public async Task Install_CountsOnlyTheRemainingBytesAgainstFreeSpace()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(100_000);
        h.Server.Add(Path1, content);
        using var manager = h.Manager([Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf")], freeSpace: _ => 30_000);
        var part = Path.Combine(Staging(h, "m"), "dl", "model.gguf.part");
        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
        await File.WriteAllBytesAsync(part, content.AsMemory(0, 80_000).ToArray(), TestContext.Current.CancellationToken);

        await manager.InstallAsync("m", TestContext.Current.CancellationToken); // 20 000 left, 30 000 free

        Assert.Equal(ModelInstallState.Installed, manager.GetStatus("m").State);
    }

    [Fact]
    public async Task Cancel_KeepsThePartFileAndTheNextInstallResumes()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(600_000);
        var route = h.Server.Add(Path1, content);
        route.ChunkSize = 16 * 1024;
        route.ChunkDelay = TimeSpan.FromMilliseconds(15);
        using var manager = h.Manager([Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf")], progressInterval: TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        manager.StatusChanged += status =>
        {
            if (status.BytesDone > 100_000)
            {
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.InstallAsync("m", cts.Token));

        var status = manager.GetStatus("m");
        Assert.Equal(ModelInstallState.NotInstalled, status.State);
        Assert.True(status.BytesDone > 100_000);
        Assert.Null(status.Error);
        Assert.False(File.Exists(Target(h, "gguf/model.gguf")));
        var part = Path.Combine(Staging(h, "m"), "dl", "model.gguf.part");
        Assert.True(File.Exists(part));
        var kept = new FileInfo(part).Length;
        Assert.InRange(kept, 100_001, content.Length - 1);

        route.ChunkDelay = TimeSpan.Zero;
        using var manager2 = h.Manager([Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf")]);
        await manager2.InstallAsync("m", TestContext.Current.CancellationToken);

        Assert.Equal(content, await File.ReadAllBytesAsync(Target(h, "gguf/model.gguf"), TestContext.Current.CancellationToken));
        Assert.Contains(h.Server.Requests, r => r.Range == $"bytes={kept}-");
    }

    [Fact]
    public async Task Cancel_BeforeStartingLeavesNothing()
    {
        await using var h = new Harness();
        h.Server.Add(Path1, Harness.Bytes(1000));
        using var manager = h.Manager([Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf")]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.InstallAsync("m", cts.Token));

        Assert.Equal(ModelInstallState.NotInstalled, manager.GetStatus("m").State);
        Assert.Empty(h.Server.Requests);
    }

    [Fact]
    public async Task MultiFileItem_IsNotInstalledUntilEveryFileIsComplete()
    {
        await using var h = new Harness();
        var a = Harness.Bytes(30_000, 1);
        var b = Harness.Bytes(40_000, 2);
        var c = Harness.Bytes(50_000, 3);
        var ra = h.Server.Add("/m/a.bin", a);
        var rb = h.Server.Add("/m/b.bin", b);
        var rc = h.Server.Add("/m/c.bin", c);
        rc.StatusFor = hit => hit >= 2 ? 404 : null; // the size probe passes, the download does not
        var item = Harness.DirectoryItem("multi", h.Server, "multi-dir", "/m/a.bin", "/m/b.bin", "/m/c.bin");
        using var manager = h.Manager([item]);

        await Assert.ThrowsAsync<ModelInstallException>(() => manager.InstallAsync("multi", TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(Target(h, "multi-dir"))); // nothing half-installed
        var failed = manager.GetStatus("multi");
        Assert.Equal(ModelInstallState.Failed, failed.State);
        Assert.Equal(a.Length + b.Length, failed.BytesDone); // the finished files stay staged
        var hitsA = ra.Hits;
        var hitsB = rb.Hits;

        rc.StatusFor = null;
        await manager.InstallAsync("multi", TestContext.Current.CancellationToken);

        Assert.Equal(hitsA, ra.Hits); // complete files are not fetched again
        Assert.Equal(hitsB, rb.Hits);
        Assert.Equal(a, await File.ReadAllBytesAsync(Target(h, "multi-dir/a.bin"), TestContext.Current.CancellationToken));
        Assert.Equal(b, await File.ReadAllBytesAsync(Target(h, "multi-dir/b.bin"), TestContext.Current.CancellationToken));
        Assert.Equal(c, await File.ReadAllBytesAsync(Target(h, "multi-dir/c.bin"), TestContext.Current.CancellationToken));
        Assert.Equal(ModelInstallState.Installed, manager.GetStatus("multi").State);
        Assert.False(Directory.Exists(Path.Combine(h.Models, ".staging")));
    }

    [Fact]
    public async Task MultiFileItem_CancelledMidwayLeavesNothingInstalled()
    {
        await using var h = new Harness();
        h.Server.Add("/m/a.bin", Harness.Bytes(30_000, 1));
        var slow = h.Server.Add("/m/b.bin", Harness.Bytes(500_000, 2));
        slow.ChunkSize = 16 * 1024;
        slow.ChunkDelay = TimeSpan.FromMilliseconds(20);
        var item = Harness.DirectoryItem("multi", h.Server, "multi-dir", "/m/a.bin", "/m/b.bin");
        using var manager = h.Manager([item], progressInterval: TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        manager.StatusChanged += status =>
        {
            if (status.BytesDone > 60_000)
            {
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.InstallAsync("multi", cts.Token));

        Assert.False(Directory.Exists(Target(h, "multi-dir")));
        Assert.Equal(ModelInstallState.NotInstalled, manager.GetStatus("multi").State);
        Assert.True(File.Exists(Path.Combine(Staging(h, "multi"), "dl", "a.bin")));
    }

    [Fact]
    public async Task Probe_ReportsSizesWithRangeSupportAndWithout()
    {
        await using var h = new Harness();
        h.Server.Add("/a", Harness.Bytes(1234));
        h.Server.Add("/b", Harness.Bytes(4321)).SupportsRange = false;
        h.Server.Add("/c", Harness.Bytes(10)).NotFound = true;
        var item = Harness.DirectoryItem("multi", h.Server, "multi-dir", "/a", "/b");
        using var manager = h.Manager([item, Harness.FileItem("missing", h.Server, "/c", "gguf/c.gguf")]);

        var probes = await manager.ProbeAsync("multi", TestContext.Current.CancellationToken);

        Assert.Equal([1234L, 4321L], probes.Select(p => p.ContentLength));
        Assert.Equal(h.Server.Url("/a"), probes[0].Url);
        await Assert.ThrowsAsync<ModelInstallException>(() => manager.ProbeAsync("missing", TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(h.Models, ".staging"))); // probing downloads nothing
    }
}
