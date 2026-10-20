using Murmur.Core.Settings;

namespace Murmur.Capture.Windows.Ai;

/// <summary>A DXGI adapter as far as the acceleration decision needs it.</summary>
/// <param name="Name">The adapter description (e.g. <c>NVIDIA GeForce RTX 4070</c>).</param>
/// <param name="VendorId">PCI vendor id: 0x10DE NVIDIA, 0x1002 AMD, 0x8086 Intel, 0x1414 Microsoft (software).</param>
/// <param name="DedicatedVideoMemory">Dedicated VRAM in bytes; picks the biggest GPU on a multi-GPU PC.</param>
internal sealed record GpuAdapterInfo(string Name, uint VendorId, long DedicatedVideoMemory)
{
    public const uint NvidiaVendorId = 0x10DE;

    /// <summary>The Microsoft Basic Render Driver / WARP: not a GPU anybody wants to run a model on.</summary>
    public const uint MicrosoftVendorId = 0x1414;

    public bool IsSoftware => VendorId == MicrosoftVendorId;

    public bool IsNvidia => VendorId == NvidiaVendorId;
}

/// <summary>What the PC offers: the supported builds and the name of the GPU the GPU builds would use.</summary>
internal sealed record AccelerationSupport(IReadOnlyList<AiAcceleration> Supported, string? GpuName)
{
    public static AccelerationSupport CpuOnly { get; } = new([AiAcceleration.Cpu], null);

    /// <summary>
    /// <see cref="AiAcceleration.Cpu"/> always; <see cref="AiAcceleration.Vulkan"/> when a Vulkan loader is present;
    /// <see cref="AiAcceleration.Cuda"/> when a (hardware) NVIDIA adapter is present. The name is the NVIDIA adapter
    /// with the most VRAM when CUDA is offered, else the hardware adapter with the most VRAM; <c>null</c> when only
    /// the CPU build is offered or no hardware adapter is known.
    /// </summary>
    public static AccelerationSupport Decide(IReadOnlyList<GpuAdapterInfo> adapters, bool vulkanLoaderPresent)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        GpuAdapterInfo? nvidia = null;
        GpuAdapterInfo? best = null;
        foreach (var adapter in adapters)
        {
            if (adapter.IsSoftware)
            {
                continue;
            }

            if (best is null || adapter.DedicatedVideoMemory > best.DedicatedVideoMemory)
            {
                best = adapter;
            }

            if (adapter.IsNvidia && (nvidia is null || adapter.DedicatedVideoMemory > nvidia.DedicatedVideoMemory))
            {
                nvidia = adapter;
            }
        }

        var supported = new List<AiAcceleration>(3) { AiAcceleration.Cpu };
        if (vulkanLoaderPresent)
        {
            supported.Add(AiAcceleration.Vulkan);
        }

        if (nvidia is not null)
        {
            supported.Add(AiAcceleration.Cuda);
        }

        var chosen = nvidia ?? (supported.Count > 1 ? best : null);
        return new AccelerationSupport(supported, chosen is { Name.Length: > 0 } ? chosen.Name : null);
    }
}
