using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Murmur.Capture.Windows.Screen.Interop;
using Murmur.Capture.Windows.Video.Interop;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// Video player interop checked on any OS: struct layouts, constants and GUIDs against Microsoft.Windows.SDK.Win32Metadata
/// 71.0.30-preview (mfmediaengine.h, wincodec.h), the vtable slot every raw wrapper calls (a fake object whose entry N
/// records N, like ScreenInteropLayoutTests), and the generated IMFMediaEngineNotify CCW.
/// </summary>
public sealed unsafe class VideoInteropLayoutTests
{
    [ThreadStatic]
    private static int _lastSlot;

    [Fact]
    public void StructLayouts_MatchHeaders()
    {
        Assert.Equal(16, sizeof(MFVideoNormalizedRect));
        Assert.Equal(4, sizeof(MFARGB));
        Assert.Equal(16, sizeof(WICRect));
        Assert.Equal(8, (int)Marshal.OffsetOf<MFVideoNormalizedRect>(nameof(MFVideoNormalizedRect.Right)));
        Assert.Equal(2, (int)Marshal.OffsetOf<MFARGB>(nameof(MFARGB.Red)));
        Assert.Equal(3, (int)Marshal.OffsetOf<MFARGB>(nameof(MFARGB.Alpha)));
        Assert.Equal(8, (int)Marshal.OffsetOf<WICRect>(nameof(WICRect.Width)));
    }

    [Fact]
    public void Guids_MatchHeaders()
    {
        Assert.Equal(new Guid("B44392DA-499B-446B-A4CB-005FEAD0E6D5"), VideoConstants.ClsidMFMediaEngineClassFactory);
        Assert.Equal(new Guid("4D645ACE-26AA-4688-9BE1-DF3516990B93"), VideoConstants.IidIMFMediaEngineClassFactory);
        Assert.Equal(new Guid("98A1B0BB-03EB-4935-AE7C-93C1FA0E1C93"), VideoConstants.IidIMFMediaEngine);
        Assert.Equal(new Guid("83015EAD-B1E6-40D0-A98A-37145FFE1AD1"), VideoConstants.IidIMFMediaEngineEx);
        Assert.Equal(new Guid("FEE7C112-E776-42B5-9BBF-0048524E2BD5"), VideoConstants.IidIMFMediaEngineNotify);
        Assert.Equal(new Guid("FC0E10D2-AB2A-4501-A951-06BB1075184C"), VideoConstants.IidIMFMediaError);
        Assert.Equal(new Guid("CACAF262-9370-4615-A13B-9F5539DA4C0A"), VideoConstants.ClsidWICImagingFactory);
        Assert.Equal(new Guid("EC5EC8A9-C395-4314-9C77-54D7A935FF70"), VideoConstants.IidIWICImagingFactory);
        Assert.Equal(new Guid("C60381B8-83A4-41F8-A3D0-DE05076849A9"), VideoConstants.MfMediaEngineCallback);
        Assert.Equal(new Guid("065702DA-1094-486D-8617-EE7CC4EE4648"), VideoConstants.MfMediaEngineDxgiManager);
        Assert.Equal(new Guid("5066893C-8CF9-42BC-8B8A-472212E52726"), VideoConstants.MfMediaEngineVideoOutputFormat);
        Assert.Equal(new Guid("6FDDC324-4E03-4BFE-B185-3D77768DC90F"), VideoConstants.WicPixelFormat32bppBgra);

        // The generated interface carries the same IID the engine queries for.
        Assert.Equal(VideoConstants.IidIMFMediaEngineNotify, typeof(IMFMediaEngineNotify).GUID);
    }

