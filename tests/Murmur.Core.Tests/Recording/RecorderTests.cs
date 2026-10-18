using Murmur.Core.Audio;
using Murmur.Core.Capture;
using Murmur.Core.Capture.Synthetic;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Recording;

[Collection(RealtimeCollection.Name)]
public sealed class RecorderTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly InMemorySessionStore _store;

    public RecorderTests()
    {
        _store = new InMemorySessionStore(_dir.Path);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task StartStop_WritesBothTracksAndReturnsARecordedSession()
    {
        var capture = new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 10));
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);
        var states = new List<RecorderState>();
        recorder.StateChanged += s =>
        {
            lock (states)
            {
                states.Add(s);
            }
        };

        var started = await recorder.StartAsync(new RecordingOptions(Title: "Standup"), Ct);
        Assert.Equal(RecorderState.Recording, recorder.State);
        Assert.Same(started, recorder.Current);
        Assert.Equal(SessionState.Recording, started.State);

        await Task.Delay(500, Ct);
        Assert.True(recorder.Elapsed > TimeSpan.FromMilliseconds(400));
        Assert.True(recorder.Levels.Microphone > 0.05f, $"mic level {recorder.Levels.Microphone}");
        Assert.True(recorder.Levels.SystemAudio > 0.05f, $"system level {recorder.Levels.SystemAudio}");
        Assert.True(recorder.Stats.MicrophoneFrames > 0);

        var finished = await recorder.StopAsync(Ct);

        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Null(recorder.Current);
        Assert.Equal(TimeSpan.Zero, recorder.Elapsed);
        Assert.Equal([RecorderState.Starting, RecorderState.Recording, RecorderState.Stopping, RecorderState.Idle], states);
        Assert.Equal(SessionState.Recorded, finished.State);
        Assert.Equal("Standup", finished.Title);
        Assert.True(finished.HasMicrophone);
        Assert.True(finished.HasSystemAudio);
        Assert.Null(finished.Error);
        Assert.Empty(finished.ScreenSegments);
        Assert.Equal(finished, _store.Get(finished.Id));

        var files = _store.GetFiles(finished.Id);
        var sources = capture.CreatedSources;
        foreach (var (path, source) in new[] { (files.MicrophoneAudio, sources[0]), (files.SystemAudio, sources[1]) })
        {
            using var reader = WavReader.Open(path);
            Assert.Equal(AudioFormat.Asr, reader.Format);
            Assert.Equal((source.FramesEmitted + 2) / 3, reader.FrameCount);
        }

        Assert.InRange(finished.Duration.TotalSeconds, 4, 8); // ~0.5 s x 10
        Assert.Equal(0, recorder.Stats.DroppedFrames);
        Assert.Equal(sources[0].FramesEmitted, recorder.Stats.MicrophoneFrames);
        Assert.Equal(sources[1].FramesEmitted, recorder.Stats.SystemFrames);
    }

    [Fact]
    public async Task DefaultTitle_UsesTheStartTime()
    {
        var recorder = new Recorder(new SyntheticCapture(), _store, TimeProvider.System, RecorderSettings.Default);

        var started = await recorder.StartAsync(new RecordingOptions(), Ct);
        await recorder.StopAsync(Ct);

        Assert.Equal(Recorder.DefaultTitle(started.StartedAt), started.Title);
        Assert.StartsWith("Запис ", started.Title, StringComparison.Ordinal);
        Assert.Matches(@"^Запис \d{4}-\d{2}-\d{2} \d{2}:\d{2}$", started.Title);
    }

    [Fact]
    public async Task MicrophoneOnly_WritesOnlyMicWav()
    {
        var recorder = new Recorder(new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 5)), _store, TimeProvider.System, RecorderSettings.Default);

        var started = await recorder.StartAsync(new RecordingOptions(SystemAudio: false), Ct);
        await Task.Delay(100, Ct);
        var finished = await recorder.StopAsync(Ct);

        var files = _store.GetFiles(started.Id);
        Assert.True(File.Exists(files.MicrophoneAudio));
        Assert.False(File.Exists(files.SystemAudio));
        Assert.True(finished.HasMicrophone);
        Assert.False(finished.HasSystemAudio);
        Assert.Equal(0, recorder.Stats.SystemFrames);
    }

    [Fact]
    public async Task InvalidTransitions_Throw()
    {
        var recorder = new Recorder(new SyntheticCapture(), _store, TimeProvider.System, RecorderSettings.Default);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await recorder.StopAsync(Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await recorder.SetScreenEnabledAsync(true, Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await recorder.StartAsync(new RecordingOptions(Microphone: false, SystemAudio: false), Ct));
        Assert.Equal(RecorderState.Idle, recorder.State);

        await recorder.StartAsync(new RecordingOptions(), Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await recorder.StartAsync(new RecordingOptions(), Ct));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await recorder.SetScreenEnabledAsync(true, Ct));
        await recorder.StopAsync(Ct);
    }

    [Fact]
    public async Task ScreenToggling_RecordsSegmentsWithOffsets()
    {
        var capture = new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 1, Screen: true));
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        var started = await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        await Task.Delay(300, Ct);
        await recorder.SetScreenEnabledAsync(false, Ct);
        await recorder.SetScreenEnabledAsync(false, Ct); // no-op
        await Task.Delay(300, Ct);
        await recorder.SetScreenEnabledAsync(true, Ct);
        await recorder.SetScreenEnabledAsync(true, Ct); // no-op
        await Task.Delay(300, Ct);
        var finished = await recorder.StopAsync(Ct);

        var files = _store.GetFiles(started.Id);
        Assert.Equal(2, finished.ScreenSegments.Count);
        var (first, second) = (finished.ScreenSegments[0], finished.ScreenSegments[1]);
        Assert.Equal(0, first.Index);
        Assert.Equal(1, second.Index);
        Assert.InRange(first.Offset, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
        Assert.InRange(first.Duration.TotalMilliseconds, 250, 1_000);
        Assert.InRange(second.Offset.TotalMilliseconds, 550, 1_500);
        Assert.True(second.Offset >= first.Offset + first.Duration);
        Assert.InRange(second.Duration.TotalMilliseconds, 250, 1_000);
        Assert.True(second.Offset + second.Duration <= finished.Duration + TimeSpan.FromMilliseconds(50));
        Assert.True(File.Exists(files.ScreenSegment(0)));
        Assert.True(File.Exists(files.ScreenSegment(1)));

        var screen = Assert.Single(capture.CreatedScreenRecorders);
        Assert.Equal([files.ScreenSegment(0), files.ScreenSegment(1)], screen.Segments);
        Assert.True(screen.IsDisposed);
    }

    [Fact]
    public async Task ScreenRequested_ButUnsupported_RecordsAudioOnly()
    {
        var recorder = new Recorder(new SyntheticCapture(), _store, TimeProvider.System, RecorderSettings.Default);

        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        var finished = await recorder.StopAsync(Ct);

        Assert.Empty(finished.ScreenSegments);
        Assert.Equal(SessionState.Recorded, finished.State);
    }

    [Fact]
    public async Task FaultedSource_IsPaddedWithSilenceAndTheSessionStillRecords()
    {
        var capture = new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 10, MicrophoneFaultAfter: TimeSpan.FromSeconds(1)));
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        var started = await recorder.StartAsync(new RecordingOptions(), Ct);
        await Task.Delay(600, Ct);
        Assert.Equal(RecorderState.Recording, recorder.State);
        Assert.Equal(0f, recorder.Levels.Microphone);
        var finished = await recorder.StopAsync(Ct);

        Assert.Equal(SessionState.Recorded, finished.State);
        Assert.True(finished.HasMicrophone);
        var files = _store.GetFiles(started.Id);
        var (_, mic) = WavReader.ReadAll(files.MicrophoneAudio);
        var (_, system) = WavReader.ReadAll(files.SystemAudio);

        // The mic track stays aligned: as long as the healthy track, silent after the fault.
        Assert.InRange(mic.Length, system.Length - 2, system.Length + 2);
        Assert.True(system.Length > 3 * 16_000);
        Assert.True(CaptureTrack.Peak(mic.AsSpan(0, 15_000)) > 0.05f);
        var firstNonZero = Array.FindIndex(mic, 16_100, v => v != 0);
        var lastNonZero = Array.FindLastIndex(mic, v => v != 0);
        Assert.True(firstNonZero < 0, $"mic has audio at {firstNonZero}..{lastNonZero} of {mic.Length}");
    }

    [Fact]
    public async Task AllSourcesFaulted_StopsByItselfWithFailedState()
    {
        var capture = new SyntheticCapture(new SyntheticCaptureOptions(
            SpeedFactor: 10,
            MicrophoneFaultAfter: TimeSpan.FromSeconds(0.5),
            SystemFaultAfter: TimeSpan.FromSeconds(1)));
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.StateChanged += s =>
        {
            if (s == RecorderState.Idle)
            {
                idle.TrySetResult();
            }
        };

        var started = await recorder.StartAsync(new RecordingOptions(), Ct);
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(RecorderState.Idle, recorder.State);
        var stored = _store.Get(started.Id);
        Assert.NotNull(stored);
        Assert.Equal(SessionState.Failed, stored.State);
        Assert.NotNull(stored.Error);
        Assert.Contains("All audio sources failed", stored.Error, StringComparison.Ordinal);
        Assert.InRange(stored.Duration.TotalSeconds, 0.99, 3);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await recorder.StopAsync(Ct));
    }

    [Fact]
    public async Task StartFailure_MarksTheSessionFailedAndReturnsToIdle()
    {
        var recorder = new Recorder(new SyntheticCapture(), _store, TimeProvider.System, RecorderSettings.Default);

        // The only requested track cannot be created: nothing can be recorded, so the start fails.
        await Assert.ThrowsAsync<ArgumentException>(async () => await recorder.StartAsync(new RecordingOptions(SystemAudio: false, MicrophoneDeviceId: "missing"), Ct));

        Assert.Equal(RecorderState.Idle, recorder.State);
        var session = Assert.Single(_store.Updates);
        Assert.Equal(SessionState.Failed, session.State);
        Assert.NotNull(session.Error);
    }

    [Fact]
    public async Task WriterLoop_DoesNotAllocateInSteadyState()
    {
        var capture = new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 20));
        var settings = RecorderSettings.Default with { FlushInterval = TimeSpan.FromMilliseconds(100) };
        var recorder = new Recorder(capture, _store, TimeProvider.System, settings);

        await recorder.StartAsync(new RecordingOptions(), Ct);
        await Task.Delay(1_500, Ct);
        await recorder.StopAsync(Ct);

        Assert.True(recorder.WriterIterations > 20, $"{recorder.WriterIterations} iterations");
        Assert.Equal(0, recorder.WriterSteadyStateAllocatedBytes);
    }

    [Fact]
    public async Task FlushInterval_KeepsTheHeaderCurrentWhileRecording()
    {
        var capture = new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 5));
        var settings = RecorderSettings.Default with { FlushInterval = TimeSpan.FromMilliseconds(200) };
        var recorder = new Recorder(capture, _store, TimeProvider.System, settings);

        var started = await recorder.StartAsync(new RecordingOptions(), Ct);
        await Task.Delay(800, Ct);
        long declared;
        using (var reader = WavReader.Open(_store.GetFiles(started.Id).SystemAudio))
        {
            declared = reader.Layout.DeclaredDataBytes;
        }

        await recorder.StopAsync(Ct);

        Assert.True(declared > 16_000 * 2, $"declared {declared} bytes"); // > 1 s of audio flushed while recording
    }

    [Fact]
    public async Task Stop_WhenSourcesThrowOnRelease_StillReleasesEverySourceAndReturnsToIdle()
    {
        var capture = new MisbehavingCapture(new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 10)));
        capture.ThrowOnDispose.Add(AudioDeviceKind.Microphone);
        capture.ThrowOnDispose.Add(AudioDeviceKind.SystemOutput);
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        var started = await recorder.StartAsync(new RecordingOptions(), Ct);
        await Task.Delay(300, Ct);
        var stopped = await recorder.StopAsync(Ct);

        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Equal(SessionState.Recorded, stopped.State);
        Assert.Equal(SessionState.Recorded, _store.Get(started.Id)?.State);
        Assert.Equal(2, capture.Sources.Count);
        Assert.All(capture.Sources, s => Assert.Equal(1, s.DisposeCalls));

        // The recorder is usable again.
        capture.ThrowOnDispose.Clear();
        await recorder.StartAsync(new RecordingOptions(), Ct);
        Assert.Equal(RecorderState.Recording, recorder.State);
        await recorder.StopAsync(Ct);
        Assert.Equal(RecorderState.Idle, recorder.State);
    }

    [Fact]
    public async Task StartFailure_WhenASourceThrowsOnStop_StillRollsBackEverything()
    {
        var capture = new MisbehavingCapture(new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 10, Screen: true)))
        {
            ThrowOnCreateScreenRecorder = true,
        };
        capture.ThrowOnStop.Add(AudioDeviceKind.Microphone);
        capture.ThrowOnDispose.Add(AudioDeviceKind.Microphone);
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await recorder.StartAsync(new RecordingOptions(Screen: true), Ct));

        Assert.Contains("Screen recorder", error.Message, StringComparison.Ordinal);
        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Equal(2, capture.Sources.Count);
        Assert.All(capture.Sources, s =>
        {
            Assert.Equal(1, s.StopCalls);
            Assert.Equal(1, s.DisposeCalls);
        });

        var session = Assert.Single(_store.Updates);
        Assert.Equal(SessionState.Failed, session.State);
        Assert.Equal(error.Message, session.Error);

        // Both files are closed with a final header, and nothing keeps writing to them.
        var files = _store.GetFiles(session.Id);
        foreach (var path in new[] { files.MicrophoneAudio, files.SystemAudio })
        {
            var length = new FileInfo(path).Length;
            using (var reader = WavReader.Open(path))
            {
                Assert.Equal(length - reader.Layout.DataOffset, reader.Layout.DeclaredDataBytes);
            }

            await Task.Delay(100, Ct);
            Assert.Equal(length, new FileInfo(path).Length);
        }

        // The recorder is usable again.
        capture.ThrowOnCreateScreenRecorder = false;
        capture.ThrowOnStop.Clear();
        capture.ThrowOnDispose.Clear();
        await recorder.StartAsync(new RecordingOptions(), Ct);
        await recorder.StopAsync(Ct);
        Assert.Equal(RecorderState.Idle, recorder.State);
    }

    [Fact]
    public async Task Stop_WhenTheScreenSegmentFailsToFinalize_StillReleasesTheScreenRecorderAndKeepsTheSegment()
    {
        var synthetic = new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 1, Screen: true));
        var capture = new MisbehavingCapture(synthetic) { ThrowOnStopScreenSegment = true };
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        var started = await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        await Task.Delay(300, Ct);
        var finished = await recorder.StopAsync(Ct);

        // Finalizing threw, yet the screen recorder (capture session, device, encoder) was released and the stop completed.
        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Null(recorder.Current);
        var screen = Assert.Single(capture.ScreenRecorders);
        Assert.Equal(1, screen.StopSegmentCalls);
        Assert.Equal(1, screen.DisposeCalls);
        Assert.True(Assert.Single(synthetic.CreatedScreenRecorders).IsDisposed);

        // The audio is intact, so the session is recorded with the screen failure reported, and the segment whose file
        // is on disk stays on the timeline, ending where the user stopped.
        Assert.Equal(SessionState.Recorded, finished.State);
        Assert.Equal("Screen segment failed to finalize.", finished.Error);
        Assert.Equal(finished, _store.Get(started.Id));
        var segment = Assert.Single(finished.ScreenSegments);
        Assert.Equal(0, segment.Index);
        Assert.True(segment.Duration >= TimeSpan.FromMilliseconds(250), $"segment {segment}");
        Assert.True(segment.Offset + segment.Duration <= finished.Duration + TimeSpan.FromMilliseconds(50), $"segment {segment}, audio {finished.Duration}");
        Assert.True(File.Exists(_store.GetFiles(started.Id).ScreenSegment(0)));

        // The recorder is usable again.
        capture.ThrowOnStopScreenSegment = false;
        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        var next = await recorder.StopAsync(Ct);
        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Null(next.Error);
        Assert.Single(next.ScreenSegments);
        Assert.All(capture.ScreenRecorders, s => Assert.Equal(1, s.DisposeCalls));
    }

    [Fact]
    public async Task ScreenOff_WhenFinalizingFails_ThrowsButKeepsTheSegmentAndKeepsRecording()
    {
        var capture = new MisbehavingCapture(new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 1, Screen: true)))
        {
            ThrowOnStopScreenSegment = true,
        };
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        var started = await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        await Task.Delay(200, Ct);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await recorder.SetScreenEnabledAsync(false, Ct));

        Assert.Equal("Screen segment failed to finalize.", error.Message);
        Assert.Equal(RecorderState.Recording, recorder.State);

        // The failed segment is closed, so "on" starts the next one instead of being a no-op.
        capture.ThrowOnStopScreenSegment = false;
        await recorder.SetScreenEnabledAsync(true, Ct);
        await Task.Delay(200, Ct);
        var finished = await recorder.StopAsync(Ct);

        Assert.Equal(SessionState.Recorded, finished.State);
        Assert.Null(finished.Error);
        Assert.Equal(2, finished.ScreenSegments.Count);
        var (first, second) = (finished.ScreenSegments[0], finished.ScreenSegments[1]);
        Assert.Equal(0, first.Index);
        Assert.Equal(1, second.Index);
        Assert.True(first.Duration >= TimeSpan.FromMilliseconds(150), $"first {first}");
        Assert.True(second.Offset >= first.Offset + first.Duration, $"first {first}, second {second}");
        var files = _store.GetFiles(started.Id);
        Assert.True(File.Exists(files.ScreenSegment(0)));
        Assert.True(File.Exists(files.ScreenSegment(1)));
        var screen = Assert.Single(capture.ScreenRecorders);
        Assert.Equal(2, screen.StopSegmentCalls);
        Assert.Equal(1, screen.DisposeCalls);
    }

    private static Exception ScreenStopFailure(bool cancelled) => cancelled
        ? new OperationCanceledException("Screen segment stop was cancelled.")
        : new InvalidOperationException("Screen segment failed before the sink stopped.");

    private static async Task<Exception> AssertScreenStopThrows(bool cancelled, Func<Task> action) => cancelled
        ? await Assert.ThrowsAsync<OperationCanceledException>(action)
        : await Assert.ThrowsAsync<InvalidOperationException>(action);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScreenOff_WhenStoppingFailsBeforeTheRecorderStops_KeepsTheSegmentOpenAndRetries(bool cancelled)
    {
        var capture = new MisbehavingCapture(new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 1, Screen: true)));
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        var started = await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        var screen = Assert.Single(capture.ScreenRecorders);
        await Task.Delay(200, Ct);

        capture.ThrowBeforeStopScreenSegment = () => ScreenStopFailure(cancelled);
        var error = await AssertScreenStopThrows(cancelled, async () => await recorder.SetScreenEnabledAsync(false, Ct));
        Assert.Equal(ScreenStopFailure(cancelled).Message, error.Message);

        // The screen recorder never left the segment, so the segment stays open and the session keeps recording.
        Assert.True(screen.IsRecording);
        Assert.Equal(RecorderState.Recording, recorder.State);

        // "On" while the segment is still open is a no-op, not a second StartSegmentAsync on a recording recorder.
        await recorder.SetScreenEnabledAsync(true, Ct);
        Assert.True(screen.IsRecording);

        // A second "off" retries the finalize and closes the segment.
        capture.ThrowBeforeStopScreenSegment = null;
        await Task.Delay(100, Ct);
        await recorder.SetScreenEnabledAsync(false, Ct);
        Assert.False(screen.IsRecording);

        // The screen can be turned back on: "on" starts segment 1.
        await recorder.SetScreenEnabledAsync(true, Ct);
        Assert.True(screen.IsRecording);
        await Task.Delay(200, Ct);
        var finished = await recorder.StopAsync(Ct);

        Assert.Equal(SessionState.Recorded, finished.State);
        Assert.Null(finished.Error);
        Assert.Equal(2, finished.ScreenSegments.Count);
        var (first, second) = (finished.ScreenSegments[0], finished.ScreenSegments[1]);
        Assert.Equal(0, first.Index);
        Assert.Equal(1, second.Index);

        // The first segment ends at the successful "off", not at the failed one.
        Assert.True(first.Duration >= TimeSpan.FromMilliseconds(250), $"first {first}");
        Assert.True(second.Offset >= first.Offset + first.Duration, $"first {first}, second {second}");
        var files = _store.GetFiles(started.Id);
        Assert.True(File.Exists(files.ScreenSegment(0)));
        Assert.True(File.Exists(files.ScreenSegment(1)));
        Assert.Equal(3, screen.StopSegmentCalls);
        Assert.Equal(1, screen.DisposeCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stop_AfterAFailedScreenOff_StillRecordsTheSegmentAndReleasesTheScreenRecorder(bool cancelled)
    {
        var synthetic = new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 1, Screen: true));
        var capture = new MisbehavingCapture(synthetic);
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        var started = await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        var screen = Assert.Single(capture.ScreenRecorders);
        await Task.Delay(200, Ct);

        // "Off" fails before the recorder stops, and so does the finalize on the final stop.
        capture.ThrowBeforeStopScreenSegment = () => ScreenStopFailure(cancelled);
        await AssertScreenStopThrows(cancelled, async () => await recorder.SetScreenEnabledAsync(false, Ct));
        Assert.True(screen.IsRecording);
        await Task.Delay(100, Ct);
        var finished = await recorder.StopAsync(Ct);

        // The stop still retried the finalize, released the recorder and completed.
        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Null(recorder.Current);
        Assert.Equal(2, screen.StopSegmentCalls);
        Assert.Equal(1, screen.DisposeCalls);
        Assert.True(Assert.Single(synthetic.CreatedScreenRecorders).IsDisposed);

        // The audio is intact, the screen failure is reported, and the segment ends where the user stopped.
        Assert.Equal(SessionState.Recorded, finished.State);
        Assert.Equal(ScreenStopFailure(cancelled).Message, finished.Error);
        Assert.Equal(finished, _store.Get(started.Id));
        var segment = Assert.Single(finished.ScreenSegments);
        Assert.Equal(0, segment.Index);
        Assert.True(segment.Duration >= TimeSpan.FromMilliseconds(250), $"segment {segment}");
        Assert.True(segment.Offset + segment.Duration <= finished.Duration + TimeSpan.FromMilliseconds(50), $"segment {segment}, audio {finished.Duration}");
        Assert.True(File.Exists(_store.GetFiles(started.Id).ScreenSegment(0)));
    }

    [Fact]
    public async Task Stop_TakesTheFinalStatsBeforeReleasingTheScreenRecorder()
    {
        // The screen recorder reports 7 dropped frames while alive and throws from Stats once disposed; its release
        // takes a while, so the stop stays in Stopping long enough for the poller below to read Stats meanwhile.
        var synthetic = new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 10, Screen: true));
        var capture = new MisbehavingCapture(synthetic)
        {
            ScreenFramesDropped = 7,
            ScreenDisposeDelay = TimeSpan.FromMilliseconds(300),
        };
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        await Task.Delay(200, Ct);
        Assert.Equal(7, recorder.Stats.ScreenFramesDropped);

        // Poll Stats like the UI does, all through the stop.
        using var polling = new CancellationTokenSource();
        var sawStopping = false;
        var poller = Task.Run(
            () =>
            {
                while (!polling.IsCancellationRequested)
                {
                    sawStopping |= recorder.State == RecorderState.Stopping;
                    Assert.Equal(7, recorder.Stats.ScreenFramesDropped);
                    Thread.Sleep(1);
                }
            },
            Ct);
        var finished = await recorder.StopAsync(Ct);
        await polling.CancelAsync();
        await poller;

        Assert.True(sawStopping);
        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Equal(SessionState.Recorded, finished.State);
        Assert.Null(finished.Error);
        Assert.Equal(7, recorder.Stats.ScreenFramesDropped);
        Assert.Equal(synthetic.CreatedSources[0].FramesEmitted, recorder.Stats.MicrophoneFrames);
        var screen = Assert.Single(capture.ScreenRecorders);
        Assert.Equal(1, screen.DisposeCalls);
        Assert.Throws<ObjectDisposedException>(() => screen.Stats);
    }

    [Fact]
    public void Settings_AreValidated()
    {
        var capture = new SyntheticCapture();

        Assert.Throws<ArgumentOutOfRangeException>(() => new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default with { RingSeconds = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default with { WriterPeriod = TimeSpan.Zero }));
        Assert.Equal(TimeSpan.FromSeconds(2), RecorderSettings.Default.FlushInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(20), RecorderSettings.Default.WriterPeriod);
        Assert.Equal(4, RecorderSettings.Default.RingSeconds);
    }
}
