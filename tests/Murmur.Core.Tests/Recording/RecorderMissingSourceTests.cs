using Murmur.Core.Audio;
using Murmur.Core.Capture;
using Murmur.Core.Capture.Synthetic;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Recording;

/// <summary>A requested audio source that cannot be created or started costs only its own track.</summary>
[Collection(RealtimeCollection.Name)]
public sealed class RecorderMissingSourceTests : IDisposable
{
    private const string MicDenied = "rec.mic.denied\nrec.only.system";

    private readonly TempDirectory _dir = new();
    private readonly InMemorySessionStore _store;

    public RecorderMissingSourceTests()
    {
        _store = new InMemorySessionStore(_dir.Path);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    private static Exception AccessDenied() =>
        new UnauthorizedAccessException("Microphone access is denied by Windows privacy settings.");

    [Fact]
    public async Task MicrophoneDenied_RecordsSystemAudioOnly_WithAWarning()
    {
        var capture = new MisbehavingCapture(new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 10)));
        capture.ThrowOnStart[AudioDeviceKind.Microphone] = AccessDenied;
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        var started = await recorder.StartAsync(new RecordingOptions(), Ct);

        Assert.Equal(RecorderState.Recording, recorder.State);
        Assert.Equal(SessionState.Recording, started.State);
        Assert.False(started.HasMicrophone);
        Assert.True(started.HasSystemAudio);
        Assert.Equal(MicDenied, started.Error);
        Assert.Equal(started, _store.Get(started.Id));

        var mic = Assert.Single(capture.Sources, s => s.Kind == AudioDeviceKind.Microphone);
        Assert.Equal(1, mic.DisposeCalls); // released at once, not at stop

        await Task.Delay(500, Ct);
        Assert.Equal(0f, recorder.Levels.Microphone);
        Assert.True(recorder.Levels.SystemAudio > 0.05f, $"system level {recorder.Levels.SystemAudio}");
        var finished = await recorder.StopAsync(Ct);

        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Equal(SessionState.Recorded, finished.State);
        Assert.False(finished.HasMicrophone);
        Assert.True(finished.HasSystemAudio);
        Assert.Equal(MicDenied, finished.Error);
        Assert.Equal(finished, _store.Get(finished.Id));
        Assert.Equal(1, mic.DisposeCalls);
        Assert.Equal(0, mic.StopCalls);
        Assert.All(capture.Sources, s => Assert.Equal(1, s.DisposeCalls));

        var files = _store.GetFiles(finished.Id);
        Assert.False(File.Exists(files.MicrophoneAudio));
        using (var reader = WavReader.Open(files.SystemAudio))
        {
            Assert.True(reader.Duration > TimeSpan.FromSeconds(3), $"system.wav {reader.Duration}");
        }

