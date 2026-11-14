using System.Diagnostics;
using Murmur.Core.Audio;
using Murmur.Core.Capture;
using Murmur.Core.Capture.Synthetic;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Recording;

/// <summary>
/// Pause and resume: the paused time is left out of the session timeline, the files and the screen segments; the sources
/// run on (no device is re-opened) and the writer stays allocation-free. The recorder and the synthetic sources run on a
/// <see cref="SampleClock"/>: the test moves the clock and every move waits for the sources to deliver the frames due, so the
/// timeline, the files and the screen segments are compared exactly and a starved test machine cannot change the outcome.
/// </summary>
[Collection(RealtimeCollection.Name)]
public sealed class RecorderPauseTests : IDisposable
{
    private readonly SampleClock _clock = new();
    private readonly TempDirectory _dir = new();
    private readonly InMemorySessionStore _store;

    public RecorderPauseTests() => _store = new InMemorySessionStore(_dir.Path);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    private SyntheticCapture NewCapture(SyntheticCaptureOptions? options = null) => new((options ?? new SyntheticCaptureOptions()) with { TimeProvider = _clock });

    private Recorder NewRecorder(ICapture capture, RecorderSettings? settings = null) => new(capture, _store, _clock, settings ?? RecorderSettings.Default);

    private static TimeSpan At(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private static TimeSpan AudioLength(string path)
    {
        using var reader = WavReader.Open(path);
        return TimeSpan.FromSeconds((double)reader.FrameCount / reader.Format.SampleRate);
    }

    [Fact]
    public async Task Pause_StopsTheTimelineAndTheLevels_ResumeContinuesIt()
    {
        var capture = NewCapture();
        var recorder = NewRecorder(capture);
        var states = new List<RecorderState>();
        recorder.StateChanged += states.Add;

        await recorder.StartAsync(new RecordingOptions(), Ct);
        _clock.Advance(At(400), capture);
        SampleClock.WaitUntil(() => recorder.Levels.Microphone > 0.05f, "the microphone level");

        await recorder.PauseAsync(Ct);
        Assert.Equal(RecorderState.Paused, recorder.State);
        var elapsedAtPause = recorder.Elapsed;
        Assert.Equal(At(400), elapsedAtPause);
        Assert.Equal(default, recorder.Levels);

        _clock.Advance(At(500), capture);
        Assert.Equal(elapsedAtPause, recorder.Elapsed);
        Assert.Equal(default, recorder.Levels);

        await recorder.ResumeAsync(Ct);
        Assert.Equal(RecorderState.Recording, recorder.State);
        _clock.Advance(At(300), capture);
        Assert.Equal(elapsedAtPause + At(300), recorder.Elapsed);
        SampleClock.WaitUntil(() => recorder.Levels.Microphone > 0.05f, "the microphone level after the resume");

        await recorder.StopAsync(Ct);
        Assert.Equal([RecorderState.Starting, RecorderState.Recording, RecorderState.Paused, RecorderState.Recording, RecorderState.Stopping, RecorderState.Idle], states);
    }

    [Fact]
    public async Task PausedTime_IsLeftOutOfTheFiles_AndTheSourcesKeepRunning()
    {
        var capture = NewCapture();
        var recorder = NewRecorder(capture);

        var started = await recorder.StartAsync(new RecordingOptions(), Ct);
        _clock.Advance(At(400), capture);
        await recorder.PauseAsync(Ct);
        var atPause = recorder.Elapsed;
        _clock.Advance(At(700), capture);
        await recorder.ResumeAsync(Ct);
        _clock.Advance(At(400), capture);
        var finished = await recorder.StopAsync(Ct);

        // 0.8 s were recorded, 0.7 s were paused: the files hold the first, not the second.
        Assert.Equal(SessionState.Recorded, finished.State);
        Assert.Equal(At(800), finished.Duration);
        var files = _store.GetFiles(started.Id);
        foreach (var path in new[] { files.MicrophoneAudio, files.SystemAudio })
        {
            Assert.Equal(finished.Duration.TotalSeconds, AudioLength(path).TotalSeconds, 0.05);
        }

        // The devices were not re-opened and kept delivering during the pause (their audio was thrown away).
        Assert.Equal(2, capture.CreatedSources.Count);
        foreach (var source in capture.CreatedSources)
        {
            Assert.Equal(1.5 * SyntheticSignals.SampleRate, source.FramesEmitted);
        }

        Assert.Equal(0, recorder.Stats.DroppedFrames);
        Assert.Equal(At(400), atPause);
    }

    [Fact]
    public async Task Resume_LeavesNeitherAGapNorSilenceAtTheSeam()
    {
        var capture = NewCapture();
        var recorder = NewRecorder(capture);

        var started = await recorder.StartAsync(new RecordingOptions(SystemAudio: false), Ct);
        _clock.Advance(At(500), capture);
        await recorder.PauseAsync(Ct);
        _clock.Advance(At(400), capture);
        await recorder.ResumeAsync(Ct);
        _clock.Advance(At(500), capture);
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
        var capture = NewCapture(new SyntheticCaptureOptions(Screen: true));
        var recorder = NewRecorder(capture);

        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        _clock.Advance(At(400), capture);
        Assert.True(recorder.IsScreenRecording);
        await recorder.PauseAsync(Ct);
        var atPause = recorder.Elapsed;
        Assert.False(recorder.IsScreenRecording);
        _clock.Advance(At(600), capture);
        await recorder.ResumeAsync(Ct);
        Assert.True(recorder.IsScreenRecording);
        _clock.Advance(At(300), capture);
        var finished = await recorder.StopAsync(Ct);

        Assert.Equal(2, finished.ScreenSegments.Count);
        var (first, second) = (finished.ScreenSegments[0], finished.ScreenSegments[1]);
        Assert.Equal([0, 1], finished.ScreenSegments.Select(s => s.Index));
        Assert.Equal(TimeSpan.Zero, first.Offset);

        // The first one ends where the pause was called; the second one starts where the timeline continues: the 600 ms of
        // the pause are not between them.
        Assert.Equal(atPause, first.Offset + first.Duration);
        Assert.Equal(atPause, second.Offset);
        Assert.Equal(At(300), second.Duration);
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
        var capture = NewCapture();
        var recorder = NewRecorder(capture);
        var states = new List<RecorderState>();
        recorder.StateChanged += states.Add;

        var started = await recorder.StartAsync(new RecordingOptions(), Ct);
        _clock.Advance(At(500), capture);
        await recorder.PauseAsync(Ct);
        var atPause = recorder.Elapsed;
        _clock.Advance(At(500), capture);
        var finished = await recorder.StopAsync(Ct);

        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Equal(SessionState.Recorded, finished.State);
        Assert.Null(finished.Error);
        Assert.Equal(At(500), atPause);
        Assert.Equal(atPause, finished.Duration);
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
        var capture = NewCapture(new SyntheticCaptureOptions(Screen: true));
        var recorder = NewRecorder(capture);

        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        _clock.Advance(At(300), capture);
        await recorder.PauseAsync(Ct);
        var finished = await recorder.StopAsync(Ct);

        var segment = Assert.Single(finished.ScreenSegments);
        Assert.Equal(At(300), segment.Duration);
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
        var capture = NewCapture();
        var recorder = NewRecorder(capture);

        var started = await recorder.StartAsync(new RecordingOptions(), Ct);
        for (var i = 0; i < 6; i++)
        {
            _clock.Advance(At(80), capture);
            await recorder.PauseAsync(Ct);
            _clock.Advance(At(40), capture);
            await recorder.ResumeAsync(Ct);
        }

        _clock.Advance(At(80), capture);
        var elapsed = recorder.Elapsed;
        var finished = await recorder.StopAsync(Ct);

        // Seven stretches of 80 ms were recorded, six of 40 ms were paused: the timeline and the files leave the pauses out.
        Assert.Equal(At(560), elapsed);
        Assert.Equal(At(560), finished.Duration);
        var files = _store.GetFiles(started.Id);

        // Each file ends where its own source delivered the last frame before each switch, so on the sample clock the tracks
        // differ by resampler rounding only (a few samples per switch); a track that kept the paused time would be 240 ms
        // longer than the other.
        var micLength = AudioLength(files.MicrophoneAudio).TotalSeconds;
        var systemLength = AudioLength(files.SystemAudio).TotalSeconds;
        Assert.Equal(0.560, micLength, 0.02);
        Assert.Equal(0.560, systemLength, 0.02);
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
    public async Task RenamingTheSessionWhileRecording_SurvivesTheStop_AndTheStopStillStoresTheRest()
    {
        var capture = new SyntheticCapture();
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        var started = await recorder.StartAsync(new RecordingOptions(Title: "Before"), Ct);
        await _store.RenameSessionAsync(started.Id, "After", Ct);
        await Task.Delay(150, Ct);
        await recorder.PauseAsync(Ct);
        var finished = await recorder.StopAsync(Ct);

        var stored = await _store.GetSessionAsync(started.Id, Ct);
        Assert.Equal("After", stored!.Title);
        Assert.Equal(SessionState.Recorded, stored.State);
        Assert.Equal(finished.Duration, stored.Duration);
        Assert.True(stored.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task TheRecorder_NeverWritesTheTitleAfterTheCreate()
    {
        var recorder = new Recorder(new SyntheticCapture(new SyntheticCaptureOptions(Screen: true)), _store, TimeProvider.System, RecorderSettings.Default);

        var started = await recorder.StartAsync(new RecordingOptions(Title: "Original", Screen: true), Ct);
        await _store.RenameSessionAsync(started.Id, "Renamed", Ct);
        await recorder.SetScreenEnabledAsync(false, Ct);
        await recorder.StopAsync(Ct);

        // Every later write carries the old title in its record, but the store keeps the one the user chose.
        Assert.Equal("Renamed", (await _store.GetSessionAsync(started.Id, Ct))!.Title);
        Assert.All(_store.Updates, u => Assert.Equal("Original", u.Title));
    }

    [Fact]
    public async Task Elapsed_NeverStepsBack_WhileAThreadPollsItAcrossPausesAndResumes()
    {
        var recorder = new Recorder(new SyntheticCapture(), _store, TimeProvider.System, RecorderSettings.Default);
        await recorder.StartAsync(new RecordingOptions(), Ct);
        using var stop = new CancellationTokenSource();
        var steps = new List<TimeSpan>();
        var poller = Task.Run(
            () =>
            {
                var last = TimeSpan.Zero;
                var worst = TimeSpan.Zero;
                while (!stop.IsCancellationRequested)
                {
                    var now = recorder.Elapsed;
                    if (now < last && last - now > worst)
                    {
                        worst = last - now;
                    }

                    last = now > last ? now : last;
                }

                steps.Add(worst);
            },
            Ct);

        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(30, Ct);
            await recorder.PauseAsync(Ct);
            await Task.Delay(30, Ct);
            await recorder.ResumeAsync(Ct);
        }

        await stop.CancelAsync();
        await poller;
        await recorder.StopAsync(Ct);

        Assert.Equal(TimeSpan.Zero, Assert.Single(steps)); // the largest step back the poller ever saw
    }

    [Fact]
    public async Task ThePausedTimeline_IsExactlyWhatAFaultedTrackIsPaddedTo()
    {
        // The microphone dies after 0.2 s: its file is filled with silence up to the timeline, which stops at the pause.
        var fault = At(200);
        var capture = NewCapture(new SyntheticCaptureOptions(MicrophoneFaultAfter: fault));
        var recorder = NewRecorder(capture);

        var started = await recorder.StartAsync(new RecordingOptions(), Ct);
        _clock.Advance(At(700), capture, microphoneFaultAfter: fault);
        await recorder.PauseAsync(Ct);
        var frozen = recorder.Elapsed;
        _clock.Advance(At(300), capture, microphoneFaultAfter: fault);
        Assert.Equal(frozen, recorder.Elapsed);
        var finished = await recorder.StopAsync(Ct);

        var files = _store.GetFiles(started.Id);
        var mic = AudioLength(files.MicrophoneAudio).TotalSeconds;
        var system = AudioLength(files.SystemAudio).TotalSeconds;

        // The faulted microphone is padded to the frozen timeline, the healthy system track holds the frames its source
        // delivered before the pause: both end at the pause. The microphone alone proves the padding: without it the file
        // would end at 0.2 s.
        Assert.Equal(At(700), frozen);
        Assert.Equal(frozen.TotalSeconds, system, 0.02);
        Assert.Equal(frozen.TotalSeconds, mic, 0.02);
        Assert.Equal(frozen, finished.Duration);
    }

    [Fact]
    public async Task AScreenSegmentThatCannotBeFinalizedAtThePause_IsFailedAtThePauseMark_AndTheResumeStartsANewOne()
    {
        var screen = new StubbornScreenRecorder();
        var inner = NewCapture(new SyntheticCaptureOptions(Screen: true));
        var capture = new ScreenCapture(inner, screen);
        var recorder = NewRecorder(capture);
        var warnings = new List<RecorderWarning>();
        recorder.Warning += warnings.Add;

        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        _clock.Advance(At(300), inner);
        screen.FailNextStop = true;
        await recorder.PauseAsync(Ct);
        var pauseMark = recorder.Elapsed;

        Assert.Equal(RecorderState.Paused, recorder.State);
        Assert.False(recorder.IsScreenRecording); // the segment is marked failed
        Assert.Contains(warnings, w => w.Code == RecorderWarningCode.ScreenStopped);

        _clock.Advance(At(500), inner);
        await recorder.ResumeAsync(Ct);
        Assert.True(recorder.IsScreenRecording); // the failed one was finished, a new one runs
        Assert.Equal(2, screen.Starts);
        _clock.Advance(At(200), inner);
        var finished = await recorder.StopAsync(Ct);

        Assert.Equal(2, finished.ScreenSegments.Count);
        var (first, second) = (finished.ScreenSegments[0], finished.ScreenSegments[1]);
        Assert.Equal(pauseMark, first.Offset + first.Duration); // not the 500 ms later it was finished at
        Assert.Equal(pauseMark, second.Offset);
    }

    private sealed class ScreenCapture(SyntheticCapture inner, IScreenRecorder screen) : ICapture
    {
        public CaptureCapabilities Capabilities => inner.Capabilities;

        public IReadOnlyList<AudioDevice> GetAudioDevices(AudioDeviceKind kind) => inner.GetAudioDevices(kind);

        public IAudioSource CreateAudioSource(AudioDeviceKind kind, string? deviceId) => inner.CreateAudioSource(kind, deviceId);

        public IReadOnlyList<DisplayInfo> GetDisplays() => inner.GetDisplays();

        public IScreenRecorder CreateScreenRecorder(ScreenCaptureOptions options) => screen;
    }

    /// <summary>A screen recorder whose next stop fails without leaving the segment (it keeps recording), then works again.</summary>
    private sealed class StubbornScreenRecorder : IScreenRecorder
    {
        private volatile bool _recording;
        private int _starts;

        public volatile bool FailNextStop;

        public int Starts => Volatile.Read(ref _starts);

        public bool IsRecording => _recording;

        public ScreenRecorderStats Stats => new(0, 0, 0, "stubborn");

        public event Action<CaptureFault>? Faulted
        {
            add { }
            remove { }
        }

        public ValueTask StartSegmentAsync(string filePath, CancellationToken cancellationToken)
        {
            if (_recording)
            {
                throw new InvalidOperationException("A segment is already being recorded.");
            }

            File.Create(filePath).Dispose();
            Interlocked.Increment(ref _starts);
            _recording = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopSegmentAsync(CancellationToken cancellationToken)
        {
            if (FailNextStop)
            {
                FailNextStop = false;
                throw new InvalidOperationException("finalize failed");
            }

            _recording = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _recording = false;
            return ValueTask.CompletedTask;
        }
    }
}
