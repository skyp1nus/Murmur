using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Screen;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// Screen-capture interop checked on any OS: struct layouts against the Windows SDK 10.0.26100 headers, constants,
/// and the vtable slot every raw COM wrapper calls. The expected slots were derived mechanically from
/// Microsoft.Windows.SDK.Win32Metadata 71.0.30-preview and the Windows SDK projection winmd (3/6 + base-interface
/// method counts + index); this test pins the wrappers to them. A fake object whose vtable entry N records "N" stands
/// in for the native object (extra arguments are ignored by the stubs; x64/arm64 calling conventions are
/// caller-cleaned).
/// </summary>
public sealed unsafe class ScreenInteropLayoutTests
{
    [ThreadStatic]
    private static int _lastSlot;

    [Fact]
    public void StructSizes_MatchHeaders()
    {
        Assert.Equal(16, sizeof(Rect32));
        Assert.Equal(104, sizeof(MonitorInfoExW));
        Assert.Equal(220, sizeof(DevModeW));
        Assert.Equal(840, sizeof(DisplayDeviceW));
        Assert.Equal(8, sizeof(SizeInt32Abi));
        Assert.Equal(44, sizeof(D3D11Texture2DDesc));
        Assert.Equal(24, sizeof(D3D11Box));
        Assert.Equal(IntPtr.Size == 8 ? 16 : 12, sizeof(D3D11SubresourceData));
        Assert.Equal(40, sizeof(D3D11VideoProcessorContentDesc));
        Assert.Equal(16, sizeof(D3D11VideoProcessorInputViewDesc));
        Assert.Equal(16, sizeof(D3D11VideoProcessorOutputViewDesc));
        Assert.Equal(IntPtr.Size == 8 ? 72 : 44, sizeof(D3D11VideoProcessorStream));
        Assert.Equal(16, sizeof(D3D11VideoColorRgba));
    }

    [Fact]
    public void FieldOffsets_MatchHeaders()
    {
        Assert.Equal(4, Offset<MonitorInfoExW>(nameof(MonitorInfoExW.Monitor)));
        Assert.Equal(36, Offset<MonitorInfoExW>(nameof(MonitorInfoExW.Flags)));
        Assert.Equal(40, Offset<MonitorInfoExW>(nameof(MonitorInfoExW.Device)));

        Assert.Equal(68, Offset<DevModeW>(nameof(DevModeW.Size)));
        Assert.Equal(72, Offset<DevModeW>(nameof(DevModeW.Fields)));
        Assert.Equal(76, Offset<DevModeW>(nameof(DevModeW.PositionX)));
        Assert.Equal(168, Offset<DevModeW>(nameof(DevModeW.BitsPerPel)));
        Assert.Equal(172, Offset<DevModeW>(nameof(DevModeW.PelsWidth)));
        Assert.Equal(176, Offset<DevModeW>(nameof(DevModeW.PelsHeight)));
        Assert.Equal(184, Offset<DevModeW>(nameof(DevModeW.DisplayFrequency)));

        Assert.Equal(4, Offset<DisplayDeviceW>(nameof(DisplayDeviceW.DeviceName)));
        Assert.Equal(68, Offset<DisplayDeviceW>(nameof(DisplayDeviceW.DeviceString)));
        Assert.Equal(324, Offset<DisplayDeviceW>(nameof(DisplayDeviceW.StateFlags)));
        Assert.Equal(328, Offset<DisplayDeviceW>(nameof(DisplayDeviceW.DeviceId)));
        Assert.Equal(584, Offset<DisplayDeviceW>(nameof(DisplayDeviceW.DeviceKey)));

        Assert.Equal(16, Offset<D3D11Texture2DDesc>(nameof(D3D11Texture2DDesc.Format)));
        Assert.Equal(20, Offset<D3D11Texture2DDesc>(nameof(D3D11Texture2DDesc.SampleDesc)));
        Assert.Equal(28, Offset<D3D11Texture2DDesc>(nameof(D3D11Texture2DDesc.Usage)));
        Assert.Equal(32, Offset<D3D11Texture2DDesc>(nameof(D3D11Texture2DDesc.BindFlags)));

        Assert.Equal(4, Offset<D3D11VideoProcessorContentDesc>(nameof(D3D11VideoProcessorContentDesc.InputFrameRate)));
        Assert.Equal(20, Offset<D3D11VideoProcessorContentDesc>(nameof(D3D11VideoProcessorContentDesc.OutputFrameRate)));
        Assert.Equal(36, Offset<D3D11VideoProcessorContentDesc>(nameof(D3D11VideoProcessorContentDesc.Usage)));

        if (IntPtr.Size == 8)
        {
            Assert.Equal(24, Offset<D3D11VideoProcessorStream>(nameof(D3D11VideoProcessorStream.PastSurfaces)));
            Assert.Equal(32, Offset<D3D11VideoProcessorStream>(nameof(D3D11VideoProcessorStream.InputSurface)));
            Assert.Equal(56, Offset<D3D11VideoProcessorStream>(nameof(D3D11VideoProcessorStream.InputSurfaceRight)));
        }
    }

