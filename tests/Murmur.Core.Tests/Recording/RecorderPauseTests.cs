using Murmur.Core.Audio;
using Murmur.Core.Capture.Synthetic;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Recording;

/// <summary>
/// Pause and resume: the paused time is left out of the session timeline, the files and the screen segments; the sources
/// run on (no device is re-opened) and the writer stays allocation-free. The synthetic sources run in real time (speed 1),
/// so recorded time and wall time are comparable, with slack for a loaded machine.
/// </summary>
[Collection(RealtimeCollection.Name)]
public sealed class RecorderPauseTests : IDisposable
{
    private static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(250);

    private readonly TempDirectory _dir = new();
    private readonly InMemorySessionStore _store;

    public RecorderPauseTests() => _store = new InMemorySessionStore(_dir.Path);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    private static TimeSpan AudioLength(string path)
    {
        using var reader = WavReader.Open(path);
        return TimeSpan.FromSeconds((double)reader.FrameCount / reader.Format.SampleRate);
    }

    [Fact]
    public async Task Pause_StopsTheTimelineAndTheLevels_ResumeContinuesIt()
    {
        var recorder = new Recorder(new SyntheticCapture(), _store, TimeProvider.System, RecorderSettings.Default);
        var states = new List<RecorderState>();
        recorder.StateChanged += states.Add;

        await recorder.StartAsync(new RecordingOptions(), Ct);
        await Task.Delay(400, Ct);
        Assert.True(recorder.Levels.Microphone > 0.05f);

        await recorder.PauseAsync(Ct);
        Assert.Equal(RecorderState.Paused, recorder.State);
        var elapsedAtPause = recorder.Elapsed;
        Assert.InRange(elapsedAtPause, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(400) + Slack);
        Assert.Equal(default, recorder.Levels);

        await Task.Delay(500, Ct);
        Assert.Equal(elapsedAtPause, recorder.Elapsed);
        Assert.Equal(default, recorder.Levels);

        await recorder.ResumeAsync(Ct);
        Assert.Equal(RecorderState.Recording, recorder.State);
        await Task.Delay(300, Ct);
        Assert.InRange(recorder.Elapsed, elapsedAtPause + TimeSpan.FromMilliseconds(250), elapsedAtPause + TimeSpan.FromMilliseconds(300) + Slack);
        Assert.True(recorder.Levels.Microphone > 0.05f, $"mic level {recorder.Levels.Microphone}");

        await recorder.StopAsync(Ct);
        Assert.Equal([RecorderState.Starting, RecorderState.Recording, RecorderState.Paused, RecorderState.Recording, RecorderState.Stopping, RecorderState.Idle], states);
    }

    [Fact]
    public async Task PausedTime_IsLeftOutOfTheFiles_AndTheSourcesKeepRunning()
    {
        var capture = new SyntheticCapture();
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        var started = await recorder.StartAsync(new RecordingOptions(), Ct);
        await Task.Delay(400, Ct);
        await recorder.PauseAsync(Ct);
        var atPause = recorder.Elapsed;
        await Task.Delay(700, Ct);
        await recorder.ResumeAsync(Ct);
        await Task.Delay(400, Ct);
        var finished = await recorder.StopAsync(Ct);

        // 0.8 s were recorded, 0.7 s were paused: the files hold the first, not the second.
        Assert.Equal(SessionState.Recorded, finished.State);
        Assert.InRange(finished.Duration, TimeSpan.FromMilliseconds(650), TimeSpan.FromMilliseconds(800) + Slack);
        var files = _store.GetFiles(started.Id);
        foreach (var path in new[] { files.MicrophoneAudio, files.SystemAudio })
        {
            Assert.Equal(finished.Duration.TotalSeconds, AudioLength(path).TotalSeconds, 0.05);
        }

        // The devices were not re-opened and kept delivering during the pause (their audio was thrown away).
        Assert.Equal(2, capture.CreatedSources.Count);
        foreach (var source in capture.CreatedSources)
        {
            Assert.True(source.FramesEmitted >= 1.4 * SyntheticSignals.SampleRate, $"{source.FramesEmitted} frames emitted");
        }

        Assert.Equal(0, recorder.Stats.DroppedFrames);
        Assert.True(atPause < finished.Duration);
    }

