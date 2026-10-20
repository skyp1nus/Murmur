using Murmur.Capture.Windows;
using Murmur.Core;
using Murmur.Core.Audio;
using Murmur.Core.Capture;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// Pause and resume of the real <see cref="Recorder"/> on the real devices: the WASAPI loopback source keeps running (it is
/// not re-opened), its audio is dropped while paused, and the Windows Graphics Capture segment ends at the pause and a new
/// one starts on resume. Skipped where there is no render endpoint (loopback) or the screen cannot be captured.
/// </summary>
public sealed class WindowsRecorderPauseTests(ITestOutputHelper output)
{
    private const string WindowsOnly = "WASAPI and WGC recording tests run only on Windows.";

    [Fact]
    [Trait("Category", "Windows")]
    public async Task PauseAndResume_KeepTheDeviceOpen_AndLeaveThePausedTimeOutOfTheFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var ct = TestContext.Current.CancellationToken;
        var inner = new WindowsCapture();
        Assert.SkipWhen(inner.GetAudioDevices(AudioDeviceKind.SystemOutput).Count == 0, "No active render endpoint on this machine.");

        var root = Path.Combine(Path.GetTempPath(), "murmur-pause-" + Guid.NewGuid().ToString("N"));
        try
        {
            var capture = new CountingCapture(inner);
            var store = new InMemorySessionStore(root);
            var recorder = new Recorder(capture, store, TimeProvider.System, RecorderSettings.Default);

            var started = await recorder.StartAsync(new RecordingOptions(Microphone: false, SystemAudio: true), ct);
            await Task.Delay(1_000, ct);
            await recorder.PauseAsync(ct);
            Assert.Equal(RecorderState.Paused, recorder.State);
            var atPause = recorder.Elapsed;
            Assert.Equal(default, recorder.Levels);
            await Task.Delay(1_000, ct);
            Assert.Equal(atPause, recorder.Elapsed);
            await recorder.ResumeAsync(ct);
            await Task.Delay(1_000, ct);
            var finished = await recorder.StopAsync(ct);

            output.WriteLine($"paused at {atPause.TotalSeconds:F3} s, recorded {finished.Duration.TotalSeconds:F3} s, sources opened {capture.AudioSourcesCreated}");
            Assert.Equal(1, capture.AudioSourcesCreated); // the device was not re-opened
            Assert.InRange(finished.Duration.TotalSeconds, 1.8, 2.4); // 2 s recorded, 1 s paused
            using var reader = WavReader.Open(store.GetFiles(started.Id).SystemAudio);
            Assert.Equal(finished.Duration.TotalSeconds, (double)reader.FrameCount / reader.Format.SampleRate, 0.1);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    [Trait("Category", "Windows")]
    public async Task PauseAndResume_SplitTheScreenSegment_OnTheSessionTimeline()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var ct = TestContext.Current.CancellationToken;
        var inner = new WindowsCapture();
        Assert.SkipWhen(!inner.Capabilities.Screen, "Screen capture is not supported on this machine.");
        Assert.SkipWhen(inner.GetAudioDevices(AudioDeviceKind.SystemOutput).Count == 0, "No active render endpoint on this machine.");

        var root = Path.Combine(Path.GetTempPath(), "murmur-pause-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new InMemorySessionStore(root);
            var recorder = new Recorder(inner, store, TimeProvider.System, RecorderSettings.Default);

            SessionRecord started;
            try
            {
                started = await recorder.StartAsync(new RecordingOptions(Microphone: false, SystemAudio: true, Screen: true), ct);
            }
            catch (Exception ex)
            {
                Assert.Skip("The screen recorder could not start here: " + ex.Message);
                return;
            }

            await Task.Delay(1_500, ct);
            await recorder.PauseAsync(ct);
            Assert.False(recorder.IsScreenRecording);
            var atPause = recorder.Elapsed;
            await Task.Delay(1_000, ct);
            await recorder.ResumeAsync(ct);
            await Task.Delay(1_500, ct);
            var finished = await recorder.StopAsync(ct);

            output.WriteLine($"paused at {atPause.TotalSeconds:F3} s, segments: {string.Join("; ", finished.ScreenSegments)}");
            if (finished.ScreenSegments.Count < 2)
            {
                // A screen that stopped by itself (no GPU, a fault) is reported as a warning; the audio is what this test is about.
                Assert.NotNull(finished.Error);
                return;
            }

            var (first, second) = (finished.ScreenSegments[0], finished.ScreenSegments[1]);
            Assert.InRange((first.Offset + first.Duration).TotalSeconds, atPause.TotalSeconds - 0.25, atPause.TotalSeconds + 0.25);
            Assert.InRange(second.Offset.TotalSeconds, atPause.TotalSeconds - 0.25, atPause.TotalSeconds + 0.25);
            foreach (var segment in finished.ScreenSegments)
            {
                Assert.True(File.Exists(store.GetFiles(started.Id).ScreenSegment(segment.Index)), $"segment {segment.Index} has no file");
            }

            // The first video ends where the pause began: its media duration is the segment's duration on the timeline (D-033), not
            // the second of the pause longer.
            var scan = MurmurMp4Scan.Scan(store.GetFiles(started.Id).ScreenSegment(first.Index));
            output.WriteLine($"first segment: {scan.DurationS:F3} s of media ({scan.DurationSource}), {first.Duration.TotalSeconds:F3} s on the timeline");
            Assert.Null(scan.Error);
            Assert.InRange(scan.DurationS, first.Duration.TotalSeconds - 0.4, first.Duration.TotalSeconds + 0.4);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // A temp folder left behind is harmless.
        }
    }

    /// <summary>Counts the audio sources the recorder opens.</summary>
    private sealed class CountingCapture(ICapture inner) : ICapture
    {
        private int _sources;

        public int AudioSourcesCreated => Volatile.Read(ref _sources);

        public CaptureCapabilities Capabilities => inner.Capabilities;

        public IReadOnlyList<AudioDevice> GetAudioDevices(AudioDeviceKind kind) => inner.GetAudioDevices(kind);

        public IAudioSource CreateAudioSource(AudioDeviceKind kind, string? deviceId)
        {
            Interlocked.Increment(ref _sources);
            return inner.CreateAudioSource(kind, deviceId);
        }

        public IReadOnlyList<DisplayInfo> GetDisplays() => inner.GetDisplays();

        public IScreenRecorder CreateScreenRecorder(ScreenCaptureOptions options) => inner.CreateScreenRecorder(options);
    }
}
