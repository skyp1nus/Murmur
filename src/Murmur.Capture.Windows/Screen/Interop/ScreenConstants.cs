namespace Murmur.Capture.Windows.Screen.Interop;

/// <summary>
/// IIDs, Media Foundation attribute GUIDs, HRESULTs and flags used by screen capture. Every value is copied from the
/// Windows SDK 10.0.26100 headers and cross-checked against Microsoft.Windows.SDK.Win32Metadata 71.0.30-preview /
/// the Windows SDK projection winmd (see ScreenInteropLayoutTests), except where a comment says otherwise.
/// </summary>
internal static class ScreenConstants
{
    // ---- Interface IDs ----

    /// <summary>IInspectable (inspectable.h).</summary>
    public static readonly Guid IidIInspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");

    /// <summary>IDXGIDevice (dxgi.h).</summary>
    public static readonly Guid IidIDXGIDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    /// <summary>ID3D11Texture2D (d3d11.h).</summary>
    public static readonly Guid IidID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    /// <summary>ID3D11VideoDevice (d3d11.h).</summary>
    public static readonly Guid IidID3D11VideoDevice = new("10EC4D5B-975A-4689-B9E4-D0AAC30FE333");

    /// <summary>ID3D11VideoContext (d3d11.h).</summary>
    public static readonly Guid IidID3D11VideoContext = new("61F21C45-3C0E-4A74-9CEA-67100D9AD5E4");

    /// <summary>ID3D11Multithread (d3d11_4.h; same IID as ID3D10Multithread).</summary>
    public static readonly Guid IidID3D11Multithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");

    /// <summary>IMFDXGIBuffer (mfobjects.h).</summary>
    public static readonly Guid IidIMFDXGIBuffer = new("E7174CFA-1C9E-48B1-8866-626226BFC258");

    /// <summary>IMFMediaBuffer (mfobjects.h).</summary>
    public static readonly Guid IidIMFMediaBuffer = new("045FA593-8799-42B8-BC8D-8968C6453507");

    /// <summary>IMF2DBuffer (mfobjects.h).</summary>
    public static readonly Guid IidIMF2DBuffer = new("7DC9D5F9-9ED9-44EC-9BBF-0600BB589FBB");

    /// <summary>IMFVideoSampleAllocatorEx (mfidl.h).</summary>
    public static readonly Guid IidIMFVideoSampleAllocatorEx = new("545B3A48-3283-4F62-866F-A62D8F598F9F");

    /// <summary>IMFVideoSampleAllocatorCallback (mfidl.h).</summary>
    public static readonly Guid IidIMFVideoSampleAllocatorCallback = new("992388B4-3372-4F67-8B6F-C84C071F4751");

    /// <summary>IMFTransform (mftransform.h).</summary>
    public static readonly Guid IidIMFTransform = new("BF94C121-5B05-4E6F-8000-BA598961414D");

    /// <summary>IDirect3DDxgiInterfaceAccess (windows.graphics.directx.direct3d11.interop.h).</summary>
    public static readonly Guid IidIDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    /// <summary>IGraphicsCaptureItemInterop (windows.graphics.capture.interop.h).</summary>
    public static readonly Guid IidIGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    /// <summary>Windows.Graphics.Capture.IGraphicsCaptureItem (default interface of GraphicsCaptureItem).</summary>
    public static readonly Guid IidIGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>Windows.Graphics.Capture.IDirect3D11CaptureFramePool (default interface of Direct3D11CaptureFramePool).</summary>
    public static readonly Guid IidIDirect3D11CaptureFramePool = new("24EB6D22-1975-422E-82E7-780DBD8DDF24");

    /// <summary>Windows.Foundation.IClosable.</summary>
    public static readonly Guid IidIClosable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");

    // ---- Media Foundation attributes and GUID values (mfapi.h, mfidl.h, mfreadwrite.h, mftransform.h) ----

