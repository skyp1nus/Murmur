using System.Diagnostics;
using Murmur.Capture.Windows.Screen;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// The IScreenRecorder segment contract on the real recorder: media time 0 of the file is the moment
/// StartSegmentAsync is called and the file ends when StopSegmentAsync is called, so the file's media duration is the
/// wall time between the two calls even when starting (or stopping) takes a long time. A start-up that is artificially
/// slow (<see cref="WgcScreenRecorder.StartupDelay"/>, before the encoder opens) is what the owner's PC shows for real
/// (~0.45 s of Media Foundation + capture set-up on the GPU path).
/// </summary>
[Trait("Category", "Windows")]
public sealed class ScreenSegmentClockTests(ITestOutputHelper output)
{
    /// <summary>The file lasts the wall time between the calls, give or take this (and one frame on CPU readback).</summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(50);

    [Fact]
    public Task SlowStart_FileStillStartsAtTheStartCall() =>
        RecordAndCompareAsync(new WgcScreenRecorder(new ScreenCaptureOptions()) { StartupDelay = TimeSpan.FromMilliseconds(700) }, TimeSpan.FromSeconds(2), minimumStart: TimeSpan.FromMilliseconds(700), minimumStop: TimeSpan.Zero);

    [Fact]
    public Task SlowStop_FileStillEndsAtTheStopCall() =>
        RecordAndCompareAsync(new WgcScreenRecorder(new ScreenCaptureOptions()) { StopDelay = TimeSpan.FromMilliseconds(700) }, TimeSpan.FromSeconds(2), minimumStart: TimeSpan.Zero, minimumStop: TimeSpan.FromMilliseconds(700));

    [Fact]
    public Task RealStartUp_FileStillStartsAtTheStartCall() =>
        RecordAndCompareAsync(new WgcScreenRecorder(new ScreenCaptureOptions()), TimeSpan.FromSeconds(2), minimumStart: TimeSpan.Zero, minimumStop: TimeSpan.Zero);

    [Fact]
    public Task SlowStart_OnCpuReadback_FileStillStartsAtTheStartCall() =>
        RecordAndCompareAsync(new WgcScreenRecorder(new ScreenCaptureOptions()) { ForceCpuReadback = true, StartupDelay = TimeSpan.FromMilliseconds(700) }, TimeSpan.FromSeconds(2), minimumStart: TimeSpan.FromMilliseconds(700), minimumStop: TimeSpan.Zero);

    private async Task RecordAndCompareAsync(WgcScreenRecorder recorder, TimeSpan recording, TimeSpan minimumStart, TimeSpan minimumStop)
    {
        var reason = ScreenTestEnvironment.SkipReason();
        if (reason is not null)
        {
            await recorder.DisposeAsync();
            Assert.Skip(reason);
        }

        var path = Path.Combine(Path.GetTempPath(), $"murmur-segclock-{Guid.NewGuid():N}.mp4");
        try
        {
            await using (recorder)
            {
                var faults = new List<CaptureFault>();
                recorder.Faulted += f =>
                {
                    lock (faults)
                    {
                        faults.Add(f);
                    }
                };

                var startCall = Stopwatch.GetTimestamp();
                await recorder.StartSegmentAsync(path, TestContext.Current.CancellationToken);
                var startReturned = Stopwatch.GetTimestamp();
                await Task.Delay(recording, TestContext.Current.CancellationToken);
                var stopCall = Stopwatch.GetTimestamp();
                await recorder.StopSegmentAsync(TestContext.Current.CancellationToken);
                var stopReturned = Stopwatch.GetTimestamp();

                var starting = Stopwatch.GetElapsedTime(startCall, startReturned);
                var stopping = Stopwatch.GetElapsedTime(stopCall, stopReturned);
                var wall = Stopwatch.GetElapsedTime(startCall, stopCall);
                output.WriteLine($"start took {starting.TotalMilliseconds:F0} ms, stop took {stopping.TotalMilliseconds:F0} ms, wall start call..stop call {wall.TotalSeconds:F3} s; framePath='{recorder.FramePath}' fps={recorder.EncoderFrameRate} captured={recorder.Stats.FramesCaptured} placeholders={recorder.PlaceholderFrames}");
                Assert.True(starting >= minimumStart, $"the start took only {starting}: the test seam did not delay it");
                Assert.True(stopping >= minimumStop, $"the stop took only {stopping}: the test seam did not delay it");
                if (recorder.LastSegmentEmpty)
                {
                    Assert.Skip("The segment ended empty (no frame captured on this machine): " + recorder.LastSegmentDiagnostic);
                }

                Assert.Empty(faults);
                var scan = MurmurMp4Scan.Scan(path);
                output.WriteLine($"{scan.DurationSource} {scan.DurationS:F3} s, mvhd {scan.MvhdDurationS:F3} s, {scan.Frames} frames, error: {scan.Error ?? "none"}");
                Assert.Null(scan.Error);

                // Microsoft's software encoder (CPU readback) re-times its output to whole frames.
                var slack = Tolerance + (recorder.FramePath!.StartsWith("CPU readback", StringComparison.Ordinal) ? TimeSpan.FromTicks(MediaTime.FrameDuration(recorder.EncoderFrameRate)) : TimeSpan.Zero);
                Assert.InRange(scan.DurationS, (wall - slack).TotalSeconds, (wall + slack).TotalSeconds);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
