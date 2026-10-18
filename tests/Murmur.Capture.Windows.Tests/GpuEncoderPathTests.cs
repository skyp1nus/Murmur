using System.Diagnostics;
using Murmur.Capture.Windows.Screen;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// The zero-copy path on the real GPU and hardware encoder, fed synthetic frames, so the result does not depend on
/// what Windows.Graphics.Capture happens to deliver: the sample pool must keep recycling (it drained after 12 frames
/// while the copier cached the samples' media buffers) and an empty pool must fail at once instead of blocking the
/// capture thread. Skipped without a hardware H.264 encoder MFT on a real GPU (CI runners: WARP).
/// </summary>
[Trait("Category", "Windows")]
public sealed unsafe class GpuEncoderPathTests(ITestOutputHelper output)
{
    private static readonly PixelSize Size = new(1280, 720);

    [Fact]
    public void ZeroCopyEncoder_ThreeSecondsAt30Fps_EncodesEveryFrame()
    {
        using var gpu = HardwareGpuOrSkip();
        var path = Path.Combine(Path.GetTempPath(), $"murmur-gpu-{Guid.NewGuid():N}.mp4");
        nint source = 0, last = 0;
        var waits = 0;
        try
        {
            using (var frames = GpuFrameCopier.Create(gpu.Device, gpu.Context, gpu.DeviceManager, Size, 30, WgcScreenRecorder.InitialSamples, WgcScreenRecorder.MaximumSamples))
            {
                using (var writer = H264SinkWriter.Create(path, new H264EncoderSettings(Size, 30, 4_000), EncoderInput.Gpu, gpu.DeviceManager))
                {
                    output.WriteLine($"device='{gpu.Description}' encoder='{writer.Encoder}'");
                    Assert.SkipWhen(writer.IsHardware == false, $"The zero-copy writer picked a software encoder ({writer.Encoder}).");
                    source = Gradient(gpu.Device);
                    const int Count = 90;
                    var duration = MediaTime.FrameDuration(30);
                    for (var i = 0; i < Count; i++)
                    {
                        // Like the recorder: the last written sample stays referenced (for repeats) while the next one is filled.
                        var sample = AllocateWithin(frames, TimeSpan.FromSeconds(2), ref waits);
                        Assert.True(sample != 0, $"frame {i}: the sample pool stayed empty for 2 s ({frames.SamplesHandedOut} samples handed out)");
                        Assert.Equal(ScreenConstants.SOk, frames.CopyFrame(sample, source, Size));
                        Assert.Equal(ScreenConstants.SOk, IMFSample.SetSampleTime(sample, i * duration));
                        Assert.Equal(ScreenConstants.SOk, IMFSample.SetSampleDuration(sample, duration));
                        Assert.Equal(ScreenConstants.SOk, writer.WriteSample(sample));
                        ComCall.Release(ref last);
                        last = sample;
                    }

                    ComCall.Release(ref last);
                    Assert.Equal(ScreenConstants.SOk, writer.FinalizeFile());
                }

                output.WriteLine($"samples handed out: {frames.SamplesHandedOut}; allocation retries: {waits}");
                Assert.InRange(frames.SamplesHandedOut, 1, (int)WgcScreenRecorder.MaximumSamples);
            }

            var probe = Mp4Probe.Read(path);
            output.WriteLine(probe.ToString());
            Assert.Equal(90, probe.Frames);
            Assert.InRange(TimeSpan.FromTicks(probe.Duration), TimeSpan.FromSeconds(2.9), TimeSpan.FromSeconds(3.1));
        }
        finally
        {
            ComCall.Release(ref last);
            ComCall.Release(ref source);
            File.Delete(path);
        }
    }

