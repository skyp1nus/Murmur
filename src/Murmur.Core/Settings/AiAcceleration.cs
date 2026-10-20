namespace Murmur.Core.Settings;

/// <summary>Which build of the local AI runtime (<c>llama-server</c>) runs the chat model.</summary>
public enum AiAcceleration
{
    /// <summary>The CPU build: works everywhere, the default.</summary>
    Cpu,

    /// <summary>The Vulkan build: AMD, NVIDIA and Intel GPUs through the Vulkan loader.</summary>
    Vulkan,

    /// <summary>The CUDA build: NVIDIA GPUs only.</summary>
    Cuda,
}
