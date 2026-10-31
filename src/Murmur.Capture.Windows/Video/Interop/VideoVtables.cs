using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Video.Interop;

// Raw vtable calls for the Media Foundation Media Engine and WIC, following the conventions of Screen/Interop/Vtables.cs
// (one static class per COM interface, first parameter = the interface pointer, callers own returned references and
// release them with ComCall.Release). Raw calls keep the per-frame path (OnVideoStreamTick, TransferVideoFrame, the
// copy) allocation-free.
//
// Slots = position in the C "<Interface>Vtbl" struct (IUnknown = 0..2). Every slot below was derived mechanically from
// Microsoft.Windows.SDK.Win32Metadata 71.0.30-preview (Windows.Win32.winmd: 3 + base-interface method counts + index
// in the interface) and matches mfmediaengine.h / wincodec.h of the Windows SDK 10.0.26100; VideoInteropLayoutTests
// pins every wrapper to its slot.

/// <summary>mfmediaengine.h IMFMediaEngineClassFactory (IUnknown-based), IID 4D645ACE-26AA-4688-9BE1-DF3516990B93.</summary>
internal static unsafe class IMFMediaEngineClassFactory
{
    // [3] HRESULT CreateInstance(DWORD dwFlags, IMFAttributes *pAttr, IMFMediaEngine **ppPlayer)
    public static int CreateInstance(nint self, uint flags, nint attributes, nint* engine) =>
        ((delegate* unmanaged[MemberFunction]<nint, uint, nint, nint*, int>)ComCall.Vtbl(self)[3])(self, flags, attributes, engine);
}

/// <summary>mfmediaengine.h IMFMediaEngine (IUnknown-based), IID 98A1B0BB-03EB-4935-AE7C-93C1FA0E1C93.</summary>
internal static unsafe class IMFMediaEngine
{
    // [3] HRESULT GetError(IMFMediaError **ppError)
    public static int GetError(nint self, nint* error) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint*, int>)ComCall.Vtbl(self)[3])(self, error);

    // [6] HRESULT SetSource(BSTR pUrl)
    public static int SetSource(nint self, char* url) =>
        ((delegate* unmanaged[MemberFunction]<nint, char*, int>)ComCall.Vtbl(self)[6])(self, url);

    // [12] HRESULT Load(void)
    public static int Load(nint self) =>
        ((delegate* unmanaged[MemberFunction]<nint, int>)ComCall.Vtbl(self)[12])(self);

    // [14] USHORT GetReadyState(void)
    public static ushort GetReadyState(nint self) =>
        ((delegate* unmanaged[MemberFunction]<nint, ushort>)ComCall.Vtbl(self)[14])(self);

    // [15] BOOL IsSeeking(void)
    public static int IsSeeking(nint self) =>
        ((delegate* unmanaged[MemberFunction]<nint, int>)ComCall.Vtbl(self)[15])(self);

    // [16] double GetCurrentTime(void)
    public static double GetCurrentTime(nint self) =>
        ((delegate* unmanaged[MemberFunction]<nint, double>)ComCall.Vtbl(self)[16])(self);

    // [17] HRESULT SetCurrentTime(double seekTime)
    public static int SetCurrentTime(nint self, double seconds) =>
        ((delegate* unmanaged[MemberFunction]<nint, double, int>)ComCall.Vtbl(self)[17])(self, seconds);

    // [19] double GetDuration(void)
    public static double GetDuration(nint self) =>
        ((delegate* unmanaged[MemberFunction]<nint, double>)ComCall.Vtbl(self)[19])(self);

    // [20] BOOL IsPaused(void)
    public static int IsPaused(nint self) =>
        ((delegate* unmanaged[MemberFunction]<nint, int>)ComCall.Vtbl(self)[20])(self);

    // [22] HRESULT SetDefaultPlaybackRate(double Rate)
    public static int SetDefaultPlaybackRate(nint self, double rate) =>
        ((delegate* unmanaged[MemberFunction]<nint, double, int>)ComCall.Vtbl(self)[22])(self, rate);

    // [24] HRESULT SetPlaybackRate(double Rate)
    public static int SetPlaybackRate(nint self, double rate) =>
        ((delegate* unmanaged[MemberFunction]<nint, double, int>)ComCall.Vtbl(self)[24])(self, rate);

    // [27] BOOL IsEnded(void)
    public static int IsEnded(nint self) =>
        ((delegate* unmanaged[MemberFunction]<nint, int>)ComCall.Vtbl(self)[27])(self);

    // [29] HRESULT SetAutoPlay(BOOL AutoPlay)
    public static int SetAutoPlay(nint self, int autoPlay) =>
        ((delegate* unmanaged[MemberFunction]<nint, int, int>)ComCall.Vtbl(self)[29])(self, autoPlay);

    // [31] HRESULT SetLoop(BOOL Loop)
    public static int SetLoop(nint self, int loop) =>
        ((delegate* unmanaged[MemberFunction]<nint, int, int>)ComCall.Vtbl(self)[31])(self, loop);

    // [32] HRESULT Play(void)
    public static int Play(nint self) =>
        ((delegate* unmanaged[MemberFunction]<nint, int>)ComCall.Vtbl(self)[32])(self);

    // [33] HRESULT Pause(void)
    public static int Pause(nint self) =>
        ((delegate* unmanaged[MemberFunction]<nint, int>)ComCall.Vtbl(self)[33])(self);

    // [35] HRESULT SetMuted(BOOL Muted)
    public static int SetMuted(nint self, int muted) =>
        ((delegate* unmanaged[MemberFunction]<nint, int, int>)ComCall.Vtbl(self)[35])(self, muted);

    // [38] BOOL HasVideo(void)
    public static int HasVideo(nint self) =>
        ((delegate* unmanaged[MemberFunction]<nint, int>)ComCall.Vtbl(self)[38])(self);

    // [40] HRESULT GetNativeVideoSize(DWORD *cx, DWORD *cy)
    public static int GetNativeVideoSize(nint self, uint* width, uint* height) =>
        ((delegate* unmanaged[MemberFunction]<nint, uint*, uint*, int>)ComCall.Vtbl(self)[40])(self, width, height);

    // [42] HRESULT Shutdown(void)
    public static int Shutdown(nint self) =>
        ((delegate* unmanaged[MemberFunction]<nint, int>)ComCall.Vtbl(self)[42])(self);

    // [43] HRESULT TransferVideoFrame(IUnknown *pDstSurf, const MFVideoNormalizedRect *pSrc, const RECT *pDst, const MFARGB *pBorderClr)
    public static int TransferVideoFrame(nint self, nint destination, MFVideoNormalizedRect* source, Rect32* destinationRect, MFARGB* border) =>
        ((delegate* unmanaged[MemberFunction]<nint, nint, MFVideoNormalizedRect*, Rect32*, MFARGB*, int>)ComCall.Vtbl(self)[43])(self, destination, source, destinationRect, border);

    // [44] HRESULT OnVideoStreamTick(LONGLONG *pPts)
    public static int OnVideoStreamTick(nint self, long* pts) =>
        ((delegate* unmanaged[MemberFunction]<nint, long*, int>)ComCall.Vtbl(self)[44])(self, pts);
}

