using Murmur.Core.Models;
using Murmur.Core.Settings;
using Murmur.Models;

namespace Murmur.Desktop.Tests;

/// <summary>The map the Settings uses to find the runtime item of a GPU build must name items the model manager has.</summary>
public sealed class AccelerationRuntimeIdsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "murmur-accel-ids-" + Guid.NewGuid().ToString("N"));

    public AccelerationRuntimeIdsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void EveryMappedId_ResolvesInTheModelManager_AsAnLlmRuntime()
    {
        using var manager = new ModelManager(_directory);

        var ids = CompositionRoot.AccelerationRuntimeIds(manager);

        Assert.NotEmpty(ids);
        Assert.All(ids, pair =>
        {
            Assert.NotEqual(AiAcceleration.Cpu, pair.Key);
            Assert.Equal(ModelKind.LlmRuntime, manager.GetStatus(pair.Value).Model.Kind);
        });
    }

    [Fact]
    public void TheVulkanRuntime_IsMappedOnEveryPlatformTheCatalogHasIt_AndTheCudaOneOnlyOnWindows()
    {
        using var manager = new ModelManager(_directory);

        var ids = CompositionRoot.AccelerationRuntimeIds(manager);

        Assert.Equal(ModelCatalog.LlamaCppVulkanId, ids[AiAcceleration.Vulkan]);
        Assert.Equal(OperatingSystem.IsWindows(), ids.ContainsKey(AiAcceleration.Cuda));
        if (ids.TryGetValue(AiAcceleration.Cuda, out var cuda))
        {
            Assert.Equal(ModelCatalog.LlamaCppCudaId, cuda);
        }
    }

    [Fact]
    public void TheMappedItems_AreNotListedInTheCatalog()
    {
        using var manager = new ModelManager(_directory);

        var ids = CompositionRoot.AccelerationRuntimeIds(manager);

        Assert.All(ids.Values, id => Assert.DoesNotContain(manager.Catalog, m => m.Id == id));
    }
}
