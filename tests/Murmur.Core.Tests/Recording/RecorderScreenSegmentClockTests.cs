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
/// start-up and finalize time must neither shift nor stretch the segment). The slow screen recorder does not sleep: it
/// jumps the recorder's clock, which makes the check exact.
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
        await Task.Delay(150, Ct);
        await recorder.SetScreenEnabledAsync(false, Ct);
        await Task.Delay(150, Ct);
        await recorder.SetScreenEnabledAsync(true, Ct);
        await Task.Delay(150, Ct);
        var finished = await recorder.StopAsync(Ct);

        var screen = Assert.Single(capture.Recorders);
        var calls = screen.Calls;
        Assert.Equal(2, calls.Starts.Count);
        Assert.Equal(2, calls.Stops.Count);
        Assert.Equal(2, finished.ScreenSegments.Count);
        var (first, second) = (finished.ScreenSegments[0], finished.ScreenSegments[1]);

        // Duration = start call to stop call (it includes the slow start, not the slow stop).
        AssertNear(clock.Between(calls.Starts[0], calls.Stops[0]), first.Duration);
        AssertNear(clock.Between(calls.Starts[1], calls.Stops[1]), second.Duration);
        Assert.True(first.Duration >= SlowStart + TimeSpan.FromMilliseconds(100), $"first {first}");

        // Offsets are the session times of the start calls: the distance between them is exactly the distance
        // between the calls (the audio start cancels out), and the first one is the audio start plus a short wait.
        AssertNear(clock.Between(calls.Starts[0], calls.Starts[1]), second.Offset - first.Offset);
        Assert.InRange(first.Offset, TimeSpan.Zero, TimeSpan.FromSeconds(1));
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
            await Task.Delay(60, Ct);
            await recorder.SetScreenEnabledAsync(false, Ct);
            await Task.Delay(60, Ct);
            await recorder.SetScreenEnabledAsync(true, Ct);
        }

        await Task.Delay(60, Ct);
        var finished = await recorder.StopAsync(Ct);

        var calls = Assert.Single(capture.Recorders).Calls;
        Assert.Equal(4, calls.Starts.Count);
        Assert.Equal(4, calls.Stops.Count);
        Assert.Equal(4, finished.ScreenSegments.Count);
        Assert.Equal([0, 1, 2, 3], finished.ScreenSegments.Select(s => s.Index));
        for (var i = 0; i < 4; i++)
        {
            AssertNear(clock.Between(calls.Starts[i], calls.Stops[i]), finished.ScreenSegments[i].Duration);
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
        await Task.Delay(200, Ct);
        var finished = await recorder.StopAsync(Ct);

        var calls = Assert.Single(capture.Recorders).Calls;
        var segment = Assert.Single(finished.ScreenSegments);
        AssertNear(clock.Between(calls.Starts[0], calls.Stops[0]), segment.Duration);
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
        await Task.Delay(150, Ct);
        var finished = await recorder.StopAsync(Ct);

        var calls = Assert.Single(capture.Recorders).Calls;
        var screenStop = Assert.Single(calls.Stops);
        Assert.NotEmpty(capture.AudioStops);
        // The screen stop is called before the audio stop is queued, so it normally precedes every audio Stop(); the
        // margin is only scheduling noise, never the time the audio needs (the old order stopped the screen after the
        // audio: 3+ s).
        Assert.All(capture.AudioStops, audioStop => Assert.True(
            clock.Between(audioStop, screenStop) < TimeSpan.FromSeconds(1),
            $"the screen stop came {clock.Between(audioStop, screenStop)} after an audio source had stopped"));

        var segment = Assert.Single(finished.ScreenSegments);
        AssertNear(clock.Between(calls.Starts[0], screenStop), segment.Duration);
        Assert.True(segment.Duration < audioStopTime, $"segment {segment} includes the audio stop time");
    }

    /// <summary>
    /// The recorder reads the clock a few microseconds before the call (the screen recorder notes it on entry): equal up
    /// to scheduling noise, nowhere near the 450 ms / 700 ms the slow calls take.
    /// </summary>
    private static void AssertNear(TimeSpan expected, TimeSpan actual) =>
        Assert.InRange(actual, expected - TimeSpan.FromMilliseconds(100), expected + TimeSpan.FromMilliseconds(100));

    /// <summary>A clock that runs with the wall clock and can be moved forward at once (a slow call without waiting).</summary>
    private sealed class JumpingTimeProvider : TimeProvider
    {
        private long _jumpTicks;

        public override long TimestampFrequency => System.TimestampFrequency;

        public override long GetTimestamp() => System.GetTimestamp() + Volatile.Read(ref _jumpTicks);

        public void Jump(TimeSpan by) => Interlocked.Add(ref _jumpTicks, (long)(by.TotalSeconds * TimestampFrequency));

        public TimeSpan Between(long from, long to) => GetElapsedTime(from, to);
    }

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
