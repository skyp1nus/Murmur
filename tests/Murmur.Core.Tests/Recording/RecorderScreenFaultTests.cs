using System.Collections.Concurrent;
using System.Diagnostics;
using Murmur.Core.Capture;
using Murmur.Core.Capture.Synthetic;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Recording;

/// <summary>
/// Faults of the screen recorder (raised on other threads, like the real one does) reach the stats, the
/// <see cref="IRecorder.Warning"/> event and the session's warnings; a fatal one ends the segment without touching the audio.
/// </summary>
[Collection(RealtimeCollection.Name)]
public sealed class RecorderScreenFaultTests : IDisposable
{
    private const string PoolMessage = "GPU zero-copy: all 12 encoder samples stayed in use for 2.0 s (61 frames dropped in a row); the encoder does not hand frames back; the next segments use CPU readback";
    private const string CpuMessage = "CPU readback: the zero-copy writer picked a software encoder (Microsoft H.264)";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _dir = new();
    private readonly InMemorySessionStore _store;
    private readonly ConcurrentQueue<RecorderWarning> _warnings = new();

    public RecorderScreenFaultTests() => _store = new InMemorySessionStore(_dir.Path);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task NonFatalFaults_AreCounted_WarnOncePerDistinctMessage_AndLandInTheSessionWarnings()
    {
        var (recorder, screen) = Create();
        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);

        await screen.RaiseFromOtherThreadAsync(Pool());
        await screen.RaiseFromOtherThreadAsync(Pool());
        await screen.RaiseFromOtherThreadAsync(Cpu());
        await screen.RaiseFromOtherThreadAsync(Fault("Something odd happened"));
        await screen.RaiseFromOtherThreadAsync(Fault("Something odd happened"));

        Assert.True(recorder.IsScreenRecording);
        Assert.Equal(5, recorder.Stats.ScreenFaults);
        Assert.Equal(
            [RecorderWarningCode.ScreenFramesDropped, RecorderWarningCode.ScreenCpuFallback, RecorderWarningCode.ScreenOther],
            _warnings.Select(w => w.Code));
        Assert.All(_warnings, w => Assert.False(w.ScreenStopped));
        Assert.All(_warnings, w => Assert.Equal("screen", w.Source));
        Assert.Equal([CaptureFaultKind.FramesDropped, CaptureFaultKind.CpuFallback, CaptureFaultKind.Other], _warnings.Select(w => w.Kind));

        var finished = await recorder.StopAsync(Ct);

