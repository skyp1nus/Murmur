using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace Murmur.Spike.LoopbackAsr.Capture;

// Minimal hand-written WASAPI interop, source-generated ([GeneratedComInterface] + [LibraryImport]) so it is
// Native-AOT safe. Every vtable below lists the methods in exactly the order of the C "...Vtbl" structs in the
// Windows SDK 10.0.26100 headers (IUnknown's QueryInterface/AddRef/Release come first and are implied).
// All methods use [PreserveSig] so every HRESULT is checked explicitly by the caller.
// Interface pointers are returned as raw nint and wrapped by WasapiLoopbackCapture via StrategyBasedComWrappers
// (UniqueInstance) so they can be released deterministically with ComObject.FinalRelease().

/// <summary>mmdeviceapi.h, IID A95664D2-9614-4F35-A746-DE8DB63617E6.</summary>
[GeneratedComInterface]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    // HRESULT EnumAudioEndpoints(EDataFlow dataFlow, DWORD dwStateMask, IMMDeviceCollection **ppDevices)
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices);

    // HRESULT GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, IMMDevice **ppEndpoint)
    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out nint endpoint);

    // HRESULT GetDevice(LPCWSTR pwstrId, IMMDevice **ppDevice)
    [PreserveSig]
    int GetDevice(nint id, out nint device);

    // HRESULT RegisterEndpointNotificationCallback(IMMNotificationClient *pClient)
    [PreserveSig]
    int RegisterEndpointNotificationCallback(nint client);

    // HRESULT UnregisterEndpointNotificationCallback(IMMNotificationClient *pClient)
    [PreserveSig]
    int UnregisterEndpointNotificationCallback(nint client);
}

/// <summary>mmdeviceapi.h, IID D666063F-1587-4E43-81F1-B948E807363F.</summary>
[GeneratedComInterface]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    // HRESULT Activate(REFIID iid, DWORD dwClsCtx, PROPVARIANT *pActivationParams, void **ppInterface)
    [PreserveSig]
    int Activate(in Guid iid, uint clsCtx, nint activationParams, out nint ppInterface);

    // HRESULT OpenPropertyStore(DWORD stgmAccess, IPropertyStore **ppProperties)
    [PreserveSig]
    int OpenPropertyStore(uint stgmAccess, out nint properties);

    // HRESULT GetId(LPWSTR *ppstrId)  -- string must be freed with CoTaskMemFree
    [PreserveSig]
    int GetId(out nint id);

    // HRESULT GetState(DWORD *pdwState)
    [PreserveSig]
    int GetState(out uint state);
}

/// <summary>Audioclient.h, IID 1CB9AD4C-DBFA-4c32-B178-C2F568A703B2.</summary>
[GeneratedComInterface]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
internal partial interface IAudioClient
{
    // HRESULT Initialize(AUDCLNT_SHAREMODE ShareMode, DWORD StreamFlags, REFERENCE_TIME hnsBufferDuration,
    //                    REFERENCE_TIME hnsPeriodicity, const WAVEFORMATEX *pFormat, LPCGUID AudioSessionGuid)
    [PreserveSig]
    int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, nint format, nint audioSessionGuid);

    // HRESULT GetBufferSize(UINT32 *pNumBufferFrames)
    [PreserveSig]
    int GetBufferSize(out uint numBufferFrames);

    // HRESULT GetStreamLatency(REFERENCE_TIME *phnsLatency)
    [PreserveSig]
    int GetStreamLatency(out long latency);

    // HRESULT GetCurrentPadding(UINT32 *pNumPaddingFrames)
    [PreserveSig]
    int GetCurrentPadding(out uint numPaddingFrames);

    // HRESULT IsFormatSupported(AUDCLNT_SHAREMODE ShareMode, const WAVEFORMATEX *pFormat, WAVEFORMATEX **ppClosestMatch)
    [PreserveSig]
    int IsFormatSupported(int shareMode, nint format, out nint closestMatch);

    // HRESULT GetMixFormat(WAVEFORMATEX **ppDeviceFormat)  -- must be freed with CoTaskMemFree
    [PreserveSig]
    int GetMixFormat(out nint deviceFormat);

    // HRESULT GetDevicePeriod(REFERENCE_TIME *phnsDefaultDevicePeriod, REFERENCE_TIME *phnsMinimumDevicePeriod)
    [PreserveSig]
    int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);

    // HRESULT Start(void)
    [PreserveSig]
    int Start();

    // HRESULT Stop(void)
    [PreserveSig]
    int Stop();

    // HRESULT Reset(void)
    [PreserveSig]
    int Reset();

    // HRESULT SetEventHandle(HANDLE eventHandle)
    [PreserveSig]
    int SetEventHandle(nint eventHandle);

    // HRESULT GetService(REFIID riid, void **ppv)
    [PreserveSig]
    int GetService(in Guid riid, out nint service);
}

