using System.Diagnostics;
using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Capture.Windows.Screen;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// Keyframes are at most 2 s of media time apart whatever the content. The hardware encoder (GPU zero-copy) encodes
/// exactly the samples it gets, so the sink writer forces IDR frames (<see cref="KeyFrameSchedule"/>); the CPU readback
/// pipeline emits a constant frame rate, so its 2 s GOP is enough (<see cref="FramePathPolicy.Settings"/>). Fed
/// synthetic frames (the GPU cases paced like the recorder: the encoder is idle when a keyframe is requested), read
/// back with <see cref="Mp4Fragments"/>. The GPU cases are skipped without a hardware H.264 encoder on a real GPU.
/// </summary>
[Trait("Category", "Windows")]
public sealed unsafe class KeyFrameIntervalTests(ITestOutputHelper output)
{
    private static readonly PixelSize Size = new(1920, 1080);
    private const double MaxGapS = 2.0 + 0.05;

    [Theory]
    [InlineData(false)] // GPU zero-copy, hardware encoder: forced keyframes
    [InlineData(true)]  // CPU readback, Microsoft software encoder: constant frame rate, GOP
    public void StaticScreen_RepeatsOnceASecond_KeyFramesStayWithin2s(bool cpu)
    {
        // What the recorder writes for a static screen: the held frame once a second, 30 s of timeline.
        var input = cpu ? EncoderInput.Cpu : EncoderInput.Gpu;
        var times = Enumerable.Range(0, 30).Select(i => i * MediaTime.TicksPerSecond).ToArray();
        var run = Encode(input, times, 30 * MediaTime.TicksPerSecond, moving: false, pace: Pace.Gaps);
        output.WriteLine(run.ToString());

        Assert.False(run.Scan.Truncated);
        Assert.InRange(run.Scan.EndS, 29.99, 30.01);
        Assert.InRange(run.Scan.MaxSyncGapS, 1.0, MaxGapS);
        Assert.InRange(run.Scan.MaxFragmentGapS, 0.01, MaxGapS);
        if (cpu)
        {
            Assert.False(run.ForcesKeyFrames, run.KeyFrames);
            Assert.Equal(30 * 30, run.Scan.Samples.Count); // the software pipeline's constant 30 fps
        }
        else
        {
            Assert.True(run.ForcesKeyFrames, run.KeyFrames);
            Assert.Equal(30, run.Scan.Samples.Count); // one sample per repeat
            Assert.Equal(Enumerable.Range(0, 15).Select(i => 2.0 * i), Rounded(run.Scan.SyncTimesS)); // every second repeat
            Assert.Equal(14, run.ForcedKeyFrames);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SteadyFrameRate_OneKeyFrameEvery2s_NoExtraOnes(bool cpu)
    {
        // Content that changes every frame at the encoder rate, in real time: the forced keyframe lands on the frame the
        // GOP would have picked, so there is exactly one every 2 s.
        var input = cpu ? EncoderInput.Cpu : EncoderInput.Gpu;
        var fps = Settings(input).FrameRate;
        var count = 7 * fps;
        var times = Enumerable.Range(0, count).Select(i => Frame(i, fps)).ToArray();
        var run = Encode(input, times, Frame(count, fps), moving: true, pace: cpu ? Pace.None : Pace.RealTime);
        output.WriteLine(run.ToString());

        Assert.Equal(count, run.Scan.Samples.Count);
        Assert.Equal([0.0, 2.0, 4.0, 6.0], Rounded(run.Scan.SyncTimesS));
        Assert.InRange(run.Scan.MaxSyncGapS, 1.9, MaxGapS);
    }

    [Fact]
    public void HardwareEncoder_ForcedKeyFrame_RestartsItsGop()
    {
        // Half a second at 60 fps, a 2 s pause (a forced keyframe at 2.5 s), then 60 fps again: the encoder restarts its
        // GOP at the forced IDR frame, so the next keyframe is the forced one 2 s later, with no GOP keyframe in between
        // (one that kept counting from the start would add one at 4.0 s, 120 frames after the first).
        var times = Enumerable.Range(0, 30).Select(i => Frame(i, 60))
            .Concat(Enumerable.Range(0, 300).Select(i => (5 * MediaTime.TicksPerSecond / 2) + Frame(i, 60)))
            .ToArray();
        var run = Encode(EncoderInput.Gpu, times, (5 * MediaTime.TicksPerSecond / 2) + Frame(300, 60), moving: true, pace: Pace.RealTime);
        output.WriteLine(run.ToString());

        Assert.Equal([0.0, 2.5, 4.5, 6.5], Rounded(run.Scan.SyncTimesS));
    }

    [Fact]
    public void KeyFrameSchedule_ForcesTheFirstSampleAtLeastTheIntervalAfterTheLastKeyFrame()
    {
        var schedule = new KeyFrameSchedule(KeyFrameSchedule.DefaultInterval, 60);
        Assert.Null(schedule.LastKeyFrame);
        Assert.False(schedule.ShouldForce(0)); // the first sample is a keyframe anyway
        Assert.Equal(0, schedule.LastKeyFrame);
        Assert.False(schedule.ShouldForce(MediaTime.TicksPerSecond));
        Assert.False(schedule.ShouldForce((2 * MediaTime.TicksPerSecond) - MediaTime.FrameDuration(60))); // frame 119
        Assert.True(schedule.ShouldForce((2 * MediaTime.TicksPerSecond) - 1_000)); // frame 120, 0.1 ms early (jitter)
        Assert.True(schedule.ShouldForce(2 * MediaTime.TicksPerSecond)); // not forced yet: still due
        schedule.Forced(2 * MediaTime.TicksPerSecond);
        Assert.Equal(2 * MediaTime.TicksPerSecond, schedule.LastKeyFrame);
        Assert.False(schedule.ShouldForce(3 * MediaTime.TicksPerSecond));
        Assert.True(schedule.ShouldForce(10 * MediaTime.TicksPerSecond)); // after a long static stretch: at once
    }

    [Fact]
    public void KeyFrameSchedule_StartsAtTheFirstSample_WhateverItsTime()
    {
        var schedule = new KeyFrameSchedule(KeyFrameSchedule.DefaultInterval, 30);
        Assert.Equal(KeyFrameSchedule.DefaultInterval, schedule.Interval);
        Assert.False(schedule.ShouldForce(5 * MediaTime.TicksPerSecond));
        Assert.False(schedule.ShouldForce(6 * MediaTime.TicksPerSecond));
        Assert.True(schedule.ShouldForce(7 * MediaTime.TicksPerSecond));
        Assert.Throws<ArgumentOutOfRangeException>(() => new KeyFrameSchedule(0, 30));
    }

    [Fact]
    public void KeyFrameNaming_SaysHowKeyFramesAreBounded()
    {
        Assert.Equal("an IDR frame at least every 2 s (forced; 12 so far)", KeyFrameNaming.Describe(true, true, KeyFrameSchedule.DefaultInterval, 120, 12, 0));
        Assert.Equal("an IDR frame at least every 2 s (forced; 3 so far, 1 refused)", KeyFrameNaming.Describe(true, true, KeyFrameSchedule.DefaultInterval, 120, 3, 1));
        Assert.Equal("GOP of 120 frames (the encoder cannot be told to start a keyframe)", KeyFrameNaming.Describe(true, false, KeyFrameSchedule.DefaultInterval, 120, 0, 0));
        Assert.Equal("GOP of 60 frames (not forced)", KeyFrameNaming.Describe(false, false, KeyFrameSchedule.DefaultInterval, 60, 0, 0));
    }

    [Fact]
    public void Settings_ForceKeyFramesOnTheZeroCopyPathOnly()
    {
        var requested = new H264EncoderSettings(Size, 60, 12_000);
        Assert.Equal(KeyFrameSchedule.DefaultInterval, requested.KeyFrameInterval);
        Assert.Equal(KeyFrameSchedule.DefaultInterval, FramePathPolicy.Settings(EncoderInput.Gpu, requested).KeyFrameInterval);
        Assert.Equal(0, FramePathPolicy.Settings(EncoderInput.Cpu, requested).KeyFrameInterval);
        Assert.Equal(0, FramePathPolicy.Settings(EncoderInput.Cpu, requested with { FrameRate = 15 }).KeyFrameInterval);
    }

    private enum Pace
    {
        /// <summary>As fast as the encoder takes the samples.</summary>
        None,

        /// <summary>A short gap after every sample: the encoder is idle when the next one comes, as with repeats.</summary>
        Gaps,

        /// <summary>Every sample at its sample time on the wall clock.</summary>
        RealTime,
    }

    private static long Frame(int index, int fps) => (long)Math.Round(index * (MediaTime.TicksPerSecond / (double)fps));

    private static IEnumerable<double> Rounded(IEnumerable<double> seconds) => seconds.Select(t => Math.Round(t, 2));

    private static H264EncoderSettings Settings(EncoderInput input) =>
        FramePathPolicy.Settings(input, new H264EncoderSettings(Size, 60, 12_000));

    /// <summary>
    /// Encodes one synthetic frame per entry of <paramref name="times"/> (sample times, 100 ns), each lasting until the
    /// next one and the last until <paramref name="end"/>, like the recorder: the written sample stays referenced while
    /// the next one is filled; an empty pool is waited for instead of dropping the frame.
    /// </summary>
    private static KeyFrameRun Encode(EncoderInput input, long[] times, long end, bool moving, Pace pace)
    {
        var settings = Settings(input);
        using var gpu = input == EncoderInput.Gpu ? GpuTestDevice.HardwareOrSkip() : CpuTestDevice();
        var path = Path.Combine(Path.GetTempPath(), $"murmur-keyframes-{Guid.NewGuid():N}.mp4");
        nint world = 0, capture = 0, last = 0;
        try
        {
            using IFramePath frames = input == EncoderInput.Gpu
                ? GpuFrameCopier.Create(gpu.Device, gpu.Context, gpu.DeviceManager, Size, settings.FrameRate, WgcScreenRecorder.InitialSamples, WgcScreenRecorder.MaximumSamples)
                : new CpuFrameCopier(gpu.Device, gpu.Context, Size, MediaFoundationSampleFactory.Instance, WgcScreenRecorder.InitialCpuSamples, WgcScreenRecorder.MaximumCpuSamples);
            KeyFrameRun run;
            using (var writer = H264SinkWriter.Create(path, settings, input, input == EncoderInput.Gpu ? gpu.DeviceManager : 0))
            {
                Assert.SkipWhen(input == EncoderInput.Gpu && writer.IsHardware == false, $"The zero-copy writer picked a software encoder ({writer.Encoder}).");
                world = SyntheticTextures.World(gpu.Device, new PixelSize(Size.Width * 2, Size.Height));
                capture = SyntheticTextures.Create(gpu.Device, Size);
                var start = Stopwatch.GetTimestamp();
                var lastTime = 0L;
                for (var i = 0; i < times.Length; i++)
                {
                    if (pace == Pace.RealTime)
                    {
                        WaitUntil(start, times[i]);
                    }

                    var offset = moving ? (uint)(i * 16 % Size.Width) : 0;
                    var box = new D3D11Box { Left = offset, Right = offset + (uint)Size.Width, Bottom = (uint)Size.Height, Back = 1 };
                    ID3D11DeviceContext.CopySubresourceRegion(gpu.Context, capture, 0, 0, 0, 0, world, 0, &box);
                    var hr = frames.AllocateSample(out var sample);
                    var deadline = Environment.TickCount64 + 5_000;
                    while (hr == ScreenConstants.MfESampleAllocatorEmpty && Environment.TickCount64 < deadline)
                    {
                        Thread.Sleep(1);
                        hr = frames.AllocateSample(out sample);
                    }

                    Assert.Equal(ScreenConstants.SOk, hr);
                    Assert.Equal(ScreenConstants.SOk, frames.CopyFrame(sample, capture, Size));
                    if (last != 0)
                    {
                        Assert.Equal(ScreenConstants.SOk, Write(writer, last, lastTime, times[i] - lastTime));
                        ComCall.Release(ref last);
                        if (pace == Pace.Gaps)
                        {
                            Thread.Sleep(30);
                        }
                    }

                    last = sample;
                    lastTime = times[i];
                }

                Assert.Equal(ScreenConstants.SOk, Write(writer, last, lastTime, end - lastTime));
                ComCall.Release(ref last);
                Assert.Equal(ScreenConstants.SOk, writer.FinalizeFile());
                run = new KeyFrameRun(Mp4Fragments.Read(path), writer.ForcesKeyFrames, writer.ForcedKeyFrames, writer.KeyFrameDescription, writer.Encoder);
            }

            return run;
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
        while (Stopwatch.GetTimestamp() < due)
        {
            if ((due - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency > 2)
            {
                Thread.Sleep(1);
            }
            else
            {
                Thread.SpinWait(100);
            }
        }
    }

    /// <summary>Any D3D11 device (WARP included): CPU readback only needs it for the synthetic source texture.</summary>
    private static GpuDevice CpuTestDevice()
    {
        var reason = ScreenTestEnvironment.SkipReason();
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);
        MediaFoundationRuntime.EnsureStarted();
        return GpuDevice.Create();
    }

    private sealed record KeyFrameRun(Mp4FragmentScan Scan, bool ForcesKeyFrames, long ForcedKeyFrames, string KeyFrames, string Encoder)
    {
        public override string ToString() => $"encoder '{Encoder}', keyframes: {KeyFrames}; file: {Scan}";
    }
}
