using System.Runtime.CompilerServices;

namespace Murmur.Capture.Windows.Screen.Interop;

// Thin, allocation-free COM call layer for D3D11 / DXGI / Media Foundation / WinRT capture objects.
//
// Why raw vtable calls instead of [GeneratedComInterface] RCWs: the frame path touches a fresh COM object per frame
// (capture frame, its surface and texture, the IMFSample); wrapping each in an RCW would allocate on every frame.
// Raw calls keep the FrameArrived and encoder loops allocation-free and need no runtime marshalling (Native AOT safe).
//
// Conventions:
// * One static class per COM interface, named exactly like the interface; one method per used COM method, named
//   exactly like the method, first parameter = the interface pointer.
// * The slot index is the method's position in the C "<Interface>Vtbl" struct of the Windows SDK 10.0.26100 headers
//   (IUnknown = 0..2, IInspectable adds 3..5). Every slot was checked mechanically against
//   Microsoft.Windows.SDK.Win32Metadata 71.0.30-preview (Win32 interfaces) and the Windows SDK projection winmd
//   10.0.22621 (WinRT interfaces): slot = 3 (or 6) + sum of base-interface method counts + index in the interface.
// * Callers own every returned interface pointer and release it with Com.Release.

internal static unsafe class ComCall
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void** Vtbl(nint self) => *(void***)self;

    // IUnknown::QueryInterface (slot 0)
    public static int QueryInterface(nint self, Guid* iid, nint* result) =>
        ((delegate* unmanaged[MemberFunction]<nint, Guid*, nint*, int>)Vtbl(self)[0])(self, iid, result);

    // IUnknown::AddRef (slot 1)
    public static uint AddRef(nint self) =>
        ((delegate* unmanaged[MemberFunction]<nint, uint>)Vtbl(self)[1])(self);

    // IUnknown::Release (slot 2)
    public static uint ReleaseRaw(nint self) =>
        ((delegate* unmanaged[MemberFunction]<nint, uint>)Vtbl(self)[2])(self);

    /// <summary>Releases <paramref name="pointer"/> if set and clears it.</summary>
    public static void Release(ref nint pointer)
    {
        var p = pointer;
        pointer = 0;
        if (p != 0)
        {
            ReleaseRaw(p);
        }
    }

    /// <summary>QueryInterface by IID value; 0 on failure.</summary>
    public static nint Query(nint self, Guid iid)
    {
        nint result = 0;
        return QueryInterface(self, &iid, &result) >= 0 ? result : 0;
    }
}

internal static unsafe class IMFAttributes
{
    // HRESULT GetUINT32(REFGUID guidKey, UINT32 *punValue)
    public static int GetUINT32(nint self, Guid* key, uint* value) =>
        ((delegate* unmanaged[MemberFunction]<nint, Guid*, uint*, int>)ComCall.Vtbl(self)[7])(self, key, value);

    // HRESULT GetUINT64(REFGUID guidKey, UINT64 *punValue)
    public static int GetUINT64(nint self, Guid* key, ulong* value) =>
        ((delegate* unmanaged[MemberFunction]<nint, Guid*, ulong*, int>)ComCall.Vtbl(self)[8])(self, key, value);

    // HRESULT GetGUID(REFGUID guidKey, GUID *pguidValue)
    public static int GetGUID(nint self, Guid* key, Guid* value) =>
        ((delegate* unmanaged[MemberFunction]<nint, Guid*, Guid*, int>)ComCall.Vtbl(self)[10])(self, key, value);

    // HRESULT GetStringLength(REFGUID guidKey, UINT32 *pcchLength)
    public static int GetStringLength(nint self, Guid* key, uint* length) =>
        ((delegate* unmanaged[MemberFunction]<nint, Guid*, uint*, int>)ComCall.Vtbl(self)[11])(self, key, length);