/// <summary>Audioclient.h, IID C8ADBD64-E71E-48a0-A4DE-185C395CD317.</summary>
[GeneratedComInterface]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
internal partial interface IAudioCaptureClient
{
    // HRESULT GetBuffer(BYTE **ppData, UINT32 *pNumFramesToRead, DWORD *pdwFlags,
    //                   UINT64 *pu64DevicePosition, UINT64 *pu64QPCPosition)
    [PreserveSig]
    int GetBuffer(out nint data, out uint numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);

    // HRESULT ReleaseBuffer(UINT32 NumFramesRead)
    [PreserveSig]
    int ReleaseBuffer(uint numFramesRead);

    // HRESULT GetNextPacketSize(UINT32 *pNumFramesInNextPacket)
    [PreserveSig]
    int GetNextPacketSize(out uint numFramesInNextPacket);
}

/// <summary>mmreg.h WAVEFORMATEX (declared under #pragma pack(1): 18 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatEx
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort CbSize;
}

/// <summary>mmreg.h WAVEFORMATEXTENSIBLE (pack(1): 18 + 2 + 4 + 16 = 40 bytes; cbSize = 22).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatExtensible
{
    public WaveFormatEx Format;
    public ushort ValidBitsPerSample; // union { wValidBitsPerSample; wSamplesPerBlock; wReserved; }
    public uint ChannelMask;
    public Guid SubFormat;
}

[SupportedOSPlatform("windows")]
internal static partial class WasapiNative
{
    // mmdeviceapi.h: class DECLSPEC_UUID("BCDE0395-E52F-467C-8E3D-C4579291692E") MMDeviceEnumerator
    public static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IidIMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid IidIAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IidIAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    // ksmedia.h / mmreg.h
    public static readonly Guid KsDataFormatSubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");
    public static readonly Guid KsDataFormatSubtypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    public const ushort WaveFormatPcm = 0x0001;        // mmreg.h WAVE_FORMAT_PCM
    public const ushort WaveFormatIeeeFloat = 0x0003;  // mmreg.h WAVE_FORMAT_IEEE_FLOAT
    public const ushort WaveFormatExtensible = 0xFFFE; // mmreg.h WAVE_FORMAT_EXTENSIBLE

    public const int ERender = 0;   // mmdeviceapi.h EDataFlow eRender
    public const int EConsole = 0;  // mmdeviceapi.h ERole eConsole

    public const int AudclntShareModeShared = 0;             // AudioSessionTypes.h AUDCLNT_SHAREMODE_SHARED
    public const uint AudclntStreamFlagsLoopback = 0x00020000; // AudioSessionTypes.h AUDCLNT_STREAMFLAGS_LOOPBACK
    public const uint AudclntBufferFlagsDataDiscontinuity = 0x1; // Audioclient.h
    public const uint AudclntBufferFlagsSilent = 0x2;            // Audioclient.h
    public const uint AudclntBufferFlagsTimestampError = 0x4;    // Audioclient.h

    public const uint ClsctxAll = 0x1 | 0x2 | 0x4 | 0x10; // combaseapi.h CLSCTX_ALL (DCOM) = 0x17
    public const uint CoinitMultithreaded = 0x0;          // combaseapi.h COINITBASE_MULTITHREADED

    public const int SOk = 0;
    public const int SFalse = 1;
    public const int RpcEChangedMode = unchecked((int)0x80010106);

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint clsContext, in Guid iid, out nint ppv);

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(nint pv);
}
