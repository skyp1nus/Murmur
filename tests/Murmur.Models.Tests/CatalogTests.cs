using Murmur.Core.Models;
using Murmur.Core.Transcription;
using Murmur.Models.Tests.Support;

namespace Murmur.Models.Tests;

public sealed class CatalogTests
{
    private static readonly ModelCatalogSources Hosts = new("http://hf.test", "http://gh.test");

    [Fact]
    public void Catalog_ListsTheSixItemsRequiredFirst()
    {
        var items = ModelCatalog.Create(Hosts, ModelPlatform.Linux);

        Assert.Equal(
            ["parakeet-tdt-0.6b-v3-int8", "silero-vad", "llama-cpp-b11716", "embeddinggemma-300m-q8", "gemma-4-e4b-q4", "gemma-4-26b-a4b-q4"],
            items.Select(i => i.Id));
        Assert.Equal([ModelKind.Asr, ModelKind.Vad, ModelKind.LlmRuntime, ModelKind.Embedding, ModelKind.Chat, ModelKind.Chat], items.Select(i => i.Info.Kind));
        Assert.Equal([true, true, false, false, false, false], items.Select(i => i.Info.Required));
        Assert.All(items, i => Assert.True(i.Info.SizeBytes > 0));
        Assert.All(items, i => Assert.Null(i.Files.FirstOrDefault(f => f.Sha256 is not null))); // not pinned yet
    }

    [Fact]
    public void Parakeet_FilesMatchWhatModelLocatorChecks()
    {
        var parakeet = ModelCatalog.Create(Hosts, ModelPlatform.Linux).Single(i => i.Id == ModelCatalog.ParakeetId);

        Assert.Equal(ModelLocator.ParakeetDirectoryName, parakeet.TargetPath);
        Assert.True(parakeet.IsDirectory);
        Assert.Equal(ModelLocator.ParakeetFiles, parakeet.Files.Select(f => f.RelativePath));
        Assert.Equal(
            "http://hf.test/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main/encoder.int8.onnx",
            parakeet.Files[0].Url);
    }

    [Fact]
    public void Vad_IsASingleFileInTheModelsRoot()
    {
        var vad = ModelCatalog.Create(Hosts, ModelPlatform.Linux).Single(i => i.Id == ModelCatalog.SileroVadId);

        Assert.Equal(ModelLocator.VadFileName, vad.TargetPath);
        Assert.False(vad.IsDirectory);
        Assert.Equal("http://gh.test/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx", vad.Files.Single().Url);
    }

    [Fact]
    public void LlamaRuntime_IsAZipOnWindowsAndATarballOnLinux()
    {
        var windows = ModelCatalog.Create(Hosts, ModelPlatform.Windows).Single(i => i.Info.Kind == ModelKind.LlmRuntime);
        var linux = ModelCatalog.Create(Hosts, ModelPlatform.Linux).Single(i => i.Info.Kind == ModelKind.LlmRuntime);

        Assert.Equal(ModelPackaging.Zip, windows.Packaging);
        Assert.Equal("llama-server.exe", windows.ExecutableName);
        Assert.Equal("http://gh.test/ggml-org/llama.cpp/releases/download/b11716/llama-b11716-bin-win-cpu-x64.zip", windows.Files.Single().Url);
        Assert.Equal(ModelPackaging.TarGz, linux.Packaging);
        Assert.Equal("llama-server", linux.ExecutableName);
        Assert.Equal("http://gh.test/ggml-org/llama.cpp/releases/download/b11716/llama-b11716-bin-ubuntu-x64.tar.gz", linux.Files.Single().Url);
        Assert.Equal("llama.cpp", linux.TargetPath);
        Assert.True(linux.IsDirectory);
    }

    [Fact]
    public void LlamaRuntime_IsAbsentOnOtherPlatforms()
    {
        var items = ModelCatalog.Create(Hosts, ModelPlatform.Other);

        Assert.DoesNotContain(items, i => i.Info.Kind == ModelKind.LlmRuntime);
        Assert.Equal(5, items.Count);
    }

    [Fact]
    public void GgufItems_PointAtTheHuggingFaceFilesTheCiUsed()
    {
        var items = ModelCatalog.Create(Hosts, ModelPlatform.Linux);

        var embed = items.Single(i => i.Id == ModelCatalog.EmbeddingId);
        var chat = items.Single(i => i.Id == ModelCatalog.ChatE4BId);
        var big = items.Single(i => i.Id == ModelCatalog.Chat26BId);
        Assert.Equal("gguf/embeddinggemma-300M-Q8_0.gguf", embed.TargetPath);
        Assert.Equal("http://hf.test/ggml-org/embeddinggemma-300M-GGUF/resolve/main/embeddinggemma-300M-Q8_0.gguf", embed.Files.Single().Url);
        Assert.Equal("gguf/gemma-4-E4B-it-Q4_K_M.gguf", chat.TargetPath);
        Assert.Equal("ggml-org/gemma-4-E4B-it-GGUF", chat.Repo);
        Assert.Equal("http://hf.test/ggml-org/gemma-4-26B-A4B-it-GGUF/resolve/main/gemma-4-26B-A4B-it-Q4_K_M.gguf", big.Files.Single().Url);
    }

    [Fact]
    public void DefaultSources_AreTheRealHosts()
    {
        var items = ModelCatalog.ForCurrentOs();

        Assert.All(items.SelectMany(i => i.Files), f => Assert.StartsWith("https://", f.Url));
        Assert.Contains(items.SelectMany(i => i.Files), f => f.Url.StartsWith("https://huggingface.co/", StringComparison.Ordinal));
        Assert.Contains(items.SelectMany(i => i.Files), f => f.Url.StartsWith("https://github.com/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(8_000_000_000L, ModelCatalog.ChatE4BId)]
    [InlineData(16_000_000_000L, ModelCatalog.ChatE4BId)]
    [InlineData(31_000_000_000L, ModelCatalog.ChatE4BId)]
    [InlineData(32_000_000_000L, ModelCatalog.Chat26BId)]
    [InlineData(68_719_476_736L, ModelCatalog.Chat26BId)]
    public async Task RecommendedChatModel_FollowsPhysicalMemory(long memory, string expected)
    {
        await using var harness = new Harness();
        using var manager = harness.Manager(memory: () => memory);

        Assert.Equal(expected, manager.RecommendedChatModelId);
        Assert.Equal(ModelKind.Chat, manager.Catalog.Single(i => i.Id == manager.RecommendedChatModelId).Kind);
    }

    [Fact]
    public async Task Manager_ExposesTheCatalogAndDirectory()
    {
        await using var harness = new Harness();
        using var manager = harness.Manager();

        Assert.Equal(harness.Models, manager.ModelsDirectory);
        Assert.Equal(6, manager.Catalog.Count);
        Assert.True(manager.AvailableFreeSpaceBytes > 0); // the default probe works for a directory that does not exist yet
    }

    [Fact]
    public async Task Manager_RejectsDuplicateIdsAndUnknownIds()
    {
        await using var harness = new Harness();
        var item = Harness.FileItem("a", harness.Server, "/a", "gguf/a.gguf");

        Assert.Throws<ArgumentException>(() => harness.Manager([item, item]));
        using var manager = harness.Manager([item]);
        Assert.Throws<ArgumentException>(() => manager.GetStatus("nope"));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.InstallAsync("nope", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.RemoveAsync("nope", TestContext.Current.CancellationToken));
    }
}
