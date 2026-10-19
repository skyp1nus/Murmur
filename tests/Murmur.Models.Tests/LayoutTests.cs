using Murmur.Core.Models;
using Murmur.Core.Transcription;
using Murmur.Models.Tests.Support;

namespace Murmur.Models.Tests;

/// <summary>The real catalog against the fake server: what lands on disk is what ModelLocator and AiModelPaths read.</summary>
public sealed class LayoutTests
{
    private static async Task<Dictionary<string, byte[]>> ServeCatalogAsync(Harness h, ModelPlatform platform)
    {
        var served = new Dictionary<string, byte[]>();
        var seed = 1;
        foreach (var file in ModelCatalog.Create(h.Sources, platform).SelectMany(i => i.Files))
        {
            var path = new Uri(file.Url).AbsolutePath;
            var content = file.RelativePath switch
            {
                var name when name.EndsWith(".zip", StringComparison.Ordinal) => Harness.Zip(("llama-server.exe", Harness.Bytes(2000, seed)), ("ggml.dll", Harness.Bytes(500, seed + 1))),
                var name when name.EndsWith(".tar.gz", StringComparison.Ordinal) => Harness.TarGz(("llama-server", Harness.Bytes(2000, seed)), ("libggml.so", Harness.Bytes(500, seed + 1))),
                _ => Harness.Bytes(20_000 + (seed * 1000), seed),
            };
            h.Server.Add(path, content);
            served[path] = content;
            seed++;
        }

        await Task.CompletedTask;
        return served;
    }

    [Fact]
    public async Task InstallingTheAsrItems_MakesModelLocatorReportInstalled()
    {
        await using var h = new Harness();
        var served = await ServeCatalogAsync(h, ModelPlatform.Linux);
        using var manager = h.Manager();
        var locator = new ModelLocator(h.Models);
        Assert.False(locator.IsAsrInstalled);

        await manager.InstallAsync(ModelCatalog.SileroVadId, TestContext.Current.CancellationToken);
        Assert.False(locator.IsAsrInstalled); // the VAD alone is not enough
        await manager.InstallAsync(ModelCatalog.ParakeetId, TestContext.Current.CancellationToken);

        Assert.True(locator.IsAsrInstalled);
        Assert.Empty(locator.MissingAsrFiles());
        var encoder = served["/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main/encoder.int8.onnx"];
        Assert.Equal(encoder, await File.ReadAllBytesAsync(Path.Combine(locator.ParakeetDirectory, "encoder.int8.onnx"), TestContext.Current.CancellationToken));
        Assert.Equal(ModelInstallState.Installed, manager.GetStatus(ModelCatalog.ParakeetId).State);
        Assert.Equal(ModelInstallState.Installed, manager.GetStatus(ModelCatalog.SileroVadId).State);
        Assert.Equal(ModelInstallState.NotInstalled, manager.GetStatus(ModelCatalog.EmbeddingId).State);
    }

    [Fact]
    public async Task InstallingTheAiItems_WritesTheLayoutAiModelPathsReads()
    {
        await using var h = new Harness();
        await ServeCatalogAsync(h, ModelPlatform.Linux);
        using var manager = h.Manager();

        await manager.InstallAsync(ModelCatalog.LlamaCppId, TestContext.Current.CancellationToken);
        await manager.InstallAsync(ModelCatalog.EmbeddingId, TestContext.Current.CancellationToken);
        await manager.InstallAsync(ModelCatalog.ChatE4BId, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(h.Models, "llama.cpp", "llama-server")));
        Assert.True(File.Exists(Path.Combine(h.Models, "llama.cpp", "libggml.so")));
        Assert.True(File.Exists(Path.Combine(h.Models, "gguf", "embeddinggemma-300M-Q8_0.gguf")));
        Assert.True(File.Exists(Path.Combine(h.Models, "gguf", "gemma-4-E4B-it-Q4_K_M.gguf")));
        var manifest = AiModelPathsReader.Read(Path.Combine(h.Models, "gguf", "models.json"));
        Assert.NotNull(manifest);
        Assert.Equal(Path.Combine(h.Models, "gguf", "gemma-4-E4B-it-Q4_K_M.gguf"), manifest.Chat);
        Assert.Equal(Path.Combine(h.Models, "gguf", "embeddinggemma-300M-Q8_0.gguf"), manifest.Embed);
        Assert.Equal(ModelCatalog.ChatE4BId, manager.ActiveModelId(ModelKind.Chat));
        Assert.Equal(ModelCatalog.EmbeddingId, manager.ActiveModelId(ModelKind.Embedding));
    }

    [Fact]
    public async Task RealCatalogOnWindows_ExtractsTheZip()
    {
        await using var h = new Harness();
        await ServeCatalogAsync(h, ModelPlatform.Windows);
        using var manager = h.Manager(ModelCatalog.Create(h.Sources, ModelPlatform.Windows));

        await manager.InstallAsync(ModelCatalog.LlamaCppId, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(h.Models, "llama.cpp", "llama-server.exe")));
        Assert.True(File.Exists(Path.Combine(h.Models, "llama.cpp", "ggml.dll")));
    }
}
