using System.Diagnostics;
using Murmur.Capture.Windows.Screen;
using Murmur.Capture.Windows.Screen.Interop;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// The default recording configuration (1080p60, VBR 12 Mbit/s) end to end through Windows.Graphics.Capture on a
/// screen whose content changes every refresh (<see cref="AnimatedWindow"/>): ~60 fps encoded with at most 2 % drops,
/// a file whose timeline is wall-clock time at 60 fps, and the test host's working set and CPU while recording,
/// reported against the budgets of an audio + screen recording (RAM &lt; 250 MB: what recording adds to the test host must
/// fit on top of the audio-only app; CPU &lt; 8 %). Runs where the GPU zero-copy path exists (skipped without a hardware
/// H.264 encoder, e.g. on CI runners); the forced CPU readback variant measures the 1080p30 fallback on the same PC.
/// </summary>
[Trait("Category", "Windows")]
[Collection(SoloMeasurementCollection.Name)]
public sealed class WgcScreenRecorder1080p60Tests(ITestOutputHelper output)
{
    private const double RamBudgetMb = 250;

    /// <summary>The app recording audio only (owner's PC, Native AOT, 12 min: 119 MB); screen recording may add the rest.</summary>
    private const double AudioOnlyAppMb = 120;
    private const double CpuBudgetPct = 8;
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)] // GPU zero-copy: 1080p60
    [InlineData(true)]  // CPU readback (what machines without a GPU encoder use), capped at 1080p30
    public async Task Record_MovingContent_1080p60_KeepsUp_WithinTheBudgets(bool forceCpu)
    {
        var fps = forceCpu ? FramePathPolicy.CpuMaxFrameRate : 60;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var workingSetBaseline = Environment.WorkingSet;
        using (GpuTestDevice.HardwareOrSkip())
        {
            // Only checks that the zero-copy path exists here (the CPU variant is a reference measurement on the same PC).
        }

        var display = Displays.Find(null);
        var refresh = RefreshRate(display.Info.Id);
        output.WriteLine($"display {display.Info.Id} {display.Info.Width}x{display.Info.Height} @ {refresh} Hz");
        Assert.SkipWhen(refresh is > 0 and < 59, $"The primary display refreshes at {refresh} Hz: WGC cannot deliver 60 fps.");

        var work = WorkArea(display.Monitor);
        using var window = AnimatedWindow.Start(work.Right - 320, work.Bottom - 200, 300, 180);

        var path = Path.Combine(Path.GetTempPath(), $"murmur-wgc-1080p60-{Guid.NewGuid():N}.mp4");
        var warmUp = Path.Combine(Path.GetTempPath(), $"murmur-wgc-1080p60-warmup-{Guid.NewGuid():N}.mp4");
        try
        {
            await using var recorder = new WgcScreenRecorder(new ScreenCaptureOptions()) { ForceCpuReadback = forceCpu };
            var faults = new List<CaptureFault>();
            recorder.Faulted += f =>
            {
                lock (faults)
                {
                    faults.Add(f);
                }
            };

            // A short first segment JIT-compiles and initializes everything; the measured segment is the second one.
            await recorder.StartSegmentAsync(warmUp, TestContext.Current.CancellationToken);
            await Task.Delay(1_000, TestContext.Current.CancellationToken);
            await recorder.StopSegmentAsync(TestContext.Current.CancellationToken);
            var before = recorder.Stats;

            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime.Ticks;
            var windowCpuBefore = window.ThreadCpuTicks;
            var paintedBefore = window.Frames;
            var workingSetMax = 0L;
            var stopwatch = Stopwatch.StartNew();
            await recorder.StartSegmentAsync(path, TestContext.Current.CancellationToken);
            while (stopwatch.Elapsed < Duration)
            {
                await Task.Delay(250, TestContext.Current.CancellationToken);
                process.Refresh();
                workingSetMax = Math.Max(workingSetMax, process.WorkingSet64);
            }

            await recorder.StopSegmentAsync(TestContext.Current.CancellationToken);
            var elapsed = stopwatch.Elapsed;
            process.Refresh();
            var recorderCpu = process.TotalProcessorTime.Ticks - cpuBefore - (window.ThreadCpuTicks - windowCpuBefore);
            var cpuPct = 100.0 * recorderCpu / (elapsed.Ticks * (double)Environment.ProcessorCount);
            var painted = window.Frames - paintedBefore;

            var after = recorder.Stats;
            var captured = after.FramesCaptured - before.FramesCaptured;
            var encoded = after.FramesEncoded - before.FramesEncoded;
            var dropped = after.FramesDropped - before.FramesDropped;
            var scan = MurmurMp4Scan.Scan(path);
            var length = new FileInfo(path).Length;
            output.WriteLine($"framePath='{recorder.FramePath}' encoder='{after.Encoder}' rateControl='{recorder.RateControlDescription}' size={recorder.LastEncoderSize} fps={recorder.EncoderFrameRate}");
            output.WriteLine($"wall {elapsed.TotalSeconds:F3} s: window painted {painted}, captured {captured}, encoded {encoded}, dropped {dropped} (pool empty {recorder.FramesPoolExhausted}), rate-limited {recorder.FramesRateLimited}, repeated {recorder.FramesRepeated}, pool headroom {recorder.LastPoolHeadroom} of {WgcScreenRecorder.MaximumSamples}, CPU samples {recorder.LastCpuSamples}");
            output.WriteLine($"file: {scan.Frames} frames, {scan.DurationS:F3} s (trun), mvhd {scan.MvhdDurationS:F3} s, {scan.Frames / Math.Max(0.001, scan.DurationS):F2} fps, timescale {scan.Timescale}, {length / 1024.0 / 1024.0:F2} MiB ({length * 8 / elapsed.TotalSeconds / 1000:F0} kbit/s), error: {scan.Error ?? "none"}");
            output.WriteLine($"test host while recording: working set max {workingSetMax / 1024.0 / 1024.0:F1} MB, {workingSetBaseline / 1024.0 / 1024.0:F1} MB before the test: +{(workingSetMax - workingSetBaseline) / 1024.0 / 1024.0:F1} MB for recording (budget {RamBudgetMb - AudioOnlyAppMb} MB on top of the {AudioOnlyAppMb} MB audio-only app = {RamBudgetMb} MB), CPU {cpuPct:F2} % of {Environment.ProcessorCount} logical CPUs without the window thread (budget {CpuBudgetPct} %)");

            lock (faults)
            {
                Assert.Empty(faults);
            }

            Assert.Equal(fps, recorder.EncoderFrameRate);
            if (display.Info.Height >= 1080 && display.Info.Width * 9 == display.Info.Height * 16)
            {
                Assert.Equal(new PixelSize(1920, 1080), recorder.LastEncoderSize); // e.g. 2560x1440 downscaled, aspect kept
            }
            else
            {
                Assert.InRange(recorder.LastEncoderSize.Height, 2, 1080);
            }

            Assert.StartsWith("peak-constrained VBR", recorder.RateControlDescription, StringComparison.Ordinal);

            // ~60 (CPU readback: ~30) fps captured and encoded, at most 2 % lost.
            Assert.InRange(captured, (long)(elapsed.TotalSeconds * fps * 0.95), (long)(elapsed.TotalSeconds * fps) + 2);
            Assert.True(dropped <= captured / 50, $"{dropped} of {captured} frames dropped (> 2 %)");
            Assert.Equal(captured - dropped, encoded);
            Assert.Equal(0, recorder.FramesPoolExhausted);
            if (!forceCpu)
            {
                Assert.True(recorder.LastPoolHeadroom >= 2, $"the sample pool got down to {recorder.LastPoolHeadroom} free samples");
            }

            // The file: wall-clock length at ~60 (~30) fps.
            Assert.Null(scan.Error);
            Assert.Equal(HeldFrameTiming.SinkTimescale(fps), scan.Timescale);
            Assert.InRange(scan.DurationS, elapsed.TotalSeconds - 0.5, elapsed.TotalSeconds + 0.5);
            Assert.InRange(scan.MvhdDurationS, elapsed.TotalSeconds - 0.5, elapsed.TotalSeconds + 0.5);
            Assert.InRange(scan.Frames / scan.DurationS, fps * 0.95, fps + 1.0);
            Assert.True(scan.Frames >= encoded - (forceCpu ? Math.Max(1, encoded / 50) : 0), $"the file holds {scan.Frames} frames, {encoded} were encoded");

            // The budgets are those of the GPU path. CPU readback is reported only: on this 16-thread PC the software encoder
            // alone takes ~160 MB at 1080p (with CBR as with VBR), and its CPU cost depends on the machine.
            if (!forceCpu)
            {
                var addedMb = (workingSetMax - workingSetBaseline) / 1024.0 / 1024.0;
                Assert.True(addedMb < RamBudgetMb - AudioOnlyAppMb, $"recording added {addedMb:F1} MB to the working set");
                Assert.True(cpuPct < CpuBudgetPct, $"CPU {cpuPct:F2} %");
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(warmUp);
        }
    }

    private static unsafe Rect32 WorkArea(nint monitor)
    {
        MonitorInfoExW info = default;
        info.Size = (uint)sizeof(MonitorInfoExW);
        Assert.NotEqual(0, ScreenNativeMethods.GetMonitorInfoW(monitor, &info));
        return info.Work;
    }

    private static unsafe int RefreshRate(string deviceName)
    {
        DevModeW mode = default;
        mode.Size = DevModeW.NativeSize;
        fixed (char* pDevice = deviceName)
        {
            return ScreenNativeMethods.EnumDisplaySettingsW(pDevice, ScreenConstants.EnumCurrentSettings, &mode) != 0 ? (int)mode.DisplayFrequency : 0;
        }
    }
}

/// <summary>
/// Tests that measure the test host's working set and CPU: they run alone, after the parallel tests, so other tests'
/// recorders and textures do not count against the recorder.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SoloMeasurementCollection
{
    public const string Name = "Solo measurement";
}