    [Fact]
    public void Constants_MatchHeaders()
    {
        Assert.Equal(4u, VideoConstants.MfMediaEngineForceMute);
        Assert.Equal((5u, 10u, 11u, 17u, 21u), (VideoConstants.EventError, VideoConstants.EventLoadedMetadata, VideoConstants.EventLoadedData, VideoConstants.EventSeeked, VideoConstants.EventDurationChange));
        Assert.Equal((1000u, 1009u, 1012u, 1014u), (VideoConstants.EventFormatChange, VideoConstants.EventFirstFrameReady, VideoConstants.EventResourceLost, VideoConstants.EventStreamRenderingError));
        Assert.Equal((3, 4, 5), (VideoConstants.ErrDecode, VideoConstants.ErrSrcNotSupported, VideoConstants.ErrEncrypted));
        Assert.Equal((2u, 1u, 1u), (VideoConstants.WicBitmapCacheOnLoad, VideoConstants.WicBitmapLockRead, VideoConstants.ClsctxInprocServer));
        Assert.Equal(-1072873851, VideoConstants.MfEShutdown);
        Assert.Equal(-1072875836, VideoConstants.MfEUnsupportedByteStreamType);
        Assert.Equal(-1072875854, VideoConstants.MfEInvalidRequest);
        Assert.Equal(87u, ScreenConstants.DxgiFormatB8G8R8A8Unorm);
    }

    public static TheoryData<string, int> Slots() => new()
    {
        { "IMFMediaEngineClassFactory::CreateInstance", 3 },
        { "IMFMediaEngine::GetError", 3 },
        { "IMFMediaEngine::SetSource", 6 },
        { "IMFMediaEngine::Load", 12 },
        { "IMFMediaEngine::GetReadyState", 14 },
        { "IMFMediaEngine::IsSeeking", 15 },
        { "IMFMediaEngine::GetCurrentTime", 16 },
        { "IMFMediaEngine::SetCurrentTime", 17 },
        { "IMFMediaEngine::GetDuration", 19 },
        { "IMFMediaEngine::IsPaused", 20 },
        { "IMFMediaEngine::IsEnded", 27 },
        { "IMFMediaEngine::SetAutoPlay", 29 },
        { "IMFMediaEngine::SetLoop", 31 },
        { "IMFMediaEngine::Play", 32 },
        { "IMFMediaEngine::Pause", 33 },
        { "IMFMediaEngine::SetMuted", 35 },
        { "IMFMediaEngine::HasVideo", 38 },
        { "IMFMediaEngine::GetNativeVideoSize", 40 },
        { "IMFMediaEngine::Shutdown", 42 },
        { "IMFMediaEngine::TransferVideoFrame", 43 },
        { "IMFMediaEngine::OnVideoStreamTick", 44 },
        { "IMFMediaEngineEx::FrameStep", 51 },
        { "IMFMediaEngineEx::SetCurrentTimeEx", 80 },
        { "IMFMediaError::GetErrorCode", 3 },
        { "IMFMediaError::GetExtendedErrorCode", 4 },
        { "IWICImagingFactory::CreateBitmap", 17 },
        { "IWICBitmap::Lock", 8 },
        { "IWICBitmapLock::GetStride", 4 },
        { "IWICBitmapLock::GetDataPointer", 5 },
    };

    [Theory]
    [MemberData(nameof(Slots))]
    public void RawWrapper_CallsTheHeaderSlot(string method, int slot)
    {
        var self = CreateFake();
        try
        {
            _lastSlot = -1;
            Invoke(method, self);
            Assert.Equal(slot, _lastSlot);
        }
        finally
        {
            NativeMemory.Free(*(void**)self);
            NativeMemory.Free((void*)self);
        }
    }

    [Fact]
    public void NotifyCcw_Slot3_IsEventNotify_AndForwardsUntilDetached()
    {
        var events = new RecordingEvents();
        var notify = new MediaEngineNotify(events);
        var pointer = (nint)ComInterfaceMarshaller<IMFMediaEngineNotify>.ConvertToUnmanaged(notify);
        try
        {
            // What the engine does: QueryInterface for IMFMediaEngineNotify, then call slot 3.
            var queried = ComCall.Query(pointer, VideoConstants.IidIMFMediaEngineNotify);
            Assert.NotEqual(0, queried);
            ComCall.ReleaseRaw(queried);
            var eventNotify = (delegate* unmanaged[MemberFunction]<nint, uint, nuint, uint, int>)ComCall.Vtbl(pointer)[3];
            Assert.Equal(0, eventNotify(pointer, VideoConstants.EventError, 4, 0xC00D36C4));
            Assert.Equal([(VideoConstants.EventError, (nuint)4, 0xC00D36C4u)], events.Received);

            events.Throw = true; // an exception must not cross into the engine
            Assert.Equal(0, eventNotify(pointer, VideoConstants.EventSeeked, 0, 0));

            events.Throw = false;
            notify.Detach();
            Assert.Equal(0, eventNotify(pointer, VideoConstants.EventLoadedMetadata, 0, 0));
            Assert.Equal(2, events.Received.Count); // the throwing call was recorded, nothing after the detach
        }
        finally
        {
            ComCall.ReleaseRaw(pointer);
        }
    }