    [Fact]
    public async Task Resume_LeavesNeitherAGapNorSilenceAtTheSeam()
    {
        var capture = new SyntheticCapture();
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        var started = await recorder.StartAsync(new RecordingOptions(SystemAudio: false), Ct);
        await Task.Delay(500, Ct);
        await recorder.PauseAsync(Ct);
        await Task.Delay(400, Ct);
        await recorder.ResumeAsync(Ct);
        await Task.Delay(500, Ct);
        await recorder.StopAsync(Ct);

        // The microphone is a 440 Hz sine with an envelope of at least 0.2: every 5 ms window of the file has sound, also
        // around the seam, so nothing was padded with silence and no part was cut out.
        var (_, samples) = WavReader.ReadAll(_store.GetFiles(started.Id).MicrophoneAudio);
        const int window = 80;
        for (var i = 0; i + window <= samples.Length; i += window)
        {
            var peak = 0f;
            foreach (var sample in samples.AsSpan(i, window))
            {
                peak = Math.Max(peak, Math.Abs(sample));
            }

            Assert.True(peak > 0.05f, $"silence at {i / 16000.0:F3} s");
        }
    }

    [Fact]
    public async Task ScreenSegment_EndsAtThePause_AndANewOneStartsOnResume()
    {
        var capture = new SyntheticCapture(new SyntheticCaptureOptions(Screen: true));
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        await Task.Delay(400, Ct);
        Assert.True(recorder.IsScreenRecording);
        await recorder.PauseAsync(Ct);
        var atPause = recorder.Elapsed;
        Assert.False(recorder.IsScreenRecording);
        await Task.Delay(600, Ct);
        await recorder.ResumeAsync(Ct);
        Assert.True(recorder.IsScreenRecording);
        await Task.Delay(300, Ct);
        var finished = await recorder.StopAsync(Ct);

        Assert.Equal(2, finished.ScreenSegments.Count);
        var (first, second) = (finished.ScreenSegments[0], finished.ScreenSegments[1]);
        Assert.Equal([0, 1], finished.ScreenSegments.Select(s => s.Index));
        Assert.InRange(first.Offset, TimeSpan.Zero, Slack);

        // The first one ends where the pause was called (the audio stops a writer period later); the second one starts where the
        // timeline continues: the 600 ms of the pause are not between them.
        Assert.InRange(first.Offset + first.Duration, atPause - Slack, atPause + Slack);
        Assert.InRange(second.Offset, atPause - Slack, atPause + Slack);
        Assert.InRange(second.Duration, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(300) + Slack);
        Assert.Equal(2, Assert.Single(capture.CreatedScreenRecorders).Segments.Count);
    }

    [Fact]
    public async Task ScreenSwitchedWhilePaused_DecidesWhetherASegmentStartsOnResume()
    {
        var capture = new SyntheticCapture(new SyntheticCaptureOptions(Screen: true));
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        await Task.Delay(150, Ct);
        await recorder.PauseAsync(Ct);

        await recorder.SetScreenEnabledAsync(false, Ct); // off while paused: nothing starts on resume
        await recorder.ResumeAsync(Ct);
        Assert.False(recorder.IsScreenRecording);

        await recorder.PauseAsync(Ct);
        await recorder.SetScreenEnabledAsync(true, Ct); // on while paused: a segment starts on resume, not before
        Assert.False(recorder.IsScreenRecording);
        await recorder.ResumeAsync(Ct);
        Assert.True(recorder.IsScreenRecording);
        await Task.Delay(100, Ct);
        var finished = await recorder.StopAsync(Ct);

        Assert.Equal(2, finished.ScreenSegments.Count);
    }

    [Fact]
    public async Task ScreenWhichWasOffBeforeThePause_StaysOffAfterTheResume()
    {
        var capture = new SyntheticCapture(new SyntheticCaptureOptions(Screen: true));
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        await recorder.StartAsync(new RecordingOptions(Screen: false), Ct);
        await recorder.PauseAsync(Ct);
        await recorder.ResumeAsync(Ct);
        await Task.Delay(100, Ct);
        var finished = await recorder.StopAsync(Ct);

        Assert.False(recorder.IsScreenRecording);
        Assert.Empty(finished.ScreenSegments);
    }

    [Fact]
    public async Task Stop_WhilePaused_EndsTheFilesWhereThePauseLeftThem()
    {
        var capture = new SyntheticCapture();
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);
        var states = new List<RecorderState>();
        recorder.StateChanged += states.Add;

        var started = await recorder.StartAsync(new RecordingOptions(), Ct);
        await Task.Delay(500, Ct);
        await recorder.PauseAsync(Ct);
        var atPause = recorder.Elapsed;
        await Task.Delay(500, Ct);
        var finished = await recorder.StopAsync(Ct);

        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Equal(SessionState.Recorded, finished.State);
        Assert.Null(finished.Error);
        Assert.Equal(atPause.TotalSeconds, finished.Duration.TotalSeconds, 0.08);
        var files = _store.GetFiles(started.Id);
        foreach (var path in new[] { files.MicrophoneAudio, files.SystemAudio })
        {
            Assert.Equal(finished.Duration.TotalSeconds, AudioLength(path).TotalSeconds, 0.05);
        }

