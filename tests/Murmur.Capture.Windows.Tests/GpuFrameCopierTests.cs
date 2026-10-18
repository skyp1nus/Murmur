using Murmur.Capture.Windows.Screen;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// The zero-copy frame path on fake D3D11 / Media Foundation objects (any OS). Pins the two failures seen on
/// windows-latest (WARP, 1024x768, MaxHeight 720): the scaled path needs ID3D11VideoDevice (E_NOINTERFACE without video
/// support, now named in the fault), and every filled DXGI sample must carry its current length (WriteSample rejected
/// the placeholder with E_INVALIDARG while the length was 0).
/// </summary>
public sealed class GpuFrameCopierTests : IDisposable
{
    private readonly FakeDevice _device = new();
    private readonly FakeDeviceContext _context = new();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
        {
            _owned[i].Dispose();
        }

        _context.Dispose();
        _device.Dispose();
    }

    [Fact]
    public void FillBlack_DrawsBlack_AndSetsTheDxgiBuffersCurrentLength()
    {
        var encoder = new PixelSize(96, 72);
        var target = AllocatorSample(encoder, subresource: 0, out var buffer, out _);
        using var copier = Copier(encoder);
        Assert.Equal(0u, buffer.CurrentLength); // DXGI surface buffers start empty

        Assert.Equal(ScreenConstants.SOk, copier.FillBlack(target.Sample.Pointer));

        Assert.Equal(96u * 72 * 4, buffer.CurrentLength);
        Assert.Equal(1, buffer.SetLengthCalls);
        Assert.Equal(PlaceholderFrame.OpaqueBlackBgra, target.Texture[0, 0]);
        Assert.Equal(PlaceholderFrame.OpaqueBlackBgra, target.Texture[95, 71]);
    }

    [Fact]
    public void CopyFrame_SameSize_CopiesOnTheGpu_IntoTheSamplesSubresource_AndSetsTheLength()
    {
        var encoder = new PixelSize(64, 48);
        var target = AllocatorSample(encoder, subresource: 3, out var buffer, out _);
        var capture = Capture(64, 48);
        using var copier = Copier(encoder);

        Assert.Equal(ScreenConstants.SOk, copier.CopyFrame(target.Sample.Pointer, capture.Pointer, encoder));

        Assert.Equal(1, _context.Copies);
        Assert.Equal(3u, _context.LastDestinationSubresource);
        Assert.Equal(new D3D11Box { Right = 64, Bottom = 48, Back = 1 }, _context.LastBox);
        Assert.Equal(64u * 48 * 4, buffer.CurrentLength);
        Assert.Equal(0, _context.Maps); // zero-copy: no CPU readback
        Assert.Equal(EncoderInput.Gpu, copier.Input);
    }

    [Fact]
    public void CopyFrame_ThatNeedsScaling_OnADeviceWithoutVideoSupport_FailsAtTheVideoDeviceQuery()
    {
        // windows-latest: 1024x768 -> 960x720 needs the video processor; the device answers no ID3D11VideoDevice.
        var encoder = EncoderGeometry.FitEncoderSize(1024, 768, 720);
        var target = AllocatorSample(encoder, subresource: 0, out var buffer, out _);
        var capture = Capture(1024, 768);
        using var copier = Copier(encoder);

        var hr = copier.CopyFrame(target.Sample.Pointer, capture.Pointer, new PixelSize(1024, 768));

        Assert.Equal(ScreenConstants.ENoInterface, hr);
        Assert.Equal(new FrameFailure(FrameStep.VideoDevice, ScreenConstants.ENoInterface), copier.LastFailure);
        Assert.Equal(0u, buffer.CurrentLength); // nothing committed
        Assert.Equal(
            "GPU frame copy failed at QueryInterface(ID3D11VideoDevice) on the D3D11 device (no video support): HRESULT 0x80004002",
            FrameCopyNaming.Fault(copier.Input, copier.LastFailure, hr));
    }

    [Fact]
    public void CopySample_CopiesTheWholePicture_AndSetsTheLength()
    {
        var encoder = new PixelSize(32, 16);
        var source = AllocatorSample(encoder, subresource: 0, out _, out _);
        var destination = AllocatorSample(encoder, subresource: 1, out var destinationBuffer, out _);
        source.Texture.Fill((x, y) => 0xFF00_0000 | (uint)((y << 8) | x));
        using var copier = Copier(encoder);

        Assert.Equal(ScreenConstants.SOk, copier.CopySample(destination.Sample.Pointer, source.Sample.Pointer));

        Assert.Null(_context.LastBox);
        Assert.Equal(1u, _context.LastDestinationSubresource);
        Assert.Equal(source.Texture[31, 15], destination.Texture[31, 15]);
        Assert.Equal(32u * 16 * 4, destinationBuffer.CurrentLength);
    }

    [Fact]
    public void ContiguousLength_PrefersIMF2DBuffer_ThenGetMaxLength_ThenTheFrameSize()
    {
        using var withBoth = new FakeMediaBuffer(1000);
        var face = new Fake2DBufferFace(withBoth, 777);
        _owned.Add(face);
        withBoth.Interfaces[ScreenConstants.IidIMF2DBuffer] = face;
        Assert.Equal(777u, GpuFrameCopier.ContiguousLength(withBoth.Pointer, 5));
        Assert.Equal(1, withBoth.References); // the IMF2DBuffer face was released again

        using var maxOnly = new FakeMediaBuffer(1000);
        Assert.Equal(1000u, GpuFrameCopier.ContiguousLength(maxOnly.Pointer, 5));

        using var nothing = new FakeMediaBuffer(1000) { MaxLengthResult = ScreenConstants.EFail };
        Assert.Equal(5u, GpuFrameCopier.ContiguousLength(nothing.Pointer, 5));
    }

    [Fact]
    public void SampleWithoutADxgiBuffer_IsReported_AtTheBufferQuery()
    {
        var sample = new FakeSample();
        var buffer = new FakeMediaBuffer(64);
        _owned.Add(sample);
        _owned.Add(buffer);
        Assert.True(IMFSample.AddBuffer(sample.Pointer, buffer.Pointer) >= 0);
        using var copier = Copier(new PixelSize(4, 4));

        Assert.Equal(ScreenConstants.ENoInterface, copier.FillBlack(sample.Pointer));
        Assert.Equal(FrameStep.DxgiBuffer, copier.LastFailure.Step);
        Assert.Equal(2, buffer.References); // the temporary buffer reference was released
    }

    [Fact]
    public void Fill_KeepsNoReferenceToTheSamplesBuffer_SoTheSampleCanGoBackToTheAllocator()
    {
        // IMFVideoSampleAllocatorEx takes a sample back only once its media buffer is released as well: a cached buffer
        // reference kept every filled sample checked out and drained the pool after 12 frames (Radeon RX 9070 XT).
        var encoder = new PixelSize(16, 16);
        var target = AllocatorSample(encoder, subresource: 0, out var buffer, out _);
        var capture = Capture(16, 16);
        var copier = Copier(encoder);

        copier.FillBlack(target.Sample.Pointer);
        Assert.Equal(2, buffer.References);          // ours + the sample's: the copier keeps none
        copier.CopyFrame(target.Sample.Pointer, capture.Pointer, encoder);
        copier.CopySample(target.Sample.Pointer, target.Sample.Pointer);
        Assert.Equal(2, buffer.References);
        Assert.Equal(3, buffer.SetLengthCalls);      // every fill still commits the length
        Assert.Equal(2, target.Texture.References);  // ours + the cache's (textures do not pin samples)

        copier.Dispose();

        Assert.Equal(2, buffer.References);
        Assert.Equal(1, target.Texture.References);
    }

    [Fact]
    public void AllocateSample_EmptyPoolAtItsMaximum_FailsAtOnce_WithoutTheAllocatorsBlockingCall()
    {
        var allocator = Allocator(out var first, out var second);
        using var copier = new GpuFrameCopier(_device.Pointer, _context.Pointer, allocator.Pointer, new PixelSize(16, 16), 30, maximumSamples: 2);

        allocator.FreeCount = 2;
        Assert.Equal(ScreenConstants.SOk, copier.AllocateSample(out var a));
        allocator.FreeCount = 1;
        Assert.Equal(ScreenConstants.SOk, copier.AllocateSample(out var b));
        Assert.Equal(first.Pointer, a);
        Assert.Equal(second.Pointer, b);
        Assert.Equal(2, copier.SamplesHandedOut);

        allocator.FreeCount = 0;
        Assert.Equal(ScreenConstants.MfESampleAllocatorEmpty, copier.AllocateSample(out var none));
        Assert.Equal(0, none);
        Assert.Equal(2, allocator.AllocateCalls); // the real AllocateSample would have waited ~1 s here

        // A sample comes back: the pool hands it out again (and it is not counted twice).
        ComCall.Release(ref a);
        allocator.Available.Enqueue(first);
        allocator.FreeCount = 1;
        Assert.Equal(ScreenConstants.SOk, copier.AllocateSample(out var again));
        Assert.Equal(first.Pointer, again);
        Assert.Equal(2, copier.SamplesHandedOut);
        ComCall.Release(ref again);
        ComCall.Release(ref b);
    }

    [Fact]
    public void AllocateSample_BelowTheMaximum_LetsTheAllocatorGrow_EvenWithNoFreeSample()
    {
        var allocator = Allocator(out var first, out _);
        using var copier = new GpuFrameCopier(_device.Pointer, _context.Pointer, allocator.Pointer, new PixelSize(16, 16), 30, maximumSamples: 4);
        allocator.FreeCount = 0; // only created samples count as free; the allocator creates new ones up to the maximum

        Assert.Equal(ScreenConstants.SOk, copier.AllocateSample(out var sample));
        Assert.Equal(first.Pointer, sample);
        Assert.Equal(1, allocator.AllocateCalls);
        ComCall.Release(ref sample);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AllocateSample_WithoutAFreeCount_AsksTheAllocator(bool callbackFails)
    {
        var allocator = new FakeVideoSampleAllocator(withCallback: callbackFails) { FreeCountResult = ScreenConstants.EFail };
        _owned.Add(allocator);
        using var copier = new GpuFrameCopier(_device.Pointer, _context.Pointer, allocator.Pointer, new PixelSize(16, 16), 30, maximumSamples: 0);

        Assert.Equal(ScreenConstants.MfESampleAllocatorEmpty, copier.AllocateSample(out var sample));
        Assert.Equal(0, sample);
        Assert.Equal(1, allocator.AllocateCalls);
    }

    [Fact]
    public void Dispose_UninitializesAndReleasesTheAllocator_AndItsCallback()
    {
        var allocator = Allocator(out _, out _);
        ComCall.AddRef(allocator.Pointer); // the copier takes over this reference
        var copier = new GpuFrameCopier(_device.Pointer, _context.Pointer, allocator.Pointer, new PixelSize(16, 16), 30, maximumSamples: 2);
        Assert.Equal(3, allocator.References); // ours + the copier's + its callback face

        copier.Dispose();

        Assert.Equal(1, allocator.Uninitialized);
        Assert.Equal(1, allocator.References);
    }

    [Fact]
    public void AllocateAndCopy_SteadyState_DoesNotAllocate()
    {
        var encoder = new PixelSize(32, 16);
        var allocator = Allocator(out var first, out var second);
        var capture = Capture(32, 16);
        using var copier = new GpuFrameCopier(_device.Pointer, _context.Pointer, allocator.Pointer, encoder, 30, maximumSamples: 2);
        allocator.FreeCount = 1;
        void Frames()
        {
            for (var i = 0; i < 20; i++)
            {
                copier.AllocateSample(out var sample);
                copier.CopyFrame(sample, capture.Pointer, encoder);
                ComCall.Release(ref sample);
                allocator.Available.Enqueue(i % 2 == 0 ? first : second); // what the allocator does when a sample comes back
            }
        }

        Frames();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Frames();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(FrameStep.None, copier.LastFailure.Step);
    }

    private GpuFrameCopier Copier(PixelSize encoder) => new(_device.Pointer, _context.Pointer, allocator: 0, encoder, frameRate: 30);

    /// <summary>A fake allocator with two queued 32x16 samples.</summary>
    private FakeVideoSampleAllocator Allocator(out FakeSample first, out FakeSample second)
    {
        var allocator = new FakeVideoSampleAllocator();
        _owned.Add(allocator);
        first = AllocatorSample(new PixelSize(32, 16), subresource: 0, out _, out _).Sample;
        second = AllocatorSample(new PixelSize(32, 16), subresource: 0, out _, out _).Sample;
        allocator.Available.Enqueue(first);
        allocator.Available.Enqueue(second);
        return allocator;
    }

    private FakeTexture Capture(int width, int height)
    {
        var texture = new FakeTexture(FakeTexture.Bgra(width, height));
        texture.Fill((x, y) => 0xFF00_0000 | (uint)((y << 12) | x));
        _owned.Add(texture);
        return texture;
    }

    /// <summary>A sample like IMFVideoSampleAllocatorEx makes: one DXGI surface buffer (IMFDXGIBuffer + IMF2DBuffer faces) over an encoder-size texture.</summary>
    private (FakeSample Sample, FakeTexture Texture) AllocatorSample(PixelSize encoder, uint subresource, out FakeMediaBuffer buffer, out Fake2DBufferFace twoD)
    {
        var texture = new FakeTexture(FakeTexture.Bgra(encoder.Width, encoder.Height));
        buffer = new FakeMediaBuffer((uint)(encoder.Width * encoder.Height * 4));
        var dxgi = new FakeDxgiBufferFace(buffer, texture, subresource);
        twoD = new Fake2DBufferFace(buffer, (uint)(encoder.Width * encoder.Height * 4));
        buffer.Interfaces[ScreenConstants.IidIMFDXGIBuffer] = dxgi;
        buffer.Interfaces[ScreenConstants.IidIMF2DBuffer] = twoD;
        var sample = new FakeSample();
        Assert.True(IMFSample.AddBuffer(sample.Pointer, buffer.Pointer) >= 0);
        _owned.Add(texture);
        _owned.Add(buffer);
        _owned.Add(dxgi);
        _owned.Add(twoD);
        _owned.Add(sample);
        return (sample, texture);
    }
}
