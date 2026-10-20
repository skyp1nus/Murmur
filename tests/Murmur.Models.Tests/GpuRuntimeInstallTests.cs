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
}
