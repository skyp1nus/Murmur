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
            output.WriteLine($"{stats} device='{recorder.DeviceDescription}' framePath='{recorder.FramePath}' encoderSize={recorder.LastEncoderSize} frameArrived={recorder.FrameArrivedEvents} rateLimited={recorder.FramesRateLimited} repeated={recorder.FramesRepeated} placeholders={recorder.PlaceholderFrames} wall={elapsed.TotalSeconds:F3} s");
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

            await RecordAsync(recorder, paths[0], TimeSpan.FromSeconds(1.5));
            var first = recorder.Stats;
            var firstDiagnostic = recorder.LastSegmentDiagnostic;
            await RecordAsync(recorder, paths[1], TimeSpan.FromSeconds(1.5));
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
            foreach (var probe in probes)
            {
                AssertDuration(TimeSpan.FromSeconds(1.5), probe);
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

            await RecordAsync(recorder, path, TimeSpan.FromSeconds(2));

            var stats = recorder.Stats;
            output.WriteLine($"{stats} device='{recorder.DeviceDescription}' framePath='{recorder.FramePath}' encoderSize={recorder.LastEncoderSize} frameArrived={recorder.FrameArrivedEvents} repeated={recorder.FramesRepeated} placeholders={recorder.PlaceholderFrames}");
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

    private static async Task<TimeSpan> RecordAsync(IScreenRecorder recorder, string path, TimeSpan duration)
    {
        await recorder.StartSegmentAsync(path, TestContext.Current.CancellationToken);
        Assert.True(recorder.IsRecording);
        var stopwatch = Stopwatch.StartNew();
        await Task.Delay(duration, TestContext.Current.CancellationToken);
        await recorder.StopSegmentAsync(TestContext.Current.CancellationToken);
        Assert.False(recorder.IsRecording);
        return stopwatch.Elapsed;
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

        // More frames than written is fine: the software encoder pads static stretches to its frame rate.
        Assert.True(framesInFiles >= stats.FramesEncoded + recorder.FramesRepeated, $"the files hold {framesInFiles} frames, fewer than were written: " + detail);
        Assert.Null(recorder.GpuPathDisabled);
    }

    private static void AssertDuration(TimeSpan expected, Mp4ProbeResult probe)
    {
        var actual = TimeSpan.FromTicks(probe.Duration);
        Assert.InRange(actual, expected - DurationTolerance, expected + DurationTolerance);
    }

    private static string TempMp4() => Path.Combine(Path.GetTempPath(), $"murmur-screen-{Guid.NewGuid():N}.mp4");
}