    // HRESULT GetAllocatedString(REFGUID guidKey, LPWSTR *ppwszValue, UINT32 *pcchLength)
    public static int GetAllocatedString(nint self, Guid* key, char** value, uint* length) =>
        ((delegate* unmanaged[MemberFunction]<nint, Guid*, char**, uint*, int>)ComCall.Vtbl(self)[13])(self, key, value, length);

    // HRESULT SetUINT32(REFGUID guidKey, UINT32 unValue)
    public static int SetUINT32(nint self, Guid* key, uint value) =>
        ((delegate* unmanaged[MemberFunction]<nint, Guid*, uint, int>)ComCall.Vtbl(self)[21])(self, key, value);

    // HRESULT SetUINT64(REFGUID guidKey, UINT64 unValue)
    public static int SetUINT64(nint self, Guid* key, ulong value) =>
        ((delegate* unmanaged[MemberFunction]<nint, Guid*, ulong, int>)ComCall.Vtbl(self)[22])(self, key, value);

    // HRESULT SetGUID(REFGUID guidKey, REFGUID guidValue)
    public static int SetGUID(nint self, Guid* key, Guid* value) =>
        ((delegate* unmanaged[MemberFunction]<nint, Guid*, Guid*, int>)ComCall.Vtbl(self)[24])(self, key, value);

    // HRESULT SetUnknown(REFGUID guidKey, IUnknown *pUnknown)
    public static int SetUnknown(nint self, Guid* key, nint value) =>
        ((delegate* unmanaged[MemberFunction]<nint, Guid*, nint, int>)ComCall.Vtbl(self)[27])(self, key, value);
}

internal static unsafe class IMFSample
{
    // HRESULT SetSampleTime(LONGLONG hnsSampleTime)
    public static int SetSampleTime(nint self, long time) =>
        ((delegate* unmanaged[MemberFunction]<nint, long, int>)ComCall.Vtbl(self)[36])(self, time);

    // HRESULT GetSampleDuration(LONGLONG *phnsSampleDuration)
    public static int GetSampleDuration(nint self, long* duration) =>
        ((delegate* unmanaged[MemberFunction]<nint, long*, int>)ComCall.Vtbl(self)[37])(self, duration);

    // HRESULT SetSampleDuration(LONGLONG hnsSampleDuration)
    public static int SetSampleDuration(nint self, long duration) =>
        ((delegate* unmanaged[MemberFunction]<nint, long, int>)ComCall.Vtbl(self)[38])(self, duration);

    // HRESULT GetBufferByIndex(DWORD dwIndex, IMFMediaBuffer **ppBuffer)
    public static int GetBufferByIndex(nint self, uint index, nint* buffer) =>
        ((delegate* unmanaged[MemberFunction]<nint, uint, nint*, int>)ComCall.Vtbl(self)[40])(self, index, buffer);

    // HRESULT AddBuffer(IMFMediaBuffer *pBuffer)
    public static int AddBuffer(nint self, nint buffer) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint, int>)ComCall.Vtbl(self)[42])(self, buffer);
}

internal static unsafe class IMFMediaBuffer
{
    // HRESULT Lock(BYTE **ppbBuffer, DWORD *pcbMaxLength, DWORD *pcbCurrentLength)
    public static int Lock(nint self, byte** buffer, uint* maxLength, uint* currentLength) =>
        ((delegate* unmanaged[MemberFunction]<nint, byte**, uint*, uint*, int>)ComCall.Vtbl(self)[3])(self, buffer, maxLength, currentLength);

    // HRESULT Unlock(void)
    public static int Unlock(nint self) =>
        ((delegate* unmanaged[MemberFunction]<nint, int>)ComCall.Vtbl(self)[4])(self);

    // HRESULT GetCurrentLength(DWORD *pcbCurrentLength)
    public static int GetCurrentLength(nint self, uint* length) =>
        ((delegate* unmanaged[MemberFunction]<nint, uint*, int>)ComCall.Vtbl(self)[5])(self, length);

