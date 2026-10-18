using System.Diagnostics;
using Murmur.Capture.Windows.Screen;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// 1080p60 on the zero-copy path with the real GPU and hardware encoder, fed synthetic frames in real time (so the
/// result does not depend on what is on screen): the encoder must run peak-constrained VBR (a static screen's repeats
/// must not be padded to the CBR frame size), moving content must keep up at 60 fps with the recorder's sample pool,
/// and the file must hold every frame with the right timeline. Skipped without a hardware H.264 encoder MFT on a real
/// GPU (CI runners: WARP).
/// </summary>
[Trait("Category", "Windows")]
public sealed unsafe class GpuEncoder1080p60Tests(ITestOutputHelper output)
{
    private const int FrameRate = 60;
    private const int BitrateKbps = 12_000;
    private static readonly PixelSize Size = new(1920, 1080);

    [Fact]
    public void HardwareEncoder_RunsPeakConstrainedVbr_AsRequested()
    {
        using var gpu = GpuTestDevice.HardwareOrSkip();
        var path = TempMp4();
        try
        {
            using var writer = H264SinkWriter.Create(path, new H264EncoderSettings(Size, FrameRate, BitrateKbps), EncoderInput.Gpu, gpu.DeviceManager);
            output.WriteLine($"encoder='{writer.Encoder}' rateControl='{writer.RateControlDescription}' readback={writer.Readback}");
            Assert.SkipWhen(writer.IsHardware == false, $"The zero-copy writer picked a software encoder ({writer.Encoder}).");

            Assert.Null(writer.RateControlFallback);
            Assert.Equal(RateControl.PeakConstrainedVbr, writer.Settings.RateControl);
            Assert.Equal(ScreenConstants.RateControlPeakConstrainedVbr, writer.Readback.Mode);
            Assert.Equal(BitrateKbps * 1000u, writer.Readback.MeanBitrate);
            Assert.Equal(BitrateKbps * 1000u * H264EncoderSettings.PeakFactor, writer.Readback.MaxBitrate);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void HardwareEncoder_CbrStillWorks_AndReportsCbr()
    {
        using var gpu = GpuTestDevice.HardwareOrSkip();
        var path = TempMp4();
        try
        {
            using var writer = H264SinkWriter.Create(path, new H264EncoderSettings(Size, FrameRate, BitrateKbps, RateControl.Cbr), EncoderInput.Gpu, gpu.DeviceManager);
            output.WriteLine($"encoder='{writer.Encoder}' rateControl='{writer.RateControlDescription}' readback={writer.Readback}");
            Assert.SkipWhen(writer.IsHardware == false, $"The zero-copy writer picked a software encoder ({writer.Encoder}).");
            Assert.Equal(ScreenConstants.RateControlCbr, writer.Readback.Mode);
            Assert.StartsWith("CBR, 12000 kbps", writer.RateControlDescription, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MovingContent_1080p60_KeepsUpInRealTime_WithPoolHeadroom()
    {
        // 5 s of content that changes every frame, paced at 60 fps like WGC on a busy screen: every frame must get a
        // sample at once (the recorder drops a frame when the pool is empty) and the file must play 5 s at 60 fps.
        using var gpu = GpuTestDevice.HardwareOrSkip();
        var run = Encode(gpu, RateControl.PeakConstrainedVbr, moving: true, TimeSpan.FromSeconds(5), FrameRate);
        output.WriteLine(run.ToString());

        Assert.Equal(0, run.Exhausted);
        Assert.Equal(run.Written, run.Scan.Frames);
        Assert.Equal(5 * FrameRate, run.Written);
        Assert.InRange(run.Scan.DurationS, 4.95, 5.05);
        Assert.InRange(run.Scan.Frames / run.Scan.DurationS, 59.0, 61.0);
        Assert.True(run.MinAvailable >= 2, $"the {WgcScreenRecorder.MaximumSamples}-sample pool got down to {run.MinAvailable} free samples at 60 fps");

        // VBR around the mean on moving content: not starved, not above the peak.
        var kbps = run.Scan.FileBytes * 8 / run.Scan.DurationS / 1000;
        Assert.InRange(kbps, BitrateKbps / 10.0, BitrateKbps * H264EncoderSettings.PeakFactor * 1.1);
    }

    [Fact]
    public void StaticScreen_VbrRepeats_AreNotPadded_AndForcedKeyFramesCostLittle()
    {
        // What the recorder writes for a static screen: the held frame once a second (two minutes of timeline, encoded
        // faster than real time, but with the encoder idle between repeats as in real time), with an IDR frame forced
        // every 2 s (KeyFrameSchedule). CBR (the old configuration) pads repeats towards bitrate / fps; VBR must not.
        // The IDR frames (and the frame refining each of them) are what a static screen costs now: ~0.3 Mbit/s for this
        // detailed synthetic picture, against ~10 kbit/s with the GOP alone (one keyframe in two minutes).
        using var gpu = GpuTestDevice.HardwareOrSkip();
        var duration = TimeSpan.FromSeconds(120);
        var cbr = Encode(gpu, RateControl.Cbr, moving: false, duration, framesPerSecond: 1, realTime: false, gapMs: 30);
        var vbr = Encode(gpu, RateControl.PeakConstrainedVbr, moving: false, duration, framesPerSecond: 1, realTime: false, gapMs: 30);
        output.WriteLine("CBR: " + cbr);
        output.WriteLine("VBR: " + vbr);

        var cbrPerSecond = cbr.Scan.FileBytes / cbr.Scan.DurationS;
        var vbrPerSecond = vbr.Scan.FileBytes / vbr.Scan.DurationS;
        output.WriteLine($"static screen, whole file: CBR {cbrPerSecond / 1024:F1} KiB/s ({cbrPerSecond * 3600 / 1024 / 1024:F0} MiB/h), VBR {vbrPerSecond / 1024:F1} KiB/s ({vbrPerSecond * 3600 / 1024 / 1024:F0} MiB/h)");
        output.WriteLine($"static screen, per repeat: CBR {cbr.RepeatBytes:F0} B, VBR {vbr.RepeatBytes:F0} B; without the keyframes: CBR {cbr.DeltaRepeatBytes:F0} B, VBR {vbr.DeltaRepeatBytes:F0} B; keyframes CBR {cbr.KeyFrameBytes:F0} B, VBR {vbr.KeyFrameBytes:F0} B");
        Assert.Equal(ScreenConstants.RateControlPeakConstrainedVbr, vbr.RateControl.Mode);
        Assert.InRange(vbr.Scan.DurationS, 119.9, 120.1);
        Assert.Equal(120, vbr.Probe.SampleBytes.Count);
        Assert.Equal(60, vbr.Fragments.SyncTimesS.Count); // every second repeat is an IDR frame

        // Not padded: a repeat between keyframes stays below the CBR frame size (bitrate / fps), and the static screen
        // as a whole, keyframes included, below 1/24 of the 12 Mbit/s mean.
        Assert.True(vbr.DeltaRepeatBytes < BitrateKbps * 1000 / 8 / FrameRate, $"a repeat costs {vbr.DeltaRepeatBytes:F0} B with VBR");
        Assert.True(vbrPerSecond * 8 < BitrateKbps * 1000 / 24, $"a static screen costs {vbrPerSecond * 8 / 1000:F0} kbit/s with VBR");
    }

    /// <summary>
    /// Encodes <paramref name="duration"/> of synthetic frames at <paramref name="framesPerSecond"/> in real time, like
    /// the recorder does (non-blocking allocation, the last written sample stays referenced while the next is filled,
    /// every sample lasts until the next one).
    /// </summary>
    private static EncodeRun Encode(GpuDevice gpu, RateControl rateControl, bool moving, TimeSpan duration, int framesPerSecond, bool realTime = true, int gapMs = 0)
    {
        var path = TempMp4();
        nint world = 0, capture = 0, last = 0;
        try
        {
            using var frames = GpuFrameCopier.Create(gpu.Device, gpu.Context, gpu.DeviceManager, Size, FrameRate, WgcScreenRecorder.InitialSamples, WgcScreenRecorder.MaximumSamples);
            RateControlReadback readback;
            int written = 0, exhausted = 0, lastIndex = 0;
            using (var writer = H264SinkWriter.Create(path, new H264EncoderSettings(Size, FrameRate, BitrateKbps, rateControl), EncoderInput.Gpu, gpu.DeviceManager))
            {
                Assert.SkipWhen(writer.IsHardware == false, $"The zero-copy writer picked a software encoder ({writer.Encoder}).");
                readback = writer.Readback;
                world = SyntheticTextures.World(gpu.Device, new PixelSize(Size.Width * 2, Size.Height));
                capture = SyntheticTextures.Create(gpu.Device, Size);
                var count = (int)Math.Round(duration.TotalSeconds * framesPerSecond);
                var interval = MediaTime.TicksPerSecond / framesPerSecond;
                var start = Stopwatch.GetTimestamp();
                for (var i = 0; i < count; i++)
                {
                    if (realTime)
                    {
                        WaitUntil(start, i * interval);
                    }

                    var offset = moving ? (uint)(i * 16 % Size.Width) : 0;
                    var box = new D3D11Box { Left = offset, Right = offset + (uint)Size.Width, Bottom = (uint)Size.Height, Back = 1 };
                    ID3D11DeviceContext.CopySubresourceRegion(gpu.Context, capture, 0, 0, 0, 0, world, 0, &box);
                    var hr = frames.AllocateSample(out var sample);
                    while (!realTime && hr == ScreenConstants.MfESampleAllocatorEmpty)
                    {
                        Thread.Sleep(1); // faster than real time: wait for the encoder instead of dropping
                        hr = frames.AllocateSample(out sample);
                    }

                    if (hr == ScreenConstants.MfESampleAllocatorEmpty)
                    {
                        exhausted++; // the recorder would drop this frame; the previous one lasts longer
                        continue;
                    }

                    Assert.Equal(ScreenConstants.SOk, hr);
                    Assert.Equal(ScreenConstants.SOk, frames.CopyFrame(sample, capture, Size));
                    if (last != 0)
                    {
                        Assert.Equal(ScreenConstants.SOk, Write(writer, last, lastIndex * interval, (i - lastIndex) * interval));
                        written++;
                        ComCall.Release(ref last);
                        if (gapMs > 0)
                        {
                            Thread.Sleep(gapMs); // the encoder is idle when the next repeat comes, as in real time
                        }
                    }

                    last = sample;
                    lastIndex = i;
                }

                Assert.Equal(ScreenConstants.SOk, Write(writer, last, lastIndex * interval, (count - lastIndex) * interval));
                written++;
                ComCall.Release(ref last);
                Assert.Equal(ScreenConstants.SOk, writer.FinalizeFile());
            }

            return new EncodeRun(MurmurMp4Scan.Scan(path), Mp4Probe.Read(path), Mp4Fragments.Read(path), readback, written, exhausted, frames.MinAvailableSamples, frames.SamplesHandedOut);
        }
        finally
        {
            ComCall.Release(ref last);
            ComCall.Release(ref capture);
            ComCall.Release(ref world);
            File.Delete(path);
        }
    }

    private static int Write(H264SinkWriter writer, nint sample, long time, long duration)
    {
        var hr = IMFSample.SetSampleTime(sample, time);
        hr = hr < 0 ? hr : IMFSample.SetSampleDuration(sample, duration);
        return hr < 0 ? hr : writer.WriteSample(sample, time);
    }

    private static void WaitUntil(long start, long ticks)
    {
        var due = start + (ticks * Stopwatch.Frequency / MediaTime.TicksPerSecond);
        while (true)
        {
            var left = due - Stopwatch.GetTimestamp();
            if (left <= 0)
            {
                return;
            }

            if (left * 1000 / Stopwatch.Frequency > 2)
            {
                Thread.Sleep(1);
            }
            else
            {
                Thread.SpinWait(100);
            }
        }
    }

    private static string TempMp4() => Path.Combine(Path.GetTempPath(), $"murmur-1080p60-{Guid.NewGuid():N}.mp4");

    private sealed record EncodeRun(MurmurMp4ScanResult Scan, Mp4ProbeResult Probe, Mp4FragmentScan Fragments, RateControlReadback RateControl, int Written, int Exhausted, int MinAvailable, int HandedOut)
    {
        /// <summary>Mean compressed size of every sample after the first (the first is the IDR frame).</summary>
        public double RepeatBytes => Probe.SampleBytes.Count > 1 ? Probe.SampleBytes.Skip(1).Average() : 0;

        /// <summary>Mean compressed size of the samples that are not keyframes.</summary>
        public double DeltaRepeatBytes => Sizes(sync: false);

        /// <summary>Mean compressed size of the keyframes.</summary>
        public double KeyFrameBytes => Sizes(sync: true);

        public override string ToString() =>
            $"{Scan.Frames} frames, {Scan.DurationS:F3} s (trun), {Scan.FileBytes} bytes ({Scan.FileBytes * 8 / Math.Max(0.001, Scan.DurationS) / 1000:F0} kbps), "
            + $"sample bytes [{string.Join(", ", Probe.SampleBytes.Take(12))}], later samples {RepeatBytes:F0} B on average, "
            + $"written {Written}, pool empty {Exhausted}, min available {MinAvailable}, samples handed out {HandedOut}, rate control {RateControl}, error: {Scan.Error ?? "none"}";

        private double Sizes(bool sync)
        {
            var sizes = Probe.SampleBytes.Zip(Fragments.Samples, (bytes, sample) => (bytes, sample.Sync)).Where(s => s.Sync == sync).Select(s => (double)s.bytes).ToList();
            return sizes.Count == 0 ? 0 : sizes.Average();
        }
    }
}

/// <summary>Synthetic GPU frames for the encoder tests.</summary>
internal static unsafe class SyntheticTextures
{
    /// <summary>An empty B8G8R8A8 texture of <paramref name="size"/> (the "capture" frame).</summary>
    public static nint Create(nint device, PixelSize size) => Upload(device, size, null);

    /// <summary>
    /// A B8G8R8A8 texture with detailed, screen-like content (gradients, text-like stripes, blocks): scrolling a
    /// window of it gives every frame new content the encoder cannot skip.
    /// </summary>
    public static nint World(nint device, PixelSize size)
    {
        var pixels = new uint[size.Width * size.Height];
        var random = new Random(1080);
        for (var y = 0; y < size.Height; y++)
        {
            for (var x = 0; x < size.Width; x++)
            {
                var stripe = (y / 6 % 3 == 0) && (x / 4 % 7 != 0) ? 0x40u : 0u;
                pixels[(y * size.Width) + x] = 0xFF00_0000u | ((uint)((x / 3) & 0xFF) << 16) | ((uint)((y / 2) & 0xFF) << 8) | (((uint)(x ^ y) & 0xBF) + stripe);
            }
        }

        for (var block = 0; block < 400; block++)
        {
            var bx = random.Next(size.Width - 64);
            var by = random.Next(size.Height - 32);
            var color = 0xFF00_0000u | (uint)random.Next(0x100_0000);
            for (var y = by; y < by + 32; y++)
            {
                pixels.AsSpan((y * size.Width) + bx, 64).Fill(color);
            }
        }

        fixed (uint* memory = pixels)
        {
            return Upload(device, size, memory);
        }
    }

    private static nint Upload(nint device, PixelSize size, uint* memory)
    {
        var desc = new D3D11Texture2DDesc
        {
            Width = (uint)size.Width,
            Height = (uint)size.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = ScreenConstants.DxgiFormatB8G8R8A8Unorm,
            SampleDesc = new DxgiSampleDesc { Count = 1 },
            Usage = ScreenConstants.D3D11UsageDefault,
            BindFlags = ScreenConstants.D3D11BindShaderResource,
        };
        nint texture = 0;
        var data = new D3D11SubresourceData { SysMem = memory, SysMemPitch = (uint)size.Width * 4 };
        Assert.Equal(ScreenConstants.SOk, ID3D11Device.CreateTexture2D(device, &desc, memory == null ? null : &data, &texture));
        return texture;
    }
}

/// <summary>A real GPU with video support and a hardware H.264 encoder MFT, else the test is skipped.</summary>
internal static class GpuTestDevice
{
    public static GpuDevice HardwareOrSkip()
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
}
