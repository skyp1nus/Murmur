using Murmur.Core.Models;
using Murmur.Models.Tests.Support;

namespace Murmur.Models.Tests;

/// <summary>The GPU builds of the runtime against the fake server: own folder, companion archive, CPU build untouched.</summary>
public sealed class GpuRuntimeInstallTests
{
    private static ModelCatalogItem Item(ModelCatalogSources sources, string id) =>
        ModelCatalog.Create(sources, ModelPlatform.Windows).Single(i => i.Id == id);

    private static void Serve(Harness h, ModelCatalogItem item, params byte[][] archives)
    {
        for (var i = 0; i < item.Files.Count; i++)
        {
            h.Server.Add(new Uri(item.Files[i].Url).AbsolutePath, archives[i]);
        }
    }

    [Fact]
    public async Task Cuda_InstallsTheServerAndItsRuntimeDllsInTheirOwnFolder()
    {
        await using var h = new Harness();
        var cuda = Item(h.Sources, ModelCatalog.LlamaCppCudaId);
        var server = Harness.Bytes(2000, 1);
        var cudart = Harness.Bytes(300, 3);
        Serve(
            h,
            cuda,
            Harness.Zip(("llama-server.exe", server), ("ggml-cuda.dll", Harness.Bytes(500, 2))),
            Harness.Zip(("cudart64_12.dll", cudart), ("cublas64_12.dll", Harness.Bytes(400, 4))));
        using var manager = h.Manager([cuda]);

        await manager.InstallAsync(ModelCatalog.LlamaCppCudaId, TestContext.Current.CancellationToken);

        var dir = Path.Combine(h.Models, "llama.cpp-cuda");
        Assert.Equal(server, await File.ReadAllBytesAsync(Path.Combine(dir, "llama-server.exe"), TestContext.Current.CancellationToken));
        Assert.Equal(cudart, await File.ReadAllBytesAsync(Path.Combine(dir, "cudart64_12.dll"), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(dir, "cublas64_12.dll")));
        Assert.True(File.Exists(Path.Combine(dir, "ggml-cuda.dll")));
        Assert.Equal(ModelInstallState.Installed, manager.GetStatus(ModelCatalog.LlamaCppCudaId).State);
        Assert.False(Directory.Exists(Path.Combine(h.Models, ".staging")));
    }

    [Fact]
    public async Task Cuda_PutsTheRuntimeDllsNextToTheServerEvenWhenTheServerArchiveHasAFolder()
    {
        await using var h = new Harness();
        var cuda = Item(h.Sources, ModelCatalog.LlamaCppCudaId);
        Serve(
            h,
            cuda,
            Harness.Zip(("build/bin/llama-server.exe", Harness.Bytes(2000, 1)), ("build/bin/ggml-cuda.dll", Harness.Bytes(500, 2)), ("README.md", Harness.Text("x"))),
            Harness.Zip(("cudart64_12.dll", Harness.Bytes(300, 3))));
        using var manager = h.Manager([cuda]);

        await manager.InstallAsync(ModelCatalog.LlamaCppCudaId, TestContext.Current.CancellationToken);

        var dir = Path.Combine(h.Models, "llama.cpp-cuda");
        Assert.True(File.Exists(Path.Combine(dir, "llama-server.exe")));
        Assert.True(File.Exists(Path.Combine(dir, "cudart64_12.dll")));
        Assert.False(File.Exists(Path.Combine(dir, "README.md")));
    }