        Assert.InRange(finished.Duration.TotalSeconds, 3, 10);
        Assert.Equal(0, recorder.Stats.MicrophoneFrames);
        Assert.True(recorder.Stats.SystemFrames > 0);
    }

    [Fact]
    public async Task MicrophoneDenied_WriterLoopStillDoesNotAllocate()
    {
        var capture = new MisbehavingCapture(new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 20)));
        capture.ThrowOnStart[AudioDeviceKind.Microphone] = AccessDenied;
        var settings = RecorderSettings.Default with { FlushInterval = TimeSpan.FromMilliseconds(100) };
        var recorder = new Recorder(capture, _store, TimeProvider.System, settings);

        await recorder.StartAsync(new RecordingOptions(), Ct);
        await Task.Delay(1_000, Ct);
        await recorder.StopAsync(Ct);

        Assert.True(recorder.WriterIterations > 20, $"{recorder.WriterIterations} iterations");
        Assert.Equal(0, recorder.WriterSteadyStateAllocatedBytes);
    }

    [Fact]
    public async Task SystemAudioCannotBeCreated_RecordsTheMicrophoneOnly()
    {
        var capture = new MisbehavingCapture(new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 10)));
        capture.ThrowOnCreate[AudioDeviceKind.SystemOutput] = () => new InvalidOperationException("No default output is available.");
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        var started = await recorder.StartAsync(new RecordingOptions(), Ct);
        await Task.Delay(200, Ct);
        var finished = await recorder.StopAsync(Ct);

        Assert.True(started.HasMicrophone);
        Assert.False(started.HasSystemAudio);
        Assert.Equal("rec.system.unavailable|No default output is available\nrec.only.mic", started.Error);
        Assert.Equal(SessionState.Recorded, finished.State);
        Assert.True(finished.HasMicrophone);
        Assert.False(finished.HasSystemAudio);
        Assert.Equal(started.Error, finished.Error);
        var files = _store.GetFiles(finished.Id);
        Assert.True(File.Exists(files.MicrophoneAudio));
        Assert.False(File.Exists(files.SystemAudio));
    }

    [Fact]
    public async Task MissingMicrophoneDevice_RecordsSystemAudio()
    {
        var recorder = new Recorder(new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 10)), _store, TimeProvider.System, RecorderSettings.Default);

        var started = await recorder.StartAsync(new RecordingOptions(MicrophoneDeviceId: "missing"), Ct);
        var finished = await recorder.StopAsync(Ct);

        Assert.False(started.HasMicrophone);
        Assert.True(started.HasSystemAudio);
        Assert.NotNull(started.Error);
        Assert.StartsWith("rec.mic.unavailable|", started.Error, StringComparison.Ordinal);
        Assert.EndsWith("\nrec.only.system", started.Error, StringComparison.Ordinal);
        Assert.Equal(SessionState.Recorded, finished.State);
    }

    [Fact]
    public async Task BothSourcesFailToStart_FailsTheStartAndTheSession()
    {
        var capture = new MisbehavingCapture(new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 10, Screen: true)));
        capture.ThrowOnStart[AudioDeviceKind.Microphone] = AccessDenied;
        capture.ThrowOnStart[AudioDeviceKind.SystemOutput] = () => new InvalidOperationException("loopback failed");
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await recorder.StartAsync(new RecordingOptions(Screen: true), Ct));

        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Null(recorder.Current);
        var session = Assert.Single(_store.Updates);
        Assert.Equal(SessionState.Failed, session.State);
        Assert.Equal(error.Message, session.Error);
        Assert.Equal(2, capture.Sources.Count);
        Assert.All(capture.Sources, s =>
        {
            Assert.Equal(1, s.StartCalls);
            Assert.Equal(1, s.DisposeCalls);
        });
        Assert.Empty(capture.ScreenRecorders); // the screen is never opened without audio

        var files = _store.GetFiles(session.Id);
        Assert.False(File.Exists(files.MicrophoneAudio));
        Assert.False(File.Exists(files.SystemAudio));

        // The recorder is usable again.
        capture.ThrowOnStart.Clear();
        var next = await recorder.StartAsync(new RecordingOptions(), Ct);
        Assert.Null(next.Error);
        Assert.True(next.HasMicrophone);
        var stopped = await recorder.StopAsync(Ct);
        Assert.Equal(SessionState.Recorded, stopped.State);
        Assert.Null(stopped.Error);
    }

    [Fact]
    public async Task BothSourcesCannotBeCreated_FailsTheStart()
    {
        var capture = new MisbehavingCapture(new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 10)));
        capture.ThrowOnCreate[AudioDeviceKind.Microphone] = AccessDenied;
        capture.ThrowOnCreate[AudioDeviceKind.SystemOutput] = () => new InvalidOperationException("no output");
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await recorder.StartAsync(new RecordingOptions(), Ct));

        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Equal(SessionState.Failed, Assert.Single(_store.Updates).State);
    }

    [Fact]
    public async Task ScreenFailureAtStop_KeepsTheMicrophoneWarning()
    {
        var capture = new MisbehavingCapture(new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 1, Screen: true)))
        {
            ThrowOnStopScreenSegment = true,
        };
        capture.ThrowOnStart[AudioDeviceKind.Microphone] = AccessDenied;
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);

        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        await Task.Delay(200, Ct);
        var finished = await recorder.StopAsync(Ct);

        Assert.Equal(SessionState.Recorded, finished.State);
        Assert.NotNull(finished.Error);
        Assert.StartsWith("Screen segment failed to finalize.", finished.Error, StringComparison.Ordinal);
        Assert.EndsWith(MicDenied, finished.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AudioDeviceKind.Microphone, true, "rec.mic.denied\nrec.only.system")]
    [InlineData(AudioDeviceKind.Microphone, false, "rec.mic.unavailable|device busy\nrec.only.system")]
    [InlineData(AudioDeviceKind.SystemOutput, true, "rec.system.denied\nrec.only.mic")]
    [InlineData(AudioDeviceKind.SystemOutput, false, "rec.system.unavailable|device busy\nrec.only.mic")]
    public void DescribeSkippedTracks_NamesTheLostTrackAndWhatIsRecorded(AudioDeviceKind kind, bool denied, string expected)
    {
        // Access denial is recognised anywhere in the chain (a source may wrap it in its start error).
        Exception error = denied
            ? new InvalidOperationException("Could not start", new UnauthorizedAccessException("denied"))
            : new InvalidOperationException("device busy.");

        var warning = Recorder.DescribeSkippedTracks(
            [(kind, error)],
            hasMicrophone: kind != AudioDeviceKind.Microphone,
            hasSystemAudio: kind != AudioDeviceKind.SystemOutput);

        Assert.Equal(expected, warning);
    }

    [Fact]
    public void DescribeSkippedTracks_IsNullWhenNothingWasLost() =>
        Assert.Null(Recorder.DescribeSkippedTracks([], hasMicrophone: true, hasSystemAudio: true));
}
