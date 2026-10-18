using System.Diagnostics;
using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Screen;
using Murmur.Capture.Windows.Screen.Interop;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Tests;

/// <summary>Why the screen-recording tests cannot run on this machine, or null when they can.</summary>
internal static unsafe class ScreenTestEnvironment
{
    public static string? SkipReason()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Screen capture tests run only on Windows.";
        }

        foreach (var library in (ReadOnlySpan<string>)["mfplat.dll", "mfreadwrite.dll"])
        {
            if (!NativeLibrary.TryLoad(library, out var handle))
            {
                return $"Media Foundation is not installed ({library} missing: Windows N edition or Server without the Media Foundation feature).";
            }

            NativeLibrary.Free(handle);
        }

        if (!CaptureInterop.IsSupported())
        {
            return "Windows.Graphics.Capture is not supported here (GraphicsCaptureSession.IsSupported() returned false).";
        }

        uint session;
        if (ScreenNativeMethods.ProcessIdToSessionId((uint)Environment.ProcessId, &session) != 0 && session == 0)
        {
            return "The test process runs in session 0 (a service without an interactive desktop); Windows.Graphics.Capture needs a desktop session.";
        }

        try
        {
            if (Displays.Enumerate().Count == 0)
            {
                return "No display is connected.";
            }

            MediaFoundationRuntime.EnsureStarted();
        }
        catch (COMException ex)
        {
            return $"Media Foundation / display enumeration is unavailable: {ex.Message}";
        }

        return null;
    }
}

public sealed class WgcScreenRecorderTests(ITestOutputHelper output)
{
    private static readonly TimeSpan DurationTolerance = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Slack of the file timeline against the measured start/stop bounds (<see cref="AssertTimeline"/>), on top of one
    /// frame interval: the hardware path's trun sum is exact to one sink unit, but Microsoft's software H.264 encoder
    /// (CPU readback) re-times its output to a constant frame rate (it pads static stretches), so its file is a whole
    /// number of frames long (e.g. 2.000 s for 2.006 s at 15 fps); mvhd is rounded to its own timescale.
    /// </summary>
    private static readonly TimeSpan TimelineSlack = TimeSpan.FromMilliseconds(20);

