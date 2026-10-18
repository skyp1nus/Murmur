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
/// video support, multithread-protected immediate context (the FrameArrived thread, the encoder thread and Media
/// Foundation's own threads all submit work), an <c>IMFDXGIDeviceManager</c> for the sink writer and the sample
/// allocator, and the WinRT <see cref="IDirect3DDevice"/> wrapper for the frame pool. Hardware first, WARP as fallback;
/// video support is dropped only as a last resort (then hardware encoding and GPU scaling are unavailable). A
/// "hardware" device on the Microsoft Basic Render Driver (GPU-less VMs, CI runners) is WARP in disguise and is
/// reported as a <see cref="IsSoftwareRenderer"/>.
/// UNVERIFIED-ON-WINDOWS until the Windows-trait tests run in CI.
/// </summary>
internal sealed unsafe class GpuDevice : IDisposable
{
    private nint _device;
    private nint _context;
    private nint _deviceManager;

    private GpuDevice(nint device, nint context, nint deviceManager, IDirect3DDevice winRtDevice, string description, bool videoSupport, bool softwareRenderer)
    {
        _device = device;
        _context = context;
        _deviceManager = deviceManager;
        WinRtDevice = winRtDevice;
        Description = description;
        HasVideoSupport = videoSupport;
        IsSoftwareRenderer = softwareRenderer;
    }

    /// <summary>ID3D11Device (owned).</summary>
    public nint Device => _device;

    /// <summary>The immediate ID3D11DeviceContext (owned, multithread-protected).</summary>
    public nint Context => _context;

    /// <summary>IMFDXGIDeviceManager bound to <see cref="Device"/> (owned).</summary>
    public nint DeviceManager => _deviceManager;

    public IDirect3DDevice WinRtDevice { get; }

    /// <summary>"hardware" or "WARP", the feature level, the adapter name and flags (see <see cref="DeviceNaming"/>).</summary>
    public string Description { get; }

    public bool HasVideoSupport { get; }

    /// <summary>WARP or the Microsoft Basic Render Driver: no GPU, so no hardware encoder can use this device.</summary>
    public bool IsSoftwareRenderer { get; }

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
                return Complete(device, context, driver == ScreenConstants.D3DDriverTypeWarp, level, (flags & Video) != 0);
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

    private static GpuDevice Complete(nint device, nint context, bool warpDriver, int featureLevel, bool videoSupport)
    {
        // The FrameArrived thread copies frames while Media Foundation encodes on its own threads: the immediate context
        // must be multithread-protected. ID3D11Multithread on the context (Windows 10+), else ID3D10Multithread on the
        // device (same IID and vtable).
        var multithread = ComCall.Query(context, ScreenConstants.IidID3D11Multithread);
        if (multithread == 0)
        {
            multithread = ComCall.Query(device, ScreenConstants.IidID3D11Multithread);
        }

        var multithreadProtected = multithread != 0;
        if (multithreadProtected)
        {
            ID3D11Multithread.SetMultithreadProtected(multithread, 1);
            ComCall.Release(ref multithread);
        }

        var adapter = DescribeAdapter(device);
        var softwareRenderer = warpDriver || DeviceNaming.IsBasicRenderDriver(adapter.VendorId, adapter.DeviceId);
        var description = DeviceNaming.Describe(warpDriver, featureLevel, videoSupport, adapter.Name, softwareRenderer, multithreadProtected);

        nint manager = 0;
        uint token;
        ComHelpers.Check(ScreenNativeMethods.MFCreateDXGIDeviceManager(&token, &manager), "MFCreateDXGIDeviceManager");
        try
        {
            ComHelpers.Check(IMFDXGIDeviceManager.ResetDevice(manager, device, token), "IMFDXGIDeviceManager::ResetDevice");
            var winRtDevice = CreateWinRtDevice(device);
            return new GpuDevice(device, context, manager, winRtDevice, description, videoSupport, softwareRenderer);
        }
        catch
        {
            ComCall.Release(ref manager);
            throw;
        }
    }

    /// <summary>The DXGI adapter behind <paramref name="device"/> (IDXGIDevice::GetAdapter + GetDesc); empty when unavailable. Never throws.</summary>
    private static (string? Name, uint VendorId, uint DeviceId) DescribeAdapter(nint device)
    {
        nint dxgi = 0, adapter = 0;
        try
        {
            dxgi = ComCall.Query(device, ScreenConstants.IidIDXGIDevice);
            if (dxgi == 0 || IDXGIDevice.GetAdapter(dxgi, &adapter) < 0 || adapter == 0)
            {
                return (null, 0, 0);
            }

            DxgiAdapterDesc desc;
            if (IDXGIAdapter.GetDesc(adapter, &desc) < 0)
            {
                return (null, 0, 0);
            }

            var name = new ReadOnlySpan<char>(desc.Description, DxgiAdapterDesc.DescriptionChars);
            var end = name.IndexOf('\0');
            return (new string(end >= 0 ? name[..end] : name), desc.VendorId, desc.DeviceId);
        }
        finally
        {
            ComCall.Release(ref adapter);
            ComCall.Release(ref dxgi);
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

/// <summary>Formats <see cref="GpuDevice.Description"/> and classifies adapters (pure).</summary>
internal static class DeviceNaming
{
    /// <summary>The "Microsoft Basic Render Driver": WARP exposed as a hardware adapter (no GPU, no encoder).</summary>
    public static bool IsBasicRenderDriver(uint vendorId, uint deviceId) =>
        vendorId == ScreenConstants.MicrosoftVendorId && deviceId == ScreenConstants.BasicRenderDriverDeviceId;

    /// <summary>E.g. "hardware D3D11 FL 11.1 on 'NVIDIA GeForce RTX 3060'" or "hardware D3D11 FL 11.1 on 'Microsoft Basic Render Driver' (software renderer)".</summary>
    public static string Describe(bool warpDriver, int featureLevel, bool videoSupport, string? adapter, bool softwareRenderer, bool multithreadProtected = true)
    {
        var name = string.IsNullOrWhiteSpace(adapter) ? string.Empty : $" on '{adapter.Trim()}'";
        return $"{(warpDriver ? "WARP" : "hardware")} D3D11 FL {featureLevel >> 12}.{(featureLevel >> 8) & 0xF}{name}"
            + (videoSupport ? string.Empty : " without video support")
            + (softwareRenderer ? " (software renderer)" : string.Empty)
            + (multithreadProtected ? string.Empty : " (no multithread protection)");
    }
}