        Assert.Equal([RecorderState.Starting, RecorderState.Recording, RecorderState.Paused, RecorderState.Stopping, RecorderState.Idle], states);
    }

    [Fact]
    public async Task Stop_WhilePaused_ClosesTheScreenSegmentThatTheResumeDidNotStart()
    {
        var capture = new SyntheticCapture(new SyntheticCaptureOptions(Screen: true));
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        await Task.Delay(300, Ct);
        await recorder.PauseAsync(Ct);
        var finished = await recorder.StopAsync(Ct);

        var segment = Assert.Single(finished.ScreenSegments);
        Assert.InRange(segment.Duration, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(300) + Slack);
        Assert.True(Assert.Single(capture.CreatedScreenRecorders).IsDisposed);
    }

    [Fact]
    public async Task PauseAndResume_InTheWrongState_AreNoOps()
    {
        var recorder = new Recorder(new SyntheticCapture(), _store, TimeProvider.System, RecorderSettings.Default);
        var states = new List<RecorderState>();
        recorder.StateChanged += states.Add;

        await recorder.PauseAsync(Ct);
        await recorder.ResumeAsync(Ct);
        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Empty(states);

        await recorder.StartAsync(new RecordingOptions(), Ct);
        await recorder.ResumeAsync(Ct); // not paused
        Assert.Equal(RecorderState.Recording, recorder.State);
        await recorder.PauseAsync(Ct);
        await recorder.PauseAsync(Ct); // already paused
        Assert.Equal(RecorderState.Paused, recorder.State);
        await recorder.ResumeAsync(Ct);
        await recorder.ResumeAsync(Ct);
        await recorder.StopAsync(Ct);
        await recorder.PauseAsync(Ct);
        await recorder.ResumeAsync(Ct);

        Assert.Equal([RecorderState.Starting, RecorderState.Recording, RecorderState.Paused, RecorderState.Recording, RecorderState.Stopping, RecorderState.Idle], states);
    }

    [Fact]
    public async Task ManyPauses_KeepTheFilesAndTheTimelineInStep()
    {
        var capture = new SyntheticCapture();
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        var started = await recorder.StartAsync(new RecordingOptions(), Ct);
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(80, Ct);
            await recorder.PauseAsync(Ct);
            await Task.Delay(40, Ct);
            await recorder.ResumeAsync(Ct);
        }

        await Task.Delay(80, Ct);
        var elapsed = recorder.Elapsed;
        var finished = await recorder.StopAsync(Ct);

        // 7 x 80 ms recorded (and 6 x 40 ms paused, which must not be in the files); the writer period (20 ms) makes each of the
        // twelve switches up to a period late, and a loaded machine more, so the bounds are wide and the timeline is the reference.
        Assert.InRange(finished.Duration, TimeSpan.FromMilliseconds(380), TimeSpan.FromMilliseconds(560) + Slack + Slack);
        Assert.Equal(elapsed.TotalSeconds, finished.Duration.TotalSeconds, 0.15);
        var files = _store.GetFiles(started.Id);
        Assert.Equal(AudioLength(files.MicrophoneAudio).TotalSeconds, AudioLength(files.SystemAudio).TotalSeconds, 0.03);
        Assert.Equal(0, recorder.Stats.DroppedFrames);
    }

    [Fact]
    public async Task WriterLoop_DoesNotAllocate_WithPausesInTheRun()
    {
        var capture = new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 20));
        var settings = RecorderSettings.Default with { FlushInterval = TimeSpan.FromMilliseconds(100) };
        var recorder = new Recorder(capture, _store, TimeProvider.System, settings);

        await recorder.StartAsync(new RecordingOptions(), Ct);
        await Task.Delay(400, Ct); // past the warm-up iterations
        for (var i = 0; i < 4; i++)
        {
            await recorder.PauseAsync(Ct);
            await Task.Delay(150, Ct);
            await recorder.ResumeAsync(Ct);
            await Task.Delay(250, Ct);
        }

        await recorder.PauseAsync(Ct);
        await Task.Delay(250, Ct);
        await recorder.StopAsync(Ct);

        Assert.True(recorder.WriterIterations > 60, $"{recorder.WriterIterations} iterations");
        Assert.Equal(0, recorder.WriterSteadyStateAllocatedBytes);
    }

    [Fact]
    public async Task RenamingTheSessionWhileRecording_SurvivesTheStop()
    {
        var capture = new SyntheticCapture();
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        var started = await recorder.StartAsync(new RecordingOptions(Title: "Before"), Ct);
        var stored = await _store.GetSessionAsync(started.Id, Ct);
        await _store.UpdateSessionAsync(stored! with { Title = "After" }, Ct);
        await recorder.PauseAsync(Ct);
        var finished = await recorder.StopAsync(Ct);

        Assert.Equal("After", finished.Title);
        Assert.Equal("After", (await _store.GetSessionAsync(started.Id, Ct))!.Title);
    }
}
