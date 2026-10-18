using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Murmur.Capture.Windows.Audio.Interop;

// Hand-written, source-generated WASAPI / MMDevice COM interop ([GeneratedComInterface]: no built-in COM marshalling,
// Native-AOT safe). Every interface lists its methods in exactly the order of the C "<Name>Vtbl" struct in the
// Windows SDK 10.0.26100 headers; IUnknown (QueryInterface, AddRef, Release = slots 0-2) is implied, so the first
// method declared here is vtable slot 3. All methods are [PreserveSig]: callers check every HRESULT themselves.
// Interface pointers come back as raw nint and are wrapped deterministically by ComHelpers (UniqueInstance RCW +
// ComObject.FinalRelease), so no COM object lives until a finalizer runs.

/// <summary>mmdeviceapi.h, IID A95664D2-9614-4F35-A746-DE8DB63617E6 (slots 3-7).</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
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
    int GetDevice(string id, out nint device);

    // HRESULT RegisterEndpointNotificationCallback(IMMNotificationClient *pClient)
    [PreserveSig]
    int RegisterEndpointNotificationCallback(nint client);

    // HRESULT UnregisterEndpointNotificationCallback(IMMNotificationClient *pClient)
    [PreserveSig]
    int UnregisterEndpointNotificationCallback(nint client);
}

/// <summary>mmdeviceapi.h, IID 0BD7A1BE-7A1A-44DB-8397-CC5392387B5E (slots 3-4).</summary>
[GeneratedComInterface]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal partial interface IMMDeviceCollection
{
    // HRESULT GetCount(UINT *pcDevices)
    [PreserveSig]
    int GetCount(out uint count);

    // HRESULT Item(UINT nDevice, IMMDevice **ppDevice)
    [PreserveSig]
    int Item(uint index, out nint device);
}

/// <summary>mmdeviceapi.h, IID D666063F-1587-4E43-81F1-B948E807363F (slots 3-6).</summary>
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

    // HRESULT GetId(LPWSTR *ppstrId) -- the string is freed with CoTaskMemFree
    [PreserveSig]
    int GetId(out nint id);

    // HRESULT GetState(DWORD *pdwState)
    [PreserveSig]
    int GetState(out uint state);
}

/// <summary>propsys.h, IID 886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99 (slots 3-7).</summary>
[GeneratedComInterface]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal partial interface IPropertyStore
{
    // HRESULT GetCount(DWORD *cProps)
    [PreserveSig]
    int GetCount(out uint count);

    // HRESULT GetAt(DWORD iProp, PROPERTYKEY *pkey)
    [PreserveSig]
    int GetAt(uint index, out PropertyKey key);

    // HRESULT GetValue(REFPROPERTYKEY key, PROPVARIANT *pv) -- pv is cleared with PropVariantClear
    [PreserveSig]
    int GetValue(in PropertyKey key, out PropVariant value);

    // HRESULT SetValue(REFPROPERTYKEY key, REFPROPVARIANT propvar)
    [PreserveSig]
    int SetValue(in PropertyKey key, in PropVariant value);

    // HRESULT Commit(void)
    [PreserveSig]
    int Commit();
}

/// <summary>Audioclient.h, IID 1CB9AD4C-DBFA-4C32-B178-C2F568A703B2 (slots 3-14).</summary>
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
    int GetBufferSize(out uint bufferFrames);

    // HRESULT GetStreamLatency(REFERENCE_TIME *phnsLatency)
    [PreserveSig]
    int GetStreamLatency(out long latency);

    // HRESULT GetCurrentPadding(UINT32 *pNumPaddingFrames)
    [PreserveSig]
    int GetCurrentPadding(out uint paddingFrames);

    // HRESULT IsFormatSupported(AUDCLNT_SHAREMODE ShareMode, const WAVEFORMATEX *pFormat, WAVEFORMATEX **ppClosestMatch)
    [PreserveSig]
    int IsFormatSupported(int shareMode, nint format, out nint closestMatch);

    // HRESULT GetMixFormat(WAVEFORMATEX **ppDeviceFormat) -- freed with CoTaskMemFree
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

/// <summary>Audioclient.h, IID C8ADBD64-E71E-48A0-A4DE-185C395CD317 (slots 3-5).</summary>
[GeneratedComInterface]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
internal partial interface IAudioCaptureClient
{
    // HRESULT GetBuffer(BYTE **ppData, UINT32 *pNumFramesToRead, DWORD *pdwFlags,
    //                   UINT64 *pu64DevicePosition, UINT64 *pu64QPCPosition)
    [PreserveSig]
    int GetBuffer(out nint data, out uint framesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);

    // HRESULT ReleaseBuffer(UINT32 NumFramesRead)
    [PreserveSig]
    int ReleaseBuffer(uint framesRead);

    // HRESULT GetNextPacketSize(UINT32 *pNumFramesInNextPacket)
    [PreserveSig]
    int GetNextPacketSize(out uint framesInNextPacket);
}