    // HRESULT SetCurrentLength(DWORD cbCurrentLength)
    public static int SetCurrentLength(nint self, uint length) =>
        ((delegate* unmanaged[MemberFunction]<nint, uint, int>)ComCall.Vtbl(self)[6])(self, length);

    // HRESULT GetMaxLength(DWORD *pcbMaxLength)
    public static int GetMaxLength(nint self, uint* length) =>
        ((delegate* unmanaged[MemberFunction]<nint, uint*, int>)ComCall.Vtbl(self)[7])(self, length);
}

internal static unsafe class IMF2DBuffer
{
    // HRESULT GetContiguousLength(DWORD *pcbLength)
    public static int GetContiguousLength(nint self, uint* length) =>
        ((delegate* unmanaged[MemberFunction]<nint, uint*, int>)ComCall.Vtbl(self)[7])(self, length);
}

internal static unsafe class IMFDXGIBuffer
{
    // HRESULT GetResource(REFIID riid, LPVOID *ppvObject)
    public static int GetResource(nint self, Guid* iid, nint* resource) =>
        ((delegate* unmanaged[MemberFunction]<nint, Guid*, nint*, int>)ComCall.Vtbl(self)[3])(self, iid, resource);

    // HRESULT GetSubresourceIndex(UINT *puSubresource)
    public static int GetSubresourceIndex(nint self, uint* subresource) =>
        ((delegate* unmanaged[MemberFunction]<nint, uint*, int>)ComCall.Vtbl(self)[4])(self, subresource);
}

internal static unsafe class IMFSinkWriter
{
    // HRESULT AddStream(IMFMediaType *pTargetMediaType, DWORD *pdwStreamIndex)
    public static int AddStream(nint self, nint targetMediaType, uint* streamIndex) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint, uint*, int>)ComCall.Vtbl(self)[3])(self, targetMediaType, streamIndex);

    // HRESULT SetInputMediaType(DWORD dwStreamIndex, IMFMediaType *pInputMediaType, IMFAttributes *pEncodingParameters)
    public static int SetInputMediaType(nint self, uint streamIndex, nint inputMediaType, nint encodingParameters) =>
        ((delegate* unmanaged[MemberFunction]<nint, uint, nint, nint, int>)ComCall.Vtbl(self)[4])(self, streamIndex, inputMediaType, encodingParameters);

    // HRESULT BeginWriting(void)
    public static int BeginWriting(nint self) =>
        ((delegate* unmanaged[MemberFunction]<nint, int>)ComCall.Vtbl(self)[5])(self);

    // HRESULT WriteSample(DWORD dwStreamIndex, IMFSample *pSample)
    public static int WriteSample(nint self, uint streamIndex, nint sample) =>
        ((delegate* unmanaged[MemberFunction]<nint, uint, nint, int>)ComCall.Vtbl(self)[6])(self, streamIndex, sample);

    // HRESULT Finalize(void)
    public static int Finalize(nint self) =>
        ((delegate* unmanaged[MemberFunction]<nint, int>)ComCall.Vtbl(self)[11])(self);

    // HRESULT GetServiceForStream(DWORD dwStreamIndex, REFGUID guidService, REFIID riid, LPVOID *ppvObject)
    public static int GetServiceForStream(nint self, uint streamIndex, Guid* service, Guid* iid, nint* result) =>
        ((delegate* unmanaged[MemberFunction]<nint, uint, Guid*, Guid*, nint*, int>)ComCall.Vtbl(self)[12])(self, streamIndex, service, iid, result);
}

internal static unsafe class IMFDXGIDeviceManager
{
    // HRESULT ResetDevice(IUnknown *pUnkDevice, UINT resetToken)
    public static int ResetDevice(nint self, nint device, uint resetToken) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint, uint, int>)ComCall.Vtbl(self)[7])(self, device, resetToken);
}

