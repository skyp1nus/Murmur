using Murmur.Core.Intelligence;
using Murmur.Core.Settings;

namespace Murmur.Intelligence.Tests;

/// <summary>
/// The real GPU build of llama-server against the real machine. Skipped unless <c>MURMUR_GPU_SMOKE_MODELS_DIR</c> points at a
/// models folder with <c>llama.cpp/</c> (CPU), <c>llama.cpp-vulkan/</c> (or <c>-cuda/</c>) and <c>gguf/embeddinggemma-300M-Q8_0.gguf</c>
/// (any small GGUF serves as the "chat" model here). On the CI runner without a GPU (WARP) set <c>MURMUR_GPU_SMOKE_EXPECT=none</c>:
/// the GPU build must end in <see cref="AccelerationFallback.StartFailed"/> (not in a healthy CPU-only server) and the CPU build must serve.
/// </summary>
[Trait("Category", "Windows")]
public sealed class GpuRuntimeSmokeTests
{
    [Theory]
    [InlineData(AiAcceleration.Vulkan)]
    [InlineData(AiAcceleration.Cuda)]
    public async Task TheGpuBuild_OnThisMachine_EitherRunsOnTheGpuOrFallsBackToTheCpu(AiAcceleration acceleration)
    {
        var models = Environment.GetEnvironmentVariable("MURMUR_GPU_SMOKE_MODELS_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(models), "MURMUR_GPU_SMOKE_MODELS_DIR is not set.");
        Assert.SkipUnless(File.Exists(LocalLlmOptions.DefaultServerPath(models!, acceleration)), $"The {acceleration} build is not installed.");
        var expectNoGpu = Environment.GetEnvironmentVariable("MURMUR_GPU_SMOKE_EXPECT") == "none";
        var model = Path.Combine(models!, "gguf", LocalLlmOptions.DefaultEmbeddingModelFileName);
        var options = LocalLlmOptions.FromEnvironment(name => name == "MURMUR_MODELS_DIR" ? models : null) with
        {
            Acceleration = acceleration,
            AcceleratedServerPath = LocalLlmOptions.DefaultServerPath(models!, acceleration),
            ChatModelPath = model,
            EmbeddingModelPath = model,
            StartupTimeout = TimeSpan.FromMinutes(2),
        };
        await using var llm = new LocalLlm(options, () => false);

        await llm.GetChatClientAsync(TestContext.Current.CancellationToken);

        var status = llm.Current;
        if (expectNoGpu)
        {
            Assert.Equal(AccelerationFallback.StartFailed, status.Fallback);
            Assert.Equal(AiAcceleration.Cpu, status.Active);
            Assert.False(string.IsNullOrWhiteSpace(status.Detail));
        }
        else
        {
            // A real GPU: it either runs, or the status says why not.
            Assert.True(status.Fallback == AccelerationFallback.None || !string.IsNullOrWhiteSpace(status.Detail));
        }

        Assert.Equal(LlmStatus.Ready, llm.Status); // some server serves in both cases
    }
}
