using System.Collections.Concurrent;
using Murmur.Core.Models;
using Murmur.Models.Tests.Support;

namespace Murmur.Models.Tests;

/// <summary>Scenarios driven only through <see cref="IModelManager"/> (plus a second manager over the same folder as a "restart").</summary>
public sealed class ManagerEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<ModelCatalogItem> Multi(Harness h) =>
        [Harness.DirectoryItem("multi", h.Server, "multi", "/m/a.bin", "/m/b.bin", "/m/c.bin")];

    private static (IModelManager Manager, ConcurrentQueue<ModelStatus> Events) Observe(ModelManager manager)
    {
        var events = new ConcurrentQueue<ModelStatus>();
        manager.StatusChanged += events.Enqueue;
        return (manager, events);
    }

    [Fact]
    public async Task MultiFileInstall_RaisesDownloadingThenInstalled_WithGrowingProgress()
    {
        await using var h = new Harness();
        var files = new[] { Harness.Bytes(200_000, 1), Harness.Bytes(150_000, 2), Harness.Bytes(100_000, 3) };
        h.Server.Add("/m/a.bin", files[0]).ChunkDelay = TimeSpan.FromMilliseconds(3);
        h.Server.Add("/m/b.bin", files[1]).ChunkDelay = TimeSpan.FromMilliseconds(3);
        h.Server.Add("/m/c.bin", files[2]).ChunkDelay = TimeSpan.FromMilliseconds(3);
        using var concrete = h.Manager(Multi(h), progressInterval: TimeSpan.Zero);
        var (manager, events) = Observe(concrete);

        await manager.InstallAsync("multi", Ct);
        await Harness.WaitUntilAsync(() => events.Any(e => e.State == ModelInstallState.Installed));

        var seen = events.ToArray();
        Assert.Equal(ModelInstallState.Downloading, seen[0].State);
        Assert.Equal(ModelInstallState.Installed, seen[^1].State);
        Assert.All(seen[..^1], e => Assert.Equal(ModelInstallState.Downloading, e.State));
        Assert.DoesNotContain(seen, e => e.State is ModelInstallState.Failed or ModelInstallState.NotInstalled);
        var done = seen.Select(e => e.BytesDone).ToArray();
        Assert.Equal(done.Order(), done); // progress never goes backwards across files
        Assert.Equal(450_000, seen[^1].BytesDone);
        Assert.True(seen.Count(e => e.State == ModelInstallState.Downloading) > 2);
        Assert.Equal(ModelInstallState.Installed, manager.GetStatus("multi").State);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(files[i], await File.ReadAllBytesAsync(Path.Combine(h.Models, "multi", "abc"[i] + ".bin"), Ct));
        }
    }

    [Fact]
    public async Task FailureThenRetry_GoesFailedDownloadingInstalled_AndReusesThePartialBytes()
    {
        await using var h = new Harness();
        var content = Harness.Bytes(400_000, 7);
        var route = h.Server.Add("/f/model.gguf", content);
        route.ChunkSize = 16 * 1024;
        var outage = true;
        route.DisconnectAfter = hit => outage && hit == 2 ? 120_000 : null; // hit 1 is the size probe, hit 2 drops mid-stream ...
        route.StatusFor = hit => outage && hit > 2 ? 503 : null; // ... and the server stays down for every retry
        var item = Harness.FileItem("m", h.Server, "/f/model.gguf", "gguf/model.gguf");
        using var concrete = h.Manager([item], retries: 3, progressInterval: TimeSpan.Zero);
        var (manager, events) = Observe(concrete);

        await Assert.ThrowsAnyAsync<Exception>(() => manager.InstallAsync("m", Ct));
        var failed = manager.GetStatus("m");
        Assert.Equal(ModelInstallState.Failed, failed.State);
        Assert.False(string.IsNullOrWhiteSpace(failed.Error));
        Assert.Equal(ModelInstallState.Failed, events.Last().State);
        var servedBefore = route.BytesServed;
        outage = false;
        Assert.True(failed.BytesDone > 0 && failed.BytesDone < content.Length, $"BytesDone={failed.BytesDone}");

        while (events.TryDequeue(out _))
        {
        }

        await manager.InstallAsync("m", Ct);
        await Harness.WaitUntilAsync(() => events.Any(e => e.State == ModelInstallState.Installed));

        var states = events.Select(e => e.State).Distinct().ToArray();
        Assert.Equal([ModelInstallState.Downloading, ModelInstallState.Installed], states);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(h.Models, "gguf", "model.gguf"), Ct));
        // The retry fetched only what was missing (well under a full second copy).
        Assert.True(route.BytesServed - servedBefore < content.Length - 100_000, $"re-downloaded {route.BytesServed - servedBefore} bytes");
        Assert.Contains(h.Server.Requests, r => r.Range is { } range && range.StartsWith("bytes=") && range != "bytes=0-0" && range != "bytes=0-");
    }

    [Fact]
    public async Task AfterARestart_ANewManagerSeesInstalledItemsAndPartialProgress()
    {
        await using var h = new Harness();
        var small = Harness.Bytes(50_000, 1);
        var big = Harness.Bytes(400_000, 2);
        h.Server.Add("/s/small.gguf", small);
        var bigRoute = h.Server.Add("/s/big.gguf", big);
        bigRoute.ChunkSize = 16 * 1024;
        bigRoute.ChunkDelay = TimeSpan.FromMilliseconds(15);
        IReadOnlyList<ModelCatalogItem> items =
        [
            Harness.FileItem("small", h.Server, "/s/small.gguf", "gguf/small.gguf"),
            Harness.FileItem("big", h.Server, "/s/big.gguf", "gguf/big.gguf"),
        ];

        long onDisk;
        using (var first = h.Manager(items, progressInterval: TimeSpan.Zero))
        {
            await first.InstallAsync("small", Ct);
            using var cts = new CancellationTokenSource();
            first.StatusChanged += s =>
            {
                if (s.Model.Id == "big" && s.BytesDone > 100_000)
                {
                    cts.Cancel();
                }
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.InstallAsync("big", cts.Token));
            onDisk = first.GetStatus("big").BytesDone;
        }

        using var second = h.Manager(items);
        Assert.Equal(ModelInstallState.Installed, second.GetStatus("small").State);
        var partial = second.GetStatus("big");
        Assert.Equal(ModelInstallState.NotInstalled, partial.State);
        Assert.True(partial.BytesDone > 0, "partial bytes should be visible");
        Assert.Equal(onDisk, partial.BytesDone);
        Assert.False(File.Exists(Path.Combine(h.Models, "gguf", "big.gguf")));

        bigRoute.ChunkDelay = TimeSpan.Zero;
        await second.InstallAsync("big", Ct);
        Assert.Equal(ModelInstallState.Installed, second.GetStatus("big").State);
        Assert.Equal(0, second.GetStatus("big").BytesDone - big.Length);
    }

    [Fact]
    public async Task ManifestWrittenByTheManager_IsReadTheWayAiModelPathsReadsIt_AcrossAChatSwitch()
    {
        await using var h = new Harness();
        h.Server.Add("/g/chat1.gguf", Harness.Bytes(10_000, 1));
        h.Server.Add("/g/chat2.gguf", Harness.Bytes(12_000, 2));
        h.Server.Add("/g/embed.gguf", Harness.Bytes(8_000, 3));
        IReadOnlyList<ModelCatalogItem> items =
        [
            Harness.FileItem("chat1", h.Server, "/g/chat1.gguf", "gguf/chat1.gguf", ModelKind.Chat),
            Harness.FileItem("chat2", h.Server, "/g/chat2.gguf", "gguf/chat2.gguf", ModelKind.Chat),
            Harness.FileItem("embed", h.Server, "/g/embed.gguf", "gguf/embed.gguf", ModelKind.Embedding),
        ];
        using var manager = h.Manager(items);
        var manifestPath = Path.Combine(h.Models, "gguf", "models.json");
        var gguf = Path.GetFullPath(Path.Combine(h.Models, "gguf"));

        await manager.InstallAsync("embed", Ct);
        await manager.InstallAsync("chat1", Ct);
        var first = AiModelPathsReader.Read(manifestPath);
        Assert.NotNull(first);
        Assert.Equal(Path.Combine(gguf, "chat1.gguf"), first.Chat);
        Assert.Equal(Path.Combine(gguf, "embed.gguf"), first.Embed);
        Assert.True(Path.IsPathRooted(first.Chat));

        await manager.InstallAsync("chat2", Ct);
        var second = AiModelPathsReader.Read(manifestPath);
        Assert.Equal(Path.Combine(gguf, "chat2.gguf"), second!.Chat);
        Assert.Equal(Path.Combine(gguf, "embed.gguf"), second.Embed);
        Assert.True(File.Exists(Path.Combine(gguf, "chat1.gguf"))); // the old chat file stays, only the pointer moves

        await manager.InstallAsync("chat1", Ct); // already installed: just switches back
        Assert.Equal(Path.Combine(gguf, "chat1.gguf"), AiModelPathsReader.Read(manifestPath)!.Chat);

        await manager.RemoveAsync("chat1", Ct);
        var removed = AiModelPathsReader.Read(manifestPath);
        Assert.True(string.IsNullOrEmpty(removed?.Chat));
        Assert.Equal(Path.Combine(gguf, "embed.gguf"), removed!.Embed);
        Assert.DoesNotContain(".tmp", string.Concat(Directory.GetFiles(Path.Combine(h.Models, "gguf")).Select(Path.GetExtension)));
    }

    [Fact]
    public async Task LinuxRuntimeTarball_InstallsLlamaServerExecutableNextToItsLibraries()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes");
        }

        await using var h = new Harness();
        h.Server.Add("/rt.tar.gz", Harness.TarGz(
            ("llama-b1/llama-server", Harness.Text("#!/bin/sh\n")),
            ("llama-b1/libllama.so", Harness.Text("lib")),
            ("llama-b1/libggml.so", Harness.Text("lib2")),
            ("README.md", Harness.Text("outside the server folder"))));
        using var manager = h.Manager([Harness.ArchiveItem("rt", h.Server, "/rt.tar.gz", ModelPackaging.TarGz, "llama-server")]);

        await manager.InstallAsync("rt", Ct);

        var dir = Path.Combine(h.Models, "llama.cpp");
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(Path.Combine(dir, "llama-server")).HasFlag(UnixFileMode.UserExecute));
        }

        Assert.True(File.Exists(Path.Combine(dir, "libllama.so")));
        Assert.True(File.Exists(Path.Combine(dir, "libggml.so")));
        Assert.Equal(ModelInstallState.Installed, manager.GetStatus("rt").State);
        var staging = Path.Combine(h.Models, ".staging");
        Assert.True(!Directory.Exists(staging) || Directory.GetFileSystemEntries(staging).Length == 0);
    }
}
