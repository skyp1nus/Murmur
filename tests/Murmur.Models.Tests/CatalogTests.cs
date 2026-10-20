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
        // The GPU builds of the runtime are installable but unlisted: the UI list and the AI bundle stay as they were.
        var items = ModelCatalog.Create(Hosts, ModelPlatform.Linux).Where(i => i.Listed).ToList();

        Assert.Equal(
            ["parakeet-tdt-0.6b-v3-int8", "silero-vad", "llama-cpp-b11716", "embeddinggemma-300m-q8", "gemma-4-e4b-q4", "gemma-4-26b-a4b-q4"],
            items.Select(i => i.Id));
        Assert.Equal([ModelKind.Asr, ModelKind.Vad, ModelKind.LlmRuntime, ModelKind.Embedding, ModelKind.Chat, ModelKind.Chat], items.Select(i => i.Info.Kind));
        Assert.Equal([true, true, false, false, false, false], items.Select(i => i.Info.Required));
        Assert.All(items, i => Assert.True(i.Info.SizeBytes > 0));
    }

    [Fact]
    public void Parakeet_FilesMatchWhatModelLocatorChecks()
    {
        var parakeet = ModelCatalog.Create(Hosts, ModelPlatform.Linux).Single(i => i.Id == ModelCatalog.ParakeetId);

        Assert.Equal(ModelLocator.ParakeetDirectoryName, parakeet.TargetPath);
        Assert.True(parakeet.IsDirectory);
        Assert.Equal(ModelLocator.ParakeetFiles, parakeet.Files.Select(f => f.RelativePath));
        Assert.Equal(
            "http://hf.test/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/2bda32ec70b097a55adaa07d9a7173915b43cc78/encoder.int8.onnx",
            parakeet.Files[0].Url);
    }

    [Fact]
    public void ARepositoryWithARevision_IsDownloadedFromThatCommit_TheOthersFromMain()
    {
        var revisions = new Dictionary<string, string> { ["ggml-org/embeddinggemma-300M-GGUF"] = "0123456789abcdef0123456789abcdef01234567" };
        var items = ModelCatalog.Create(Hosts with { Revisions = revisions }, ModelPlatform.Linux);

        var embed = items.Single(i => i.Id == ModelCatalog.EmbeddingId).Files.Single();
        Assert.Equal("http://hf.test/ggml-org/embeddinggemma-300M-GGUF/resolve/0123456789abcdef0123456789abcdef01234567/embeddinggemma-300M-Q8_0.gguf", embed.Url);
        Assert.Equal(
            "http://hf.test/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main/encoder.int8.onnx",
            items.Single(i => i.Id == ModelCatalog.ParakeetId).Files[0].Url);
    }

    [Fact]
    public void EveryRevisionInTheCatalog_IsACommitSha()
    {
        Assert.All(ModelCatalog.HuggingFaceRevisions.Values, v => Assert.Matches("^[0-9a-f]{40}$", v));
    }

    [Fact]
    public void EveryHuggingFaceRepositoryOfTheCatalog_IsDownloadedFromAPinnedCommit()
    {
        Assert.Equal(
            [
                "bartowski/google_gemma-4-26B-A4B-it-GGUF",
                "bartowski/google_gemma-4-E4B-it-GGUF",
                "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8",
                "ggml-org/embeddinggemma-300M-GGUF",
                "unsloth/gemma-4-E4B-it-GGUF",
            ],
            ModelCatalog.HuggingFaceRevisions.Keys.Order(StringComparer.Ordinal));
        var urls = ModelCatalog.Create(Hosts, ModelPlatform.Linux).SelectMany(i => i.Files).SelectMany(f => f.Sources).Select(m => m.Url).Where(u => u.StartsWith("http://hf.test/", StringComparison.Ordinal));
        Assert.All(urls, u => Assert.DoesNotContain("/resolve/main/", u));
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
        var windows = ModelCatalog.Create(Hosts, ModelPlatform.Windows).Single(i => i.Info.Kind == ModelKind.LlmRuntime && i.Listed);
        var linux = ModelCatalog.Create(Hosts, ModelPlatform.Linux).Single(i => i.Info.Kind == ModelKind.LlmRuntime && i.Listed);

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
    public void GgufItems_ListTheirMirrorsInOrderWithTheCanonicalLocalName()
    {
        var items = ModelCatalog.Create(Hosts, ModelPlatform.Linux);

        var embed = items.Single(i => i.Id == ModelCatalog.EmbeddingId);
        Assert.Equal("gguf/embeddinggemma-300M-Q8_0.gguf", embed.TargetPath);
        Assert.Equal(
            ["http://hf.test/ggml-org/embeddinggemma-300M-GGUF/resolve/0f741b5a6585bd53aeb15cd1372c56f2a0f65e12/embeddinggemma-300M-Q8_0.gguf"],
            embed.Files.Single().Sources.Select(m => m.Url));

        var chat = items.Single(i => i.Id == ModelCatalog.ChatE4BId);
        Assert.Equal("gguf/gemma-4-E4B-it-Q4_K_M.gguf", chat.TargetPath);
        Assert.Equal("gemma-4-E4B-it-Q4_K_M.gguf", chat.Files.Single().RelativePath);
        Assert.Equal(
            [
                "http://hf.test/unsloth/gemma-4-E4B-it-GGUF/resolve/bfc15c382204943c3a8fff0c750b94ae2364d7a3/gemma-4-E4B-it-Q4_K_M.gguf",
                "http://hf.test/bartowski/google_gemma-4-E4B-it-GGUF/resolve/029e94146666900b08caf49a3b47b413dfa8ec66/google_gemma-4-E4B-it-Q4_K_M.gguf",
            ],
            chat.Files.Single().Sources.Select(m => m.Url));
        Assert.Equal(["unsloth/gemma-4-E4B-it-GGUF", "bartowski/google_gemma-4-E4B-it-GGUF"], chat.Files.Single().Sources.Select(m => m.Repo));
        Assert.Equal("google_gemma-4-E4B-it-Q4_K_M.gguf", chat.Files.Single().Sources[1].FileName);

        var big = items.Single(i => i.Id == ModelCatalog.Chat26BId);
        Assert.Equal(
            ["http://hf.test/bartowski/google_gemma-4-26B-A4B-it-GGUF/resolve/10f3b41bcf8d3047f4e136e7197ffc2dd1654c9d/google_gemma-4-26B-A4B-it-Q4_K_M.gguf"],
            big.Files.Single().Sources.Select(m => m.Url));
    }

    [Fact]
    public void SingleSourceItems_HaveExactlyOneSource()
    {
        var items = ModelCatalog.Create(Hosts, ModelPlatform.Linux);

        foreach (var id in new[] { ModelCatalog.ParakeetId, ModelCatalog.SileroVadId, ModelCatalog.LlamaCppId })
        {
            Assert.All(items.Single(i => i.Id == id).Files, f => Assert.Single(f.Sources));
        }
    }

    [Fact]
    public void PinnedFiles_CarryTheHashesAndSizesOfTheFirstRealRun()
    {
        var items = ModelCatalog.Create(Hosts, ModelPlatform.Linux);

        var parakeet = items.Single(i => i.Id == ModelCatalog.ParakeetId).Files.ToDictionary(f => f.RelativePath);
        Assert.Equal(("acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247", 652_184_281L), (parakeet["encoder.int8.onnx"].Sha256, parakeet["encoder.int8.onnx"].SizeBytes));
        Assert.Equal(("179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e", 11_845_275L), (parakeet["decoder.int8.onnx"].Sha256, parakeet["decoder.int8.onnx"].SizeBytes));
        Assert.Equal(("3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3", 6_355_277L), (parakeet["joiner.int8.onnx"].Sha256, parakeet["joiner.int8.onnx"].SizeBytes));
        Assert.Equal(("d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d", 93_939L), (parakeet["tokens.txt"].Sha256, parakeet["tokens.txt"].SizeBytes));

        var vad = items.Single(i => i.Id == ModelCatalog.SileroVadId).Files.Single();
        Assert.Equal(("9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6", 643_854L), (vad.Sha256, vad.SizeBytes));

        var llama = items.Single(i => i.Id == ModelCatalog.LlamaCppId).Files.Single();
        Assert.Equal(("8c08b19bee40d118b803d58fd8f6633ba311ce3c7f1f0138262e636f02aa11f0", 17_968_449L), (llama.Sha256, llama.SizeBytes));

        var embed = items.Single(i => i.Id == ModelCatalog.EmbeddingId).Files.Single();
        Assert.Equal(("b5ce9d77a3fc4b3b39ccb5643c36777911cc4eb46a66962eadfa3f5f60490d63", 333_590_944L), (embed.Sha256, embed.SizeBytes));

        // Each source of a chat model carries its own pin: the same quantisation from another publisher is another file.
        var e4b = items.Single(i => i.Id == ModelCatalog.ChatE4BId).Files.Single().Sources;
        Assert.Equal(("85a896a047553e842f25297ee5b031d64ff30147d9c4af17b1e4b394cd1fab87", 4_977_171_584L), (e4b[0].Sha256, e4b[0].SizeBytes));
        Assert.Equal(("d35a3aa7a6d47de237fd488bb6f7f3efd62908d828f85a63da0ee93acdbefe9f", 5_405_170_144L), (e4b[1].Sha256, e4b[1].SizeBytes));
        var big = items.Single(i => i.Id == ModelCatalog.Chat26BId).Files.Single();
        Assert.Equal(("a07f72221e8e3f77455ab0d7f7652d01a9f63c262b954aa6932a53275a0e895a", 17_035_039_872L), (big.Sha256, big.SizeBytes));
    }

    [Fact]
    public void DisplaySizes_AreTheSizeOfTheFirstSource()
    {
        var items = ModelCatalog.Create(Hosts, ModelPlatform.Linux);

        Assert.All(
            items.Where(i => i.Info.Kind is ModelKind.Chat or ModelKind.Embedding),
            i => Assert.Equal(i.Files.Single().SizeBytes, i.Info.SizeBytes));
    }

    [Fact]
    public void TheWindowsRuntime_IsPinnedLikeTheLinuxOne()
    {
        var windows = ModelCatalog.Create(Hosts, ModelPlatform.Windows).Single(i => i.Id == ModelCatalog.LlamaCppId).Files.Single();

        Assert.Equal(("79b143075ab87fb5aff6f8f8ca80500a984f4120b5199fa9f9b70b284f17428e", 19_662_702L), (windows.Sha256, windows.SizeBytes));
    }

    [Fact]
    public void GpuRuntimes_OnWindows_AreTheVulkanAndCudaBuildsOfTheSameRelease()
    {
        var items = ModelCatalog.Create(Hosts, ModelPlatform.Windows);
        var release = $"http://gh.test/ggml-org/llama.cpp/releases/download/{ModelCatalog.LlamaCppTag}/";

        var vulkan = items.Single(i => i.Id == ModelCatalog.LlamaCppVulkanId);
        Assert.Equal("llama-cpp-vulkan-" + ModelCatalog.LlamaCppTag, vulkan.Id);
        Assert.Equal(ModelKind.LlmRuntime, vulkan.Info.Kind);
        Assert.False(vulkan.Info.Required);
        Assert.False(vulkan.Listed);
        Assert.Equal(ModelPackaging.Zip, vulkan.Packaging);
        Assert.Equal("llama-server.exe", vulkan.ExecutableName);
        Assert.Equal("llama.cpp-vulkan", vulkan.TargetPath);
        Assert.True(vulkan.IsDirectory);
        Assert.Equal([release + "llama-b11716-bin-win-vulkan-x64.zip"], vulkan.Files.Select(f => f.Url));

        var cuda = items.Single(i => i.Id == ModelCatalog.LlamaCppCudaId);
        Assert.False(cuda.Listed);
        Assert.Equal(ModelPackaging.Zip, cuda.Packaging);
        Assert.Equal("llama.cpp-cuda", cuda.TargetPath);

        // The CUDA runtime DLLs are a second archive of the same item, extracted next to the server.
        Assert.Equal(
            [release + "llama-b11716-bin-win-cuda-12.4-x64.zip", release + "cudart-llama-bin-win-cuda-12.4-x64.zip"],
            cuda.Files.Select(f => f.Url));
        Assert.Equal(
            ["llama-b11716-bin-win-cuda-12.4-x64.zip", "cudart-llama-bin-win-cuda-12.4-x64.zip"],
            cuda.Files.Select(f => f.RelativePath));
    }

    [Fact]
    public void GpuRuntimes_OnLinux_AreVulkanOnly()
    {
        var items = ModelCatalog.Create(Hosts, ModelPlatform.Linux);

        var vulkan = items.Single(i => i.Id == ModelCatalog.LlamaCppVulkanId);
        Assert.Equal(ModelPackaging.TarGz, vulkan.Packaging);
        Assert.Equal("llama-server", vulkan.ExecutableName);
        Assert.Equal("llama.cpp-vulkan", vulkan.TargetPath);
        Assert.Equal(
            "http://gh.test/ggml-org/llama.cpp/releases/download/b11716/llama-b11716-bin-ubuntu-vulkan-x64.tar.gz",
            vulkan.Files.Single().Url);
        Assert.DoesNotContain(items, i => i.Id == ModelCatalog.LlamaCppCudaId);
        Assert.DoesNotContain(ModelCatalog.Create(Hosts, ModelPlatform.Other), i => i.Info.Kind == ModelKind.LlmRuntime);
    }

    [Fact]
    public void GpuRuntimes_HaveTheirOwnFolderSoTheCpuBuildStaysIntact()
    {
        var targets = ModelCatalog.Create(Hosts, ModelPlatform.Windows).Where(i => i.Info.Kind == ModelKind.LlmRuntime).Select(i => i.TargetPath).ToList();

        Assert.Equal(["llama.cpp", "llama.cpp-vulkan", "llama.cpp-cuda"], targets);
        Assert.Equal(targets.Count, targets.Distinct().Count());
    }

    [Theory]
    [InlineData(ModelPlatform.Windows)]
    [InlineData(ModelPlatform.Linux)]
    public async Task GpuRuntimes_AreEitherFullyPinned_OrTheManagerRefusesToInstallThem(ModelPlatform platform)
    {
        // The hashes come from `ModelFetch --resolve-pins` (GitHub publishes the digest of every release asset). Until they are
        // pasted into the catalog the items fail closed: a binary that will be run is never installed unverified.
        var items = ModelCatalog.Create(Hosts, platform).Where(i => !i.Listed).ToList();
        Assert.NotEmpty(items);
        using var manager = new ModelManager(new ModelManagerOptions { ModelsDirectory = Path.Combine(Path.GetTempPath(), "murmur-pin-" + Guid.NewGuid().ToString("N")), Catalog = [.. items] });

        foreach (var item in items)
        {
            Assert.True(item.RequirePins);
            var sources = item.Files.SelectMany(f => f.Sources).ToList();
            if (sources.All(m => m.Sha256 is not null && m.SizeBytes is not null))
            {
                Assert.Null(item.UnpinnedReason);
                Assert.All(sources, m => Assert.Matches("^[0-9a-f]{64}$", m.Sha256!));
            }
            else
            {
                var ex = await Assert.ThrowsAsync<ModelInstallException>(() => manager.InstallAsync(item.Id, TestContext.Current.CancellationToken));
                Assert.Contains("not pinned yet", ex.Message, StringComparison.Ordinal);
                var status = manager.GetStatus(item.Id);
                Assert.Equal(ModelInstallState.Failed, status.State);
                Assert.Contains("not pinned yet", status.Error, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void GpuRuntimes_BuiltWithoutPins_DoNotRequireThem()
    {
        var items = ModelCatalog.Create(Hosts with { PinHashes = false }, ModelPlatform.Windows).Where(i => !i.Listed);

        Assert.All(items, i =>
        {
            Assert.False(i.RequirePins);
            Assert.Null(i.UnpinnedReason);
        });
    }

    [Fact]
    public void ManagerCatalog_LeavesTheGpuRuntimesOut_ButKnowsTheirIds()
    {
        using var manager = new ModelManager(new ModelManagerOptions { ModelsDirectory = Path.Combine(Path.GetTempPath(), "murmur-cat-" + Guid.NewGuid().ToString("N")), Catalog = ModelCatalog.Create(Hosts, ModelPlatform.Windows) });

        Assert.DoesNotContain(manager.Catalog, m => m.Id is ModelCatalog.LlamaCppVulkanId or ModelCatalog.LlamaCppCudaId);
        Assert.Equal(1, manager.Catalog.Count(m => m.Kind == ModelKind.LlmRuntime));
        Assert.Contains(manager.Items, i => i.Id == ModelCatalog.LlamaCppVulkanId);

        // Status, install and remove work by id for an unlisted item, so the Settings control can drive them.
        var status = manager.GetStatus(ModelCatalog.LlamaCppVulkanId);
        Assert.Equal(ModelInstallState.Failed, status.State); // unavailable until the pins are in the catalog (fail closed)
        Assert.Contains("not pinned yet", status.Error, StringComparison.Ordinal);
        Assert.Equal(ModelKind.LlmRuntime, status.Model.Kind);
        Assert.True(status.Model.SizeBytes > 0);
    }

    [Fact]
    public void WithoutPins_NothingIsPinned()
    {
        var items = ModelCatalog.Create(Hosts with { PinHashes = false }, ModelPlatform.Linux);

        Assert.All(items.SelectMany(i => i.Files).SelectMany(f => f.Sources), m =>
        {
            Assert.Null(m.Sha256);
            Assert.Null(m.SizeBytes);
        });
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