    [Theory]
    [InlineData(0, 1080, 6000)]
    [InlineData(241, 1080, 6000)]
    [InlineData(30, 1, 6000)]
    [InlineData(30, 1080, 0)]
    public void Constructor_RejectsInvalidOptions(int fps, int maxHeight, int bitrate) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new WgcScreenRecorder(new ScreenCaptureOptions(null, fps, maxHeight, bitrate)));

    [Fact]
    public async Task NewRecorder_IsIdle_StopIsNoOp_AndDisposeIsIdempotent()
    {
        var recorder = new WindowsCapture().CreateScreenRecorder(new ScreenCaptureOptions());

        Assert.False(recorder.IsRecording);
        Assert.Equal(new ScreenRecorderStats(0, 0, 0, EncoderNaming.None), recorder.Stats);
        await recorder.StopSegmentAsync(TestContext.Current.CancellationToken);
        await recorder.DisposeAsync();
        await recorder.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await recorder.StartSegmentAsync(Path.Combine(Path.GetTempPath(), "x.mp4"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartSegment_ValidatesArguments()
    {
        await using var recorder = new WgcScreenRecorder(new ScreenCaptureOptions());
        await Assert.ThrowsAsync<ArgumentException>(async () => await recorder.StartSegmentAsync(string.Empty, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await recorder.StartSegmentAsync("x.mp4", cancelled.Token));
        Assert.False(recorder.IsRecording);
    }

    [Fact]
    public async Task StartSegment_WithoutGraphicsCapture_ThrowsNotSupported()
    {
        Assert.SkipWhen(CaptureInterop.IsSupported(), "Windows.Graphics.Capture is supported on this machine.");
        await using var recorder = new WgcScreenRecorder(new ScreenCaptureOptions());
        var path = Path.Combine(Path.GetTempPath(), $"murmur-screen-{Guid.NewGuid():N}.mp4");

        await Assert.ThrowsAsync<NotSupportedException>(async () => await recorder.StartSegmentAsync(path, TestContext.Current.CancellationToken));
        Assert.False(recorder.IsRecording);
        Assert.False(File.Exists(path));
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void GetDisplays_ReturnsNamedDisplays_WithOnePrimary()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Display enumeration runs only on Windows.");

        var displays = new WindowsCapture().GetDisplays();

        foreach (var display in displays)
        {
            output.WriteLine($"{display.Id}: '{display.Name}' {display.Width}x{display.Height} primary={display.IsPrimary}");
            Assert.StartsWith(@"\\.\", display.Id, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(display.Name));
            Assert.True(display.Width > 0 && display.Height > 0);
        }

        Assert.SkipWhen(displays.Count == 0, "No display is connected.");
        Assert.Equal(1, displays.Count(d => d.IsPrimary));
        Assert.True(Displays.Find(null).Info.IsPrimary);
        Assert.Equal(displays[^1].Id, Displays.Find(displays[^1].Id.ToLowerInvariant()).Info.Id);
        Assert.Throws<ArgumentException>(() => Displays.Find(@"\\.\NO-SUCH-DISPLAY"));
    }

    [Fact]
    [Trait("Category", "Windows")]
    public async Task Record_ThreeSecondsOfPrimaryDisplay_ProducesPlayableH264()
    {
        var reason = ScreenTestEnvironment.SkipReason();
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);
        var path = TempMp4();
        try
        {
            await using var recorder = new WgcScreenRecorder(new ScreenCaptureOptions());
            var faults = Collect(recorder);

            var elapsed = await RecordAsync(recorder, path, TimeSpan.FromSeconds(3));

            var stats = recorder.Stats;
            output.WriteLine($"{stats} device='{recorder.DeviceDescription}' framePath='{recorder.FramePath}' encoderSize={recorder.LastEncoderSize} fps={recorder.EncoderFrameRate} rateControl='{recorder.RateControlDescription}' frameArrived={recorder.FrameArrivedEvents} rateLimited={recorder.FramesRateLimited} repeated={recorder.FramesRepeated} placeholders={recorder.PlaceholderFrames} wall={elapsed}");
            output.WriteLine($"segment: empty={recorder.LastSegmentEmpty} {recorder.LastSegmentDiagnostic}; faults: {Describe(faults)}");
            Assert.NotEqual(EncoderNaming.None, stats.Encoder);
            SkipIfNoFramesCaptured(recorder, stats.FramesCaptured, [path], faults);

            Assert.Empty(faults);
            Assert.False(recorder.LastSegmentEmpty, recorder.LastSegmentDiagnostic);
            var length = new FileInfo(path).Length;
            output.WriteLine($"file {length} bytes");
            Assert.True(stats.FramesCaptured >= stats.FramesEncoded + stats.FramesDropped);

            var probe = Mp4Probe.Read(path);
            output.WriteLine(probe.ToString());
            Assert.Equal(ScreenConstants.MfVideoFormatH264, probe.VideoSubtype);
            Assert.True(probe.Frames >= 1);
            Assert.Equal(recorder.LastEncoderSize, new PixelSize(probe.Width, probe.Height));
            Assert.True(length > 10 * 1024, $"segment is only {length} bytes");
            Assert.True(stats.FramesEncoded >= 1, stats.ToString());
            Assert.Equal(0, recorder.PlaceholderFrames);
            AssertDuration(TimeSpan.FromSeconds(3), probe);
            AssertFrameRate(recorder, requested: 60);
            AssertTimeline(path, elapsed, recorder.EncoderFrameRate);
            AssertEncoderKeptUp(recorder, stats, probe.Frames);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("Category", "Windows")]
    public async Task Record_TwoConsecutiveSegments_ProducesTwoPlayableFiles()
    {
        var reason = ScreenTestEnvironment.SkipReason();
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);
        string[] paths = [TempMp4(), TempMp4()];
        try
        {
            await using var recorder = new WgcScreenRecorder(new ScreenCaptureOptions(FrameRate: 15, MaxHeight: 720, BitrateKbps: 4_000));
            var faults = Collect(recorder);

            var elapsed = new SegmentClock[paths.Length];
            elapsed[0] = await RecordAsync(recorder, paths[0], TimeSpan.FromSeconds(1.5));
            var first = recorder.Stats;
            var firstDiagnostic = recorder.LastSegmentDiagnostic;
            elapsed[1] = await RecordAsync(recorder, paths[1], TimeSpan.FromSeconds(1.5));
            var second = recorder.Stats;

            output.WriteLine($"after 1: {first}; after 2: {second}; frameArrived={recorder.FrameArrivedEvents} placeholders={recorder.PlaceholderFrames} repeated={recorder.FramesRepeated}");
            output.WriteLine($"device='{recorder.DeviceDescription}' framePath='{recorder.FramePath}' encoderSize={recorder.LastEncoderSize}");
            output.WriteLine($"segment 1: {firstDiagnostic}; segment 2: empty={recorder.LastSegmentEmpty} {recorder.LastSegmentDiagnostic}; faults: {Describe(faults)}");

            // Either segment without captured frames: skip (never pass silently), naming what happened to the segment.
            var capturedPerSegment = Math.Min(first.FramesCaptured, second.FramesCaptured - first.FramesCaptured);
            SkipIfNoFramesCaptured(recorder, capturedPerSegment, paths, faults, firstDiagnostic);

            Assert.Empty(faults);
            var probes = new Mp4ProbeResult[paths.Length];
            for (var i = 0; i < paths.Length; i++)
            {
                var probe = probes[i] = Mp4Probe.Read(paths[i]);
                output.WriteLine($"{Path.GetFileName(paths[i])}: {new FileInfo(paths[i]).Length} bytes, {probe}");
                Assert.Equal(ScreenConstants.MfVideoFormatH264, probe.VideoSubtype);
                Assert.True(probe.Frames >= 1);
                Assert.True(probe.Height <= 720);
            }

            Assert.Equal(0, recorder.PlaceholderFrames);
            Assert.True(second.FramesEncoded > first.FramesEncoded, "the second segment encoded nothing");
            for (var i = 0; i < paths.Length; i++)
            {
                AssertDuration(TimeSpan.FromSeconds(1.5), probes[i]);
                AssertTimeline(paths[i], elapsed[i], frameRate: 15);
            }

            AssertEncoderKeptUp(recorder, second, probes.Sum(p => p.Frames));
        }
        finally
        {
            foreach (var path in paths)
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    [Trait("Category", "Windows")]
    public async Task Record_WithForcedCpuReadback_ProducesPlayableH264()
    {
        // The software-encoder path (what GPU-less machines such as windows-latest use) also on machines with a GPU.
        var reason = ScreenTestEnvironment.SkipReason();
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);
        var path = TempMp4();
        try
        {
            await using var recorder = new WgcScreenRecorder(new ScreenCaptureOptions(FrameRate: 15, MaxHeight: 720, BitrateKbps: 4_000)) { ForceCpuReadback = true };
            var faults = Collect(recorder);

            var elapsed = await RecordAsync(recorder, path, TimeSpan.FromSeconds(2));

            var stats = recorder.Stats;
            output.WriteLine($"{stats} device='{recorder.DeviceDescription}' framePath='{recorder.FramePath}' encoderSize={recorder.LastEncoderSize} fps={recorder.EncoderFrameRate} rateControl='{recorder.RateControlDescription}' frameArrived={recorder.FrameArrivedEvents} repeated={recorder.FramesRepeated} placeholders={recorder.PlaceholderFrames}");
            output.WriteLine($"segment: empty={recorder.LastSegmentEmpty} {recorder.LastSegmentDiagnostic}; faults: {Describe(faults)}");
            Assert.Contains("CPU readback", stats.Encoder, StringComparison.Ordinal);
            SkipIfNoFramesCaptured(recorder, stats.FramesCaptured, [path], faults);

            Assert.Empty(faults);
            Assert.False(recorder.LastSegmentEmpty, recorder.LastSegmentDiagnostic);
            var probe = Mp4Probe.Read(path);
            output.WriteLine(probe.ToString());
            Assert.Equal(ScreenConstants.MfVideoFormatH264, probe.VideoSubtype);
            Assert.True(probe.Frames >= 1);
            Assert.True(probe.Height <= 720);
            Assert.Equal(recorder.LastEncoderSize, new PixelSize(probe.Width, probe.Height));
            Assert.True(stats.FramesEncoded >= 1, stats.ToString());
            Assert.Equal(0, recorder.PlaceholderFrames);
            AssertDuration(TimeSpan.FromSeconds(2), probe);
            AssertTimeline(path, elapsed, frameRate: 15);
            AssertEncoderKeptUp(recorder, stats, probe.Frames);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("Category", "Windows")]
    public async Task Record_AfterTheGpuPathWasGivenUp_UsesCpuReadback()
    {
        // What the segments after a persistent GPU pool exhaustion do (the exhaustion itself is pinned by PoolExhaustionTests).
        var reason = ScreenTestEnvironment.SkipReason();
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);
        var path = TempMp4();
        var given = PoolExhaustionNaming.FallbackReason(EncoderInput.Gpu, WgcScreenRecorder.MaximumSamples);
        try
        {
            await using var recorder = new WgcScreenRecorder(new ScreenCaptureOptions(FrameRate: 15, MaxHeight: 720, BitrateKbps: 4_000)) { GpuPathDisabled = given };
            var faults = Collect(recorder);

            await RecordAsync(recorder, path, TimeSpan.FromSeconds(1));

            output.WriteLine($"{recorder.Stats} framePath='{recorder.FramePath}'; faults: {Describe(faults)}");
            Assert.Equal(given, recorder.FramePath);
            Assert.Contains("CPU readback", recorder.Stats.Encoder, StringComparison.Ordinal);
            Assert.Empty(faults);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [Trait("Category", "Windows")]
    [InlineData(false, 30)]
    [InlineData(true, 30)]
    [InlineData(false, 60)]
    [InlineData(true, 60)]
    public async Task Record_StaticScreen_FileTimelineMatchesWallClock(bool forceCpu, int fps)
    {
        // A few frames, then none for seconds: what WGC delivers on a static screen. The fragmented MP4 stores only
        // sample durations (trun, no tfdt), so with a fixed 1/fps duration a 600 s static recording played 22.75 s.
        // Runs the GPU zero-copy path where this machine has it, and the CPU readback path.
        var reason = ScreenTestEnvironment.SkipReason();
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);
        var path = TempMp4();
        try
        {
            await using var recorder = new WgcScreenRecorder(new ScreenCaptureOptions(FrameRate: fps)) { ForceCpuReadback = forceCpu, CapturedFrameLimit = 3 };
            var faults = Collect(recorder);

            var elapsed = await RecordAsync(recorder, path, TimeSpan.FromSeconds(5));

            var stats = recorder.Stats;
            output.WriteLine($"{stats} framePath='{recorder.FramePath}' fps={recorder.EncoderFrameRate} rateControl='{recorder.RateControlDescription}' repeated={recorder.FramesRepeated} repeatsSkipped={recorder.RepeatsSkipped} wall={elapsed}");
            output.WriteLine($"segment: {recorder.LastSegmentDiagnostic}; faults: {Describe(faults)}");
            SkipIfNoFramesCaptured(recorder, stats.FramesCaptured, [path], faults);

            Assert.Empty(faults);
            Assert.InRange(stats.FramesCaptured, 1, 3);
            // About one repeat a second (4-5 in 5 s); a loaded machine may delay a few, so only demand that repeats
            // keep the file growing at all. The timeline below is the exact check: it holds however many repeats ran.
            Assert.True(recorder.FramesRepeated >= 2, $"only {recorder.FramesRepeated} repeats in 5 s of static screen");
            Assert.Equal(0, recorder.RepeatsSkipped);
            AssertFrameRate(recorder, fps);
            AssertTimeline(path, elapsed, recorder.EncoderFrameRate);
            AssertDuration(TimeSpan.FromSeconds(5), Mp4Probe.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("Category", "Windows")]
    public async Task Record_StaticScreen_WithNoFreeSampleForRepeats_CountsThem_AndKeepsTheTimeline()
    {
        // One CPU sample: the held frame occupies it, so every repeat finds the pool empty. Skipped repeats are
        // counted (not silent), and the held frame simply lasts until the stop.
        var reason = ScreenTestEnvironment.SkipReason();
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);
        var path = TempMp4();
        try
        {
            await using var recorder = new WgcScreenRecorder(new ScreenCaptureOptions(FrameRate: 15, MaxHeight: 720, BitrateKbps: 4_000))
            {
                ForceCpuReadback = true,
                CapturedFrameLimit = 1,
                CpuSampleLimit = 1,
            };
            var faults = Collect(recorder);

            var elapsed = await RecordAsync(recorder, path, TimeSpan.FromSeconds(3));

            var stats = recorder.Stats;
            output.WriteLine($"{stats} repeated={recorder.FramesRepeated} repeatsSkipped={recorder.RepeatsSkipped} wall={elapsed}; segment: {recorder.LastSegmentDiagnostic}; faults: {Describe(faults)}");
            SkipIfNoFramesCaptured(recorder, stats.FramesCaptured, [path], faults);

            Assert.Empty(faults);
            Assert.Equal(1, stats.FramesEncoded);
            Assert.Equal(0, recorder.FramesRepeated);
            Assert.True(recorder.RepeatsSkipped >= 1, "the skipped repeats were not counted");
            AssertTimeline(path, elapsed, frameRate: 15);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CpuSampleLimit_MustBePositive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new WgcScreenRecorder(new ScreenCaptureOptions()) { CpuSampleLimit = 0 });

    [Fact]
    [Trait("Category", "Windows")]
    public async Task StartSegment_UnknownDisplay_ThrowsArgumentException_AndStaysIdle()
    {
        var reason = ScreenTestEnvironment.SkipReason();
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);
        await using var recorder = new WgcScreenRecorder(new ScreenCaptureOptions(DisplayId: @"\\.\NO-SUCH-DISPLAY"));

        await Assert.ThrowsAsync<ArgumentException>(async () => await recorder.StartSegmentAsync(TempMp4(), TestContext.Current.CancellationToken));
        Assert.False(recorder.IsRecording);
    }

    /// <summary>
    /// Never fail and never pass silently when WGC delivered nothing: stopping must not have thrown (the caller got
    /// here), an empty segment must have no file, and the skip message names the placeholder outcome and the HRESULTs.
    /// </summary>
    private void SkipIfNoFramesCaptured(
        WgcScreenRecorder recorder,
        long framesCaptured,
        IReadOnlyList<string> paths,
        List<CaptureFault> faults,
        string? earlierDiagnostic = null)
    {
        if (framesCaptured != 0)
        {
            return;
        }

        var diagnostic = earlierDiagnostic is null || earlierDiagnostic == recorder.LastSegmentDiagnostic
            ? recorder.LastSegmentDiagnostic
            : $"segment 1: {earlierDiagnostic} | last segment: {recorder.LastSegmentDiagnostic}";
        var message = NoFramesSkipMessage(diagnostic, recorder, faults);
        output.WriteLine(message);
        if (recorder.LastSegmentEmpty)
        {
            Assert.False(File.Exists(paths[^1]), "an empty segment must not leave its partial file behind");
        }

        Assert.Skip(message);
    }

    private static string NoFramesSkipMessage(string? diagnostic, WgcScreenRecorder recorder, List<CaptureFault> faults) =>
        $"Windows.Graphics.Capture delivered no frames on this machine (headless CI session?); {diagnostic ?? "no diagnostic"}; "
        + $"frameArrived={recorder.FrameArrivedEvents}; device='{recorder.DeviceDescription}'; framePath='{recorder.FramePath}'; encoder='{recorder.Stats.Encoder}'; faults: {Describe(faults)}";

    private static string Describe(List<CaptureFault> faults)
    {
        lock (faults)
        {
            return faults.Count == 0 ? "none" : string.Join(" | ", faults.Select(f => f.Message));
        }
    }

    private static async Task<SegmentClock> RecordAsync(IScreenRecorder recorder, string path, TimeSpan duration)
    {
        var outer = Stopwatch.StartNew();
        await recorder.StartSegmentAsync(path, TestContext.Current.CancellationToken);
        var started = outer.Elapsed;
        Assert.True(recorder.IsRecording);
        await Task.Delay(duration, TestContext.Current.CancellationToken);
        var stopping = outer.Elapsed;
        await recorder.StopSegmentAsync(TestContext.Current.CancellationToken);
        var stopped = outer.Elapsed;
        Assert.False(recorder.IsRecording);
        return new SegmentClock(stopping - started, stopped);
    }

    private static List<CaptureFault> Collect(IScreenRecorder recorder)
    {
        var faults = new List<CaptureFault>();
        recorder.Faulted += f =>
        {
            lock (faults)
            {
                faults.Add(f);
            }
        };
        return faults;
    }

    /// <summary>
    /// Every captured frame reached the file (WGC decides how many frames there are; the frame path must not lose
    /// them): the GPU path once kept 12 frames of a 3 s segment and dropped the rest while its sample pool sat empty.
    /// </summary>
    private static void AssertEncoderKeptUp(WgcScreenRecorder recorder, ScreenRecorderStats stats, int framesInFiles)
    {
        var detail = $"{stats}; poolExhausted={recorder.FramesPoolExhausted} repeated={recorder.FramesRepeated} frameArrived={recorder.FrameArrivedEvents} framePath='{recorder.FramePath}'";
        Assert.True(stats.FramesDropped <= Math.Max(2, stats.FramesCaptured / 10), "more than 10% of the captured frames were dropped: " + detail);

        // More frames than written is fine: the software encoder pads static stretches to its frame rate. It also
        // re-times its output to that constant rate, so two captured frames within one frame interval (vsync jitter
        // against the rate limiter's tolerance) leave one frame: allow that on CPU readback, nowhere else.
        var written = stats.FramesEncoded + recorder.FramesRepeated;
        var retimed = recorder.FramePath!.StartsWith("CPU readback", StringComparison.Ordinal) ? Math.Max(1, written / 50) : 0;
        Assert.True(framesInFiles >= written - retimed, $"the files hold {framesInFiles} frames, fewer than were written: " + detail);
        Assert.Null(recorder.GpuPathDisabled);
    }

    /// <summary>
    /// The file's own timeline (the sum of its trun sample durations: what spec-compliant players and
    /// scripts/check-recording.ps1 go by) and its header duration (mvhd) both match the wall-clock length of the segment.
    /// The segment's timeline starts inside StartSegmentAsync and ends at the stop request inside StopSegmentAsync, so
    /// the file lasts at least <see cref="SegmentClock.Inner"/> and at most <see cref="SegmentClock.Outer"/>, give or
    /// take one frame interval plus <see cref="TimelineSlack"/>: a tight bound on an idle machine that only widens by
    /// the time the start and stop calls really took under load.
    /// </summary>
    private void AssertTimeline(string path, SegmentClock elapsed, int frameRate)
    {
        var slack = TimeSpan.FromTicks(MediaTime.FrameDuration(frameRate)) + TimelineSlack;
        var scan = MurmurMp4Scan.Scan(path);
        output.WriteLine($"{Path.GetFileName(path)}: {scan.DurationSource} {scan.DurationS:F3} s, mvhd {scan.MvhdDurationS:F3} s, {scan.Frames} frames, {scan.Fragments} fragments, wall {elapsed}, error: {scan.Error ?? "none"}");
        Assert.Null(scan.Error);
        Assert.True(scan.Fragmented, "not a fragmented MP4");
        Assert.Equal("trun", scan.DurationSource);

        // The timeline is quantized to this timescale (HeldFrameTiming): the sink must still use it.
        Assert.Equal(HeldFrameTiming.SinkTimescale(frameRate), scan.Timescale);
        var low = (elapsed.Inner - slack).TotalSeconds;
        var high = (elapsed.Outer + slack).TotalSeconds;
        Assert.InRange(scan.DurationS, low, high);
        Assert.InRange(scan.MvhdDurationS, low, high);
    }

    /// <summary>The GPU zero-copy path encodes at the requested rate; CPU readback caps it at 30 fps.</summary>
    private static void AssertFrameRate(WgcScreenRecorder recorder, int requested)
    {
        var cpu = recorder.FramePath!.StartsWith("CPU readback", StringComparison.Ordinal);
        Assert.Equal(cpu ? Math.Min(requested, FramePathPolicy.CpuMaxFrameRate) : requested, recorder.EncoderFrameRate);
    }

    private static void AssertDuration(TimeSpan expected, Mp4ProbeResult probe)
    {
        var actual = TimeSpan.FromTicks(probe.Duration);
        Assert.InRange(actual, expected - DurationTolerance, expected + DurationTolerance);
    }

    private static string TempMp4() => Path.Combine(Path.GetTempPath(), $"murmur-screen-{Guid.NewGuid():N}.mp4");

    /// <summary>
    /// Wall-clock bounds of a recorded segment: <see cref="Inner"/> from the return of StartSegmentAsync to the call
    /// of StopSegmentAsync, <see cref="Outer"/> from the call of StartSegmentAsync to the return of StopSegmentAsync.
    /// </summary>
    private readonly record struct SegmentClock(TimeSpan Inner, TimeSpan Outer)
    {
        public override string ToString() => $"{Inner.TotalSeconds:F3}..{Outer.TotalSeconds:F3} s";
    }
}