internal static unsafe class IMFVideoSampleAllocatorEx
{
    // IMFVideoSampleAllocator::SetDirectXManager(IUnknown *pManager)
    public static int SetDirectXManager(nint self, nint manager) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint, int>)ComCall.Vtbl(self)[3])(self, manager);

    // IMFVideoSampleAllocator::UninitializeSampleAllocator(void)
    public static int UninitializeSampleAllocator(nint self) =>
        ((delegate* unmanaged[MemberFunction]<nint, int>)ComCall.Vtbl(self)[4])(self);

    // IMFVideoSampleAllocator::AllocateSample(IMFSample **ppSample)
    public static int AllocateSample(nint self, nint* sample) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint*, int>)ComCall.Vtbl(self)[6])(self, sample);

    // HRESULT InitializeSampleAllocatorEx(DWORD cInitialSamples, DWORD cMaximumSamples, IMFAttributes *pAttributes, IMFMediaType *pMediaType)
    public static int InitializeSampleAllocatorEx(nint self, uint initialSamples, uint maximumSamples, nint attributes, nint mediaType) =>
        ((delegate* unmanaged[MemberFunction]<nint, uint, uint, nint, nint, int>)ComCall.Vtbl(self)[7])(self, initialSamples, maximumSamples, attributes, mediaType);
}

internal static unsafe class IMFVideoSampleAllocatorCallback
{
    // HRESULT GetFreeSampleCount(LONG *plSamples)
    public static int GetFreeSampleCount(nint self, int* samples) =>
        ((delegate* unmanaged[MemberFunction]<nint, int*, int>)ComCall.Vtbl(self)[4])(self, samples);
}

internal static unsafe class IMFTransform
{
    // HRESULT GetAttributes(IMFAttributes **pAttributes)
    public static int GetAttributes(nint self, nint* attributes) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint*, int>)ComCall.Vtbl(self)[8])(self, attributes);
}

internal static unsafe class IMFSourceReader
{
    // HRESULT GetCurrentMediaType(DWORD dwStreamIndex, IMFMediaType **ppMediaType)
    public static int GetCurrentMediaType(nint self, uint streamIndex, nint* mediaType) =>
        ((delegate* unmanaged[MemberFunction]<nint, uint, nint*, int>)ComCall.Vtbl(self)[6])(self, streamIndex, mediaType);

    // HRESULT ReadSample(DWORD dwStreamIndex, DWORD dwControlFlags, DWORD *pdwActualStreamIndex, DWORD *pdwStreamFlags, LONGLONG *pllTimestamp, IMFSample **ppSample)
    public static int ReadSample(nint self, uint streamIndex, uint controlFlags, uint* actualStreamIndex, uint* streamFlags, long* timestamp, nint* sample) =>
        ((delegate* unmanaged[MemberFunction]<nint, uint, uint, uint*, uint*, long*, nint*, int>)ComCall.Vtbl(self)[9])(self, streamIndex, controlFlags, actualStreamIndex, streamFlags, timestamp, sample);

    // HRESULT GetPresentationAttribute(DWORD dwStreamIndex, REFGUID guidAttribute, PROPVARIANT *pvarAttribute)
    public static int GetPresentationAttribute(nint self, uint streamIndex, Guid* attribute, void* value) =>
        ((delegate* unmanaged[MemberFunction]<nint, uint, Guid*, void*, int>)ComCall.Vtbl(self)[12])(self, streamIndex, attribute, value);
}

internal static unsafe class ID3D11Device
{
    // HRESULT CreateTexture2D(const D3D11_TEXTURE2D_DESC *pDesc, const D3D11_SUBRESOURCE_DATA *pInitialData, ID3D11Texture2D **ppTexture2D)
    public static int CreateTexture2D(nint self, D3D11Texture2DDesc* desc, void* initialData, nint* texture) =>
        ((delegate* unmanaged[MemberFunction]<nint, D3D11Texture2DDesc*, void*, nint*, int>)ComCall.Vtbl(self)[5])(self, desc, initialData, texture);

    // D3D_FEATURE_LEVEL GetFeatureLevel(void)
    public static int GetFeatureLevel(nint self) =>
        ((delegate* unmanaged[MemberFunction]<nint, int>)ComCall.Vtbl(self)[37])(self);