    private static void Invoke(string method, nint s)
    {
        nint p;
        uint u;
        long l;
        Guid g = default;
        byte* b;
        MFVideoNormalizedRect source;
        Rect32 rect;
        MFARGB border;
        WICRect wic;
        switch (method)
        {
            case "IMFMediaEngineClassFactory::CreateInstance": IMFMediaEngineClassFactory.CreateInstance(s, 0, 0, &p); break;
            case "IMFMediaEngine::GetError": IMFMediaEngine.GetError(s, &p); break;
            case "IMFMediaEngine::SetSource": IMFMediaEngine.SetSource(s, null); break;
            case "IMFMediaEngine::Load": IMFMediaEngine.Load(s); break;
            case "IMFMediaEngine::GetReadyState": IMFMediaEngine.GetReadyState(s); break;
            case "IMFMediaEngine::IsSeeking": IMFMediaEngine.IsSeeking(s); break;
            case "IMFMediaEngine::GetCurrentTime": IMFMediaEngine.GetCurrentTime(s); break;
            case "IMFMediaEngine::SetCurrentTime": IMFMediaEngine.SetCurrentTime(s, 1.5); break;
            case "IMFMediaEngine::GetDuration": IMFMediaEngine.GetDuration(s); break;
            case "IMFMediaEngine::IsPaused": IMFMediaEngine.IsPaused(s); break;
            case "IMFMediaEngine::IsEnded": IMFMediaEngine.IsEnded(s); break;
            case "IMFMediaEngine::SetAutoPlay": IMFMediaEngine.SetAutoPlay(s, 0); break;
            case "IMFMediaEngine::SetLoop": IMFMediaEngine.SetLoop(s, 0); break;
            case "IMFMediaEngine::Play": IMFMediaEngine.Play(s); break;
            case "IMFMediaEngine::Pause": IMFMediaEngine.Pause(s); break;
            case "IMFMediaEngine::SetMuted": IMFMediaEngine.SetMuted(s, 1); break;
            case "IMFMediaEngine::HasVideo": IMFMediaEngine.HasVideo(s); break;
            case "IMFMediaEngine::GetNativeVideoSize": IMFMediaEngine.GetNativeVideoSize(s, &u, &u); break;
            case "IMFMediaEngine::Shutdown": IMFMediaEngine.Shutdown(s); break;
            case "IMFMediaEngine::TransferVideoFrame": IMFMediaEngine.TransferVideoFrame(s, 0, &source, &rect, &border); break;
            case "IMFMediaEngine::OnVideoStreamTick": IMFMediaEngine.OnVideoStreamTick(s, &l); break;
            case "IMFMediaEngineEx::FrameStep": IMFMediaEngineEx.FrameStep(s, 1); break;
            case "IMFMediaEngineEx::SetCurrentTimeEx": IMFMediaEngineEx.SetCurrentTimeEx(s, 1.5, VideoConstants.SeekModeApproximate); break;
            case "IMFMediaError::GetErrorCode": IMFMediaError.GetErrorCode(s); break;
            case "IMFMediaError::GetExtendedErrorCode": IMFMediaError.GetExtendedErrorCode(s); break;
            case "IWICImagingFactory::CreateBitmap": IWICImagingFactory.CreateBitmap(s, 1, 1, &g, 0, &p); break;
            case "IWICBitmap::Lock": IWICBitmap.Lock(s, &wic, 1, &p); break;
            case "IWICBitmapLock::GetStride": IWICBitmapLock.GetStride(s, &u); break;
            case "IWICBitmapLock::GetDataPointer": IWICBitmapLock.GetDataPointer(s, &u, &b); break;
            default: throw new ArgumentOutOfRangeException(nameof(method), method, null);
        }
    }