    [Fact]
    public void Constants_MatchHeaders()
    {
        Assert.Equal(0x0002_0070u, ScreenConstants.MfVersion);
        Assert.Equal(1u, ScreenConstants.MfStartupLite);
        Assert.Equal(unchecked((int)0xC00D4A3E), ScreenConstants.MfESampleAllocatorEmpty);
        Assert.Equal(unchecked((int)0xC00D4A44), ScreenConstants.MfESinkNoSamplesProcessed);
        Assert.Equal(7u, ScreenConstants.D3D11SdkVersion);
        Assert.Equal(87u, ScreenConstants.DxgiFormatB8G8R8A8Unorm);
        Assert.Equal(0x20u | 0x800u, ScreenConstants.D3D11CreateDeviceBgraSupport | ScreenConstants.D3D11CreateDeviceVideoSupport);
        Assert.Equal((1, 5), (ScreenConstants.D3DDriverTypeHardware, ScreenConstants.D3DDriverTypeWarp));
        Assert.Equal(0x28u, ScreenConstants.D3D11BindRenderTarget | ScreenConstants.D3D11BindShaderResource);
        Assert.Equal(0x0018_0000u, ScreenConstants.DmPelsWidthHeight);
        Assert.Equal(0xFFFF_FFFCu, ScreenConstants.MfSourceReaderFirstVideoStream);
        Assert.Equal(2u, ScreenConstants.MfVideoInterlaceProgressive);
        Assert.Equal(100u, ScreenConstants.H264ProfileHigh);
    }