    // HRESULT GetDeviceRemovedReason(void)
    public static int GetDeviceRemovedReason(nint self) =>
        ((delegate* unmanaged[MemberFunction]<nint, int>)ComCall.Vtbl(self)[39])(self);
}

internal static unsafe class ID3D11DeviceContext
{
    // HRESULT Map(ID3D11Resource *pResource, UINT Subresource, D3D11_MAP MapType, UINT MapFlags, D3D11_MAPPED_SUBRESOURCE *pMappedResource)
    public static int Map(nint self, nint resource, uint subresource, uint mapType, uint mapFlags, D3D11MappedSubresource* mapped) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint, uint, uint, uint, D3D11MappedSubresource*, int>)ComCall.Vtbl(self)[14])(self, resource, subresource, mapType, mapFlags, mapped);

    // void Unmap(ID3D11Resource *pResource, UINT Subresource)
    public static void Unmap(nint self, nint resource, uint subresource) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint, uint, void>)ComCall.Vtbl(self)[15])(self, resource, subresource);

    // void CopySubresourceRegion(ID3D11Resource *pDstResource, UINT DstSubresource, UINT DstX, UINT DstY, UINT DstZ, ID3D11Resource *pSrcResource, UINT SrcSubresource, const D3D11_BOX *pSrcBox)
    public static void CopySubresourceRegion(nint self, nint destination, uint destinationSubresource, uint x, uint y, uint z, nint source, uint sourceSubresource, D3D11Box* sourceBox) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint, uint, uint, uint, uint, nint, uint, D3D11Box*, void>)ComCall.Vtbl(self)[46])(self, destination, destinationSubresource, x, y, z, source, sourceSubresource, sourceBox);

    // void CopyResource(ID3D11Resource *pDstResource, ID3D11Resource *pSrcResource)
    public static void CopyResource(nint self, nint destination, nint source) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint, nint, void>)ComCall.Vtbl(self)[47])(self, destination, source);
}

internal static unsafe class ID3D11Texture2D
{
    // void GetDesc(D3D11_TEXTURE2D_DESC *pDesc)
    public static void GetDesc(nint self, D3D11Texture2DDesc* desc) =>
        ((delegate* unmanaged[MemberFunction]<nint, D3D11Texture2DDesc*, void>)ComCall.Vtbl(self)[10])(self, desc);
}

internal static unsafe class IDXGIDevice
{
    // HRESULT GetAdapter(IDXGIAdapter **pAdapter)
    public static int GetAdapter(nint self, nint* adapter) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint*, int>)ComCall.Vtbl(self)[7])(self, adapter);
}

internal static unsafe class IDXGIAdapter
{
    // HRESULT GetDesc(DXGI_ADAPTER_DESC *pDesc)
    public static int GetDesc(nint self, DxgiAdapterDesc* desc) =>
        ((delegate* unmanaged[MemberFunction]<nint, DxgiAdapterDesc*, int>)ComCall.Vtbl(self)[8])(self, desc);
}

internal static unsafe class ID3D11Multithread
{
    // BOOL SetMultithreadProtected(BOOL bMTProtect)
    public static int SetMultithreadProtected(nint self, int protect) =>
        ((delegate* unmanaged[MemberFunction]<nint, int, int>)ComCall.Vtbl(self)[5])(self, protect);
}

