using Murmur.Core.Audio;
using Murmur.Core.Capture;
using Murmur.Core.Capture.Synthetic;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Recording;

/// <summary>
/// The recorder places a screen segment on the session timeline by the moment it calls the screen recorder:
/// <see cref="ScreenSegment.Offset"/> is the session time at the start call and <see cref="ScreenSegment.Duration"/>
/// the time from the start call to the stop call, however long starting or stopping takes (the screen recorder's own
/// start-up and finalize time must neither shift nor stretch the segment). The recorder runs on a clock that only moves
/// when the test (or the slow screen recorder, or a slow audio stop) jumps it, so the check is exact: no scheduling noise
/// of a loaded machine can sit between the clock reads of the recorder and of the fake.
/// </summary>
[Collection(RealtimeCollection.Name)]
public sealed class RecorderScreenSegmentClockTests : IDisposable
{
    private static readonly TimeSpan SlowStart = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan SlowStop = TimeSpan.FromMilliseconds(700);

    private readonly TempDirectory _dir = new();
    private readonly InMemorySessionStore _store;

    public RecorderScreenSegmentClockTests() => _store = new InMemorySessionStore(_dir.Path);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Segments_AreTakenAtTheStartAndStopCalls_NotAfterTheyReturn()
    {
        var clock = new JumpingTimeProvider();
        var capture = new SlowScreenCapture(new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 1, Screen: true)), clock, SlowStart, SlowStop);
        var recorder = new Recorder(capture, _store, clock, RecorderSettings.Default);

        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        clock.Jump(TimeSpan.FromMilliseconds(150));
        await recorder.SetScreenEnabledAsync(false, Ct);
        clock.Jump(TimeSpan.FromMilliseconds(150));
        await recorder.SetScreenEnabledAsync(true, Ct);
        clock.Jump(TimeSpan.FromMilliseconds(150));
        var finished = await recorder.StopAsync(Ct);

        var screen = Assert.Single(capture.Recorders);
        var calls = screen.Calls;
        Assert.Equal(2, calls.Starts.Count);
        Assert.Equal(2, calls.Stops.Count);
        Assert.Equal(2, finished.ScreenSegments.Count);
        var (first, second) = (finished.ScreenSegments[0], finished.ScreenSegments[1]);

        // Duration = start call to stop call (it includes the slow start, not the slow stop).
        AssertExact(clock.Between(calls.Starts[0], calls.Stops[0]), first.Duration);
        AssertExact(clock.Between(calls.Starts[1], calls.Stops[1]), second.Duration);
        Assert.True(first.Duration >= SlowStart + TimeSpan.FromMilliseconds(100), $"first {first}");

        // Offsets are the session times of the start calls: the distance between them is exactly the distance
        // between the calls (the audio start cancels out), and the first one is the audio start plus a short wait.
        AssertExact(clock.Between(calls.Starts[0], calls.Starts[1]), second.Offset - first.Offset);
        Assert.Equal(TimeSpan.Zero, first.Offset);
        Assert.True(second.Offset >= first.Offset + first.Duration + SlowStop, $"first {first}, second {second}");
    }

    [Fact]
    public async Task Segments_ToggledSeveralTimes_AreOrderedDisjointAndEachMatchesItsCalls()
    {
        var clock = new JumpingTimeProvider();
        var capture = new SlowScreenCapture(new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 1, Screen: true)), clock, SlowStart, SlowStop);
        var recorder = new Recorder(capture, _store, clock, RecorderSettings.Default);

        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        for (var i = 0; i < 3; i++)
        {
            clock.Jump(TimeSpan.FromMilliseconds(60));
            await recorder.SetScreenEnabledAsync(false, Ct);
            clock.Jump(TimeSpan.FromMilliseconds(60));
            await recorder.SetScreenEnabledAsync(true, Ct);
        }

        clock.Jump(TimeSpan.FromMilliseconds(60));
        var finished = await recorder.StopAsync(Ct);

        var calls = Assert.Single(capture.Recorders).Calls;
        Assert.Equal(4, calls.Starts.Count);
        Assert.Equal(4, calls.Stops.Count);
        Assert.Equal(4, finished.ScreenSegments.Count);
        Assert.Equal([0, 1, 2, 3], finished.ScreenSegments.Select(s => s.Index));
        for (var i = 0; i < 4; i++)
        {
            AssertExact(clock.Between(calls.Starts[i], calls.Stops[i]), finished.ScreenSegments[i].Duration);
            Assert.True(finished.ScreenSegments[i].Duration >= SlowStart, $"segment {i}: {finished.ScreenSegments[i]}");
            if (i > 0)
            {
                var previous = finished.ScreenSegments[i - 1];
                Assert.True(finished.ScreenSegments[i].Offset >= previous.Offset + previous.Duration, $"segments {i - 1} and {i} overlap");
            }
        }
    }

    [Fact]
    public async Task FinalStop_EndsTheLastSegmentAtTheStopCall_AndTheSlowFinalizeIsNotPartOfIt()
    {
        var clock = new JumpingTimeProvider();
        var capture = new SlowScreenCapture(new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 1, Screen: true)), clock, SlowStart, SlowStop);
        var recorder = new Recorder(capture, _store, clock, RecorderSettings.Default);

        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        clock.Jump(TimeSpan.FromMilliseconds(200));
        var finished = await recorder.StopAsync(Ct);

        var calls = Assert.Single(capture.Recorders).Calls;
        var segment = Assert.Single(finished.ScreenSegments);
        AssertExact(clock.Between(calls.Starts[0], calls.Stops[0]), segment.Duration);
        Assert.True(segment.Duration >= SlowStart, $"segment {segment}");
        Assert.True(segment.Duration < SlowStart + SlowStop, $"segment {segment}");
        Assert.True(Assert.Single(capture.Recorders).IsDisposed);
    }

    [Fact]
    public async Task FinalStop_RequestsTheScreenStopWhileTheAudioStops_NotAfterIt()
    {
        // Each audio source needs 3 s to stop (the clock jumps in Stop()). The screen segment must end where the
        // user stopped: the screen stop is requested together with the audio stop, not after it, and the audio's stop time is not
        // part of the segment.
        var audioStopTime = TimeSpan.FromSeconds(3);
        var clock = new JumpingTimeProvider();
        var capture = new SlowScreenCapture(new SyntheticCapture(new SyntheticCaptureOptions(SpeedFactor: 1, Screen: true)), clock, TimeSpan.Zero, TimeSpan.Zero, audioStopTime);
        var recorder = new Recorder(capture, _store, clock, RecorderSettings.Default);

        await recorder.StartAsync(new RecordingOptions(Screen: true), Ct);
        clock.Jump(TimeSpan.FromMilliseconds(150));
        var finished = await recorder.StopAsync(Ct);

        var calls = Assert.Single(capture.Recorders).Calls;
        var screenStop = Assert.Single(calls.Stops);
        Assert.NotEmpty(capture.AudioStops);
        // The screen stop is called before the audio stop is queued, so it precedes every audio Stop() (the old order
        // stopped the screen after the audio: 3+ s later).
        Assert.All(capture.AudioStops, audioStop => Assert.True(
            clock.Between(audioStop, screenStop) <= TimeSpan.Zero,
            $"the screen stop came {clock.Between(audioStop, screenStop)} after an audio source had stopped"));

        var segment = Assert.Single(finished.ScreenSegments);
        AssertExact(clock.Between(calls.Starts[0], screenStop), segment.Duration);
        Assert.True(segment.Duration < audioStopTime, $"segment {segment} includes the audio stop time");
    }

    /// <summary>The recorder reads the clock just before the call and the screen recorder notes it on entry: the same value.</summary>
    private static void AssertExact(TimeSpan expected, TimeSpan actual) => Assert.Equal(expected, actual);

    private sealed class SlowScreenCapture(SyntheticCapture inner, JumpingTimeProvider clock, TimeSpan startTime, TimeSpan stopTime, TimeSpan audioStopTime = default) : ICapture
    {
        private readonly List<SlowScreenRecorder> _recorders = [];
        private readonly List<long> _audioStops = [];

        /// <summary>The clock at the entry of every audio source's <see cref="IAudioSource.Stop"/>.</summary>
        public IReadOnlyList<long> AudioStops
        {
            get
            {
                lock (_audioStops)
                {
                    return [.. _audioStops];
                }
            }
        }

        internal void NoteAudioStop()
        {
            lock (_audioStops)
            {
                _audioStops.Add(clock.GetTimestamp());
            }

            clock.Jump(audioStopTime);
        }

        public IReadOnlyList<SlowScreenRecorder> Recorders => _recorders;

        public CaptureCapabilities Capabilities => inner.Capabilities;

        public IReadOnlyList<AudioDevice> GetAudioDevices(AudioDeviceKind kind) => inner.GetAudioDevices(kind);

        public IAudioSource CreateAudioSource(AudioDeviceKind kind, string? deviceId) =>
            audioStopTime > TimeSpan.Zero ? new SlowStopSource(inner.CreateAudioSource(kind, deviceId), this) : inner.CreateAudioSource(kind, deviceId);

        public IReadOnlyList<DisplayInfo> GetDisplays() => inner.GetDisplays();

        public IScreenRecorder CreateScreenRecorder(ScreenCaptureOptions options)
        {
            var recorder = new SlowScreenRecorder(clock, startTime, stopTime);
            _recorders.Add(recorder);
            return recorder;
        }
    }

    /// <summary>Notes the clock when stopped, really stops, then "takes" the stop time by jumping the clock.</summary>
    private sealed class SlowStopSource(IAudioSource inner, SlowScreenCapture owner) : IAudioSource
    {
        public AudioDevice Device => inner.Device;

        public AudioFormat Format => inner.Format;

        public event Action<CaptureFault>? Faulted
        {
            add => inner.Faulted += value;
            remove => inner.Faulted -= value;
        }

        public void Start(IAudioSink sink) => inner.Start(sink);

        public void Stop()
        {
            owner.NoteAudioStop();
            inner.Stop();
        }

        public void Dispose() => inner.Dispose();
    }

    private sealed record Calls(List<long> Starts, List<long> Stops);

    /// <summary>Notes the clock at the entry of every call, then "takes" its start or stop time by jumping the clock.</summary>
    private sealed class SlowScreenRecorder(JumpingTimeProvider clock, TimeSpan startTime, TimeSpan stopTime) : IScreenRecorder
    {
        private readonly List<long> _starts = [];
        private readonly List<long> _stops = [];
        private volatile bool _recording;

        public Calls Calls => new([.. _starts], [.. _stops]);

        public bool IsDisposed { get; private set; }

        public bool IsRecording => _recording;

        public ScreenRecorderStats Stats => new(0, 0, 0, "slow");

        public event Action<CaptureFault>? Faulted
        {
            add { }
            remove { }
        }

        public ValueTask StartSegmentAsync(string filePath, CancellationToken cancellationToken)
        {
            _starts.Add(clock.GetTimestamp());
            clock.Jump(startTime);
            File.Create(filePath).Dispose();
            _recording = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopSegmentAsync(CancellationToken cancellationToken)
        {
            if (!_recording)
            {
                return ValueTask.CompletedTask;
            }

            _stops.Add(clock.GetTimestamp());
            clock.Jump(stopTime);
            _recording = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