    [Fact]
    public void Guids_MatchHeaders()
    {
        Assert.Equal(new Guid("9BA876F1-419F-4B77-A1E0-35959D9D4004"), ScreenConstants.MfTranscodeContainerTypeFmpeg4);
        Assert.Equal(new Guid("34363248-0000-0010-8000-00AA00389B71"), ScreenConstants.MfVideoFormatH264);
        Assert.Equal(new Guid("00000015-0000-0010-8000-00AA00389B71"), ScreenConstants.MfVideoFormatArgb32);
        Assert.Equal(new Guid("73646976-0000-0010-8000-00AA00389B71"), ScreenConstants.MfMediaTypeVideo);
        Assert.Equal(new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), ScreenConstants.IidIGraphicsCaptureItemInterop);
        Assert.Equal(new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), ScreenConstants.IidIDirect3DDxgiInterfaceAccess);

        // FourCC subtypes: Data1 = 'H264' / D3DFMT_A8R8G8B8 (21), the rest is the MFVideoFormat base GUID.
        var h264 = ScreenConstants.MfVideoFormatH264.ToByteArray();
        Assert.Equal("H264", System.Text.Encoding.ASCII.GetString(h264, 0, 4));
        Assert.Equal(21, BitConverter.ToInt32(ScreenConstants.MfVideoFormatArgb32.ToByteArray(), 0));
    }

    public static TheoryData<string, int> Slots() => new()
    {
        { "IUnknown::QueryInterface", 0 },
        { "IUnknown::AddRef", 1 },
        { "IUnknown::Release", 2 },
        { "IMFAttributes::GetUINT32", 7 },
        { "IMFAttributes::GetUINT64", 8 },
        { "IMFAttributes::GetGUID", 10 },
        { "IMFAttributes::GetStringLength", 11 },
        { "IMFAttributes::GetAllocatedString", 13 },
        { "IMFAttributes::SetUINT32", 21 },
        { "IMFAttributes::SetUINT64", 22 },
        { "IMFAttributes::SetGUID", 24 },
        { "IMFAttributes::SetUnknown", 27 },
        { "IMFSample::SetSampleTime", 36 },
        { "IMFSample::GetSampleDuration", 37 },
        { "IMFSample::SetSampleDuration", 38 },
        { "IMFSample::GetBufferByIndex", 40 },
        { "IMFDXGIBuffer::GetResource", 3 },
        { "IMFDXGIBuffer::GetSubresourceIndex", 4 },
        { "IMFSinkWriter::AddStream", 3 },
        { "IMFSinkWriter::SetInputMediaType", 4 },
        { "IMFSinkWriter::BeginWriting", 5 },
        { "IMFSinkWriter::WriteSample", 6 },
        { "IMFSinkWriter::Finalize", 11 },
        { "IMFSinkWriter::GetServiceForStream", 12 },
        { "IMFDXGIDeviceManager::ResetDevice", 7 },
        { "IMFVideoSampleAllocator::SetDirectXManager", 3 },
        { "IMFVideoSampleAllocator::UninitializeSampleAllocator", 4 },
        { "IMFVideoSampleAllocator::AllocateSample", 6 },
        { "IMFVideoSampleAllocatorEx::InitializeSampleAllocatorEx", 7 },
        { "IMFTransform::GetAttributes", 8 },
        { "IMFSourceReader::GetCurrentMediaType", 6 },
        { "IMFSourceReader::ReadSample", 9 },
        { "IMFSourceReader::GetPresentationAttribute", 12 },
        { "ID3D11Device::CreateTexture2D", 5 },
        { "ID3D11Device::GetFeatureLevel", 37 },
        { "ID3D11Device::GetDeviceRemovedReason", 39 },
        { "ID3D11DeviceContext::CopySubresourceRegion", 46 },
        { "ID3D11DeviceContext::CopyResource", 47 },
        { "ID3D11Texture2D::GetDesc", 10 },
        { "ID3D11Multithread::SetMultithreadProtected", 5 },
        { "ID3D11VideoDevice::CreateVideoProcessor", 4 },
        { "ID3D11VideoDevice::CreateVideoProcessorInputView", 8 },
        { "ID3D11VideoDevice::CreateVideoProcessorOutputView", 9 },
        { "ID3D11VideoDevice::CreateVideoProcessorEnumerator", 10 },
        { "ID3D11VideoContext::VideoProcessorSetOutputTargetRect", 13 },
        { "ID3D11VideoContext::VideoProcessorSetOutputBackgroundColor", 14 },
        { "ID3D11VideoContext::VideoProcessorSetStreamFrameFormat", 27 },
        { "ID3D11VideoContext::VideoProcessorSetStreamSourceRect", 30 },
        { "ID3D11VideoContext::VideoProcessorSetStreamDestRect", 31 },
        { "ID3D11VideoContext::VideoProcessorSetStreamAutoProcessingMode", 37 },
        { "ID3D11VideoContext::VideoProcessorBlt", 53 },
        { "IDirect3DDxgiInterfaceAccess::GetInterface", 3 },
        { "IGraphicsCaptureItemInterop::CreateForMonitor", 4 },
        { "IDirect3D11CaptureFramePool::TryGetNextFrame", 7 },
        { "IDirect3D11CaptureFrame::get_Surface", 6 },
        { "IDirect3D11CaptureFrame::get_SystemRelativeTime", 7 },
        { "IDirect3D11CaptureFrame::get_ContentSize", 8 },
        { "IClosable::Close", 6 },
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

    private static void Invoke(string method, nint s)
    {
        Guid g = default;
        nint p;
        uint u;
        ulong ul;
        long l;
        char* c;
        D3D11Texture2DDesc t;
        Rect32 r;
        D3D11VideoColorRgba color;
        D3D11VideoProcessorContentDesc content;
        D3D11VideoProcessorInputViewDesc input;
        D3D11VideoProcessorOutputViewDesc output;
        D3D11VideoProcessorStream stream;
        SizeInt32Abi size;
        switch (method)
        {
            case "IUnknown::QueryInterface": ComCall.QueryInterface(s, &g, &p); break;
            case "IUnknown::AddRef": ComCall.AddRef(s); break;
            case "IUnknown::Release": ComCall.ReleaseRaw(s); break;
            case "IMFAttributes::GetUINT32": IMFAttributes.GetUINT32(s, &g, &u); break;
            case "IMFAttributes::GetUINT64": IMFAttributes.GetUINT64(s, &g, &ul); break;
            case "IMFAttributes::GetGUID": IMFAttributes.GetGUID(s, &g, &g); break;
            case "IMFAttributes::GetStringLength": IMFAttributes.GetStringLength(s, &g, &u); break;
            case "IMFAttributes::GetAllocatedString": IMFAttributes.GetAllocatedString(s, &g, &c, &u); break;
            case "IMFAttributes::SetUINT32": IMFAttributes.SetUINT32(s, &g, 0); break;
            case "IMFAttributes::SetUINT64": IMFAttributes.SetUINT64(s, &g, 0); break;
            case "IMFAttributes::SetGUID": IMFAttributes.SetGUID(s, &g, &g); break;
            case "IMFAttributes::SetUnknown": IMFAttributes.SetUnknown(s, &g, 0); break;
            case "IMFSample::SetSampleTime": IMFSample.SetSampleTime(s, 0); break;
            case "IMFSample::GetSampleDuration": IMFSample.GetSampleDuration(s, &l); break;
            case "IMFSample::SetSampleDuration": IMFSample.SetSampleDuration(s, 0); break;
            case "IMFSample::GetBufferByIndex": IMFSample.GetBufferByIndex(s, 0, &p); break;
            case "IMFDXGIBuffer::GetResource": IMFDXGIBuffer.GetResource(s, &g, &p); break;
            case "IMFDXGIBuffer::GetSubresourceIndex": IMFDXGIBuffer.GetSubresourceIndex(s, &u); break;
            case "IMFSinkWriter::AddStream": IMFSinkWriter.AddStream(s, 0, &u); break;
            case "IMFSinkWriter::SetInputMediaType": IMFSinkWriter.SetInputMediaType(s, 0, 0, 0); break;
            case "IMFSinkWriter::BeginWriting": IMFSinkWriter.BeginWriting(s); break;
            case "IMFSinkWriter::WriteSample": IMFSinkWriter.WriteSample(s, 0, 0); break;
            case "IMFSinkWriter::Finalize": IMFSinkWriter.Finalize(s); break;
            case "IMFSinkWriter::GetServiceForStream": IMFSinkWriter.GetServiceForStream(s, 0, &g, &g, &p); break;
            case "IMFDXGIDeviceManager::ResetDevice": IMFDXGIDeviceManager.ResetDevice(s, 0, 0); break;
            case "IMFVideoSampleAllocator::SetDirectXManager": IMFVideoSampleAllocatorEx.SetDirectXManager(s, 0); break;
            case "IMFVideoSampleAllocator::UninitializeSampleAllocator": IMFVideoSampleAllocatorEx.UninitializeSampleAllocator(s); break;
            case "IMFVideoSampleAllocator::AllocateSample": IMFVideoSampleAllocatorEx.AllocateSample(s, &p); break;
            case "IMFVideoSampleAllocatorEx::InitializeSampleAllocatorEx": IMFVideoSampleAllocatorEx.InitializeSampleAllocatorEx(s, 0, 0, 0, 0); break;
            case "IMFTransform::GetAttributes": IMFTransform.GetAttributes(s, &p); break;
            case "IMFSourceReader::GetCurrentMediaType": IMFSourceReader.GetCurrentMediaType(s, 0, &p); break;
            case "IMFSourceReader::ReadSample": IMFSourceReader.ReadSample(s, 0, 0, &u, &u, &l, &p); break;
            case "IMFSourceReader::GetPresentationAttribute": IMFSourceReader.GetPresentationAttribute(s, 0, &g, &l); break;
            case "ID3D11Device::CreateTexture2D": ID3D11Device.CreateTexture2D(s, &t, null, &p); break;
            case "ID3D11Device::GetFeatureLevel": ID3D11Device.GetFeatureLevel(s); break;
            case "ID3D11Device::GetDeviceRemovedReason": ID3D11Device.GetDeviceRemovedReason(s); break;
            case "ID3D11DeviceContext::CopySubresourceRegion": ID3D11DeviceContext.CopySubresourceRegion(s, 0, 0, 0, 0, 0, 0, 0, null); break;
            case "ID3D11DeviceContext::CopyResource": ID3D11DeviceContext.CopyResource(s, 0, 0); break;
            case "ID3D11Texture2D::GetDesc": ID3D11Texture2D.GetDesc(s, &t); break;
            case "ID3D11Multithread::SetMultithreadProtected": ID3D11Multithread.SetMultithreadProtected(s, 1); break;
            case "ID3D11VideoDevice::CreateVideoProcessor": ID3D11VideoDevice.CreateVideoProcessor(s, 0, 0, &p); break;
            case "ID3D11VideoDevice::CreateVideoProcessorInputView": ID3D11VideoDevice.CreateVideoProcessorInputView(s, 0, 0, &input, &p); break;
            case "ID3D11VideoDevice::CreateVideoProcessorOutputView": ID3D11VideoDevice.CreateVideoProcessorOutputView(s, 0, 0, &output, &p); break;
            case "ID3D11VideoDevice::CreateVideoProcessorEnumerator": ID3D11VideoDevice.CreateVideoProcessorEnumerator(s, &content, &p); break;
            case "ID3D11VideoContext::VideoProcessorSetOutputTargetRect": ID3D11VideoContext.VideoProcessorSetOutputTargetRect(s, 0, 1, &r); break;
            case "ID3D11VideoContext::VideoProcessorSetOutputBackgroundColor": ID3D11VideoContext.VideoProcessorSetOutputBackgroundColor(s, 0, 0, &color); break;
            case "ID3D11VideoContext::VideoProcessorSetStreamFrameFormat": ID3D11VideoContext.VideoProcessorSetStreamFrameFormat(s, 0, 0, 0); break;
            case "ID3D11VideoContext::VideoProcessorSetStreamSourceRect": ID3D11VideoContext.VideoProcessorSetStreamSourceRect(s, 0, 0, 1, &r); break;
            case "ID3D11VideoContext::VideoProcessorSetStreamDestRect": ID3D11VideoContext.VideoProcessorSetStreamDestRect(s, 0, 0, 1, &r); break;
            case "ID3D11VideoContext::VideoProcessorSetStreamAutoProcessingMode": ID3D11VideoContext.VideoProcessorSetStreamAutoProcessingMode(s, 0, 0, 0); break;
            case "ID3D11VideoContext::VideoProcessorBlt": ID3D11VideoContext.VideoProcessorBlt(s, 0, 0, 0, 1, &stream); break;
            case "IDirect3DDxgiInterfaceAccess::GetInterface": IDirect3DDxgiInterfaceAccess.GetInterface(s, &g, &p); break;
            case "IGraphicsCaptureItemInterop::CreateForMonitor": IGraphicsCaptureItemInterop.CreateForMonitor(s, 0, &g, &p); break;
            case "IDirect3D11CaptureFramePool::TryGetNextFrame": IDirect3D11CaptureFramePool.TryGetNextFrame(s, &p); break;
            case "IDirect3D11CaptureFrame::get_Surface": IDirect3D11CaptureFrame.get_Surface(s, &p); break;
            case "IDirect3D11CaptureFrame::get_SystemRelativeTime": IDirect3D11CaptureFrame.get_SystemRelativeTime(s, &l); break;
            case "IDirect3D11CaptureFrame::get_ContentSize": IDirect3D11CaptureFrame.get_ContentSize(s, &size); break;
            case "IClosable::Close": IClosable.Close(s); break;
            default: throw new ArgumentOutOfRangeException(nameof(method), method, null);
        }
    }

    private static int Offset<T>(string field) => (int)Marshal.OffsetOf<T>(field);

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

    private static delegate* unmanaged<nint, int>[] Stubs() =>
    [
        &S0, &S1, &S2, &S3, &S4, &S5, &S6, &S7,
        &S8, &S9, &S10, &S11, &S12, &S13, &S14, &S15,
        &S16, &S17, &S18, &S19, &S20, &S21, &S22, &S23,
        &S24, &S25, &S26, &S27, &S28, &S29, &S30, &S31,
        &S32, &S33, &S34, &S35, &S36, &S37, &S38, &S39,
        &S40, &S41, &S42, &S43, &S44, &S45, &S46, &S47,
        &S48, &S49, &S50, &S51, &S52, &S53, &S54, &S55,
    ];
}
