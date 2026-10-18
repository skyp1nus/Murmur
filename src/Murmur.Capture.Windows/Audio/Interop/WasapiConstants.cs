namespace Murmur.Capture.Windows.Audio.Interop;

/// <summary>GUIDs, enums, flags and HRESULTs used by the WASAPI path, each with the header it was checked against.</summary>
internal static class WasapiConstants
{
    // mmdeviceapi.h: class DECLSPEC_UUID("BCDE0395-E52F-467C-8E3D-C4579291692E") MMDeviceEnumerator
    public static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IidIMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid IidIAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IidIAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    public static readonly Guid IidIAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    // functiondiscoverykeys_devpkey.h: DEFINE_PROPERTYKEY(PKEY_Device_FriendlyName,
    //     0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 14)
    public static readonly PropertyKey PkeyDeviceFriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    // mmdeviceapi.h: enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    public const int ERender = 0;
    public const int ECapture = 1;

    // mmdeviceapi.h: enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }
    public const int EConsole = 0;

    // mmdeviceapi.h: #define DEVICE_STATE_ACTIVE 0x00000001
    public const uint DeviceStateActive = 0x00000001;

    // objbase.h / coml2api.h: #define STGM_READ 0x00000000L
    public const uint StgmRead = 0x00000000;

    // wtypes.h: enum VARENUM { VT_EMPTY = 0, ..., VT_LPWSTR = 31 }
    public const ushort VtLpwstr = 31;

    // combaseapi.h: CLSCTX_ALL = INPROC_SERVER | INPROC_HANDLER | LOCAL_SERVER | REMOTE_SERVER (WTypesbase.h: 0x1|0x2|0x4|0x10)
    public const uint ClsctxAll = 0x1 | 0x2 | 0x4 | 0x10;

    // combaseapi.h: COINITBASE_MULTITHREADED = 0x0 (objbase.h: COINIT_MULTITHREADED)
    public const uint CoinitMultithreaded = 0x0;

    // AudioSessionTypes.h: enum AUDCLNT_SHAREMODE { AUDCLNT_SHAREMODE_SHARED, AUDCLNT_SHAREMODE_EXCLUSIVE }
    public const int AudclntShareModeShared = 0;

    // AudioSessionTypes.h
    public const uint AudclntStreamFlagsLoopback = 0x00020000;
    public const uint AudclntStreamFlagsEventCallback = 0x00040000;
    public const uint AudclntStreamFlagsSrcDefaultQuality = 0x08000000;
    public const uint AudclntStreamFlagsAutoConvertPcm = 0x80000000;

    // Audioclient.h: enum _AUDCLNT_BUFFERFLAGS
    public const uint AudclntBufferFlagsDataDiscontinuity = 0x1;
    public const uint AudclntBufferFlagsSilent = 0x2;
    public const uint AudclntBufferFlagsTimestampError = 0x4;

    // winerror.h FACILITY_AUDCLNT = 2185 (0x889); Audioclient.h AUDCLNT_SUCCESS(n) / AUDCLNT_ERR(n)
    public const int AudclntSBufferEmpty = 0x08890001;
    public const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);
    public const int AudclntEServiceNotRunning = unchecked((int)0x88890010);
    public const int AudclntEResourcesInvalidated = unchecked((int)0x88890026);

    // winerror.h
    public const int SOk = 0;
    public const int SFalse = 1;
    public const int EFail = unchecked((int)0x80004005);
    public const int RpcEChangedMode = unchecked((int)0x80010106);
    public const int ENotFound = unchecked((int)0x80070490); // HRESULT_FROM_WIN32(ERROR_NOT_FOUND = 1168)
    public const int EAccessDenied = unchecked((int)0x80070005); // E_ACCESSDENIED: privacy settings deny the microphone

    // winbase.h / winnt.h: WAIT_OBJECT_0 = STATUS_WAIT_0 = 0, WAIT_TIMEOUT = 258, WAIT_FAILED = 0xFFFFFFFF
    public const uint WaitObject0 = 0;
    public const uint WaitTimeout = 258;
    public const uint WaitFailed = 0xFFFFFFFF;

    /// <summary>One second in REFERENCE_TIME / QPC-position units (100 ns).</summary>
    public const long HundredNsPerSecond = 10_000_000;
}
