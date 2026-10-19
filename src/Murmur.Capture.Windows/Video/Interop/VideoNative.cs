using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Murmur.Capture.Windows.Video.Interop;

/// <summary>
/// IIDs, CLSIDs, attribute GUIDs and enum values of the Media Engine and WIC. Every value was read from
/// Microsoft.Windows.SDK.Win32Metadata 71.0.30-preview (Windows.Win32.winmd) and matches mfmediaengine.h / wincodec.h /
/// combaseapi.h of the Windows SDK 10.0.26100 (pinned by VideoInteropLayoutTests).
/// </summary>
internal static class VideoConstants
{
    // ---- Classes and interfaces ----

    public static readonly Guid ClsidMFMediaEngineClassFactory = new("B44392DA-499B-446B-A4CB-005FEAD0E6D5");
    public static readonly Guid IidIMFMediaEngineClassFactory = new("4D645ACE-26AA-4688-9BE1-DF3516990B93");
    public static readonly Guid IidIMFMediaEngine = new("98A1B0BB-03EB-4935-AE7C-93C1FA0E1C93");
    public static readonly Guid IidIMFMediaEngineNotify = new("FEE7C112-E776-42B5-9BBF-0048524E2BD5");
    public static readonly Guid IidIMFMediaError = new("FC0E10D2-AB2A-4501-A951-06BB1075184C");
    public static readonly Guid ClsidWICImagingFactory = new("CACAF262-9370-4615-A13B-9F5539DA4C0A");
    public static readonly Guid IidIWICImagingFactory = new("EC5EC8A9-C395-4314-9C77-54D7A935FF70");

    // ---- Media Engine attributes (mfmediaengine.h) ----

    /// <summary>MF_MEDIA_ENGINE_CALLBACK: IUnknown of the IMFMediaEngineNotify (required).</summary>
    public static readonly Guid MfMediaEngineCallback = new("C60381B8-83A4-41F8-A3D0-DE05076849A9");

    /// <summary>MF_MEDIA_ENGINE_DXGI_MANAGER: the IMFDXGIDeviceManager for D3D11 decoding and frame transfers.</summary>
    public static readonly Guid MfMediaEngineDxgiManager = new("065702DA-1094-486D-8617-EE7CC4EE4648");

    /// <summary>MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT: a DXGI_FORMAT (UINT32).</summary>
    public static readonly Guid MfMediaEngineVideoOutputFormat = new("5066893C-8CF9-42BC-8B8A-472212E52726");

    /// <summary>GUID_WICPixelFormat32bppBGRA (wincodec.h).</summary>
    public static readonly Guid WicPixelFormat32bppBgra = new("6FDDC324-4E03-4BFE-B185-3D77768DC90F");

    // ---- MF_MEDIA_ENGINE_CREATEFLAGS ----

    /// <summary>MF_MEDIA_ENGINE_FORCEMUTE: the engine renders no audio.</summary>
    public const uint MfMediaEngineForceMute = 0x4;

    // ---- MF_MEDIA_ENGINE_EVENT ----

    public const uint EventLoadStart = 1;
    public const uint EventAbort = 4;
    public const uint EventError = 5;
    public const uint EventEmptied = 6;
    public const uint EventPlay = 8;
    public const uint EventPause = 9;
    public const uint EventLoadedMetadata = 10;
    public const uint EventLoadedData = 11;
    public const uint EventSeeked = 17;
    public const uint EventEnded = 19;
    public const uint EventDurationChange = 21;
    public const uint EventFormatChange = 1000;
    public const uint EventFirstFrameReady = 1009;
    public const uint EventResourceLost = 1012;
    public const uint EventStreamRenderingError = 1014;

    // ---- MF_MEDIA_ENGINE_ERR ----

    public const ushort ErrNoError = 0;
    public const ushort ErrAborted = 1;
    public const ushort ErrNetwork = 2;
    public const ushort ErrDecode = 3;
    public const ushort ErrSrcNotSupported = 4;
    public const ushort ErrEncrypted = 5;

    // ---- MF_MEDIA_ENGINE_READY ----

    public const ushort ReadyHaveNothing = 0;
    public const ushort ReadyHaveMetadata = 1;
    public const ushort ReadyHaveCurrentData = 2;

    // ---- WIC ----

    /// <summary>WICBitmapCacheOnLoad: the bitmap owns its pixel memory.</summary>
    public const uint WicBitmapCacheOnLoad = 2;

    /// <summary>WICBitmapLockRead.</summary>
    public const uint WicBitmapLockRead = 1;