internal static unsafe class ID3D11VideoDevice
{
    // HRESULT CreateVideoProcessor(ID3D11VideoProcessorEnumerator *pEnum, UINT RateConversionIndex, ID3D11VideoProcessor **ppVideoProcessor)
    public static int CreateVideoProcessor(nint self, nint enumerator, uint rateConversionIndex, nint* processor) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint, uint, nint*, int>)ComCall.Vtbl(self)[4])(self, enumerator, rateConversionIndex, processor);

    // HRESULT CreateVideoProcessorInputView(ID3D11Resource *pResource, ID3D11VideoProcessorEnumerator *pEnum, const D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC *pDesc, ID3D11VideoProcessorInputView **ppVPIView)
    public static int CreateVideoProcessorInputView(nint self, nint resource, nint enumerator, D3D11VideoProcessorInputViewDesc* desc, nint* view) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint, nint, D3D11VideoProcessorInputViewDesc*, nint*, int>)ComCall.Vtbl(self)[8])(self, resource, enumerator, desc, view);

    // HRESULT CreateVideoProcessorOutputView(ID3D11Resource *pResource, ID3D11VideoProcessorEnumerator *pEnum, const D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC *pDesc, ID3D11VideoProcessorOutputView **ppVPOView)
    public static int CreateVideoProcessorOutputView(nint self, nint resource, nint enumerator, D3D11VideoProcessorOutputViewDesc* desc, nint* view) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint, nint, D3D11VideoProcessorOutputViewDesc*, nint*, int>)ComCall.Vtbl(self)[9])(self, resource, enumerator, desc, view);

    // HRESULT CreateVideoProcessorEnumerator(const D3D11_VIDEO_PROCESSOR_CONTENT_DESC *pDesc, ID3D11VideoProcessorEnumerator **ppEnum)
    public static int CreateVideoProcessorEnumerator(nint self, D3D11VideoProcessorContentDesc* desc, nint* enumerator) =>
        ((delegate* unmanaged[MemberFunction]<nint, D3D11VideoProcessorContentDesc*, nint*, int>)ComCall.Vtbl(self)[10])(self, desc, enumerator);
}

internal static unsafe class ID3D11VideoContext
{
    // void VideoProcessorSetOutputTargetRect(ID3D11VideoProcessor *pVideoProcessor, BOOL Enable, const RECT *pRect)
    public static void VideoProcessorSetOutputTargetRect(nint self, nint processor, int enable, Rect32* rect) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint, int, Rect32*, void>)ComCall.Vtbl(self)[13])(self, processor, enable, rect);

    // void VideoProcessorSetOutputBackgroundColor(ID3D11VideoProcessor *pVideoProcessor, BOOL YCbCr, const D3D11_VIDEO_COLOR *pColor)
    public static void VideoProcessorSetOutputBackgroundColor(nint self, nint processor, int yCbCr, D3D11VideoColorRgba* color) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint, int, D3D11VideoColorRgba*, void>)ComCall.Vtbl(self)[14])(self, processor, yCbCr, color);

    // void VideoProcessorSetStreamFrameFormat(ID3D11VideoProcessor *pVideoProcessor, UINT StreamIndex, D3D11_VIDEO_FRAME_FORMAT FrameFormat)
    public static void VideoProcessorSetStreamFrameFormat(nint self, nint processor, uint streamIndex, uint frameFormat) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint, uint, uint, void>)ComCall.Vtbl(self)[27])(self, processor, streamIndex, frameFormat);

    // void VideoProcessorSetStreamSourceRect(ID3D11VideoProcessor *pVideoProcessor, UINT StreamIndex, BOOL Enable, const RECT *pRect)
    public static void VideoProcessorSetStreamSourceRect(nint self, nint processor, uint streamIndex, int enable, Rect32* rect) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint, uint, int, Rect32*, void>)ComCall.Vtbl(self)[30])(self, processor, streamIndex, enable, rect);

    // void VideoProcessorSetStreamDestRect(ID3D11VideoProcessor *pVideoProcessor, UINT StreamIndex, BOOL Enable, const RECT *pRect)
    public static void VideoProcessorSetStreamDestRect(nint self, nint processor, uint streamIndex, int enable, Rect32* rect) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint, uint, int, Rect32*, void>)ComCall.Vtbl(self)[31])(self, processor, streamIndex, enable, rect);

    // void VideoProcessorSetStreamAutoProcessingMode(ID3D11VideoProcessor *pVideoProcessor, UINT StreamIndex, BOOL Enable)
    public static void VideoProcessorSetStreamAutoProcessingMode(nint self, nint processor, uint streamIndex, int enable) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint, uint, int, void>)ComCall.Vtbl(self)[37])(self, processor, streamIndex, enable);

    // HRESULT VideoProcessorBlt(ID3D11VideoProcessor *pVideoProcessor, ID3D11VideoProcessorOutputView *pView, UINT OutputFrame, UINT StreamCount, const D3D11_VIDEO_PROCESSOR_STREAM *pStreams)
    public static int VideoProcessorBlt(nint self, nint processor, nint outputView, uint outputFrame, uint streamCount, D3D11VideoProcessorStream* streams) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint, nint, uint, uint, D3D11VideoProcessorStream*, int>)ComCall.Vtbl(self)[53])(self, processor, outputView, outputFrame, streamCount, streams);
}

