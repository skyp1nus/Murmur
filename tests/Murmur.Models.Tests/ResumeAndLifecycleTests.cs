using Murmur.Core.Models;
using Murmur.Models.Tests.Support;

namespace Murmur.Models.Tests;

public sealed class ResumeAndLifecycleTests
{
    private const string Path1 = "/files/model.gguf";

    private static string Part(Harness h) => Path.Combine(h.Models, ".staging", "m", "dl", "model.gguf.part");

    private static ModelCatalogItem Item(Harness h, long displaySize = 1000) =>
        Harness.FileItem("m", h.Server, Path1, "gguf/model.gguf", displaySize: displaySize);

    /// <summary>Starts an install of a slow route and cancels it once more than 100 000 bytes are on disk.</summary>
    private static async Task CancelMidwayAsync(Harness h)
    {
        using var manager = h.Manager([Item(h)], progressInterval: TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        manager.StatusChanged += status =>
        {
            if (status.BytesDone > 100_000)
            {
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.InstallAsync("m", cts.Token));
        Assert.True(File.Exists(Part(h)));
    }

    private static Route SlowRoute(Harness h, byte[] content)
    {
        var route = h.Server.Add(Path1, content);
        route.ChunkSize = 16 * 1024;
        route.ChunkDelay = TimeSpan.FromMilliseconds(15);
        return route;
    }

    [Fact]
    public async Task Resume_SendsIfRange_AndRestartsWhenTheFileChangedOnTheServer()
    {
        await using var h = new Harness();
        var v1 = Harness.Bytes(400_000, 1);
        var v2 = Harness.Bytes(400_000, 2); // same length: only the ETag can tell
        var route = SlowRoute(h, v1);
        route.ETag = "\"v1\"";
        await CancelMidwayAsync(h);
        Assert.True(File.Exists(Part(h) + ".validator"));

        route.Content = v2;
        route.ETag = "\"v2\"";
        route.ChunkDelay = TimeSpan.Zero;
        using var manager = h.Manager([Item(h)]);
        await manager.InstallAsync("m", TestContext.Current.CancellationToken);

        Assert.Equal(v2, await File.ReadAllBytesAsync(Path.Combine(h.Models, "gguf", "model.gguf"), TestContext.Current.CancellationToken));
        Assert.Contains(h.Server.Requests, r => r.IfRange == "\"v1\"" && r.Range is { } range && range != "bytes=0-0");
    }

    [Fact]
    public async Task Resume_WithAMatchingETag_ContinuesWhereItStopped()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(400_000, 1);
        var route = SlowRoute(h, content);
        route.ETag = "\"v1\"";
        await CancelMidwayAsync(h);
        var kept = new FileInfo(Part(h)).Length;
        route.ChunkDelay = TimeSpan.Zero;

        using var manager = h.Manager([Item(h)]);
        await manager.InstallAsync("m", TestContext.Current.CancellationToken);

        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(h.Models, "gguf", "model.gguf"), TestContext.Current.CancellationToken));
        Assert.Contains(h.Server.Requests, r => r.Range == $"bytes={kept}-" && r.IfRange == "\"v1\"");
        Assert.False(File.Exists(Part(h) + ".validator"));
    }

    [Fact]
    public async Task Resume_WithoutAValidator_RestartsWhenTheAnnouncedSizeChanged()
    {
        await using var h = new Harness();
        var route = SlowRoute(h, Harness.Bytes(400_000, 1)); // no ETag, no Last-Modified
        await CancelMidwayAsync(h);
        var v2 = Harness.Bytes(450_000, 3);
        route.Content = v2;
        route.ChunkDelay = TimeSpan.Zero;

        using var manager = h.Manager([Item(h)]);
        await manager.InstallAsync("m", TestContext.Current.CancellationToken);

        Assert.Equal(v2, await File.ReadAllBytesAsync(Path.Combine(h.Models, "gguf", "model.gguf"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Download_WithoutContentLength_IsCheckedAgainstTheSizeProbe()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(1000);
        var route = h.Server.Add(Path1, content);
        route.Chunked = hit => hit >= 2;
        route.ContentFor = hit => hit >= 2 ? content[..900] : null; // the server delivers less than the probe announced
        using var manager = h.Manager([Item(h)], retries: 1);

        await Assert.ThrowsAsync<ModelInstallException>(() => manager.InstallAsync("m", TestContext.Current.CancellationToken));

        Assert.False(File.Exists(Path.Combine(h.Models, "gguf", "model.gguf")));
    }

    [Fact]
    public async Task Download_WithoutContentLength_SucceedsWhenTheLengthMatchesTheProbe()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(100_000);
        h.Server.Add(Path1, content).Chunked = hit => hit >= 2;
        using var manager = h.Manager([Item(h)]);

        await manager.InstallAsync("m", TestContext.Current.CancellationToken);

        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(h.Models, "gguf", "model.gguf"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARangeIgnoringServerThatKeepsDroppingAtTheSamePoint_ExhaustsTheRetries()
    {
        await using var h = new Harness();
        var route = h.Server.Add(Path1, Harness.Bytes(300_000));
        route.SupportsRange = false;
        route.DisconnectAfter = hit => hit >= 2 ? 50_000 : null; // every attempt restarts from zero and gets the same prefix
        using var manager = h.Manager([Item(h)]);

        await Assert.ThrowsAsync<ModelInstallException>(() => manager.InstallAsync("m", TestContext.Current.CancellationToken));

        Assert.Equal(4, h.Server.Requests.Count(r => r.Range != "bytes=0-0")); // the first try plus three retries, not endless
    }

    [Fact]
    public async Task UnknownSizes_FallBackToTheApproximateCatalogSize()
    {
        await using var h = new Harness();
        h.Server.Add(Path1, Harness.Bytes(5000)).Chunked = _ => true; // not even the probe learns a size
        using var tooSmall = h.Manager([Item(h, displaySize: 1_000_000)], freeSpace: _ => 100);

        var ex = await Assert.ThrowsAsync<ModelInstallException>(() => tooSmall.InstallAsync("m", TestContext.Current.CancellationToken));
        Assert.Contains("disk space", ex.Message);

        using var roomy = h.Manager([Item(h, displaySize: 1_000_000)], freeSpace: _ => 10_000_000);
        await roomy.InstallAsync("m", TestContext.Current.CancellationToken);
        Assert.Equal(ModelInstallState.Installed, roomy.GetStatus("m").State);
    }

    [Fact]
    public async Task ArchiveInstalls_NeedRoomForTheExtraction()
    {
        await using var h = new Harness();
        var zip = Harness.Zip(("llama-server.exe", Harness.Bytes(5000, 1)));
        h.Server.Add("/llama.zip", zip);
        var item = Harness.ArchiveItem("llama", h.Server, "/llama.zip", ModelPackaging.Zip, "llama-server.exe");
        using var tight = h.Manager([item], freeSpace: _ => zip.Length * 3L); // the download alone would fit

        var ex = await Assert.ThrowsAsync<ModelInstallException>(() => tight.InstallAsync("llama", TestContext.Current.CancellationToken));
        Assert.Contains("disk space", ex.Message);

        using var roomy = h.Manager([item], freeSpace: _ => zip.Length * 6L);
        await roomy.InstallAsync("llama", TestContext.Current.CancellationToken);
        Assert.Equal(ModelInstallState.Installed, roomy.GetStatus("llama").State);
    }

    [Fact]
    public async Task ParallelInstalls_ShareTheFreeSpaceInsteadOfEachSeeingAllOfIt()
    {
        await using var h = new Harness();
        var release = new TaskCompletionSource();
        var a = h.Server.Add("/a", Harness.Bytes(100_000, 1));
        a.Before = async hit =>
        {
            if (hit == 2)
            {
                await release.Task.WaitAsync(TimeSpan.FromSeconds(20)); // hold A's download, its space stays reserved
            }
        };
        h.Server.Add("/b", Harness.Bytes(100_000, 2));
        using var manager = h.Manager(
            [Harness.FileItem("a", h.Server, "/a", "gguf/a.gguf"), Harness.FileItem("b", h.Server, "/b", "gguf/b.gguf")],
            freeSpace: _ => 150_000);
        var first = manager.InstallAsync("a", TestContext.Current.CancellationToken);
        await Harness.WaitUntilAsync(() => a.Hits >= 2);

        var ex = await Assert.ThrowsAsync<ModelInstallException>(() => manager.InstallAsync("b", TestContext.Current.CancellationToken));

        Assert.Contains("disk space", ex.Message);
        release.SetResult();
        await first;
        Assert.Equal(ModelInstallState.Installed, manager.GetStatus("a").State);
        await manager.InstallAsync("b", TestContext.Current.CancellationToken); // A's reservation is gone, 150 000 are enough again
    }

    [Fact]
    public async Task Dispose_CancelsRunningInstallsAndKeepsThePartialDownload()
    {
        await using var h = new Harness();
        SlowRoute(h, Harness.Bytes(600_000));
        var manager = h.Manager([Item(h)]);
        var install = manager.InstallAsync("m", TestContext.Current.CancellationToken);
        await Harness.WaitUntilAsync(() => manager.GetStatus("m") is { State: ModelInstallState.Downloading, BytesDone: > 0 });

        manager.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.InstallAsync("m", TestContext.Current.CancellationToken));
        manager.Dispose(); // idempotent
        using var next = h.Manager([Item(h)]);
        var status = next.GetStatus("m");
        Assert.Equal(ModelInstallState.NotInstalled, status.State);
        Assert.True(status.BytesDone > 0);
    }

    [Fact]
    public async Task AJoinerWithoutItsOwnToken_SeesTheFirstCallersCancellation()
    {
        await using var h = new Harness();
        SlowRoute(h, Harness.Bytes(600_000));
        using var manager = h.Manager([Item(h)]);
        using var cts = new CancellationTokenSource();
        var first = manager.InstallAsync("m", cts.Token);
#pragma warning disable xUnit1051 // an uncancellable token is the point: the joiner follows the first caller
        var joiner = manager.InstallAsync("m", CancellationToken.None);
#pragma warning restore xUnit1051

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joiner);
    }

    [Fact]
    public async Task Install_IsRefusedWhileTheSameItemIsBeingRemoved()
    {
        await using var h = new Harness();
        h.Server.Add(Path1, Harness.Bytes(5000));
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        using var manager = h.Manager(
            [Item(h)],
            beforeRemove: _ =>
            {
                entered.Set();
                proceed.Wait(TimeSpan.FromSeconds(20));
            });
        await manager.InstallAsync("m", TestContext.Current.CancellationToken);
        var removal = manager.RemoveAsync("m", TestContext.Current.CancellationToken);
        await Harness.WaitUntilAsync(() => entered.IsSet);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InstallAsync("m", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RemoveAsync("m", TestContext.Current.CancellationToken));

        proceed.Set();
        await removal;
        await manager.InstallAsync("m", TestContext.Current.CancellationToken); // fine once the removal finished
        Assert.Equal(ModelInstallState.Installed, manager.GetStatus("m").State);
    }

    [Fact]
    public async Task Remove_LeavesNoTrashOrStagingBehind()
    {
        await using var h = new Harness();
        h.Server.Add("/a", Harness.Bytes(100, 1));
        h.Server.Add("/b", Harness.Bytes(100, 2));
        using var manager = h.Manager([Harness.DirectoryItem("multi", h.Server, "multi-dir", "/a", "/b")]);
        await manager.InstallAsync("multi", TestContext.Current.CancellationToken);

        await manager.RemoveAsync("multi", TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.Combine(h.Models, "multi-dir")));
        Assert.False(Directory.Exists(Path.Combine(h.Models, ".staging")));
    }
}

public sealed class FileOpsTests
{
    [Fact]
    public void Retry_TriesAgainOnIoErrors()
    {
        var calls = 0;

        FileOps.Retry(
            () =>
            {
                if (++calls < 3)
                {
                    throw new IOException("locked by the antivirus");
                }
            },
            attempts: 5,
            delay: TimeSpan.FromMilliseconds(1));

        Assert.Equal(3, calls);
    }

    [Fact]
    public void Retry_GivesUpAfterTheLastAttempt()
    {
        var calls = 0;

        Assert.Throws<UnauthorizedAccessException>(() => FileOps.Retry(() =>
        {
            calls++;
            throw new UnauthorizedAccessException();
        }, attempts: 4, delay: TimeSpan.FromMilliseconds(1)));

        Assert.Equal(4, calls);
    }

    [Fact]
    public void Retry_DoesNotRetryAMissingSource()
    {
        var calls = 0;

        Assert.Throws<FileNotFoundException>(() => FileOps.Retry(() =>
        {
            calls++;
            throw new FileNotFoundException();
        }, attempts: 4, delay: TimeSpan.FromMilliseconds(1)));

        Assert.Equal(1, calls);
    }
}
