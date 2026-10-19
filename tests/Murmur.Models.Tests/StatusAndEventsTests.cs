using System.Diagnostics;
using Murmur.Core.Models;
using Murmur.Models.Tests.Support;

namespace Murmur.Models.Tests;

public sealed class StatusAndEventsTests
{
    private sealed class Recorder
    {
        public List<(long Timestamp, ModelStatus Status)> Events { get; } = [];

        public void Attach(ModelManager manager) =>
            manager.StatusChanged += status =>
            {
                lock (Events)
                {
                    Events.Add((Stopwatch.GetTimestamp(), status));
                }
            };

        public List<(long Timestamp, ModelStatus Status)> Snapshot()
        {
            lock (Events)
            {
                return [.. Events];
            }
        }
    }

    [Fact]
    public async Task GetStatus_ReflectsTheDisk()
    {
        await using var h = new Harness();
        h.Server.Add("/m", Harness.Bytes(100));
        var item = Harness.FileItem("m", h.Server, "/m", "gguf/m.gguf");
        using var manager = h.Manager([item]);
        Assert.Equal(ModelInstallState.NotInstalled, manager.GetStatus("m").State);

        Directory.CreateDirectory(Path.Combine(h.Models, "gguf"));
        await File.WriteAllBytesAsync(Path.Combine(h.Models, "gguf", "m.gguf"), [], TestContext.Current.CancellationToken);
        Assert.Equal(ModelInstallState.NotInstalled, manager.GetStatus("m").State); // an empty file is not a model

        await File.WriteAllBytesAsync(Path.Combine(h.Models, "gguf", "m.gguf"), Harness.Bytes(77), TestContext.Current.CancellationToken);
        var installed = manager.GetStatus("m");
        Assert.Equal(ModelInstallState.Installed, installed.State);
        Assert.Equal(77, installed.BytesDone);
        Assert.Equal(77, installed.BytesTotal);
        Assert.Same(item.Info, installed.Model);

        File.Delete(Path.Combine(h.Models, "gguf", "m.gguf"));
        Assert.Equal(ModelInstallState.NotInstalled, manager.GetStatus("m").State);
    }

    [Fact]
    public async Task GetStatus_ForADirectoryItemNeedsEveryFile()
    {
        await using var h = new Harness();
        h.Server.Add("/a", Harness.Bytes(10));
        h.Server.Add("/b", Harness.Bytes(10));
        using var manager = h.Manager([Harness.DirectoryItem("multi", h.Server, "multi-dir", "/a", "/b")]);
        var dir = Path.Combine(h.Models, "multi-dir");
        Directory.CreateDirectory(dir);

        await File.WriteAllBytesAsync(Path.Combine(dir, "a"), Harness.Bytes(10), TestContext.Current.CancellationToken);
        Assert.Equal(ModelInstallState.NotInstalled, manager.GetStatus("multi").State);

        await File.WriteAllBytesAsync(Path.Combine(dir, "b"), Harness.Bytes(10), TestContext.Current.CancellationToken);
        Assert.Equal(ModelInstallState.Installed, manager.GetStatus("multi").State);
        Assert.Equal(20, manager.GetStatus("multi").BytesDone);
    }

    [Fact]
    public async Task GetStatus_DuringAnInstall_ShowsProgressAndTotal()
    {
        await using var h = new Harness();
        var route = h.Server.Add("/m", Harness.Bytes(400_000));
        route.ChunkSize = 16 * 1024;
        route.ChunkDelay = TimeSpan.FromMilliseconds(15);
        using var manager = h.Manager([Harness.FileItem("m", h.Server, "/m", "gguf/m.gguf")]);
        var install = manager.InstallAsync("m", TestContext.Current.CancellationToken);

        await Harness.WaitUntilAsync(() => manager.GetStatus("m") is { State: ModelInstallState.Downloading, BytesDone: > 0 });
        var status = manager.GetStatus("m");

        Assert.Equal(400_000, status.BytesTotal);
        Assert.InRange(status.BytesDone, 1, 400_000);
        await install;
    }

