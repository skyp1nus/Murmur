using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Capture.Windows.Screen.Interop;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Murmur.Capture.Windows.Screen;

/// <summary>Process-wide Media Foundation start-up (MFStartup once, never shut down: other MF users may share it).</summary>
internal static class MediaFoundationRuntime
{
    private static readonly Lock Gate = new();
    private static bool _started;

    /// <exception cref="COMException">MFStartup failed (e.g. Windows N / Server without the Media Foundation feature).</exception>
    /// <exception cref="DllNotFoundException">mfplat.dll is missing.</exception>
    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_started)
            {
                return;
            }

            ComHelpers.Check(ScreenNativeMethods.MFStartup(ScreenConstants.MfVersion, ScreenConstants.MfStartupLite), "MFStartup");
            _started = true;
        }
    }
}

/// <summary>
/// The D3D11 device shared by Windows.Graphics.Capture, the GPU frame copy and the Media Foundation encoder: BGRA and
/// video support, multithread-protected immediate context (the FrameArrived thread and the encoder thread both submit
/// work), an <c>IMFDXGIDeviceManager</c> for the sink writer and the sample allocator, and the WinRT
/// <see cref="IDirect3DDevice"/> wrapper for the frame pool. Hardware first, WARP as fallback; video support is
/// dropped only as a last resort (then hardware encoding and GPU scaling are unavailable).
/// UNVERIFIED-ON-WINDOWS until the Windows-trait tests run in CI.
/// </summary>
internal sealed unsafe class GpuDevice : IDisposable
{
    private nint _device;
    private nint _context;
    private nint _deviceManager;

    private GpuDevice(nint device, nint context, nint deviceManager, IDirect3DDevice winRtDevice, string description, bool videoSupport)
    {
        _device = device;
        _context = context;
        _deviceManager = deviceManager;
        WinRtDevice = winRtDevice;
        Description = description;
        HasVideoSupport = videoSupport;
    }

    /// <summary>ID3D11Device (owned).</summary>
    public nint Device => _device;

    /// <summary>The immediate ID3D11DeviceContext (owned, multithread-protected).</summary>
    public nint Context => _context;

    /// <summary>IMFDXGIDeviceManager bound to <see cref="Device"/> (owned).</summary>
    public nint DeviceManager => _deviceManager;

    public IDirect3DDevice WinRtDevice { get; }

    /// <summary>"hardware" or "WARP", plus the feature level.</summary>
    public string Description { get; }

    public bool HasVideoSupport { get; }

    /// <summary>The device was removed or reset (driver update, TDR, GPU unplugged) and must be recreated.</summary>
    public bool IsLost => _device == 0 || ID3D11Device.GetDeviceRemovedReason(_device) < 0;

    public static GpuDevice Create()
    {
        const uint Bgra = ScreenConstants.D3D11CreateDeviceBgraSupport;
        const uint Video = ScreenConstants.D3D11CreateDeviceVideoSupport;
        ReadOnlySpan<(int Driver, uint Flags)> attempts =
        [
            (ScreenConstants.D3DDriverTypeHardware, Bgra | Video),
            (ScreenConstants.D3DDriverTypeWarp, Bgra | Video),
            (ScreenConstants.D3DDriverTypeHardware, Bgra),
            (ScreenConstants.D3DDriverTypeWarp, Bgra),
        ];

        var levels = stackalloc int[]
        {
            ScreenConstants.D3DFeatureLevel11_1,
            ScreenConstants.D3DFeatureLevel11_0,
            ScreenConstants.D3DFeatureLevel10_1,
            ScreenConstants.D3DFeatureLevel10_0,
        };

        var lastHr = ScreenConstants.EFail;
        foreach (var (driver, flags) in attempts)
        {
            nint device = 0, context = 0;
            int level;
            var hr = ScreenNativeMethods.D3D11CreateDevice(0, driver, 0, flags, levels, 4, ScreenConstants.D3D11SdkVersion, &device, &level, &context);
            if (hr == unchecked((int)0x80070057))
            {
                // E_INVALIDARG: a runtime without D3D_FEATURE_LEVEL_11_1 rejects the whole list; retry without it.
                hr = ScreenNativeMethods.D3D11CreateDevice(0, driver, 0, flags, levels + 1, 3, ScreenConstants.D3D11SdkVersion, &device, &level, &context);
            }

            if (hr < 0)
            {
                lastHr = hr;
                continue;
            }

            try
            {
                var description = $"{(driver == ScreenConstants.D3DDriverTypeWarp ? "WARP" : "hardware")} D3D11 FL {level >> 12}.{(level >> 8) & 0xF}"
                    + ((flags & Video) == 0 ? " without video support" : string.Empty);
                return Complete(device, context, description, (flags & Video) != 0);
            }
            catch
            {
                ComCall.Release(ref context);
                ComCall.Release(ref device);
                throw;
            }
        }

        throw new COMException($"D3D11CreateDevice failed for hardware and WARP: HRESULT 0x{lastHr:X8}", lastHr);
    }

    private static GpuDevice Complete(nint device, nint context, string description, bool videoSupport)
    {
        // The FrameArrived thread copies frames while Media Foundation encodes on its own threads.
        var multithread = ComCall.Query(context, ScreenConstants.IidID3D11Multithread);
        if (multithread != 0)
        {
            ID3D11Multithread.SetMultithreadProtected(multithread, 1);
            ComCall.Release(ref multithread);
        }

        nint manager = 0;
        uint token;
        ComHelpers.Check(ScreenNativeMethods.MFCreateDXGIDeviceManager(&token, &manager), "MFCreateDXGIDeviceManager");
        try
        {
            ComHelpers.Check(IMFDXGIDeviceManager.ResetDevice(manager, device, token), "IMFDXGIDeviceManager::ResetDevice");
            var winRtDevice = CreateWinRtDevice(device);
            return new GpuDevice(device, context, manager, winRtDevice, description, videoSupport);
        }
        catch
        {
            ComCall.Release(ref manager);
            throw;
        }
    }

    private static IDirect3DDevice CreateWinRtDevice(nint device)
    {
        var dxgi = ComCall.Query(device, ScreenConstants.IidIDXGIDevice);
        if (dxgi == 0)
        {
            throw new COMException("The D3D11 device does not expose IDXGIDevice.", ScreenConstants.EFail);
        }

        nint inspectable = 0;
        try
        {
            ComHelpers.Check(ScreenNativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgi, &inspectable), "CreateDirect3D11DeviceFromDXGIDevice");

            // FromAbi takes its own reference; ours is released below.
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            ComCall.Release(ref inspectable);
            ComCall.Release(ref dxgi);
        }
    }

    public void Dispose()
    {
        WinRtDevice.Dispose();
        ComCall.Release(ref _deviceManager);
        ComCall.Release(ref _context);
        ComCall.Release(ref _device);
    }
}
