using System.Runtime.InteropServices;

namespace Murmur.Capture.Windows.Ai;

/// <summary>Whether the Vulkan loader (<c>vulkan-1.dll</c>, installed with the GPU driver) can be loaded.</summary>
internal static class VulkanLoader
{
    public const string FileName = "vulkan-1.dll";

    /// <summary>
    /// Loads the DLL from the system folder only (never from the app folder or the PATH) and releases it again.
    /// A loader without a Vulkan driver behind it still loads; <c>llama-server</c> then fails to start and the fallback takes over.
    /// </summary>
    public static bool IsPresent()
    {
        try
        {
            var path = Path.Combine(Environment.SystemDirectory, FileName);
            if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out var handle))
            {
                return false;
            }

            NativeLibrary.Free(handle);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException)
        {
            return false;
        }
    }
}
