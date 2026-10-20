using Murmur.Core.Settings;

namespace Murmur.Core.Intelligence;

/// <summary>
/// What AI acceleration this PC can offer, for the Settings control. The answer is detected once and cached: reading
/// the properties is cheap, never throws and never blocks on a driver call after the first read.
/// </summary>
public interface IAccelerationProbe
{
    /// <summary>
    /// The accelerations this PC supports, <see cref="AiAcceleration.Cpu"/> always first:
    /// <see cref="AiAcceleration.Vulkan"/> when a Vulkan loader is installed, <see cref="AiAcceleration.Cuda"/> when an
    /// NVIDIA GPU is present. Support means "worth offering", not "will load": a GPU server that fails to start falls back
    /// to the CPU build at run time.
    /// </summary>
    IReadOnlyList<AiAcceleration> Supported { get; }

    /// <summary>Marketing name of the GPU the GPU builds would use (e.g. <c>NVIDIA GeForce RTX 4070</c>), or <c>null</c> when there is none.</summary>
    string? GpuName { get; }
}
