using Murmur.Core.Audio;
using Murmur.Core.Capture;
using Murmur.Core.Capture.Synthetic;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Recording;

/// <summary>
/// A warning raised while recording reaches the store soon after, not only at the stop (#32): the flusher thread writes it at
/// the next flush, off the capture callbacks, once per burst. The recorder runs on a <see cref="SampleClock"/> that only moves
/// when the test advances it.
/// </summary>
[Collection(RealtimeCollection.Name)]
public sealed class RecorderWarningSaveTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly InMemorySessionStore _store;
    private readonly SampleClock _clock = new();
    private readonly SyntheticCapture _synthetic;
    private readonly RaisingScreenRecorder _screen = new();

    public RecorderWarningSaveTests()
    {
        _store = new InMemorySessionStore(_dir.Path);
        _synthetic = new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 1, Screen: true, TimeProvider: _clock));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task AWarning_IsInTheStoreBeforeTheStop_AndTheStopDoesNotDuplicateIt()
    {
        var recorder = Create(RecorderSettings.Default);
        var session = await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);

        _clock.Advance(TimeSpan.FromSeconds(0.5), _synthetic);
        _screen.Raise(Dropped());
        Assert.Null(_store.Get(session.Id)?.Error);

        _clock.Advance(TimeSpan.FromSeconds(2.1), _synthetic);
        SampleClock.WaitUntil(() => _store.Get(session.Id)?.Error is not null, "the warning to reach the store");

        var stored = _store.Get(session.Id)!;
        Assert.Equal(SessionState.Recording, stored.State);
        Assert.Equal(SessionNotes.ScreenFramesDropped, stored.Error);

        var finished = await recorder.StopAsync(Ct);

        Assert.Equal(SessionNotes.ScreenFramesDropped, finished.Error);
        Assert.Equal(SessionNotes.ScreenFramesDropped, _store.Get(session.Id)?.Error);
        Assert.Equal(3, _store.Updates.Count); // the start, the warning, the stop
    }

    [Fact]
    public async Task ABurstOfWarnings_IsOneWrite_AndNothingChangedIsNotWrittenAgain()
    {
        var recorder = Create(RecorderSettings.Default);
        var session = await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);

        _clock.Advance(TimeSpan.FromSeconds(0.5), _synthetic);
        _screen.Raise(Dropped());
        _screen.Raise(new CaptureFault("screen", "cpu", false, CaptureFaultKind.CpuFallback));
        _screen.Raise(new CaptureFault("screen", "odd", false, CaptureFaultKind.Other));
        _screen.Raise(Dropped());

        _clock.Advance(TimeSpan.FromSeconds(2.1), _synthetic);
        var all = $"{SessionNotes.ScreenFramesDropped}\n{SessionNotes.ScreenCpuFallback}\n{SessionNotes.ScreenOther}";
        SampleClock.WaitUntil(() => _store.Get(session.Id)?.Error == all, "the burst to reach the store");

        // Several more flushes with nothing new: no write.
        _clock.Advance(TimeSpan.FromSeconds(6), _synthetic);
        await Task.Delay(200, Ct);

        Assert.Equal(2, _store.Updates.Count); // the start and the one write of the burst
        await recorder.StopAsync(Ct);
        Assert.Equal(all, _store.Get(session.Id)?.Error);
    }

    [Fact]
    public async Task WritesAreAtLeastASecondApart_AndALaterWarningFollows()
    {
        var recorder = Create(RecorderSettings.Default with { FlushInterval = TimeSpan.FromMilliseconds(200) });
        var session = await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);

        _screen.Raise(Dropped());
        _clock.Advance(TimeSpan.FromSeconds(0.5), _synthetic);
        await Task.Delay(200, Ct);
        Assert.Single(_store.Updates); // flushes ran, but the last write (the start) is less than a second old

        AdvanceUntil(() => _store.Get(session.Id)?.Error == SessionNotes.ScreenFramesDropped, "the first warning to reach the store");

        _screen.Raise(new CaptureFault("screen", "cpu", false, CaptureFaultKind.CpuFallback));
        _clock.Advance(TimeSpan.FromSeconds(0.3), _synthetic);
        await Task.Delay(200, Ct);
        Assert.Equal(2, _store.Updates.Count);

        AdvanceUntil(
            () => _store.Get(session.Id)?.Error == $"{SessionNotes.ScreenFramesDropped}\n{SessionNotes.ScreenCpuFallback}",
            "the second warning to reach the store");
        Assert.Equal(3, _store.Updates.Count);

        await recorder.StopAsync(Ct);
    }

    [Fact]
    public async Task AFatalFault_IsStoredAtOnce_AndTheLaterFlushDoesNotWriteItAgain()
    {
        var recorder = Create(RecorderSettings.Default);
        var session = await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);

        _clock.Advance(TimeSpan.FromSeconds(0.5), _synthetic);
        _screen.Raise(new CaptureFault("screen", "gone", true));
        SampleClock.WaitUntil(() => _store.Get(session.Id)?.Error == SessionNotes.ScreenStopped, "the fatal fault to reach the store");
        var writes = _store.Updates.Count;

        _clock.Advance(TimeSpan.FromSeconds(5), _synthetic);
        await Task.Delay(200, Ct);

        Assert.Equal(writes, _store.Updates.Count);
        await recorder.StopAsync(Ct);
    }

    [Fact]
    public async Task AWarningStillUnsaved_IsStoredWhilePaused_Too()
    {
        var recorder = Create(RecorderSettings.Default);
        var session = await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        _clock.Advance(TimeSpan.FromSeconds(0.5), _synthetic);
        _screen.Raise(Dropped());
        await recorder.PauseAsync(Ct);

        _clock.Advance(TimeSpan.FromSeconds(2.1), _synthetic);
        SampleClock.WaitUntil(() => _store.Get(session.Id)?.Error == SessionNotes.ScreenFramesDropped, "the warning to reach the store while paused");

        Assert.Equal(SessionState.Recording, _store.Get(session.Id)?.State);
        await recorder.ResumeAsync(Ct);
        var finished = await recorder.StopAsync(Ct);
        Assert.Equal(SessionNotes.ScreenFramesDropped, finished.Error);
    }

    [Fact]
    public async Task AFailingWrite_IsRetriedAtTheNextFlush_AndNeverReachesTheRecording()
    {
        var failing = new FailingOnceStore(_dir.Path);
        var recorder = new Recorder(new RaisingCapture(_synthetic, _screen), failing, _clock, RecorderSettings.Default);
        var session = await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);

        _clock.Advance(TimeSpan.FromSeconds(0.5), _synthetic);
        _screen.Raise(Dropped());
        failing.FailNext = true;
        _clock.Advance(TimeSpan.FromSeconds(2.1), _synthetic);
        SampleClock.WaitUntil(() => failing.Failed, "the write to fail");
        Assert.Null(failing.Get(session.Id)?.Error);

        _clock.Advance(TimeSpan.FromSeconds(2.1), _synthetic);
        SampleClock.WaitUntil(() => failing.Get(session.Id)?.Error == SessionNotes.ScreenFramesDropped, "the retry to reach the store");

        Assert.Equal(RecorderState.Recording, recorder.State);
        await recorder.StopAsync(Ct);
    }

    /// <summary>
    /// Moves the clock in small steps until <paramref name="condition"/> holds: the writer flushes only while the clock moves, so
    /// the one jump that is "enough" on paper can land between two flushes.
    /// </summary>
    private void AdvanceUntil(Func<bool> condition, string what)
    {
        for (var i = 0; i < 40; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(0.25), _synthetic);
            var deadline = Environment.TickCount64 + 100;
            while (Environment.TickCount64 < deadline)
            {
                if (condition())
                {
                    return;
                }

                Thread.Sleep(1);
            }
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    private Recorder Create(RecorderSettings settings) => new(new RaisingCapture(_synthetic, _screen), _store, _clock, settings);

    private static CaptureFault Dropped() => new("screen", "frames dropped", false, CaptureFaultKind.FramesDropped);

    /// <summary>The in-memory store whose next update (once <see cref="FailNext"/> is set) throws, like a locked database.</summary>
    private sealed class FailingOnceStore(string root) : ISessionStore
    {
        private readonly InMemorySessionStore _inner = new(root);
        private int _failNext;
        private int _failed;

        public bool FailNext
        {
            set => Volatile.Write(ref _failNext, value ? 1 : 0);
        }

        public bool Failed => Volatile.Read(ref _failed) == 1;

        public SessionRecord? Get(SessionId id) => _inner.Get(id);

        public ValueTask<SessionRecord> CreateSessionAsync(string title, DateTimeOffset startedAt, SessionSource source, CancellationToken cancellationToken) =>
            _inner.CreateSessionAsync(title, startedAt, source, cancellationToken);

        public ValueTask UpdateSessionAsync(SessionRecord session, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _failNext, 0) == 1)
            {
                Volatile.Write(ref _failed, 1);
                throw new IOException("database is locked");
            }

            return _inner.UpdateSessionAsync(session, cancellationToken);
        }

        public ValueTask RenameSessionAsync(SessionId id, string title, CancellationToken cancellationToken) => _inner.RenameSessionAsync(id, title, cancellationToken);

        public ValueTask<SessionRecord?> GetSessionAsync(SessionId id, CancellationToken cancellationToken) => _inner.GetSessionAsync(id, cancellationToken);

        public ValueTask<IReadOnlyList<SessionRecord>> ListSessionsAsync(SessionQuery query, CancellationToken cancellationToken) => _inner.ListSessionsAsync(query, cancellationToken);

        public ValueTask DeleteSessionAsync(SessionId id, CancellationToken cancellationToken) => _inner.DeleteSessionAsync(id, cancellationToken);

        public SessionFiles GetFiles(SessionId id) => _inner.GetFiles(id);
    }

    private sealed class RaisingCapture(SyntheticCapture inner, IScreenRecorder screen) : ICapture
    {
        public CaptureCapabilities Capabilities => inner.Capabilities;

        public IReadOnlyList<AudioDevice> GetAudioDevices(AudioDeviceKind kind) => inner.GetAudioDevices(kind);

        public IAudioSource CreateAudioSource(AudioDeviceKind kind, string? deviceId) => inner.CreateAudioSource(kind, deviceId);

        public IReadOnlyList<DisplayInfo> GetDisplays() => inner.GetDisplays();

        public IScreenRecorder CreateScreenRecorder(ScreenCaptureOptions options) => screen;
    }

    private sealed class RaisingScreenRecorder : IScreenRecorder
    {
        public bool IsRecording { get; private set; }

        public ScreenRecorderStats Stats => new(0, 0, 0, "raising");

        public event Action<CaptureFault>? Faulted;

        public void Raise(CaptureFault fault) => Faulted?.Invoke(fault);

        public ValueTask StartSegmentAsync(string filePath, CancellationToken cancellationToken)
        {
            File.Create(filePath).Dispose();
            IsRecording = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopSegmentAsync(CancellationToken cancellationToken)
        {
            IsRecording = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