    // ---- COM ----

    /// <summary>CLSCTX_INPROC_SERVER.</summary>
    public const uint ClsctxInprocServer = 1;

    /// <summary>MF_E_SHUTDOWN: the engine was shut down.</summary>
    public const int MfEShutdown = unchecked((int)0xC00D3E85);

    /// <summary>MF_E_UNSUPPORTED_BYTESTREAM_TYPE: not a container Media Foundation can read.</summary>
    public const int MfEUnsupportedByteStreamType = unchecked((int)0xC00D36C4);

    /// <summary>MF_E_INVALIDREQUEST.</summary>
    public const int MfEInvalidRequest = unchecked((int)0xC00D36B2);

    /// <summary>S_FALSE: OnVideoStreamTick has no new frame.</summary>
    public const int SFalse = 1;
}

/// <summary>mfmediaengine.h <c>MFVideoNormalizedRect</c> (16 bytes): left, top, right, bottom in 0..1.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MFVideoNormalizedRect
{
    public float Left;
    public float Top;
    public float Right;
    public float Bottom;
}

/// <summary>mfobjects.h <c>MFARGB</c> (4 bytes): rgbBlue, rgbGreen, rgbRed, rgbAlpha.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MFARGB
{
    public byte Blue;
    public byte Green;
    public byte Red;
    public byte Alpha;
}

/// <summary>wincodec.h <c>WICRect</c> (16 bytes): X, Y, Width, Height.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WICRect
{
    public int X;
    public int Y;
    public int Width;
    public int Height;
}

/// <summary>Flat imports for the video player (ole32 / oleaut32), source-generated.</summary>
internal static unsafe partial class VideoNativeMethods
{
    /// <summary><c>HRESULT CoCreateInstance(REFCLSID rclsid, LPUNKNOWN pUnkOuter, DWORD dwClsContext, REFIID riid, LPVOID *ppv)</c>.</summary>
    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(Guid* clsid, nint outer, uint clsContext, Guid* iid, nint* instance);

    /// <summary>
    /// <c>HRESULT CoIncrementMTAUsage(CO_MTA_USAGE_COOKIE *pCookie)</c> (Windows 8+): keeps the process MTA alive, so
    /// threads that never initialized COM (thread-pool continuations) are in the implicit MTA.
    /// </summary>
    [LibraryImport("ole32.dll")]
    public static partial int CoIncrementMTAUsage(nint* cookie);

    /// <summary><c>BSTR SysAllocString(const OLECHAR *psz)</c>; 0 when out of memory.</summary>
    [LibraryImport("oleaut32.dll")]
    public static partial nint SysAllocString(char* text);

    /// <summary><c>void SysFreeString(BSTR bstrString)</c>.</summary>
    [LibraryImport("oleaut32.dll")]
    public static partial void SysFreeString(nint text);
}

/// <summary>
/// mfmediaengine.h IMFMediaEngineNotify, IID FEE7C112-E776-42B5-9BBF-0048524E2BD5 (slot 3), implemented by
/// <see cref="MediaEngineNotify"/>. Called on Media Foundation worker threads.
/// </summary>
[GeneratedComInterface]
[Guid("FEE7C112-E776-42B5-9BBF-0048524E2BD5")]
internal partial interface IMFMediaEngineNotify
{
    // [3] HRESULT EventNotify(DWORD event, DWORD_PTR param1, DWORD param2)
    [PreserveSig]
    int EventNotify(uint mediaEvent, nuint param1, uint param2);
}

/// <summary>Receives one engine's events and forwards them to an <see cref="IMediaEngineEvents"/> until detached.</summary>
[GeneratedComClass]
internal sealed partial class MediaEngineNotify : IMFMediaEngineNotify
{
    private volatile IMediaEngineEvents? _target;

    public MediaEngineNotify(IMediaEngineEvents target)
    {
        _target = target;
    }

    /// <summary>Stops forwarding (the engine may still call in while it shuts down).</summary>
    public void Detach() => _target = null;

    public int EventNotify(uint mediaEvent, nuint param1, uint param2)
    {
        try
        {
            _target?.OnEngineEvent(mediaEvent, param1, param2);
        }
        catch (Exception)
        {
            // Never let an exception cross into Media Foundation's worker thread.
        }

        return 0;
    }
}

/// <summary>What <see cref="MediaEngineNotify"/> forwards to (any Media Foundation thread).</summary>
internal interface IMediaEngineEvents
{
    void OnEngineEvent(uint mediaEvent, nuint param1, uint param2);
}
