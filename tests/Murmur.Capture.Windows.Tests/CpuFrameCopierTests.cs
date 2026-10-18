using System.Buffers.Binary;
using Murmur.Capture.Windows.Screen;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// The CPU readback frame path end to end on fake D3D11 / Media Foundation objects (any OS): staging copy, Map, CPU copy
/// or scale into the pooled system-memory sample, and the buffer's current length (the WriteSample E_INVALIDARG fix).
/// </summary>
public sealed unsafe class CpuFrameCopierTests : IDisposable
{
    private readonly FakeDevice _device = new();
    private readonly FakeDeviceContext _context = new();
    private readonly FakeSampleFactory _factory = new();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var item in _owned)
        {
            item.Dispose();
        }

        _factory.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    [Fact]
    public void CopyFrame_SameSize_CopiesThePicture_AndSetsTheFullLength()
    {
        var capture = Capture(64, 48, (x, y) => 0xFF00_0000 | (uint)((y << 8) | x));
        using var copier = Copier(new PixelSize(64, 48));

        Assert.Equal(ScreenConstants.SOk, copier.AllocateSample(out var sample));
        Assert.Equal(ScreenConstants.SOk, copier.CopyFrame(sample, capture.Pointer, new PixelSize(64, 48)));

        var buffer = _factory.Buffers[0];
        Assert.Equal(64u * 48 * 4, buffer.CurrentLength);
        Assert.Equal(0, buffer.LockDepth);
        for (var y = 0; y < 48; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                Assert.Equal(capture[x, y], Pixel(buffer, 64, x, y));
            }
        }

        var staging = Assert.Single(_device.Created);
        Assert.Equal(ScreenConstants.D3D11UsageStaging, staging.Desc.Usage);
        Assert.Equal(ScreenConstants.D3D11CpuAccessRead, staging.Desc.CpuAccessFlags);
        Assert.Equal(0u, staging.Desc.BindFlags);
        Assert.Equal(capture.Desc.Format, staging.Desc.Format);
        Assert.Equal((64u, 48u, 1u, 1u), (staging.Desc.Width, staging.Desc.Height, staging.Desc.MipLevels, staging.Desc.ArraySize));
        Assert.Equal(new D3D11Box { Right = 64, Bottom = 48, Back = 1 }, _context.LastBox);
        Assert.Equal((1, 1, ScreenConstants.D3D11MapRead), (_context.Maps, _context.Unmaps, _context.LastMapType));
        Assert.Equal(EncoderInput.Cpu, copier.Input);
        ComCall.Release(ref sample);
    }

    [Fact]
    public void CopyFrame_ScalesTheCiDisplay_1024x768_IntoA960x720Frame()
    {
        // The windows-latest geometry that hit the video-processor path (MaxHeight 720): on the CPU path no D3D11 video
        // support is needed.
        var encoder = EncoderGeometry.FitEncoderSize(1024, 768, 720);
        var capture = Capture(1024, 768, (_, _) => 0xFF20_4060);
        using var copier = Copier(encoder);

        copier.AllocateSample(out var sample);
        Assert.Equal(ScreenConstants.SOk, copier.CopyFrame(sample, capture.Pointer, new PixelSize(1024, 768)));

        var buffer = _factory.Buffers[0];
        Assert.Equal(960u * 720 * 4, buffer.CurrentLength);
        Assert.Equal(new D3D11Box { Right = 1024, Bottom = 768, Back = 1 }, _context.LastBox);
        foreach (var (x, y) in (ReadOnlySpan<(int, int)>)[(0, 0), (959, 0), (480, 360), (0, 719), (959, 719)])
        {
            Assert.Equal(0xFF20_4060u, Pixel(buffer, 960, x, y));
        }

        ComCall.Release(ref sample);
    }

    [Fact]
    public void CopyFrame_ContentSmallerThanTheTexture_CopiesOnlyTheContent_AndLetterboxes()
    {
        // The display shrank to 32x48 before the frame pool was recreated: the texture is still 64x48.
        var capture = Capture(64, 48, (x, _) => x < 32 ? 0xFFFF_FFFF : 0xFF12_3456);
        using var copier = Copier(new PixelSize(64, 48));

        copier.AllocateSample(out var sample);
        Assert.Equal(ScreenConstants.SOk, copier.CopyFrame(sample, capture.Pointer, new PixelSize(32, 48)));

        var buffer = _factory.Buffers[0];
        Assert.Equal(new D3D11Box { Right = 32, Bottom = 48, Back = 1 }, _context.LastBox);
        Assert.Equal(0xFF00_0000u, Pixel(buffer, 64, 0, 24));   // pillarbox
        Assert.Equal(0xFFFF_FFFFu, Pixel(buffer, 64, 32, 24));  // scaled content, none of the stale right half
        Assert.Equal(0xFF00_0000u, Pixel(buffer, 64, 63, 24));
        ComCall.Release(ref sample);
    }

    [Fact]
    public void CopyFrame_ReusesTheStagingTexture_UntilTheCaptureSizeChanges()
    {
        var small = Capture(32, 24, (_, _) => 0xFF01_0203);
        var large = Capture(40, 30, (_, _) => 0xFF04_0506);
        using var copier = Copier(new PixelSize(32, 24));

        for (var i = 0; i < 3; i++)
        {
            copier.AllocateSample(out var sample);
            Assert.Equal(ScreenConstants.SOk, copier.CopyFrame(sample, small.Pointer, new PixelSize(32, 24)));
            ComCall.Release(ref sample);
        }

        Assert.Single(_device.Created);
        copier.AllocateSample(out var resized);
        Assert.Equal(ScreenConstants.SOk, copier.CopyFrame(resized, large.Pointer, new PixelSize(40, 30)));
        Assert.Equal(2, _device.Created.Count);
        Assert.Equal((40u, 30u), (_device.Created[1].Desc.Width, _device.Created[1].Desc.Height));
        Assert.Equal(0, _device.Created[0].References); // the old staging texture was released
        ComCall.Release(ref resized);
    }

    [Fact]
    public void CopyFrame_RejectsTexturesThatAreNot32BitBgra()
    {
        var capture = Capture(16, 16, (_, _) => 0, format: 28); // DXGI_FORMAT_R8G8B8A8_UNORM
        using var copier = Copier(new PixelSize(16, 16));
        copier.AllocateSample(out var sample);

        Assert.Equal(ScreenConstants.EInvalidArg, copier.CopyFrame(sample, capture.Pointer, new PixelSize(16, 16)));
        Assert.Equal(new FrameFailure(FrameStep.SourceFormat, ScreenConstants.EInvalidArg), copier.LastFailure);
        Assert.Equal(0, _context.Maps);
        ComCall.Release(ref sample);
    }

    [Fact]
    public void CopyFrame_MapFailure_NamesTheStep_AndCommitsNothing()
    {
        const int DeviceRemoved = unchecked((int)0x887A0005);
        var capture = Capture(16, 16, (_, _) => 0xFFFF_FFFF);
        using var copier = Copier(new PixelSize(16, 16));
        copier.AllocateSample(out var sample);
        _context.MapResult = DeviceRemoved;

        Assert.Equal(DeviceRemoved, copier.CopyFrame(sample, capture.Pointer, new PixelSize(16, 16)));
        Assert.Equal(new FrameFailure(FrameStep.MapStaging, DeviceRemoved), copier.LastFailure);
        Assert.Equal(0, _context.Unmaps);
        Assert.Equal(0, _factory.Buffers[0].Locks);
        Assert.Equal(0u, _factory.Buffers[0].CurrentLength);
        Assert.Contains("ID3D11DeviceContext::Map", FrameCopyNaming.Fault(EncoderInput.Cpu, copier.LastFailure, DeviceRemoved), StringComparison.Ordinal);
        ComCall.Release(ref sample);
    }

    [Fact]
    public void CopyFrame_StagingCreationFailure_NamesTheStep()
    {
        var capture = Capture(16, 16, (_, _) => 0xFFFF_FFFF);
        using var copier = Copier(new PixelSize(16, 16));
        copier.AllocateSample(out var sample);
        _device.CreateResult = unchecked((int)0x8007000E);

        Assert.Equal(unchecked((int)0x8007000E), copier.CopyFrame(sample, capture.Pointer, new PixelSize(16, 16)));
        Assert.Equal(FrameStep.StagingTexture, copier.LastFailure.Step);
        ComCall.Release(ref sample);
    }

    [Fact]
    public void LockFailure_UnmapsTheStaging_AndNamesTheStep()
    {
        var capture = Capture(16, 16, (_, _) => 0xFFFF_FFFF);
        using var copier = Copier(new PixelSize(16, 16));
        copier.AllocateSample(out var sample);
        _factory.Buffers[0].LockResult = ScreenConstants.EFail;

        Assert.Equal(ScreenConstants.EFail, copier.CopyFrame(sample, capture.Pointer, new PixelSize(16, 16)));
        Assert.Equal(new FrameFailure(FrameStep.LockBuffer, ScreenConstants.EFail), copier.LastFailure);
        Assert.Equal((1, 1), (_context.Maps, _context.Unmaps));
        ComCall.Release(ref sample);
    }

    [Fact]
    public void BufferSmallerThanAFrame_IsUnlocked_AndReported()
    {
        var capture = Capture(16, 16, (_, _) => 0xFFFF_FFFF);
        using var copier = Copier(new PixelSize(16, 16));
        copier.AllocateSample(out var sample);
        _factory.Buffers[0].MaxLength = 16;

        Assert.Equal(ScreenConstants.EInvalidArg, copier.CopyFrame(sample, capture.Pointer, new PixelSize(16, 16)));
        Assert.Equal(FrameStep.BufferTooSmall, copier.LastFailure.Step);
        Assert.Equal(0, _factory.Buffers[0].LockDepth);
        ComCall.Release(ref sample);
    }

    [Fact]
    public void UnknownSamples_AreRejected()
    {
        var capture = Capture(16, 16, (_, _) => 0);
        using var copier = Copier(new PixelSize(16, 16));
        using var stranger = new FakeSample();

        Assert.Equal(ScreenConstants.EInvalidArg, copier.CopyFrame(stranger.Pointer, capture.Pointer, new PixelSize(16, 16)));
        Assert.Equal(FrameStep.SampleCache, copier.LastFailure.Step);
        Assert.Equal(ScreenConstants.EInvalidArg, copier.FillBlack(stranger.Pointer));
        Assert.Equal(ScreenConstants.EInvalidArg, copier.CopySample(stranger.Pointer, stranger.Pointer));
    }

    [Fact]
    public void FillBlack_WritesOpaqueBlack_AndSetsTheFullLength()
    {
        using var copier = Copier(new PixelSize(20, 10));
        copier.AllocateSample(out var sample);
        _factory.Buffers[0].Bytes.Fill(0x55);

        Assert.Equal(ScreenConstants.SOk, copier.FillBlack(sample));

        var buffer = _factory.Buffers[0];
        Assert.Equal(20u * 10 * 4, buffer.CurrentLength);
        for (var i = 0; i < 20 * 10; i++)
        {
            Assert.Equal(PlaceholderFrame.OpaqueBlackBgra, BinaryPrimitives.ReadUInt32LittleEndian(buffer.Bytes.Slice(i * 4, 4)));
        }

        ComCall.Release(ref sample);
    }

    [Fact]
    public void CopySample_DuplicatesTheLastFrame_IntoAnotherPoolSample()
    {
        var capture = Capture(20, 10, (x, y) => 0xFF00_0000 | (uint)((x * 7) + y));
        using var copier = Copier(new PixelSize(20, 10));
        copier.AllocateSample(out var last);
        copier.CopyFrame(last, capture.Pointer, new PixelSize(20, 10));

        Assert.Equal(ScreenConstants.SOk, copier.AllocateSample(out var repeat));
        Assert.NotEqual(last, repeat); // the last frame is still held
        Assert.Equal(ScreenConstants.SOk, copier.CopySample(repeat, last));

        Assert.True(_factory.Buffers[0].Bytes[..(20 * 10 * 4)].SequenceEqual(_factory.Buffers[1].Bytes[..(20 * 10 * 4)]));
        Assert.Equal(20u * 10 * 4, _factory.Buffers[1].CurrentLength);
        Assert.All(_factory.Buffers, b => Assert.Equal(0, b.LockDepth));
        ComCall.Release(ref last);
        ComCall.Release(ref repeat);
    }

    [Fact]
    public void CopyFrame_SteadyState_DoesNotAllocate()
    {
        var capture = Capture(96, 54, (x, y) => (uint)(x ^ y));
        using var copier = Copier(new PixelSize(64, 36));
        void Frames()
        {
            for (var i = 0; i < 20; i++)
            {
                copier.AllocateSample(out var sample);
                copier.CopyFrame(sample, capture.Pointer, new PixelSize(96, 54));
                ComCall.Release(ref sample);
            }
        }

        Frames();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Frames();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(ScreenConstants.SOk, copier.LastFailure.HResult);
    }

    [Fact]
    public void Geometry_Helpers()
    {
        Assert.Equal(1920 * 4, CpuFrameCopier.Stride(new PixelSize(1920, 1080)));
        Assert.Equal(1920u * 4 * 1080, CpuFrameCopier.FrameBytes(new PixelSize(1920, 1080)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CpuFrameCopier.FrameBytes(new PixelSize(1920, 0)));
        Assert.True(CpuFrameCopier.IsBgra32(ScreenConstants.DxgiFormatB8G8R8A8Unorm));
        Assert.True(CpuFrameCopier.IsBgra32(ScreenConstants.DxgiFormatB8G8R8X8Unorm));
        Assert.True(CpuFrameCopier.IsBgra32(ScreenConstants.DxgiFormatB8G8R8A8UnormSrgb));
        Assert.True(CpuFrameCopier.IsBgra32(ScreenConstants.DxgiFormatB8G8R8X8UnormSrgb));
        Assert.False(CpuFrameCopier.IsBgra32(28));  // R8G8B8A8_UNORM
        Assert.False(CpuFrameCopier.IsBgra32(10));  // R16G16B16A16_FLOAT (HDR)
    }

    private static uint Pixel(FakeMediaBuffer buffer, int width, int x, int y) =>
        BinaryPrimitives.ReadUInt32LittleEndian(buffer.Bytes.Slice(((y * width) + x) * 4, 4));

    private CpuFrameCopier Copier(PixelSize encoder) =>
        new(_device.Pointer, _context.Pointer, encoder, _factory, initialSamples: 1, maximumSamples: 3);

    private FakeTexture Capture(int width, int height, Func<int, int, uint> pixel, uint format = ScreenConstants.DxgiFormatB8G8R8A8Unorm)
    {
        var texture = new FakeTexture(FakeTexture.Bgra(width, height, format));
        texture.Fill(pixel);
        _owned.Add(texture);
        return texture;
    }
}
