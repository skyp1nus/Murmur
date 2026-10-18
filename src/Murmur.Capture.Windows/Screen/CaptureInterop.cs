using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Capture.Windows.Screen.Interop;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using WinRT;

namespace Murmur.Capture.Windows.Screen;

/// <summary>
/// Windows.Graphics.Capture glue on top of the CsWinRT projection (AOT-safe: the projection ships with the Windows
/// SDK targeting pack and its source generator; no reflection is used here).
/// UNVERIFIED-ON-WINDOWS until the Windows-trait tests run in CI.
/// </summary>
internal static unsafe class CaptureInterop
{
    private const string GraphicsCaptureItemClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
    private const string GraphicsCaptureSessionClass = "Windows.Graphics.Capture.GraphicsCaptureSession";

    /// <summary>GraphicsCaptureSession.IsSupported(); false wherever WinRT or WGC is unavailable (never throws).</summary>
    public static bool IsSupported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return GraphicsCaptureSession.IsSupported();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>A capture item for a monitor via IGraphicsCaptureItemInterop::CreateForMonitor.</summary>
    public static GraphicsCaptureItem CreateItemForMonitor(nint monitor)
    {
        using var factory = ActivationFactory.Get(GraphicsCaptureItemClass, ScreenConstants.IidIGraphicsCaptureItemInterop);
        var iid = ScreenConstants.IidIGraphicsCaptureItem;
        nint item = 0;
        ComHelpers.Check(IGraphicsCaptureItemInterop.CreateForMonitor(factory.ThisPtr, monitor, &iid, &item), "IGraphicsCaptureItemInterop::CreateForMonitor");
        try
        {
            return GraphicsCaptureItem.FromAbi(item);
        }
        finally
        {
            ComCall.Release(ref item);
        }
    }

    /// <summary>An owned IDirect3D11CaptureFramePool pointer for the allocation-free frame path.</summary>
    public static nint GetFramePoolAbi(Direct3D11CaptureFramePool pool)
    {
        var hr = ((IWinRTObject)pool).NativeObject.TryAs(ScreenConstants.IidIDirect3D11CaptureFramePool, out var pointer);
        ComHelpers.Check(hr, "QueryInterface(IDirect3D11CaptureFramePool)");
        return pointer;
    }

    /// <summary>Cursor on, yellow border off where the OS has the property (the border needs borderless access; best effort).</summary>
    public static void Configure(GraphicsCaptureSession session)
    {
        if (ApiInformation.IsPropertyPresent(GraphicsCaptureSessionClass, nameof(GraphicsCaptureSession.IsCursorCaptureEnabled)))
        {
            session.IsCursorCaptureEnabled = true;
        }

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)
            && ApiInformation.IsPropertyPresent(GraphicsCaptureSessionClass, nameof(GraphicsCaptureSession.IsBorderRequired)))
        {
            try
            {
                session.IsBorderRequired = false;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or COMException or InvalidOperationException)
            {
                // Without GraphicsCaptureAccess borderless permission Windows keeps the border; capture still works.
            }
        }
    }

    /// <summary>Releases the frame (IClosable::Close returns its buffer to the pool) and our reference to it.</summary>
    public static void CloseFrame(ref nint frame)
    {
        var closable = ComCall.Query(frame, ScreenConstants.IidIClosable);
        if (closable != 0)
        {
            IClosable.Close(closable);
            ComCall.Release(ref closable);
        }

        ComCall.Release(ref frame);
    }

    /// <summary>
    /// The ID3D11Texture2D behind a capture frame (owned), or 0 with the failing HRESULT and step. The surface is the
    /// native ABI pointer returned by get_Surface, so the QueryInterface reaches the WinRT object itself (no projection).
    /// </summary>
    public static int GetFrameTexture(nint frame, out nint texture, out FrameStep failedStep)
    {
        texture = 0;
        failedStep = FrameStep.None;
        nint surface = 0, access = 0, result = 0;
        try
        {
            var hr = IDirect3D11CaptureFrame.get_Surface(frame, &surface);
            if (hr < 0 || surface == 0)
            {
                failedStep = FrameStep.CaptureSurface;
                return hr < 0 ? hr : ScreenConstants.EPointer;
            }

            access = ComCall.Query(surface, ScreenConstants.IidIDirect3DDxgiInterfaceAccess);
            if (access == 0)
            {
                failedStep = FrameStep.SurfaceAccess;
                return ScreenConstants.ENoInterface;
            }

            var iid = ScreenConstants.IidID3D11Texture2D;
            hr = IDirect3DDxgiInterfaceAccess.GetInterface(access, &iid, &result);
            if (hr < 0 || result == 0)
            {
                failedStep = FrameStep.SurfaceTexture;
                return hr < 0 ? hr : ScreenConstants.EPointer;
            }

            texture = result;
            result = 0;
            return ScreenConstants.SOk;
        }
        finally
        {
            ComCall.Release(ref result);
            ComCall.Release(ref access);
            ComCall.Release(ref surface);
        }
    }
}
