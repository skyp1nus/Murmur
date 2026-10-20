using Murmur.Capture.Windows.Ai;
using Murmur.Core.Settings;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// The detection rules are pure (<see cref="AccelerationSupport.Decide"/>) and always run; the tests of the real
/// DXGI / Vulkan calls only check the contract (never throws, CPU first, cached) because the CI runner has no GPU.
/// </summary>
[Trait("Category", "Windows")]
public sealed class AccelerationProbeTests
{
    private static GpuAdapterInfo Nvidia(string name = "NVIDIA GeForce RTX 4070", long vram = 12L << 30) => new(name, 0x10DE, vram);

    private static GpuAdapterInfo Amd(string name = "AMD Radeon RX 7800 XT", long vram = 16L << 30) => new(name, 0x1002, vram);

    private static GpuAdapterInfo Intel(string name = "Intel(R) Arc(TM) A770", long vram = 8L << 30) => new(name, 0x8086, vram);

    private static GpuAdapterInfo Warp() => new("Microsoft Basic Render Driver", 0x1414, 0);

    [Fact]
    public void NoGpuAndNoLoader_IsCpuOnly()
    {
        var support = AccelerationSupport.Decide([], vulkanLoaderPresent: false);

        Assert.Equal([AiAcceleration.Cpu], support.Supported);
        Assert.Null(support.GpuName);
    }

    [Fact]
    public void TheSoftwareAdapterIsNotAGpu()
    {
        var support = AccelerationSupport.Decide([Warp()], vulkanLoaderPresent: false);

        Assert.Equal([AiAcceleration.Cpu], support.Supported);
        Assert.Null(support.GpuName);
    }

    [Fact]
    public void Nvidia_OffersCudaWithoutAVulkanLoader()
    {
        var support = AccelerationSupport.Decide([Nvidia(), Warp()], vulkanLoaderPresent: false);

        Assert.Equal([AiAcceleration.Cpu, AiAcceleration.Cuda], support.Supported);
        Assert.Equal("NVIDIA GeForce RTX 4070", support.GpuName);
    }

    [Fact]
    public void Nvidia_WithTheLoader_OffersVulkanAndCuda_CpuFirst()
    {
        var support = AccelerationSupport.Decide([Nvidia()], vulkanLoaderPresent: true);

        Assert.Equal([AiAcceleration.Cpu, AiAcceleration.Vulkan, AiAcceleration.Cuda], support.Supported);
    }

    [Fact]
    public void Amd_WithTheLoader_OffersVulkanOnly()
    {
        var support = AccelerationSupport.Decide([Amd()], vulkanLoaderPresent: true);

        Assert.Equal([AiAcceleration.Cpu, AiAcceleration.Vulkan], support.Supported);
        Assert.Equal("AMD Radeon RX 7800 XT", support.GpuName);
    }

    [Fact]
    public void AGpuWithoutTheLoaderAndWithoutNvidia_OffersNothingToChooseFrom()
    {
        var support = AccelerationSupport.Decide([Amd()], vulkanLoaderPresent: false);

        Assert.Equal([AiAcceleration.Cpu], support.Supported);
        Assert.Null(support.GpuName);
    }

    [Fact]
    public void ALoaderWithoutAnyHardwareAdapter_StillOffersVulkan_WithoutAName()
    {
        var support = AccelerationSupport.Decide([Warp()], vulkanLoaderPresent: true);

        Assert.Equal([AiAcceleration.Cpu, AiAcceleration.Vulkan], support.Supported);
        Assert.Null(support.GpuName);
    }

    [Fact]
    public void OnAHybridLaptop_TheNvidiaCardIsNamed_NotTheIntegratedOne()
    {
        var support = AccelerationSupport.Decide([Intel("Intel(R) UHD Graphics", 128L << 20), Nvidia("NVIDIA GeForce RTX 3050 Laptop GPU", 4L << 30)], vulkanLoaderPresent: true);

        Assert.Equal("NVIDIA GeForce RTX 3050 Laptop GPU", support.GpuName);
    }

    [Fact]
    public void WithoutNvidia_TheAdapterWithTheMostVideoMemoryIsNamed()
    {
        var support = AccelerationSupport.Decide([Intel("Intel(R) UHD Graphics", 128L << 20), Amd("AMD Radeon 780M", 512L << 20), Amd("AMD Radeon RX 6600", 8L << 30)], vulkanLoaderPresent: true);

        Assert.Equal("AMD Radeon RX 6600", support.GpuName);
    }

    [Fact]
    public void WithTwoNvidiaCards_TheBiggerOneIsNamed()
    {
        var support = AccelerationSupport.Decide([Nvidia("NVIDIA GeForce GTX 1060", 6L << 30), Nvidia("NVIDIA GeForce RTX 4090", 24L << 30)], vulkanLoaderPresent: false);

        Assert.Equal("NVIDIA GeForce RTX 4090", support.GpuName);
    }

    [Fact]
    public void Probe_DetectsOnce_AndCachesTheAnswer()
    {
        var calls = 0;
        var probe = new WindowsAccelerationProbe(() =>
        {
            calls++;
            return AccelerationSupport.Decide([Nvidia()], vulkanLoaderPresent: true);
        });

        Assert.Equal(0, calls); // lazy: nothing is detected before the first read
        var first = probe.Supported;
        _ = probe.GpuName;

        Assert.Same(first, probe.Supported);
        Assert.Equal(1, calls);
        Assert.Equal("NVIDIA GeForce RTX 4070", probe.GpuName);
    }

    [Fact]
    public void Probe_WhoseDetectionThrows_OffersTheCpuBuildInstead()
    {
        var probe = new WindowsAccelerationProbe(() => throw new InvalidOperationException("driver exploded"));

        Assert.Equal([AiAcceleration.Cpu], probe.Supported);
        Assert.Null(probe.GpuName);
    }

    [Fact]
    public void RealProbe_NeverThrows_ListsCpuFirst_AndIsCached()
    {
        var probe = new WindowsAccelerationProbe();

        var supported = probe.Supported;

        Assert.Equal(AiAcceleration.Cpu, supported[0]);
        Assert.Equal(supported.Count, supported.Distinct().Count());
        Assert.Same(supported, probe.Supported);
        Assert.True(probe.GpuName is null || probe.GpuName.Length > 0);

        // A name only comes with a GPU build that could use it.
        if (supported.Count == 1)
        {
            Assert.Null(probe.GpuName);
        }
    }

    [Fact]
    public void RealDxgiEnumeration_NeverThrows_AndNamesEveryAdapter()
    {
        var adapters = DxgiAdapters.Enumerate();

        Assert.All(adapters, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
        Assert.All(adapters, a => Assert.True(a.DedicatedVideoMemory >= 0));
    }

    [Fact]
    public void RealVulkanCheck_NeverThrows()
    {
        var first = VulkanLoader.IsPresent();

        Assert.Equal(first, VulkanLoader.IsPresent());
    }
}