/// <summary>
/// mfmediaengine.h IMFMediaEngineEx (IMFMediaEngine 3..44 + own methods), IID 83015EAD-B1E6-40D0-A98A-37145FFE1AD1; reached
/// with QueryInterface on the engine. Diagnostic use only (seek recovery).
/// </summary>
internal static unsafe class IMFMediaEngineEx
{
    // [51] HRESULT FrameStep(BOOL Forward)
    public static int FrameStep(nint self, int forward) =>
        ((delegate* unmanaged[MemberFunction]<nint, int, int>)ComCall.Vtbl(self)[51])(self, forward);

    // [80] HRESULT SetCurrentTimeEx(double seekTime, MF_MEDIA_ENGINE_SEEK_MODE seekMode)
    public static int SetCurrentTimeEx(nint self, double seconds, int mode) =>
        ((delegate* unmanaged[MemberFunction]<nint, double, int, int>)ComCall.Vtbl(self)[80])(self, seconds, mode);
}

/// <summary>mfmediaengine.h IMFMediaError (IUnknown-based), IID FC0E10D2-AB2A-4501-A951-06BB1075184C.</summary>
internal static unsafe class IMFMediaError
{
    // [3] USHORT GetErrorCode(void)
    public static ushort GetErrorCode(nint self) =>
        ((delegate* unmanaged[MemberFunction]<nint, ushort>)ComCall.Vtbl(self)[3])(self);

    // [4] HRESULT GetExtendedErrorCode(void)
    public static int GetExtendedErrorCode(nint self) =>
        ((delegate* unmanaged[MemberFunction]<nint, int>)ComCall.Vtbl(self)[4])(self);
}

/// <summary>wincodec.h IWICImagingFactory (IUnknown-based), IID EC5EC8A9-C395-4314-9C77-54D7A935FF70.</summary>
internal static unsafe class IWICImagingFactory
{
    // [17] HRESULT CreateBitmap(UINT uiWidth, UINT uiHeight, REFWICPixelFormatGUID pixelFormat, WICBitmapCreateCacheOption option, IWICBitmap **ppIBitmap)
    public static int CreateBitmap(nint self, uint width, uint height, Guid* pixelFormat, uint option, nint* bitmap) =>
        ((delegate* unmanaged[MemberFunction]<nint, uint, uint, Guid*, uint, nint*, int>)ComCall.Vtbl(self)[17])(self, width, height, pixelFormat, option, bitmap);
}

/// <summary>wincodec.h IWICBitmap (IWICBitmapSource 3..7 + own methods), IID 00000121-A8F2-4877-BA0A-FD2B6645FB94.</summary>
internal static unsafe class IWICBitmap
{
    // [8] HRESULT Lock(const WICRect *prcLock, DWORD flags, IWICBitmapLock **ppILock)
    public static int Lock(nint self, WICRect* rect, uint flags, nint* bitmapLock) =>
        ((delegate* unmanaged[MemberFunction]<nint, WICRect*, uint, nint*, int>)ComCall.Vtbl(self)[8])(self, rect, flags, bitmapLock);
}

/// <summary>wincodec.h IWICBitmapLock (IUnknown-based), IID 00000123-A8F2-4877-BA0A-FD2B6645FB94.</summary>
internal static unsafe class IWICBitmapLock
{
    // [4] HRESULT GetStride(UINT *pcbStride)
    public static int GetStride(nint self, uint* stride) =>
        ((delegate* unmanaged[MemberFunction]<nint, uint*, int>)ComCall.Vtbl(self)[4])(self, stride);

    // [5] HRESULT GetDataPointer(UINT *pcbBufferSize, WICInProcPointer *ppbData)
    public static int GetDataPointer(nint self, uint* size, byte** data) =>
        ((delegate* unmanaged[MemberFunction]<nint, uint*, byte**, int>)ComCall.Vtbl(self)[5])(self, size, data);
}
