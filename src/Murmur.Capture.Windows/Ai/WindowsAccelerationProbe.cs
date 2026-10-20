using Murmur.Core.Intelligence;
using Murmur.Core.Settings;

namespace Murmur.Capture.Windows.Ai;

/// <summary>
/// <see cref="IAccelerationProbe"/> for Windows: <see cref="AiAcceleration.Vulkan"/> when <c>vulkan-1.dll</c> loads,
/// <see cref="AiAcceleration.Cuda"/> when a DXGI adapter has the NVIDIA vendor id (0x10DE). The detection runs once, on
/// the first read (a few milliseconds, no allocation afterwards), and the answer is cached for the life of the process.
/// </summary>
public sealed class WindowsAccelerationProbe : IAccelerationProbe
{
    private readonly Lazy<AccelerationSupport> _support;

    public WindowsAccelerationProbe()
        : this(static () => AccelerationSupport.Decide(DxgiAdapters.Enumerate(), VulkanLoader.IsPresent()))
    {
    }

    internal WindowsAccelerationProbe(Func<AccelerationSupport> detect)
    {
        ArgumentNullException.ThrowIfNull(detect);
        _support = new Lazy<AccelerationSupport>(() => SafeDetect(detect), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyList<AiAcceleration> Supported => _support.Value.Supported;

    public string? GpuName => _support.Value.GpuName;

    private static AccelerationSupport SafeDetect(Func<AccelerationSupport> detect)
    {
        try
        {
            return detect();
        }
        catch (Exception)
        {
            // The probe must never break the Settings page: whatever failed, the CPU build is always there.
            return AccelerationSupport.CpuOnly;
        }
    }
}
