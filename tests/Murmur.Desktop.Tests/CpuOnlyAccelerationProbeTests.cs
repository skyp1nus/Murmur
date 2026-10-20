using Murmur.Core.Settings;

namespace Murmur.Desktop.Tests;

public sealed class CpuOnlyAccelerationProbeTests
{
    [Fact]
    public void OffersTheCpuBuildOnly_AndNoGpuName()
    {
        var probe = new CpuOnlyAccelerationProbe();

        Assert.Equal([AiAcceleration.Cpu], probe.Supported);
        Assert.Null(probe.GpuName);
    }

    [Fact]
    public void TheAnswerIsStable()
    {
        var probe = new CpuOnlyAccelerationProbe();

        Assert.Same(probe.Supported, probe.Supported);
    }

    [Fact]
    public void TheCompositionRoot_OffersTheCpuOnlyProbe_OnANonWindowsBuild()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The Windows build uses the DXGI/Vulkan probe.");
        using var root = new CompositionRoot([]);

        Assert.Equal([AiAcceleration.Cpu], root.AccelerationProbe.Supported);
        Assert.Same(root.AccelerationProbe, root.AccelerationProbe);
    }
}
