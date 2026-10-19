using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Screen.Interop;
using Murmur.Capture.Windows.Video.Interop;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Video;

/// <summary>
/// Windows <see cref="IVideoPlayback"/>: <see cref="MediaEngineVideoPlayer"/>s on the Media Foundation Media Engine.
/// UNVERIFIED-ON-WINDOWS until the Windows-trait tests run in CI.
/// </summary>
public sealed class WindowsVideoPlayback : IVideoPlayback
{
    private readonly Lazy<string?> _unsupportedReason = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Media Foundation starts and the Media Engine class factory can be created (checked once). Decoding itself works
    /// on every such machine: hardware through D3D11 where there is a GPU, software into WIC otherwise.
    /// </summary>
    public bool IsSupported => _unsupportedReason.Value is null;

    /// <summary>Why <see cref="IsSupported"/> is false (English, for logs); null when supported.</summary>
    public string? UnsupportedReason => _unsupportedReason.Value;

    /// <summary>A closed player; nothing native is touched until <see cref="IVideoPlayer.OpenAsync"/>.</summary>
    public IVideoPlayer CreateVideoPlayer() => new MediaEngineVideoPlayer();

    private static unsafe string? Probe()
    {
        foreach (var library in (ReadOnlySpan<string>)["mfplat.dll", "mfmediaengine.dll"])
        {
            if (!NativeLibrary.TryLoad(library, out var handle))
            {
                return $"Media Foundation is not installed ({library} missing: Windows N edition or Server without the Media Foundation feature).";
            }

            NativeLibrary.Free(handle);
        }

        try
        {
            VideoRuntime.EnsureStarted();
            var clsid = VideoConstants.ClsidMFMediaEngineClassFactory;
            var iid = VideoConstants.IidIMFMediaEngineClassFactory;
            nint factory = 0;
            var hr = VideoNativeMethods.CoCreateInstance(&clsid, 0, VideoConstants.ClsctxInprocServer, &iid, &factory);
            ComCall.Release(ref factory);
            return hr < 0 ? $"The Media Engine is not available: CoCreateInstance(MFMediaEngineClassFactory) failed with HRESULT 0x{hr:X8}." : null;
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            return $"Media Foundation could not be started: {ex.Message}";
        }
    }
}