    [Fact]
    public void SampleAllocator_RecyclesFilledSamples_AndAnEmptyPoolFailsAtOnce()
    {
        using var gpu = HardwareGpuOrSkip();
        const uint Maximum = 4;
        using var frames = GpuFrameCopier.Create(gpu.Device, gpu.Context, gpu.DeviceManager, Size, 30, 2, Maximum);
        var source = Gradient(gpu.Device);
        var held = new nint[Maximum];
        try
        {
            // Fill and release far more samples than the pool has: each must come back.
            var waits = 0;
            for (var i = 0; i < 5 * Maximum; i++)
            {
                var sample = AllocateWithin(frames, TimeSpan.FromSeconds(2), ref waits);
                Assert.True(sample != 0, $"allocation {i}: a filled and released sample never came back to the allocator");
                Assert.Equal(ScreenConstants.SOk, frames.CopyFrame(sample, source, Size));
                ComCall.Release(ref sample);
            }

            for (var i = 0; i < held.Length; i++)
            {
                var sample = AllocateWithin(frames, TimeSpan.FromSeconds(2), ref waits);
                Assert.True(sample != 0, $"sample {i} of {Maximum} could not be allocated");
                held[i] = sample;
            }

            var stopwatch = Stopwatch.StartNew();
            Assert.Equal(ScreenConstants.MfESampleAllocatorEmpty, frames.AllocateSample(out var none));
            stopwatch.Stop();
            output.WriteLine($"empty pool answered in {stopwatch.Elapsed.TotalMilliseconds:F2} ms; allocation retries: {waits}");
            Assert.Equal(0, none);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100), $"AllocateSample on an empty pool took {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
        }
        finally
        {
            for (var i = 0; i < held.Length; i++)
            {
                ComCall.Release(ref held[i]);
            }

            ComCall.Release(ref source);
        }
    }

    /// <summary>A real GPU with video support and a hardware H.264 encoder MFT, else the test is skipped.</summary>
    private static GpuDevice HardwareGpuOrSkip()
    {
        var reason = ScreenTestEnvironment.SkipReason();
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);
        var gpu = GpuDevice.Create();
        var encoders = gpu.HasVideoSupport && !gpu.IsSoftwareRenderer ? HardwareEncoders.CountH264() : 0;
        if (FramePathPolicy.Choose(gpu.HasVideoSupport, gpu.IsSoftwareRenderer, encoders) != EncoderInput.Gpu)
        {
            var why = FramePathPolicy.Describe(gpu.HasVideoSupport, gpu.IsSoftwareRenderer, encoders);
            gpu.Dispose();
            Assert.Skip($"No zero-copy path on this machine: {why}.");
        }

        return gpu;
    }

    /// <summary>Samples come back to the allocator asynchronously once released: retry briefly before giving up.</summary>
    private static nint AllocateWithin(GpuFrameCopier frames, TimeSpan timeout, ref int waits)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            var hr = frames.AllocateSample(out var sample);
            if (hr >= 0)
            {
                return sample;
            }

            Assert.Equal(ScreenConstants.MfESampleAllocatorEmpty, hr);
            if (Stopwatch.GetTimestamp() > deadline)
            {
                return 0;
            }

            waits++;
            Thread.Sleep(2);
        }
    }

    /// <summary>An encoder-size B8G8R8A8 texture with a gradient (the "capture" frame).</summary>
    private static nint Gradient(nint device)
    {
        var pixels = new uint[Size.Width * Size.Height];
        for (var y = 0; y < Size.Height; y++)
        {
            for (var x = 0; x < Size.Width; x++)
            {
                pixels[(y * Size.Width) + x] = 0xFF00_0000u | ((uint)(x & 0xFF) << 16) | ((uint)(y & 0xFF) << 8) | (uint)((x + y) & 0xFF);
            }
        }

        var desc = new D3D11Texture2DDesc
        {
            Width = (uint)Size.Width,
            Height = (uint)Size.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = ScreenConstants.DxgiFormatB8G8R8A8Unorm,
            SampleDesc = new DxgiSampleDesc { Count = 1 },
            Usage = ScreenConstants.D3D11UsageDefault,
            BindFlags = ScreenConstants.D3D11BindShaderResource,
        };
        nint texture = 0;
        fixed (uint* memory = pixels)
        {
            var data = new D3D11SubresourceData { SysMem = memory, SysMemPitch = (uint)Size.Width * 4 };
            Assert.Equal(ScreenConstants.SOk, ID3D11Device.CreateTexture2D(device, &desc, &data, &texture));
        }

        return texture;
    }
}