    [Fact]
    public async Task Events_StartWithDownloadingAndEndWithInstalled()
    {
        await using var h = new Harness();
        h.Server.Add("/m", Harness.Bytes(200_000)).ChunkDelay = TimeSpan.FromMilliseconds(5);
        using var manager = h.Manager([Harness.FileItem("m", h.Server, "/m", "gguf/m.gguf")], progressInterval: TimeSpan.Zero);
        var recorder = new Recorder();
        recorder.Attach(manager);

        await manager.InstallAsync("m", TestContext.Current.CancellationToken);

        var events = recorder.Snapshot().Select(e => e.Status).ToList();
        Assert.True(events.Count >= 3);
        Assert.Equal(ModelInstallState.Downloading, events[0].State);
        Assert.Equal(ModelInstallState.Installed, events[^1].State);
        Assert.Equal(200_000, events[^1].BytesDone);
        Assert.All(events[..^1], e => Assert.Equal(ModelInstallState.Downloading, e.State));
        Assert.Equal(events.Select(e => e.BytesDone).Order(), events.Select(e => e.BytesDone)); // progress never goes backwards
        Assert.Contains(events, e => e.BytesTotal == 200_000);
    }

    [Fact]
    public async Task Events_ReportFailedWithTheError()
    {
        await using var h = new Harness();
        h.Server.Add("/m", Harness.Bytes(100)).NotFound = true;
        using var manager = h.Manager([Harness.FileItem("m", h.Server, "/m", "gguf/m.gguf")]);
        var recorder = new Recorder();
        recorder.Attach(manager);

        await Assert.ThrowsAsync<ModelInstallException>(() => manager.InstallAsync("m", TestContext.Current.CancellationToken));

        var last = recorder.Snapshot()[^1].Status;
        Assert.Equal(ModelInstallState.Failed, last.State);
        Assert.Contains("404", last.Error);
    }

    [Fact]
    public async Task Events_AreThrottledToTenPerSecond()
    {
        await using var h = new Harness();
        var route = h.Server.Add("/m", Harness.Bytes(1_000_000));
        route.ChunkSize = 8 * 1024; // ~120 chunks, 3 ms apart: ~400 ms of body
        route.ChunkDelay = TimeSpan.FromMilliseconds(3);
        using var manager = h.Manager([Harness.FileItem("m", h.Server, "/m", "gguf/m.gguf")]); // default 100 ms interval
        var recorder = new Recorder();
        recorder.Attach(manager);

        await manager.InstallAsync("m", TestContext.Current.CancellationToken);

        var events = recorder.Snapshot();
        var downloading = events.Where(e => e.Status.State == ModelInstallState.Downloading).ToList();
        Assert.InRange(downloading.Count, 2, 12); // far fewer than the ~120 chunks
        for (var i = 1; i < downloading.Count; i++)
        {
            var gap = Stopwatch.GetElapsedTime(downloading[i - 1].Timestamp, downloading[i].Timestamp);
            Assert.True(gap >= TimeSpan.FromMilliseconds(90), $"events {i - 1} and {i} are only {gap.TotalMilliseconds:0} ms apart");
        }

        Assert.Equal(ModelInstallState.Installed, events[^1].Status.State); // the final state is never throttled away
    }

    [Fact]
    public async Task Events_AreRaisedOffTheCallersThread()
    {
        await using var h = new Harness();
        h.Server.Add("/m", Harness.Bytes(1000));
        using var manager = h.Manager([Harness.FileItem("m", h.Server, "/m", "gguf/m.gguf")]);
        using var release = new ManualResetEventSlim();
        manager.StatusChanged += _ => release.Wait(TimeSpan.FromSeconds(10)); // a handler that blocks

        var started = Stopwatch.StartNew();
        var install = manager.InstallAsync("m", TestContext.Current.CancellationToken);

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3), "InstallAsync must not run the subscribers on the caller's thread");
        Assert.Equal(ModelInstallState.Downloading, manager.GetStatus("m").State);
        release.Set();
        await install;
    }

    [Fact]
    public async Task AThrowingSubscriber_DoesNotBreakTheInstall()
    {
        await using var h = new Harness();
        h.Server.Add("/m", Harness.Bytes(1000));
        using var manager = h.Manager([Harness.FileItem("m", h.Server, "/m", "gguf/m.gguf")]);
        manager.StatusChanged += _ => throw new InvalidOperationException("subscriber bug");

        await manager.InstallAsync("m", TestContext.Current.CancellationToken);

        Assert.Equal(ModelInstallState.Installed, manager.GetStatus("m").State);
    }
}