    private static nint CreateFake()
    {
        var stubs = Stubs();
        var vtable = (nint*)NativeMemory.Alloc((nuint)(stubs.Length * sizeof(nint)));
        for (var i = 0; i < stubs.Length; i++)
        {
            vtable[i] = (nint)stubs[i];
        }

        var self = (nint*)NativeMemory.Alloc((nuint)sizeof(nint));
        *self = (nint)vtable;
        return (nint)self;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Hit(int slot)
    {
        _lastSlot = slot;
        return slot;
    }

    [UnmanagedCallersOnly] private static int S0(nint self) => Hit(0);
    [UnmanagedCallersOnly] private static int S1(nint self) => Hit(1);
    [UnmanagedCallersOnly] private static int S2(nint self) => Hit(2);
    [UnmanagedCallersOnly] private static int S3(nint self) => Hit(3);
    [UnmanagedCallersOnly] private static int S4(nint self) => Hit(4);
    [UnmanagedCallersOnly] private static int S5(nint self) => Hit(5);
    [UnmanagedCallersOnly] private static int S6(nint self) => Hit(6);
    [UnmanagedCallersOnly] private static int S7(nint self) => Hit(7);
    [UnmanagedCallersOnly] private static int S8(nint self) => Hit(8);
    [UnmanagedCallersOnly] private static int S9(nint self) => Hit(9);
    [UnmanagedCallersOnly] private static int S10(nint self) => Hit(10);
    [UnmanagedCallersOnly] private static int S11(nint self) => Hit(11);
    [UnmanagedCallersOnly] private static int S12(nint self) => Hit(12);
    [UnmanagedCallersOnly] private static int S13(nint self) => Hit(13);
    [UnmanagedCallersOnly] private static int S14(nint self) => Hit(14);
    [UnmanagedCallersOnly] private static int S15(nint self) => Hit(15);
    [UnmanagedCallersOnly] private static int S16(nint self) => Hit(16);
    [UnmanagedCallersOnly] private static int S17(nint self) => Hit(17);
    [UnmanagedCallersOnly] private static int S18(nint self) => Hit(18);
    [UnmanagedCallersOnly] private static int S19(nint self) => Hit(19);
    [UnmanagedCallersOnly] private static int S20(nint self) => Hit(20);
    [UnmanagedCallersOnly] private static int S21(nint self) => Hit(21);
    [UnmanagedCallersOnly] private static int S22(nint self) => Hit(22);
    [UnmanagedCallersOnly] private static int S23(nint self) => Hit(23);
    [UnmanagedCallersOnly] private static int S24(nint self) => Hit(24);
    [UnmanagedCallersOnly] private static int S25(nint self) => Hit(25);
    [UnmanagedCallersOnly] private static int S26(nint self) => Hit(26);
    [UnmanagedCallersOnly] private static int S27(nint self) => Hit(27);
    [UnmanagedCallersOnly] private static int S28(nint self) => Hit(28);
    [UnmanagedCallersOnly] private static int S29(nint self) => Hit(29);
    [UnmanagedCallersOnly] private static int S30(nint self) => Hit(30);
    [UnmanagedCallersOnly] private static int S31(nint self) => Hit(31);
    [UnmanagedCallersOnly] private static int S32(nint self) => Hit(32);
    [UnmanagedCallersOnly] private static int S33(nint self) => Hit(33);
    [UnmanagedCallersOnly] private static int S34(nint self) => Hit(34);
    [UnmanagedCallersOnly] private static int S35(nint self) => Hit(35);
    [UnmanagedCallersOnly] private static int S36(nint self) => Hit(36);
    [UnmanagedCallersOnly] private static int S37(nint self) => Hit(37);
    [UnmanagedCallersOnly] private static int S38(nint self) => Hit(38);
    [UnmanagedCallersOnly] private static int S39(nint self) => Hit(39);
    [UnmanagedCallersOnly] private static int S40(nint self) => Hit(40);
    [UnmanagedCallersOnly] private static int S41(nint self) => Hit(41);
    [UnmanagedCallersOnly] private static int S42(nint self) => Hit(42);
    [UnmanagedCallersOnly] private static int S43(nint self) => Hit(43);
    [UnmanagedCallersOnly] private static int S44(nint self) => Hit(44);
    [UnmanagedCallersOnly] private static int S45(nint self) => Hit(45);
    [UnmanagedCallersOnly] private static int S46(nint self) => Hit(46);
    [UnmanagedCallersOnly] private static int S47(nint self) => Hit(47);
    [UnmanagedCallersOnly] private static int S48(nint self) => Hit(48);
    [UnmanagedCallersOnly] private static int S49(nint self) => Hit(49);
    [UnmanagedCallersOnly] private static int S50(nint self) => Hit(50);
    [UnmanagedCallersOnly] private static int S51(nint self) => Hit(51);
    [UnmanagedCallersOnly] private static int S52(nint self) => Hit(52);
    [UnmanagedCallersOnly] private static int S53(nint self) => Hit(53);
    [UnmanagedCallersOnly] private static int S54(nint self) => Hit(54);
    [UnmanagedCallersOnly] private static int S55(nint self) => Hit(55);
    [UnmanagedCallersOnly] private static int S56(nint self) => Hit(56);
    [UnmanagedCallersOnly] private static int S57(nint self) => Hit(57);
    [UnmanagedCallersOnly] private static int S58(nint self) => Hit(58);
    [UnmanagedCallersOnly] private static int S59(nint self) => Hit(59);
    [UnmanagedCallersOnly] private static int S60(nint self) => Hit(60);
    [UnmanagedCallersOnly] private static int S61(nint self) => Hit(61);
    [UnmanagedCallersOnly] private static int S62(nint self) => Hit(62);
    [UnmanagedCallersOnly] private static int S63(nint self) => Hit(63);
    [UnmanagedCallersOnly] private static int S64(nint self) => Hit(64);
    [UnmanagedCallersOnly] private static int S65(nint self) => Hit(65);
    [UnmanagedCallersOnly] private static int S66(nint self) => Hit(66);
    [UnmanagedCallersOnly] private static int S67(nint self) => Hit(67);
    [UnmanagedCallersOnly] private static int S68(nint self) => Hit(68);
    [UnmanagedCallersOnly] private static int S69(nint self) => Hit(69);
    [UnmanagedCallersOnly] private static int S70(nint self) => Hit(70);
    [UnmanagedCallersOnly] private static int S71(nint self) => Hit(71);
    [UnmanagedCallersOnly] private static int S72(nint self) => Hit(72);
    [UnmanagedCallersOnly] private static int S73(nint self) => Hit(73);
    [UnmanagedCallersOnly] private static int S74(nint self) => Hit(74);
    [UnmanagedCallersOnly] private static int S75(nint self) => Hit(75);
    [UnmanagedCallersOnly] private static int S76(nint self) => Hit(76);
    [UnmanagedCallersOnly] private static int S77(nint self) => Hit(77);
    [UnmanagedCallersOnly] private static int S78(nint self) => Hit(78);
    [UnmanagedCallersOnly] private static int S79(nint self) => Hit(79);
    [UnmanagedCallersOnly] private static int S80(nint self) => Hit(80);

    private static delegate* unmanaged<nint, int>[] Stubs() =>
    [
        &S0, &S1, &S2, &S3, &S4, &S5, &S6, &S7,
        &S8, &S9, &S10, &S11, &S12, &S13, &S14, &S15,
        &S16, &S17, &S18, &S19, &S20, &S21, &S22, &S23,
        &S24, &S25, &S26, &S27, &S28, &S29, &S30, &S31,
        &S32, &S33, &S34, &S35, &S36, &S37, &S38, &S39,
        &S40, &S41, &S42, &S43, &S44, &S45, &S46, &S47,
        &S48, &S49, &S50, &S51, &S52, &S53, &S54, &S55,
        &S56, &S57, &S58, &S59, &S60, &S61, &S62, &S63,
        &S64, &S65, &S66, &S67, &S68, &S69, &S70, &S71,
        &S72, &S73, &S74, &S75, &S76, &S77, &S78, &S79,
        &S80,
    ];

    private sealed class RecordingEvents : IMediaEngineEvents
    {
        public List<(uint Event, nuint Param1, uint Param2)> Received { get; } = [];

        public bool Throw { get; set; }

        public void OnEngineEvent(uint mediaEvent, nuint param1, uint param2)
        {
            Received.Add((mediaEvent, param1, param2));
            if (Throw)
            {
                throw new InvalidOperationException("subscriber failure");
            }
        }
    }
}