    [Fact]
    public async Task Cuda_WithABrokenRuntimeArchive_InstallsNothing()
    {
        await using var h = new Harness();
        var cuda = Item(h.Sources, ModelCatalog.LlamaCppCudaId);
        Serve(h, cuda, Harness.Zip(("llama-server.exe", Harness.Bytes(2000, 1))), Harness.Bytes(100, 9));
        using var manager = h.Manager([cuda]);

        await Assert.ThrowsAsync<ModelInstallException>(() => manager.InstallAsync(ModelCatalog.LlamaCppCudaId, TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(Path.Combine(h.Models, "llama.cpp-cuda")));
        Assert.Equal(ModelInstallState.Failed, manager.GetStatus(ModelCatalog.LlamaCppCudaId).State);
    }

    [Fact]
    public async Task InstallingAndRemovingAGpuBuild_LeavesTheCpuBuildIntact()
    {
        await using var h = new Harness();
        var cpu = Item(h.Sources, ModelCatalog.LlamaCppId);
        var vulkan = Item(h.Sources, ModelCatalog.LlamaCppVulkanId);
        var cpuServer = Harness.Bytes(2000, 1);
        Serve(h, cpu, Harness.Zip(("llama-server.exe", cpuServer)));
        Serve(h, vulkan, Harness.Zip(("llama-server.exe", Harness.Bytes(2000, 2)), ("ggml-vulkan.dll", Harness.Bytes(100, 3))));
        using var manager = h.Manager([cpu, vulkan]);

        await manager.InstallAsync(ModelCatalog.LlamaCppId, TestContext.Current.CancellationToken);
        await manager.InstallAsync(ModelCatalog.LlamaCppVulkanId, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(h.Models, "llama.cpp-vulkan", "ggml-vulkan.dll")));
        Assert.False(File.Exists(Path.Combine(h.Models, "llama.cpp", "ggml-vulkan.dll")));

        await manager.RemoveAsync(ModelCatalog.LlamaCppVulkanId, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.Combine(h.Models, "llama.cpp-vulkan")));
        Assert.Equal(cpuServer, await File.ReadAllBytesAsync(Path.Combine(h.Models, "llama.cpp", "llama-server.exe"), TestContext.Current.CancellationToken));
        Assert.Equal(ModelInstallState.Installed, manager.GetStatus(ModelCatalog.LlamaCppId).State);
        Assert.Equal(ModelInstallState.NotInstalled, manager.GetStatus(ModelCatalog.LlamaCppVulkanId).State);
    }

    [Fact]
    public async Task Status_OfAnInstalledGpuBuild_CountsEveryFileOfTheItem()
    {
        await using var h = new Harness();
        var cuda = Item(h.Sources, ModelCatalog.LlamaCppCudaId);
        Serve(h, cuda, Harness.Zip(("llama-server.exe", Harness.Bytes(2000, 1))), Harness.Zip(("cudart64_12.dll", Harness.Bytes(300, 3))));
        using var manager = h.Manager([cuda]);

        await manager.InstallAsync(ModelCatalog.LlamaCppCudaId, TestContext.Current.CancellationToken);

        var status = manager.GetStatus(ModelCatalog.LlamaCppCudaId);
        Assert.Equal(ModelInstallState.Installed, status.State);
        Assert.True(status.BytesDone > 2000);
    }

    private static ModelCatalogItem WithPins(ModelCatalogItem item, params byte[][] archives) =>
        item with
        {
            RequirePins = true,
            Files = [.. item.Files.Select((f, i) => f with { Sha256 = Harness.Sha256(archives[i]), SizeBytes = archives[i].Length })],
        };

    [Fact]
    public async Task AnItemThatRequiresPins_RefusesToDownloadWhileAFileIsUnpinned_AndTouchesNothing()
    {
        await using var h = new Harness();
        var cuda = Item(h.Sources, ModelCatalog.LlamaCppCudaId) with { RequirePins = true };
        var zips = new[] { Harness.Zip(("llama-server.exe", Harness.Bytes(2000, 1))), Harness.Zip(("cudart64_12.dll", Harness.Bytes(300, 3))) };
        Serve(h, cuda, zips);
        using var manager = h.Manager([cuda]);

        var ex = await Assert.ThrowsAsync<ModelInstallException>(() => manager.InstallAsync(ModelCatalog.LlamaCppCudaId, TestContext.Current.CancellationToken));

        Assert.Contains("not pinned yet", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ModelInstallState.Failed, manager.GetStatus(ModelCatalog.LlamaCppCudaId).State);
        Assert.False(Directory.Exists(Path.Combine(h.Models, "llama.cpp-cuda")));
        Assert.False(Directory.Exists(Path.Combine(h.Models, ".staging")));
        Assert.Empty(h.Server.Requests);
    }

    [Fact]
    public async Task AFullyPinnedItem_InstallsAndVerifiesEveryFile()
    {
        await using var h = new Harness();
        var vulkan = Item(h.Sources, ModelCatalog.LlamaCppVulkanId);
        var zip = Harness.Zip(("llama-server.exe", Harness.Bytes(2000, 1)));
        Serve(h, vulkan, zip);
        using var manager = h.Manager([WithPins(vulkan, zip)]);

        await manager.InstallAsync(ModelCatalog.LlamaCppVulkanId, TestContext.Current.CancellationToken);

        Assert.Equal(ModelInstallState.Installed, manager.GetStatus(ModelCatalog.LlamaCppVulkanId).State);
    }

    [Fact]
    public async Task AFullyPinnedItem_WithATamperedDownload_IsRejected()
    {
        await using var h = new Harness();
        var vulkan = Item(h.Sources, ModelCatalog.LlamaCppVulkanId);
        var genuine = Harness.Zip(("llama-server.exe", Harness.Bytes(2000, 1)));
        var tampered = Harness.Zip(("llama-server.exe", Harness.Bytes(2000, 2)));
        Serve(h, vulkan, tampered);
        using var manager = h.Manager([WithPins(vulkan, genuine)]);

        await Assert.ThrowsAsync<ModelInstallException>(() => manager.InstallAsync(ModelCatalog.LlamaCppVulkanId, TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(Path.Combine(h.Models, "llama.cpp-vulkan")));
    }

    [Fact]
    public async Task AnUnpinnedItemThatIsAlreadyInstalled_StaysInstalled()
    {
        await using var h = new Harness();
        var vulkan = Item(h.Sources, ModelCatalog.LlamaCppVulkanId) with { RequirePins = true };
        Directory.CreateDirectory(Path.Combine(h.Models, "llama.cpp-vulkan"));
        await File.WriteAllBytesAsync(Path.Combine(h.Models, "llama.cpp-vulkan", "llama-server.exe"), Harness.Bytes(100), TestContext.Current.CancellationToken);
        using var manager = h.Manager([vulkan]);

        Assert.Equal(ModelInstallState.Installed, manager.GetStatus(ModelCatalog.LlamaCppVulkanId).State);
        await manager.InstallAsync(ModelCatalog.LlamaCppVulkanId, TestContext.Current.CancellationToken);
    }
}
