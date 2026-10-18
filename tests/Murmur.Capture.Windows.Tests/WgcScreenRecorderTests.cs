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
    internal const string NoFramesSkipMessage =
        "Windows.Graphics.Capture delivered no frames on this machine (headless session?) — encoder path verified with a black frame only";

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
            output.WriteLine($"{stats} device='{recorder.DeviceDescription}' encoderSize={recorder.LastEncoderSize} frameArrived={recorder.FrameArrivedEvents} rateLimited={recorder.FramesRateLimited} repeated={recorder.FramesRepeated} placeholders={recorder.PlaceholderFrames} wall={elapsed.TotalSeconds:F3} s");
            Assert.Empty(faults);
            var length = new FileInfo(path).Length;
            output.WriteLine($"file {length} bytes");
            Assert.True(stats.FramesCaptured >= stats.FramesEncoded + stats.FramesDropped);
            Assert.NotEqual(EncoderNaming.None, stats.Encoder);

            // The file must be a playable H.264 MP4 in every case, including the black placeholder of an empty segment.
            var probe = Mp4Probe.Read(path);
            output.WriteLine(probe.ToString());
            Assert.Equal(ScreenConstants.MfVideoFormatH264, probe.VideoSubtype);
            Assert.True(probe.Frames >= 1);
            Assert.Equal(recorder.LastEncoderSize, new PixelSize(probe.Width, probe.Height));
            SkipIfNoFramesCaptured(recorder, stats.FramesCaptured, probe);

            Assert.True(length > 10 * 1024, $"segment is only {length} bytes");
            Assert.True(stats.FramesEncoded >= 1, stats.ToString());
            Assert.Equal(0, recorder.PlaceholderFrames);
            AssertDuration(TimeSpan.FromSeconds(3), probe);
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
            await RecordAsync(recorder, paths[1], TimeSpan.FromSeconds(1.5));
            var second = recorder.Stats;

            output.WriteLine($"after 1: {first}; after 2: {second}; frameArrived={recorder.FrameArrivedEvents} placeholders={recorder.PlaceholderFrames}");
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

            var emptySegments = (first.FramesCaptured == 0 ? 1 : 0) + (second.FramesCaptured == first.FramesCaptured ? 1 : 0);
            Assert.Equal(emptySegments, recorder.PlaceholderFrames);
            if (emptySegments > 0)
            {
                foreach (var probe in probes)
                {
                    Assert.True(probe.Frames >= 1, probe.ToString());
                }

                Assert.Skip(NoFramesSkipMessage);
            }

            Assert.True(second.FramesEncoded > first.FramesEncoded, "the second segment encoded nothing");
            foreach (var probe in probes)
            {
                AssertDuration(TimeSpan.FromSeconds(1.5), probe);
            }
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
    public async Task StartSegment_UnknownDisplay_ThrowsArgumentException_AndStaysIdle()
    {
        var reason = ScreenTestEnvironment.SkipReason();
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);
        await using var recorder = new WgcScreenRecorder(new ScreenCaptureOptions(DisplayId: @"\\.\NO-SUCH-DISPLAY"));

        await Assert.ThrowsAsync<ArgumentException>(async () => await recorder.StartSegmentAsync(TempMp4(), TestContext.Current.CancellationToken));
        Assert.False(recorder.IsRecording);
    }

    /// <summary>
    /// Never pass silently when WGC delivered nothing: the caller has verified the file; check the placeholder
    /// bookkeeping and skip with an explicit message.
    /// </summary>
    private void SkipIfNoFramesCaptured(WgcScreenRecorder recorder, long framesCaptured, Mp4ProbeResult probe)
    {
        if (framesCaptured != 0)
        {
            return;
        }

        output.WriteLine($"no frames captured: frameArrived={recorder.FrameArrivedEvents}, placeholder probe {probe}");
        Assert.Equal(0, recorder.Stats.FramesEncoded);
        Assert.Equal(1, recorder.PlaceholderFrames);
        Assert.Equal(1, probe.Frames);
        Assert.Skip(NoFramesSkipMessage);
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

    private static void AssertDuration(TimeSpan expected, Mp4ProbeResult probe)
    {
        var actual = TimeSpan.FromTicks(probe.Duration);
        Assert.InRange(actual, expected - DurationTolerance, expected + DurationTolerance);
    }

    private static string TempMp4() => Path.Combine(Path.GetTempPath(), $"murmur-screen-{Guid.NewGuid():N}.mp4");
}