internal static unsafe class IDirect3DDxgiInterfaceAccess
{
    // HRESULT GetInterface(REFIID iid, void **p)
    public static int GetInterface(nint self, Guid* iid, nint* result) =>
        ((delegate* unmanaged[MemberFunction]<nint, Guid*, nint*, int>)ComCall.Vtbl(self)[3])(self, iid, result);
}

internal static unsafe class IGraphicsCaptureItemInterop
{
    // HRESULT CreateForMonitor(HMONITOR monitor, REFIID riid, void **result)
    public static int CreateForMonitor(nint self, nint monitor, Guid* iid, nint* result) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint, Guid*, nint*, int>)ComCall.Vtbl(self)[4])(self, monitor, iid, result);
}

/// <summary>Windows.Graphics.Capture.IDirect3D11CaptureFramePool (IInspectable-based: own methods start at slot 6).</summary>
internal static unsafe class IDirect3D11CaptureFramePool
{
    // HRESULT TryGetNextFrame(IDirect3D11CaptureFrame **result)
    public static int TryGetNextFrame(nint self, nint* frame) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint*, int>)ComCall.Vtbl(self)[7])(self, frame);
}

/// <summary>Windows.Graphics.Capture.IDirect3D11CaptureFrame (IInspectable-based).</summary>
internal static unsafe class IDirect3D11CaptureFrame
{
    // HRESULT get_Surface(IDirect3DSurface **value)
    public static int get_Surface(nint self, nint* surface) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint*, int>)ComCall.Vtbl(self)[6])(self, surface);

    // HRESULT get_SystemRelativeTime(TimeSpan *value) -- 100 ns units on the QPC timeline
    public static int get_SystemRelativeTime(nint self, long* value) =>
        ((delegate* unmanaged[MemberFunction]<nint, long*, int>)ComCall.Vtbl(self)[7])(self, value);

    // HRESULT get_ContentSize(SizeInt32 *value)
    public static int get_ContentSize(nint self, SizeInt32Abi* value) =>
        ((delegate* unmanaged[MemberFunction]<nint, SizeInt32Abi*, int>)ComCall.Vtbl(self)[8])(self, value);
}

/// <summary>Windows.Foundation.IClosable (IInspectable-based).</summary>
internal static unsafe class IClosable
{
    // HRESULT Close(void)
    public static int Close(nint self) =>
        ((delegate* unmanaged[MemberFunction]<nint, int>)ComCall.Vtbl(self)[6])(self);
}

/// <summary>ICodecAPI (strmif.h): encoder properties such as the rate control mode. IUnknown-based.</summary>
internal static unsafe class ICodecAPI
{
    // HRESULT IsSupported(const GUID *Api)
    public static int IsSupported(nint self, Guid* api) =>
        ((delegate* unmanaged[MemberFunction]<nint, Guid*, int>)ComCall.Vtbl(self)[3])(self, api);

    // HRESULT GetValue(const GUID *Api, VARIANT *Value)
    public static int GetValue(nint self, Guid* api, Variant* value) =>
        ((delegate* unmanaged[MemberFunction]<nint, Guid*, Variant*, int>)ComCall.Vtbl(self)[8])(self, api, value);
}
