using Murmur.Core.Settings;
using Murmur.Intelligence;
using Murmur.Models;

namespace Murmur.Desktop.Tests;

public sealed class AiModelPathsTests : IDisposable
{
    private readonly string _models = Path.Combine(Path.GetTempPath(), "murmur-aipaths-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_models))
        {
            Directory.Delete(_models, recursive: true);
        }
    }

    [Fact]
    public void ReadManifest_WorksWhileAnotherHandleHasTheFileOpenForWriting()
    {
        var gguf = Directory.CreateDirectory(Path.Combine(_models, AiModelPaths.GgufDirectoryName)).FullName;
        var manifest = Path.Combine(gguf, AiModelPaths.ManifestFileName);
        File.WriteAllText(manifest, """{ "chat": "/m/chat.gguf", "embed": "/m/embed.gguf" }""");

        // The model manager keeps the manifest open while it rewrites it; reading must neither fail nor block it.
        using var writer = new FileStream(manifest, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var read = AiModelPaths.ReadManifest(manifest);

        Assert.Equal("/m/chat.gguf", read?.Chat);
        Assert.Equal("/m/embed.gguf", read?.Embed);
    }

    [Fact]
    public void Resolve_FollowsTheManifestAtEveryCall()
    {
        var gguf = Directory.CreateDirectory(Path.Combine(_models, AiModelPaths.GgufDirectoryName)).FullName;
        var manifest = Path.Combine(gguf, AiModelPaths.ManifestFileName);
        File.WriteAllText(manifest, """{ "chat": "/m/e4b.gguf" }""");
        Assert.Equal("/m/e4b.gguf", AiModelPaths.Resolve(_models, _ => null).ChatModelPath);

        File.WriteAllText(manifest, """{ "chat": "/m/26b.gguf" }""");

        Assert.Equal("/m/26b.gguf", AiModelPaths.Resolve(_models, _ => null).ChatModelPath);
    }

    [Fact]
    public void Resolve_WithoutAnAcceleration_IsTheCpuBuildOnly()
    {
        var options = AiModelPaths.Resolve(_models, _ => null);

        Assert.Equal(AiAcceleration.Cpu, options.Acceleration);
        Assert.Null(options.AcceleratedServerPath);
        Assert.Equal(LocalLlmOptions.DefaultServerPath(_models), options.LlamaServerPath);
    }

    [Theory]
    [InlineData(AiAcceleration.Vulkan, "llama.cpp-vulkan")]
    [InlineData(AiAcceleration.Cuda, "llama.cpp-cuda")]
    public void Resolve_WithAGpuBuild_PointsAtItsFolderAndKeepsTheCpuBuildAsTheFallback(AiAcceleration acceleration, string folder)
    {
        var options = AiModelPaths.Resolve(_models, _ => null, acceleration);

        Assert.Equal(acceleration, options.Acceleration);
        Assert.Equal(Path.Combine(_models, folder, OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server"), options.AcceleratedServerPath);
        Assert.Equal(Path.Combine(_models, "llama.cpp", OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server"), options.LlamaServerPath);
    }

    [Fact]
    public void ServerFolders_AreTheOnesTheCatalogInstallsInto()
    {
        Assert.Equal(ModelCatalog.LlamaCppDirectoryName, LocalLlmOptions.ServerDirectoryName(AiAcceleration.Cpu));
        Assert.Equal(ModelCatalog.LlamaCppVulkanDirectoryName, LocalLlmOptions.ServerDirectoryName(AiAcceleration.Vulkan));
        Assert.Equal(ModelCatalog.LlamaCppCudaDirectoryName, LocalLlmOptions.ServerDirectoryName(AiAcceleration.Cuda));
    }

    [Fact]
    public void Resolve_ReadsTheSettingAtEveryCall_SoALiveSwitchNeedsNoRestart()
    {
        var choice = AiAcceleration.Cpu;
        Func<LocalLlmOptions> provider = () => AiModelPaths.Resolve(_models, _ => null, choice);

        Assert.Equal(AiAcceleration.Cpu, provider().Acceleration);
        choice = AiAcceleration.Vulkan;

        Assert.Equal(AiAcceleration.Vulkan, provider().Acceleration);
    }
}
