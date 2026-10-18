using System.Diagnostics;
using System.Globalization;
using Murmur.Capture.Windows.Screen;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// What a crash leaves of a screen segment: a static screen recorded for 12 s (the recorder repeats the held frame about
/// once a second), with the file read every second while it is being written. What is on disk at that moment is what a
/// crashed process leaves behind (the sink writer is abandoned without Finalize; nothing more reaches the file), so
/// each snapshot must hold complete fragments up to at most 3 s before the snapshot, readable by Media Foundation's
/// source reader; and the finished file must have keyframes and fragments at most 3 s apart. GPU zero-copy (skipped
/// without a hardware H.264 encoder on a real GPU) and forced CPU readback.
/// </summary>
[Trait("Category", "Windows")]
public sealed class ScreenCrashRecoveryTests(ITestOutputHelper output)
{
    private const double MaxLossS = 3.0;
    private const double MaxGapS = 3.0;
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(12);

    [Theory]
    [InlineData(false)] // GPU zero-copy, forced keyframes
    [InlineData(true)]  // CPU readback
    public async Task StaticScreen_AFileCutOffAtAnyMoment_KeepsAllButTheLast3s(bool forceCpu)
    {
        var reason = ScreenTestEnvironment.SkipReason();
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);
        if (!forceCpu)
        {
            using (GpuTestDevice.HardwareOrSkip())
            {
            }
        }

        var path = Path.Combine(Path.GetTempPath(), $"murmur-crash-{Guid.NewGuid():N}.mp4");
        var copy = Path.ChangeExtension(path, ".snapshot.mp4");
        try
        {
            await using var recorder = new WgcScreenRecorder(new ScreenCaptureOptions()) { ForceCpuReadback = forceCpu, CapturedFrameLimit = 3 };
            var faults = new List<CaptureFault>();
            recorder.Faulted += f =>
            {
                lock (faults)
                {
                    faults.Add(f);
                }
            };

            // Times on the segment's timeline are measured from the start call: the timeline starts inside it, so the
            // loss computed here is, if anything, too large.
            var clock = Stopwatch.StartNew();
            await recorder.StartSegmentAsync(path, TestContext.Current.CancellationToken);
            var worstLoss = 0.0;
            var snapshots = 0;
            while (clock.Elapsed < Duration)
            {
                await Task.Delay(1_000, TestContext.Current.CancellationToken);
                var at = clock.Elapsed.TotalSeconds;
                var snapshot = Mp4Fragments.Read(path);
                var loss = at - snapshot.EndS;
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"cut off at {at:F2} s: {snapshot.Fragments.Count} complete fragments up to {snapshot.EndS:F2} s (loss {loss:F2} s), {snapshot.SyncTimesS.Count} keyframes, {snapshot.RecoverableBytes} of {snapshot.FileBytes} bytes"));
                if (at >= MaxLossS)
                {
                    worstLoss = Math.Max(worstLoss, loss);
                    snapshots++;
                    Assert.True(loss <= MaxLossS, string.Create(CultureInfo.InvariantCulture, $"a crash at {at:F2} s would keep only {snapshot.EndS:F2} s of video"));
                }
            }

            // The last snapshot as a file of its own: Media Foundation's source reader reads every complete sample.
            File.Copy(path, copy, overwrite: true);
            var cut = Mp4Fragments.Read(copy);
            var probe = Mp4Probe.Read(copy);
            output.WriteLine($"snapshot file: {cut}; source reader: {probe.Frames} samples up to {probe.LastSampleEnd / 1e7:F2} s");
            Assert.True(probe.Frames >= cut.Samples.Count, $"the source reader read {probe.Frames} of {cut.Samples.Count} complete samples of the cut-off file");

            await recorder.StopSegmentAsync(TestContext.Current.CancellationToken);
            var elapsed = clock.Elapsed.TotalSeconds;
            var scan = Mp4Fragments.Read(path);
            output.WriteLine($"framePath='{recorder.FramePath}' encoder='{recorder.Stats.Encoder}' keyframes='{recorder.KeyFrameDescription}' repeated={recorder.FramesRepeated} repeatsSkipped={recorder.RepeatsSkipped}");
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"finished file ({elapsed:F2} s wall): {scan}; worst loss {worstLoss:F2} s over {snapshots} snapshots"));
            lock (faults)
            {
                Assert.Empty(faults);
            }

            Assert.SkipWhen(recorder.Stats.FramesCaptured == 0, "Windows.Graphics.Capture delivered no frames on this machine.");
            Assert.True(snapshots >= 8, $"only {snapshots} snapshots");
            Assert.False(scan.Truncated);
            Assert.InRange(scan.EndS, Duration.TotalSeconds - 0.5, elapsed + 0.5);
            Assert.InRange(scan.MaxSyncGapS, 0.1, MaxGapS);
            Assert.InRange(scan.MaxFragmentGapS, 0.01, MaxGapS);
            Assert.Equal(!forceCpu, recorder.KeyFrameDescription!.StartsWith("an IDR frame at least every 2 s (forced", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
            File.Delete(copy);
        }
    }
}