        Assert.Equal(SessionState.Recorded, finished.State);
        Assert.Equal(5, recorder.Stats.ScreenFaults);
        Assert.Equal($"{SessionNotes.ScreenFramesDropped}\n{SessionNotes.ScreenCpuFallback}\n{SessionNotes.ScreenOther}", finished.Error);
        Assert.Single(finished.ScreenSegments);
    }

    [Fact]
    public async Task TwoDifferentMessagesOfTheSameProblem_AreOneWarning_ButBothAreCounted()
    {
        var (recorder, screen) = Create();
        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);

        await screen.RaiseFromOtherThreadAsync(Fault("first oddity"));
        await screen.RaiseFromOtherThreadAsync(Fault("second oddity"));
        await screen.RaiseFromOtherThreadAsync(Pool("another pool message"));
        var finished = await recorder.StopAsync(Ct);

        Assert.Equal(2, _warnings.Count);
        Assert.Equal(3, recorder.Stats.ScreenFaults);
        Assert.Equal($"{SessionNotes.ScreenOther}\n{SessionNotes.ScreenFramesDropped}", finished.Error);
    }

    [Theory]
    [InlineData(CaptureFaultKind.FramesDropped, false, RecorderWarningCode.ScreenFramesDropped)]
    [InlineData(CaptureFaultKind.CpuFallback, false, RecorderWarningCode.ScreenCpuFallback)]
    [InlineData(CaptureFaultKind.Other, false, RecorderWarningCode.ScreenOther)]
    [InlineData(CaptureFaultKind.Other, true, RecorderWarningCode.ScreenStopped)]
    [InlineData(CaptureFaultKind.FramesDropped, true, RecorderWarningCode.ScreenStopped)]
    public void ScreenFaults_GetTheirCode_ByKindAndSeverity(CaptureFaultKind kind, bool fatal, RecorderWarningCode expected) =>
        Assert.Equal(expected, Recorder.ScreenWarningCode(Fault("detail", fatal: fatal, kind: kind)));

    [Fact]
    public async Task TheSessionStoresCodes_NeverSentences()
    {
        var (recorder, screen) = Create();
        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);

        await screen.RaiseFromOtherThreadAsync(Pool());
        var finished = await recorder.StopAsync(Ct);

        Assert.Equal(SessionNotes.ScreenFramesDropped, finished.Error);
        Assert.Equal(SessionNotes.ScreenFramesDropped, Assert.Single(SessionNotes.Parse(finished.Error)).Code);
    }

    [Fact]
    public async Task TheKind_NotTheMessage_DecidesTheText()
    {
        var (recorder, screen) = Create();
        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);

        // Messages that used to be matched as text now say nothing: the kind does.
        await screen.RaiseFromOtherThreadAsync(Fault("CPU readback: all 12 encoder samples stayed in use"));
        await screen.RaiseFromOtherThreadAsync(Fault("anything", kind: CaptureFaultKind.CpuFallback));
        await recorder.StopAsync(Ct);

        Assert.Equal([RecorderWarningCode.ScreenOther, RecorderWarningCode.ScreenCpuFallback], _warnings.Select(w => w.Code));
    }

    [Fact]
    public async Task FatalFault_EndsTheSegmentWithItsRealEnd_AndTheAudioGoesOn()
    {
        var (recorder, screen) = Create();
        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        await Task.Delay(150, Ct);
        var systemBefore = recorder.Stats.SystemFrames;

        await screen.RaiseFromOtherThreadAsync(Fault("device lost", fatal: true));

        // The pill must turn off at once; the segment is cleaned up right behind.
        Assert.False(recorder.IsScreenRecording);
        await WaitUntilAsync(() => screen.StopTimestamps.Count == 1);
        Assert.Equal(RecorderState.Recording, recorder.State);
        await Task.Delay(300, Ct);
        Assert.True(recorder.Stats.SystemFrames > systemBefore, "the audio stopped with the screen");
        var finished = await recorder.StopAsync(Ct);

        var warning = Assert.Single(_warnings);
        Assert.True(warning.ScreenStopped);
        Assert.Equal(RecorderWarningCode.ScreenStopped, warning.Code);
        Assert.Single(finished.ScreenSegments);
        Assert.Equal(SessionState.Recorded, finished.State);
        Assert.True(finished.HasSystemAudio);
        Assert.Equal(SessionNotes.ScreenStopped, finished.Error);
        Assert.Equal(1, recorder.Stats.ScreenFaults);

        // The segment ends where the recorder stopped the screen (right after the fault), not at the final stop.
        var segment = finished.ScreenSegments[0];
        var expected = Stopwatch.GetElapsedTime(screen.StartTimestamps[0], screen.StopTimestamps[0]);
        Assert.InRange(segment.Duration, expected - TimeSpan.FromMilliseconds(100), expected + TimeSpan.FromMilliseconds(100));
        Assert.True(segment.Duration < finished.Duration - TimeSpan.FromMilliseconds(200), $"segment {segment.Duration}, session {finished.Duration}");
        Assert.Single(screen.StopTimestamps);
        Assert.Equal(0, screen.Subscribers);
    }

    [Fact]
    public async Task AFailedSegment_EndsAtTheFault_EvenWhenTheCleanupRunsLater()
    {
        var (recorder, screen) = Create();
        await recorder.StartAsync(new RecordingOptions(Screen: false), Ct);
        long faultAt = 0;

        // The fault arrives while "on" is still starting the segment (the gate is held); the cleanup can only run 400 ms later.
        screen.OnStart = () =>
        {
            screen.RaiseFromOtherThreadAsync(Fault("device lost", fatal: true)).GetAwaiter().GetResult();
            faultAt = Stopwatch.GetTimestamp();
            Thread.Sleep(400);
        };
        await recorder.SetScreenEnabledAsync(true, Ct).AsTask().WaitAsync(Timeout, Ct);
        await WaitUntilAsync(() => screen.StopTimestamps.Count == 1);
        var finished = await recorder.StopAsync(Ct);

        var segment = Assert.Single(finished.ScreenSegments);
        var expected = Stopwatch.GetElapsedTime(screen.StartTimestamps[0], faultAt);
        Assert.InRange(segment.Duration, TimeSpan.Zero, expected + TimeSpan.FromMilliseconds(100));
        Assert.True(segment.Duration < TimeSpan.FromMilliseconds(300), $"segment {segment.Duration} includes the wait for the cleanup");
    }

    [Fact]
    public async Task AFatalFault_IsStoredInTheSessionRightAway()
    {
        var (recorder, screen) = Create();
        var session = await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);

        await screen.RaiseFromOtherThreadAsync(Fault("device lost", fatal: true));

        await WaitUntilAsync(() => _store.GetSessionAsync(session.Id, Ct).AsTask().GetAwaiter().GetResult()?.Error == SessionNotes.ScreenStopped);
        Assert.Equal(RecorderState.Recording, recorder.State);
        await recorder.StopAsync(Ct);
    }

    [Fact]
    public async Task AFinalizeFailureOfAFailedSegment_NeitherEscapesTheCleanup_NorBlocksSwitchingOnAgain()
    {
        var (recorder, screen) = Create();
        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        screen.ThrowOnStop = true;

        await screen.RaiseFromOtherThreadAsync(Fault("device lost", fatal: true));
        await WaitUntilAsync(() => screen.StopTimestamps.Count == 1);
        await Task.Delay(100, Ct);
        Assert.Equal(RecorderState.Recording, recorder.State);

        // "On" right after a second failure whose finalize fails too.
        await recorder.SetScreenEnabledAsync(true, Ct).AsTask().WaitAsync(Timeout, Ct);
        Assert.True(recorder.IsScreenRecording);
        await screen.RaiseFromOtherThreadAsync(Fault("device lost again", fatal: true));
        await recorder.SetScreenEnabledAsync(true, Ct).AsTask().WaitAsync(Timeout, Ct);

        Assert.True(recorder.IsScreenRecording);
        screen.ThrowOnStop = false;
        var finished = await recorder.StopAsync(Ct);
        Assert.Equal(3, finished.ScreenSegments.Count);
    }

    [Fact]
    public async Task AfterAFatalFault_TheScreenCanBeSwitchedOnAgain()
    {
        var (recorder, screen) = Create();
        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        await screen.RaiseFromOtherThreadAsync(Fault("encoder failed", fatal: true));

        // Not waiting for the cleanup: "on" right after the fault must finish the failed segment itself.
        await recorder.SetScreenEnabledAsync(true, Ct).AsTask().WaitAsync(Timeout, Ct);

        Assert.True(recorder.IsScreenRecording);
        Assert.Equal(2, screen.StartTimestamps.Count);
        await screen.RaiseFromOtherThreadAsync(Fault("encoder failed again", fatal: true));
        Assert.False(recorder.IsScreenRecording);
        await recorder.SetScreenEnabledAsync(true, Ct).AsTask().WaitAsync(Timeout, Ct);
        await Task.Delay(50, Ct);
        var finished = await recorder.StopAsync(Ct);

        Assert.Equal([0, 1, 2], finished.ScreenSegments.Select(s => s.Index));
        Assert.Equal(3, screen.StartTimestamps.Count);
        Assert.Equal(3, screen.StopTimestamps.Count);
        Assert.Equal(2, _warnings.Count(w => w.ScreenStopped));
        Assert.Equal(SessionNotes.ScreenStopped, finished.Error);
        Assert.Equal(2, recorder.Stats.ScreenFaults);
        Assert.All(finished.ScreenSegments, s => Assert.True(s.Duration >= TimeSpan.Zero));
    }

    [Fact]
    public async Task AFaultOfAFinishedSegment_IsIgnored()
    {
        var (recorder, screen) = Create();
        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        await recorder.SetScreenEnabledAsync(false, Ct);

        Assert.Equal(0, screen.Subscribers);
        Assert.False(recorder.IsScreenRecording);

        // A fault that was queued before the unsubscribe and is delivered after it, through the old handler.
        screen.RaiseThroughStaleHandler(Fault("late", fatal: true));
        screen.RaiseThroughStaleHandler(Pool());

        Assert.Empty(_warnings);
        Assert.Equal(0, recorder.Stats.ScreenFaults);
        var finished = await recorder.StopAsync(Ct);
        Assert.Null(finished.Error);
        Assert.Equal(0, recorder.Stats.ScreenFaults);
    }

    [Fact]
    public async Task AFaultAfterTheRecordingEnded_IsIgnored()
    {
        var (recorder, screen) = Create();
        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        await recorder.StopAsync(Ct);

        screen.RaiseThroughStaleHandler(Fault("late", fatal: true));

        Assert.Empty(_warnings);
        Assert.Equal(0, recorder.Stats.ScreenFaults);
        Assert.False(recorder.IsScreenRecording);
        Assert.Equal(0, screen.Subscribers);
    }

    [Fact]
    public async Task FaultsRaisedWhileTheSegmentStarts_DoNotDeadlock_AndAFatalOneEndsTheSegment()
    {
        var (recorder, screen) = Create();
        await recorder.StartAsync(new RecordingOptions(Screen: false), Ct);
        screen.OnStart = () =>
        {
            screen.RaiseFromOtherThreadAsync(Pool()).GetAwaiter().GetResult();
            screen.RaiseFromOtherThreadAsync(Fault("device lost", fatal: true)).GetAwaiter().GetResult();
        };

        await recorder.SetScreenEnabledAsync(true, Ct).AsTask().WaitAsync(Timeout, Ct);

        Assert.False(recorder.IsScreenRecording);
        await WaitUntilAsync(() => screen.StopTimestamps.Count == 1);
        var finished = await recorder.StopAsync(Ct).AsTask().WaitAsync(Timeout, Ct);

        Assert.Single(finished.ScreenSegments);
        Assert.Equal(2, recorder.Stats.ScreenFaults);
        Assert.Equal($"{SessionNotes.ScreenFramesDropped}\n{SessionNotes.ScreenStopped}", finished.Error);
    }

    [Fact]
    public async Task FaultsRaisedWhileTheSegmentStops_DoNotDeadlock()
    {
        var (recorder, screen) = Create();
        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        screen.OnStop = () =>
        {
            screen.RaiseFromOtherThreadAsync(Cpu()).GetAwaiter().GetResult();
            screen.RaiseFromOtherThreadAsync(Fault("encoder did not finish", fatal: true)).GetAwaiter().GetResult();
        };

        await recorder.SetScreenEnabledAsync(false, Ct).AsTask().WaitAsync(Timeout, Ct);

        Assert.False(recorder.IsScreenRecording);
        Assert.Single(screen.StopTimestamps);
        var finished = await recorder.StopAsync(Ct).AsTask().WaitAsync(Timeout, Ct);
        Assert.Single(finished.ScreenSegments);
        Assert.Single(screen.StopTimestamps);
    }

    [Fact]
    public async Task FaultsRaisedDuringTheFinalStop_DoNotDeadlock_AndTheSegmentIsKept()
    {
        var (recorder, screen) = Create();
        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        await Task.Delay(100, Ct);
        screen.OnStop = () => screen.RaiseFromOtherThreadAsync(Fault("encoder did not finish", fatal: true)).GetAwaiter().GetResult();

        var finished = await recorder.StopAsync(Ct).AsTask().WaitAsync(Timeout, Ct);

        Assert.Equal(SessionState.Recorded, finished.State);
        Assert.Single(finished.ScreenSegments);
        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Equal(0, screen.Subscribers);
        Assert.Equal(1, recorder.Stats.ScreenFaults);
    }

    [Fact]
    public async Task AThrowingWarningSubscriber_DoesNotReachTheCaptureThread()
    {
        var (recorder, screen) = Create();
        recorder.Warning += _ => throw new InvalidOperationException("subscriber failed");
        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);

        await screen.RaiseFromOtherThreadAsync(Pool());

        Assert.Equal(1, recorder.Stats.ScreenFaults);
        await recorder.StopAsync(Ct);
    }

    [Fact]
    public async Task AFreshRecording_StartsWithoutTheEarlierFaults()
    {
        var (recorder, screen) = Create();
        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        await screen.RaiseFromOtherThreadAsync(Pool());
        await recorder.StopAsync(Ct);

        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        Assert.Equal(0, recorder.Stats.ScreenFaults);
        Assert.True(recorder.IsScreenRecording);
        await screen.RaiseFromOtherThreadAsync(Pool());
        var finished = await recorder.StopAsync(Ct);

        // The same message is a new problem in a new recording.
        Assert.Equal(2, _warnings.Count);
        Assert.Equal(SessionNotes.ScreenFramesDropped, finished.Error);
    }

    private (Recorder Recorder, FaultingScreenRecorder Screen) Create()
    {
        var screen = new FaultingScreenRecorder();
        var capture = new FaultingScreenCapture(new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 1, Screen: true)), screen);
        var recorder = new Recorder(capture, _store, TimeProvider.System, RecorderSettings.Default);
        recorder.Warning += _warnings.Enqueue;
        return (recorder, screen);
    }

    private static CaptureFault Fault(string message, bool fatal = false, CaptureFaultKind kind = CaptureFaultKind.Other) => new("screen", message, fatal, kind);

    private static CaptureFault Pool(string message = PoolMessage) => Fault(message, kind: CaptureFaultKind.FramesDropped);

    private static CaptureFault Cpu() => Fault(CpuMessage, kind: CaptureFaultKind.CpuFallback);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 10);
        while (!condition())
        {
            Assert.True(Stopwatch.GetTimestamp() < deadline, "the condition did not become true in 10 s");
            await Task.Delay(10, Ct);
        }
    }

    private sealed class FaultingScreenCapture(SyntheticCapture inner, IScreenRecorder screen) : ICapture
    {
        public CaptureCapabilities Capabilities => inner.Capabilities;

        public IReadOnlyList<AudioDevice> GetAudioDevices(AudioDeviceKind kind) => inner.GetAudioDevices(kind);

        public IAudioSource CreateAudioSource(AudioDeviceKind kind, string? deviceId) => inner.CreateAudioSource(kind, deviceId);

        public IReadOnlyList<DisplayInfo> GetDisplays() => inner.GetDisplays();

        public IScreenRecorder CreateScreenRecorder(ScreenCaptureOptions options) => screen;
    }

    /// <summary>
    /// A screen recorder that raises the faults it is told to, like the real one on a thread-pool thread. It stays
    /// "recording" after a fatal fault until the segment is stopped, as the real one does.
    /// </summary>
    private sealed class FaultingScreenRecorder : IScreenRecorder
    {
        private readonly List<long> _starts = [];
        private readonly List<long> _stops = [];
        private Action<CaptureFault>? _faulted;
        private Action<CaptureFault>? _lastHandler;
        private int _subscribers;
        private volatile bool _recording;

        public Action? OnStart { get; set; }

        public Action? OnStop { get; set; }

        /// <summary>The next stops leave the segment and then fail, like a finalize error from Media Foundation.</summary>
        public volatile bool ThrowOnStop;

        public int Subscribers => Volatile.Read(ref _subscribers);

        public List<long> StartTimestamps
        {
            get
            {
                lock (_starts)
                {
                    return [.. _starts];
                }
            }
        }

        public List<long> StopTimestamps
        {
            get
            {
                lock (_stops)
                {
                    return [.. _stops];
                }
            }
        }

        public bool IsRecording => _recording;

        public ScreenRecorderStats Stats => new(0, 0, 0, "faulting");

        public event Action<CaptureFault>? Faulted
        {
            add
            {
                _faulted += value;
                _lastHandler = value;
                Interlocked.Increment(ref _subscribers);
            }
            remove
            {
                _faulted -= value;
                Interlocked.Decrement(ref _subscribers);
            }
        }

        /// <summary>Raises on a thread-pool thread and waits until every handler has returned.</summary>
        public Task RaiseFromOtherThreadAsync(CaptureFault fault) => Task.Run(() => _faulted?.Invoke(fault));

        /// <summary>Calls the handler the recorder registered last, whether or not it is still subscribed.</summary>
        public void RaiseThroughStaleHandler(CaptureFault fault) => _lastHandler?.Invoke(fault);

        public ValueTask StartSegmentAsync(string filePath, CancellationToken cancellationToken)
        {
            lock (_starts)
            {
                _starts.Add(Stopwatch.GetTimestamp());
            }

            File.Create(filePath).Dispose();
            _recording = true;
            OnStart?.Invoke();
            return ValueTask.CompletedTask;
        }

        public ValueTask StopSegmentAsync(CancellationToken cancellationToken)
        {
            if (!_recording)
            {
                return ValueTask.CompletedTask;
            }

            lock (_stops)
            {
                _stops.Add(Stopwatch.GetTimestamp());
            }

            OnStop?.Invoke();
            _recording = false;
            return ThrowOnStop ? throw new InvalidOperationException("finalize failed") : ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _recording = false;
            return ValueTask.CompletedTask;
        }
    }
}
