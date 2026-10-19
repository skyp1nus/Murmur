using System.Diagnostics;
using System.Globalization;
using Murmur.Capture.Windows.Screen;
using Murmur.Capture.Windows.Screen.Interop;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// The default recording configuration (1080p60, VBR 12 Mbit/s) end to end through Windows.Graphics.Capture on a
/// screen whose content changes every refresh (<see cref="AnimatedWindow"/>): ~60 fps encoded with at most 2 % drops,
/// a file whose timeline is wall-clock time at 60 fps, and the test host's working set and CPU while recording. Memory
/// has no absolute cap with the screen (owner's decision, 2026-10-19: quality first; audio-only keeps &lt; 150 MB): it
/// is reported (absolute, and what recording adds to the test host), and the soak variant checks that it stays flat
/// after the first minute (&lt; 1 MB/min). CPU &lt; 8 % on the GPU path. Runs where the GPU zero-copy path exists
/// (skipped without a hardware H.264 encoder, e.g. on CI runners); the forced CPU readback variant measures the 1080p30
/// fallback on the same PC.
/// </summary>
[Trait("Category", "Windows")]
[Collection(SoloMeasurementCollection.Name)]
public sealed class WgcScreenRecorder1080p60Tests(ITestOutputHelper output)
{
    /// <summary>The app recording audio only (owner's PC, Native AOT, 12 min: 119 MB): what the screen adds comes on top.</summary>
    private const double AudioOnlyAppMb = 120;
    private const double CpuBudgetPct = 8;
    private const double MaxSlopeMbPerMin = 1;
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(60);

    /// <summary>The app's RecordingHeapTrimmer interval: a gen0 collection every 30 s while recording.</summary>
    private static readonly TimeSpan YoungCollectionInterval = TimeSpan.FromSeconds(30);

    /// <summary>Seconds the soak variant records (e.g. 240); unset: the soak variant is skipped.</summary>
    private const string SoakSecondsVariable = "MURMUR_SCREEN_SOAK_SECONDS";

    [Theory]
    [InlineData(false)] // GPU zero-copy: 1080p60
    [InlineData(true)]  // CPU readback (what machines without a GPU encoder use), capped at 1080p30
    public async Task Record_MovingContent_1080p60_KeepsUp_WithinTheCpuBudget(bool forceCpu)
    {
        var run = await RecordAsync(forceCpu, Duration);
        var fps = run.Fps;
        var scan = run.Scan;

        Assert.Equal(fps, run.EncoderFrameRate);
        if (run.Display.Height >= 1080 && run.Display.Width * 9 == run.Display.Height * 16)
        {
            Assert.Equal(new PixelSize(1920, 1080), run.EncoderSize); // e.g. 2560x1440 downscaled, aspect kept
        }
        else
        {
            Assert.InRange(run.EncoderSize.Height, 2, 1080);
        }

        Assert.StartsWith("peak-constrained VBR", run.RateControl, StringComparison.Ordinal);

        // ~60 (CPU readback: ~30) fps captured and encoded, at most 2 % lost.
        Assert.InRange(run.Captured, (long)(run.Elapsed.TotalSeconds * fps * 0.95), (long)(run.Elapsed.TotalSeconds * fps) + 2);
        Assert.True(run.Dropped <= run.Captured / 50, $"{run.Dropped} of {run.Captured} frames dropped (> 2 %)");
        Assert.Equal(run.Captured - run.Dropped, run.Encoded);
        Assert.Equal(0, run.PoolExhausted);
        if (!forceCpu)
        {
            Assert.True(run.PoolHeadroom >= 2, $"the sample pool got down to {run.PoolHeadroom} free samples");
        }

        // The file: wall-clock length at ~60 (~30) fps.
        Assert.Null(scan.Error);
        Assert.Equal(HeldFrameTiming.SinkTimescale(fps), scan.Timescale);
        Assert.InRange(scan.DurationS, run.Elapsed.TotalSeconds - 0.5, run.Elapsed.TotalSeconds + 0.5);
        Assert.InRange(scan.MvhdDurationS, run.Elapsed.TotalSeconds - 0.5, run.Elapsed.TotalSeconds + 0.5);
        Assert.InRange(scan.Frames / scan.DurationS, fps * 0.95, fps + 1.0);
        Assert.True(scan.Frames >= run.Encoded - (forceCpu ? Math.Max(1, run.Encoded / 50) : 0), $"the file holds {scan.Frames} frames, {run.Encoded} were encoded");
        Assert.InRange(run.Fragments.MaxSyncGapS, 0.1, 2.05);

        // The CPU budget is that of the GPU path; CPU readback's cost depends on the machine and is reported only.
        if (!forceCpu)
        {
            Assert.True(run.CpuPct < CpuBudgetPct, $"CPU {run.CpuPct:F2} %");
        }
    }

