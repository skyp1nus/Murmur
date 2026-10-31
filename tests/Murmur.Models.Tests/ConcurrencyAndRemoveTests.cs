using Murmur.Core.Models;
using Murmur.Models.Tests.Support;

namespace Murmur.Models.Tests;

public sealed class ConcurrencyAndRemoveTests
{
    private static Route Slow(Harness h, string path, int length)
    {
        var route = h.Server.Add(path, Harness.Bytes(length));
        route.ChunkSize = 16 * 1024;
        route.ChunkDelay = TimeSpan.FromMilliseconds(15);
        return route;
    }

    [Fact]
    public async Task ASecondInstallOfTheSameIdJoinsTheFirst()
    {
        await using var h = new Harness();
        var route = Slow(h, "/m", 300_000);
        using var manager = h.Manager([Harness.FileItem("m", h.Server, "/m", "gguf/m.gguf")]);

#pragma warning disable xUnit1051 // an uncancellable token is the point: the joiner then gets the very same task
        var first = manager.InstallAsync("m", CancellationToken.None);
        var second = manager.InstallAsync("m", CancellationToken.None);
        await Harness.WaitUntilAsync(() => manager.GetStatus("m").State == ModelInstallState.Downloading);
        var third = manager.InstallAsync("m", CancellationToken.None);
#pragma warning restore xUnit1051

        Assert.Same(first, second);
        Assert.Same(first, third);
        await Task.WhenAll(first, second, third);
        Assert.Equal(1, h.Server.Requests.Count(r => r.Range != "bytes=0-0")); // one download, one probe
        await Harness.ServedSettledAsync(route, 300_000);
        Assert.Equal(300_000, route.BytesServed);
    }

    [Fact]
    public async Task AJoinerWithItsOwnTokenCanStopWaitingWithoutStoppingTheInstall()
    {
        await using var h = new Harness();
        Slow(h, "/m", 300_000);
        using var manager = h.Manager([Harness.FileItem("m", h.Server, "/m", "gguf/m.gguf")]);
        var first = manager.InstallAsync("m", TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();

        var joiner = manager.InstallAsync("m", cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joiner);
        await first;
        Assert.Equal(ModelInstallState.Installed, manager.GetStatus("m").State);
    }

    [Fact]
    public async Task DifferentIdsInstallInParallel()
    {
        await using var h = new Harness();
        var arrived = 0;
        var bothArrived = new TaskCompletionSource();
        Func<int, Task> before = async hit =>
        {
            if (hit == 1)
            {
                if (Interlocked.Increment(ref arrived) == 2)
                {
                    bothArrived.TrySetResult();
                }

                await bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(10)); // proves the other request is in flight too
            }
        };
        h.Server.Add("/a", Harness.Bytes(20_000, 1)).Before = before;
        h.Server.Add("/b", Harness.Bytes(30_000, 2)).Before = before;
        using var manager = h.Manager([Harness.FileItem("a", h.Server, "/a", "gguf/a.gguf"), Harness.FileItem("b", h.Server, "/b", "gguf/b.gguf")]);

        await Task.WhenAll(manager.InstallAsync("a", TestContext.Current.CancellationToken), manager.InstallAsync("b", TestContext.Current.CancellationToken));

        Assert.True(h.Server.MaxConcurrentRequests >= 2);
        Assert.Equal(ModelInstallState.Installed, manager.GetStatus("a").State);
        Assert.Equal(ModelInstallState.Installed, manager.GetStatus("b").State);
    }

