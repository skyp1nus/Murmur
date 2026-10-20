using Murmur.Core.Intelligence;
using Murmur.Core.Settings;

namespace Murmur.Desktop;

/// <summary>
/// <see cref="IAccelerationProbe"/> for builds without GPU detection (Linux development and CI, macOS until phase 5):
/// only the CPU build is offered.
/// </summary>
internal sealed class CpuOnlyAccelerationProbe : IAccelerationProbe
{
    public IReadOnlyList<AiAcceleration> Supported { get; } = [AiAcceleration.Cpu];

    public string? GpuName => null;
}