    /// <summary>
    /// Minutes of 1080p60 moving content (<see cref="SoakSecondsVariable"/>, e.g. 240): the working set must stay flat
    /// after the first minute, i.e. its least-squares slope over the samples after <see cref="WarmUp"/> stays below
    /// <see cref="MaxSlopeMbPerMin"/>. Opt-in, as it takes minutes; perf-record.yml gates the app over an hour.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Record_MovingContent_ForMinutes_WorkingSetStaysFlat(bool forceCpu)
    {
        var seconds = Environment.GetEnvironmentVariable(SoakSecondsVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(seconds), $"Set {SoakSecondsVariable} (e.g. 240) to record for minutes and check that the working set stays flat.");
        var duration = TimeSpan.FromSeconds(double.Parse(seconds!, CultureInfo.InvariantCulture));
        Assert.True(duration >= WarmUp + TimeSpan.FromMinutes(1), $"{SoakSecondsVariable} must leave at least a minute after the {WarmUp.TotalSeconds} s warm-up");
        var run = await RecordAsync(forceCpu, duration);
        var slope = WorkingSetSlope.MbPerMinute(run.WorkingSet, WarmUp.TotalSeconds);
        output.WriteLine("working set by 30 s: " + string.Join(", ", run.WorkingSet.GroupBy(s => (int)(s.Seconds / 30)).Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key * 30}s {g.Average(s => s.Mb):F1}"))));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"soak {duration.TotalSeconds:F0} s: working set slope after {WarmUp.TotalSeconds:F0} s {slope:F3} MB/min over {run.WorkingSet.Count(s => s.Seconds >= WarmUp.TotalSeconds)} samples (limit {MaxSlopeMbPerMin} MB/min)"));

        Assert.True(run.Dropped <= run.Captured / 50, $"{run.Dropped} of {run.Captured} frames dropped (> 2 %)");
        Assert.InRange(slope, double.MinValue, MaxSlopeMbPerMin);
    }

    /// <summary>
    /// A warm-up segment (JIT, Media Foundation), then the measured segment of <paramref name="duration"/> with the
    /// working set sampled every 250 ms; reports everything it measured.
    /// </summary>
    private async Task<Run> RecordAsync(bool forceCpu, TimeSpan duration)
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
            var samples = new List<WorkingSetSample>();
            var stopwatch = Stopwatch.StartNew();
            await recorder.StartSegmentAsync(path, TestContext.Current.CancellationToken);
            var nextCollection = YoungCollectionInterval;
            while (stopwatch.Elapsed < duration)
            {
                await Task.Delay(250, TestContext.Current.CancellationToken);
                if (stopwatch.Elapsed >= nextCollection)
                {
                    // What the app does while recording (RecordingHeapTrimmer): without it the test host's own garbage
                    // grows the working set until the GC's large gen0 budget runs out, which is not the recorder.
                    GC.Collect(0, GCCollectionMode.Forced, blocking: true, compacting: false);
                    nextCollection += YoungCollectionInterval;
                }

                process.Refresh();
                samples.Add(new WorkingSetSample(stopwatch.Elapsed.TotalSeconds, process.WorkingSet64 / 1024.0 / 1024.0));
            }

            await recorder.StopSegmentAsync(TestContext.Current.CancellationToken);
            var elapsed = stopwatch.Elapsed;
            process.Refresh();
            var recorderCpu = process.TotalProcessorTime.Ticks - cpuBefore - (window.ThreadCpuTicks - windowCpuBefore);
            var cpuPct = 100.0 * recorderCpu / (elapsed.Ticks * (double)Environment.ProcessorCount);
            var painted = window.Frames - paintedBefore;

            var after = recorder.Stats;
            var run = new Run(
                fps,
                display.Info,
                elapsed,
                after.FramesCaptured - before.FramesCaptured,
                after.FramesEncoded - before.FramesEncoded,
                after.FramesDropped - before.FramesDropped,
                recorder.FramesPoolExhausted,
                recorder.LastPoolHeadroom,
                recorder.EncoderFrameRate,
                recorder.LastEncoderSize,
                recorder.RateControlDescription ?? string.Empty,
                MurmurMp4Scan.Scan(path),
                Mp4Fragments.Read(path),
                samples,
                cpuPct);
            var length = new FileInfo(path).Length;
            var scan = run.Scan;
            var baselineMb = workingSetBaseline / 1024.0 / 1024.0;
            var maxMb = samples.Count == 0 ? 0 : samples.Max(s => s.Mb);
            var afterWarmUp = samples.Where(s => s.Seconds >= Math.Min(WarmUp.TotalSeconds, duration.TotalSeconds / 2)).ToList();
            var steadyMb = afterWarmUp.Count == 0 ? maxMb : afterWarmUp.Average(s => s.Mb);
            output.WriteLine($"framePath='{recorder.FramePath}' encoder='{after.Encoder}' rateControl='{recorder.RateControlDescription}' keyframes='{recorder.KeyFrameDescription}' size={recorder.LastEncoderSize} fps={recorder.EncoderFrameRate}");
            output.WriteLine($"wall {elapsed.TotalSeconds:F3} s: window painted {painted}, captured {run.Captured}, encoded {run.Encoded}, dropped {run.Dropped} (pool empty {recorder.FramesPoolExhausted}), rate-limited {recorder.FramesRateLimited}, repeated {recorder.FramesRepeated}, pool headroom {recorder.LastPoolHeadroom} of {WgcScreenRecorder.MaximumSamples}, CPU samples {recorder.LastCpuSamples} of {WgcScreenRecorder.MaximumCpuSamples}");
            output.WriteLine($"file: {scan.Frames} frames, {scan.DurationS:F3} s (trun), mvhd {scan.MvhdDurationS:F3} s, {scan.Frames / Math.Max(0.001, scan.DurationS):F2} fps, timescale {scan.Timescale}, {length / 1024.0 / 1024.0:F2} MiB ({length * 8 / elapsed.TotalSeconds / 1000:F0} kbit/s), max keyframe gap {run.Fragments.MaxSyncGapS:F3} s, max fragment gap {run.Fragments.MaxFragmentGapS:F3} s, error: {scan.Error ?? "none"}");
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"test host while recording: working set max {maxMb:F1} MB, steady {steadyMb:F1} MB, {baselineMb:F1} MB before the test: recording adds +{maxMb - baselineMb:F1} MB (max) / +{steadyMb - baselineMb:F1} MB (steady), i.e. ~{AudioOnlyAppMb + maxMb - baselineMb:F0} MB for the app on top of its {AudioOnlyAppMb} MB audio-only working set; CPU {cpuPct:F2} % of {Environment.ProcessorCount} logical CPUs without the window thread (budget {CpuBudgetPct} %)"));

            lock (faults)
            {
                Assert.Empty(faults);
            }

            return run;
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

    private sealed record Run(
        int Fps,
        DisplayInfo Display,
        TimeSpan Elapsed,
        long Captured,
        long Encoded,
        long Dropped,
        long PoolExhausted,
        int PoolHeadroom,
        int EncoderFrameRate,
        PixelSize EncoderSize,
        string RateControl,
        MurmurMp4ScanResult Scan,
        Mp4FragmentScan Fragments,
        IReadOnlyList<WorkingSetSample> WorkingSet,
        double CpuPct);
}

/// <summary>One working-set reading: seconds since the segment started, megabytes.</summary>
internal readonly record struct WorkingSetSample(double Seconds, double Mb);

/// <summary>Least-squares slope of the working set over the samples from <c>skipSeconds</c> on (MB per minute).</summary>
internal static class WorkingSetSlope
{
    public static double MbPerMinute(IReadOnlyList<WorkingSetSample> samples, double skipSeconds)
    {
        var tail = samples.Where(s => s.Seconds >= skipSeconds).ToList();
        if (tail.Count < 2)
        {
            return double.NaN;
        }

        var meanT = tail.Average(s => s.Seconds);
        var meanM = tail.Average(s => s.Mb);
        var cov = tail.Sum(s => (s.Seconds - meanT) * (s.Mb - meanM));
        var varT = tail.Sum(s => (s.Seconds - meanT) * (s.Seconds - meanT));
        return varT == 0 ? double.NaN : cov / varT * 60;
    }
}

/// <summary>
/// Tests that measure the test host's working set and CPU, or the recorder's real-time latency: they run alone, after
/// the parallel tests, so other tests' recorders, encoders and textures do not count against the recorder.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SoloMeasurementCollection
{
    public const string Name = "Solo measurement";
}