    public static readonly Guid MfReadwriteEnableHardwareTransforms = new("A634A91C-822B-41B9-A494-4DE4643612B0");
    public static readonly Guid MfSinkWriterD3DManager = new("EC822DA2-E1E9-4B29-A0D8-563C719F5269");
    public static readonly Guid MfTranscodeContainerType = new("150FF23F-4ABC-478B-AC4F-E1916FBA1CCA");
    public static readonly Guid MfTranscodeContainerTypeFmpeg4 = new("9BA876F1-419F-4B77-A1E0-35959D9D4004");

    /// <summary>MF_LOW_LATENCY (identical to CODECAPI_AVLowLatencyMode).</summary>
    public static readonly Guid MfLowLatency = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");

    public static readonly Guid MfMtMajorType = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    public static readonly Guid MfMtSubtype = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    public static readonly Guid MfMtAvgBitrate = new("20332624-FB0D-4D9E-BD0D-CBF6786C102E");
    public static readonly Guid MfMtFrameSize = new("1652C33D-D6B2-4012-B834-72030849A37D");
    public static readonly Guid MfMtFrameRate = new("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
    public static readonly Guid MfMtInterlaceMode = new("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD");
    public static readonly Guid MfMtPixelAspectRatio = new("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");
    public static readonly Guid MfMtMpeg2Profile = new("AD76A80B-2D5C-4E0B-B375-64E520137036");
    public static readonly Guid MfMtAllSamplesIndependent = new("C9173739-5E56-461C-B713-46FB995CB95F");
    public static readonly Guid MfMediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MfVideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid MfVideoFormatArgb32 = new("00000015-0000-0010-8000-00AA00389B71");

    /// <summary>MFVideoFormat_RGB32 (D3DFMT_X8R8G8B8 = 22): B, G, R, X bytes, the classic sink-writer input format.</summary>
    public static readonly Guid MfVideoFormatRgb32 = new("00000016-0000-0010-8000-00AA00389B71");

    /// <summary>MF_MT_DEFAULT_STRIDE (UINT32 holding a signed stride; positive = top-down rows).</summary>
    public static readonly Guid MfMtDefaultStride = new("644B4E48-1E02-4516-B0EB-C01CA9D49AC6");

    /// <summary>MFT_CATEGORY_VIDEO_ENCODER (mfapi.h).</summary>
    public static readonly Guid MftCategoryVideoEncoder = new("F79EAC7D-E545-4387-BDEE-D647D7BDE42A");
    public static readonly Guid MfSaD3D11BindFlags = new("EACF97AD-065C-4408-BEE3-FDCBFD128BE2");
    public static readonly Guid MftFriendlyNameAttribute = new("314FFBAE-5B41-4C95-9C19-4E7D586FACE3");
    public static readonly Guid MftEnumHardwareUrlAttribute = new("2FB866AC-B078-4942-AB6C-003D05CDA674");
    public static readonly Guid MfPdDuration = new("6C990D33-BB8E-477A-8598-0D5D96FCD88A");

    /// <summary>
    /// CODECAPI_AVEncMPVGOPSize (codecapi.h). Not part of Win32Metadata; value taken from codecapi.h. An encoder that
    /// does not know the key ignores it, so a wrong value could only cost the GOP length, never the recording.
    /// </summary>
    public static readonly Guid CodecApiAVEncMpvGopSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");

    // ---- Numeric constants ----

    /// <summary>MF_VERSION = (MF_SDK_VERSION 0x0002 &lt;&lt; 16) | MF_API_VERSION 0x0070.</summary>
    public const uint MfVersion = 0x0002_0070;

    /// <summary>MFSTARTUP_LITE: no socket library.</summary>
    public const uint MfStartupLite = 1;

    /// <summary>MFVideoInterlace_Progressive.</summary>
    public const uint MfVideoInterlaceProgressive = 2;

    /// <summary>eAVEncH264VProfile_High.</summary>
    public const uint H264ProfileHigh = 100;

    /// <summary>eAVEncH264VProfile_Main.</summary>
    public const uint H264ProfileMain = 77;

    public const uint MfSourceReaderFirstVideoStream = 0xFFFF_FFFC;
    public const uint MfSourceReaderMediaSource = 0xFFFF_FFFF;
    public const uint MfSourceReaderFlagError = 0x1;
    public const uint MfSourceReaderFlagEndOfStream = 0x2;

    public const uint D3D11SdkVersion = 7;
    public const int D3DDriverTypeHardware = 1;
    public const int D3DDriverTypeWarp = 5;
    public const uint D3D11CreateDeviceBgraSupport = 0x20;
    public const uint D3D11CreateDeviceVideoSupport = 0x800;
    public const int D3DFeatureLevel11_1 = 0xB100;
    public const int D3DFeatureLevel11_0 = 0xB000;
    public const int D3DFeatureLevel10_1 = 0xA100;
    public const int D3DFeatureLevel10_0 = 0xA000;
    public const uint DxgiFormatB8G8R8A8Unorm = 87;
    public const uint DxgiFormatB8G8R8X8Unorm = 88;
    public const uint DxgiFormatB8G8R8A8UnormSrgb = 91;
    public const uint DxgiFormatB8G8R8X8UnormSrgb = 93;
    public const uint D3D11BindShaderResource = 0x8;
    public const uint D3D11BindRenderTarget = 0x20;
    public const uint D3D11UsageDefault = 0;
    public const uint D3D11UsageStaging = 3;
    public const uint D3D11CpuAccessRead = 0x2_0000;
    public const uint D3D11MapRead = 1;

    /// <summary>MFT_ENUM_FLAG_HARDWARE: hardware MFTs (GPU encoders; always asynchronous).</summary>
    public const uint MftEnumFlagHardware = 0x4;

    /// <summary>MFT_ENUM_FLAG_SORTANDFILTER: apply the merit / preferred-MFT sorting and drop blocked MFTs.</summary>
    public const uint MftEnumFlagSortAndFilter = 0x40;

    /// <summary>DXGI_ADAPTER_DESC.VendorId of Microsoft's software adapters.</summary>
    public const uint MicrosoftVendorId = 0x1414;

    /// <summary>DXGI_ADAPTER_DESC.DeviceId of the "Microsoft Basic Render Driver" (WARP behind D3D_DRIVER_TYPE_HARDWARE).</summary>
    public const uint BasicRenderDriverDeviceId = 0x8C;
    public const uint D3D11VpivDimensionTexture2D = 1;
    public const uint D3D11VpovDimensionTexture2D = 1;
    public const uint D3D11VpovDimensionTexture2DArray = 2;
    public const uint D3D11VideoFrameFormatProgressive = 0;
    public const uint D3D11VideoUsagePlaybackNormal = 0;

    /// <summary>winuser.h MONITORINFOF_PRIMARY.</summary>
    public const uint MonitorInfoFlagPrimary = 1;

    /// <summary>winuser.h ENUM_CURRENT_SETTINGS ((DWORD)-1).</summary>
    public const uint EnumCurrentSettings = 0xFFFF_FFFF;

    /// <summary>wingdi.h DM_PELSWIDTH | DM_PELSHEIGHT.</summary>
    public const uint DmPelsWidthHeight = 0x0008_0000 | 0x0010_0000;

    // ---- HRESULTs ----

    public const int SOk = 0;
    public const int MfESampleAllocatorEmpty = unchecked((int)0xC00D4A3E);
    public const int MfESinkNoSamplesProcessed = unchecked((int)0xC00D4A44);
    public const int EPointer = unchecked((int)0x80004003);
    public const int EFail = unchecked((int)0x80004005);
    public const int ENoInterface = unchecked((int)0x80004002);
    public const int EInvalidArg = unchecked((int)0x80070057);

    /// <summary>HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION): the file is still open elsewhere.</summary>
    public const int ESharingViolation = unchecked((int)0x80070020);

    /// <summary>HRESULT_FROM_WIN32(ERROR_LOCK_VIOLATION).</summary>
    public const int ELockViolation = unchecked((int)0x80070021);
}
