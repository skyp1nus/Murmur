using Murmur.Core.Capture;
using Murmur.Core.Capture.Synthetic;

namespace Murmur.Core.Tests.Fakes;

/// <summary>
/// A clock for recorder tests that records "real time" without waiting for it: it only moves on <see cref="Advance"/>, and
/// each move waits until every synthetic audio source has emitted exactly the frames that are due at the new time. The
/// recorder's timeline (wall clock) and the audio the sources delivered are then the same by construction, however
/// starved the machine is, so a test can assert exact values instead of a tolerance that a loaded run eats up.
/// Also compiled into the crash-test helper (internal, so the two copies never clash).
/// </summary>
internal sealed class SampleClock : TimeProvider
{
    private const string SourceThreadPrefix = "Synthetic audio (";

    private readonly ManualTimeProvider _inner = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _sourceStarts = [];

    public override long TimestampFrequency => _inner.TimestampFrequency;

    public override long GetTimestamp()
    {
        var now = _inner.GetTimestamp();
        var name = Thread.CurrentThread.Name;
        if (name is not null && name.StartsWith(SourceThreadPrefix, StringComparison.Ordinal))
        {
            // A source thread reads the clock once as it starts: that is the instant its frame count is measured from.
            lock (_gate)
            {
                _sourceStarts.TryAdd(name, now);
            }
        }

        return now;
    }

    public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        _inner.CreateTimer(callback, state, dueTime, period);

    /// <summary>
    /// Moves the clock by <paramref name="by"/> (in steps no longer than <paramref name="step"/>, so the capture rings
    /// never hold more than a step) and returns once every source of <paramref name="capture"/> has emitted all it owes.
    /// <paramref name="microphoneFaultAfter"/> is the fault point of a microphone created with that option.
    /// </summary>
    public void Advance(TimeSpan by, SyntheticCapture capture, TimeSpan? microphoneFaultAfter = null, TimeSpan? step = null)
    {
        var sources = capture.CreatedSources;
        WaitUntil(() => sources.All(s => StartOf(s) is not null), "the audio sources to start");

        var maxStep = step ?? TimeSpan.FromMilliseconds(50);
        var left = by;
        while (left > TimeSpan.Zero)
        {
            var part = left < maxStep ? left : maxStep;
            _inner.Advance(part);
            left -= part;
            foreach (var source in sources)
            {
                var start = StartOf(source)!.Value;
                var due = (long)(_inner.GetElapsedTime(start, _inner.GetTimestamp()).TotalSeconds * SyntheticSignals.SampleRate * source.SpeedFactor);
                if (microphoneFaultAfter is { } fault && source.Device.Kind == AudioDeviceKind.Microphone)
                {
                    due = Math.Min(due, (long)Math.Round(fault.TotalSeconds * SyntheticSignals.SampleRate));
                }

                WaitUntil(() => source.FramesEmitted >= due, $"the {source.Device.Kind} source to emit {due} frames (has {source.FramesEmitted})");
            }
        }
    }

    /// <summary>Polls until <paramref name="condition"/> holds (real time: the threads under test run in it).</summary>
    public static void WaitUntil(Func<bool> condition, string what, int timeoutSeconds = 60)
    {
        var deadline = Environment.TickCount64 + (timeoutSeconds * 1000L);
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            Thread.Sleep(1);
        }
    }

    private long? StartOf(SyntheticAudioSource source)
    {
        lock (_gate)
        {
            return _sourceStarts.TryGetValue($"{SourceThreadPrefix}{source.Device.Kind})", out var start) ? start : null;
        }
    }
}