    [Fact]
    public async Task ReinstallAfterAFailureStartsANewAttempt()
    {
        await using var h = new Harness();
        var route = h.Server.Add("/m", Harness.Bytes(5000));
        route.NotFound = true;
        using var manager = h.Manager([Harness.FileItem("m", h.Server, "/m", "gguf/m.gguf")]);
        await Assert.ThrowsAsync<ModelInstallException>(() => manager.InstallAsync("m", TestContext.Current.CancellationToken));
        Assert.Equal(ModelInstallState.Failed, manager.GetStatus("m").State);

        route.NotFound = false;
        await manager.InstallAsync("m", TestContext.Current.CancellationToken);

        var status = manager.GetStatus("m");
        Assert.Equal(ModelInstallState.Installed, status.State);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task Remove_DeletesTheFilesAndAnyPartialDownload()
    {
        await using var h = new Harness();
        h.Server.Add("/m", Harness.Bytes(5000));
        var item = Harness.FileItem("m", h.Server, "/m", "gguf/m.gguf");
        using var manager = h.Manager([item]);
        await manager.InstallAsync("m", TestContext.Current.CancellationToken);
        var partial = Path.Combine(h.Models, ".staging", "m", "dl", "m.gguf.part");
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        await File.WriteAllBytesAsync(partial, Harness.Bytes(10), TestContext.Current.CancellationToken);

        await manager.RemoveAsync("m", TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(h.Models, "gguf", "m.gguf")));
        Assert.False(Directory.Exists(Path.Combine(h.Models, ".staging", "m")));
        var status = manager.GetStatus("m");
        Assert.Equal(ModelInstallState.NotInstalled, status.State);
        Assert.Equal(0, status.BytesDone);
    }

    [Fact]
    public async Task Remove_DeletesADirectoryItemAsAWhole()
    {
        await using var h = new Harness();
        h.Server.Add("/a", Harness.Bytes(100, 1));
        h.Server.Add("/b", Harness.Bytes(100, 2));
        using var manager = h.Manager([Harness.DirectoryItem("multi", h.Server, "multi-dir", "/a", "/b")]);
        await manager.InstallAsync("multi", TestContext.Current.CancellationToken);
        Assert.True(Directory.Exists(Path.Combine(h.Models, "multi-dir")));

        await manager.RemoveAsync("multi", TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.Combine(h.Models, "multi-dir")));
        Assert.Equal(ModelInstallState.NotInstalled, manager.GetStatus("multi").State);
    }

    [Fact]
    public async Task Remove_OfAnItemThatWasNeverInstalled_Succeeds()
    {
        await using var h = new Harness();
        h.Server.Add("/m", Harness.Bytes(100));
        using var manager = h.Manager([Harness.FileItem("m", h.Server, "/m", "gguf/m.gguf")]);

        await manager.RemoveAsync("m", TestContext.Current.CancellationToken);

        Assert.Equal(ModelInstallState.NotInstalled, manager.GetStatus("m").State);
    }

    [Fact]
    public async Task Remove_ClearsAFailedState()
    {
        await using var h = new Harness();
        h.Server.Add("/m", Harness.Bytes(100)).NotFound = true;
        using var manager = h.Manager([Harness.FileItem("m", h.Server, "/m", "gguf/m.gguf")]);
        await Assert.ThrowsAsync<ModelInstallException>(() => manager.InstallAsync("m", TestContext.Current.CancellationToken));

        await manager.RemoveAsync("m", TestContext.Current.CancellationToken);

        Assert.Equal(ModelInstallState.NotInstalled, manager.GetStatus("m").State);
    }

    [Fact]
    public async Task Remove_IsRefusedWhileTheItemIsInstalling()
    {
        await using var h = new Harness();
        Slow(h, "/m", 400_000);
        using var manager = h.Manager([Harness.FileItem("m", h.Server, "/m", "gguf/m.gguf")]);
        using var cts = new CancellationTokenSource();
        var install = manager.InstallAsync("m", cts.Token);
        await Harness.WaitUntilAsync(() => manager.GetStatus("m").State == ModelInstallState.Downloading);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RemoveAsync("m", TestContext.Current.CancellationToken));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
        await manager.RemoveAsync("m", TestContext.Current.CancellationToken); // fine once it stopped
        Assert.Equal(0, manager.GetStatus("m").BytesDone);
    }

    [Fact]
    public async Task Remove_RaisesStatusChanged()
    {
        await using var h = new Harness();
        h.Server.Add("/m", Harness.Bytes(100));
        using var manager = h.Manager([Harness.FileItem("m", h.Server, "/m", "gguf/m.gguf")]);
        await manager.InstallAsync("m", TestContext.Current.CancellationToken);
        var events = new List<ModelStatus>();
        manager.StatusChanged += status =>
        {
            lock (events)
            {
                events.Add(status);
            }
        };

        await manager.RemoveAsync("m", TestContext.Current.CancellationToken);

        lock (events)
        {
            Assert.Equal(ModelInstallState.NotInstalled, Assert.Single(events).State);
        }
    }
}
