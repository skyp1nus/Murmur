namespace Murmur.App.Tests.Fakes;

/// <summary>
/// A clock that only moves when told to: the wall clock (in UTC, so date maths do not depend on the machine) and the
/// monotonic timestamp both advance only through <see cref="Advance"/>. Timers (and so <c>Task.Delay(…, time)</c>)
/// fire inside <see cref="Advance"/>, on the caller's thread, once their due time is reached.
/// </summary>
internal sealed class TestTime : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now;
    private long _timestamp;

    public TestTime(DateTimeOffset now) => _now = now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    /// <summary>Timestamps are counted in <see cref="TimeSpan"/> ticks.</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Timers created and not yet disposed.</summary>
    public int ActiveTimers
    {
        get
        {
            lock (_lock)
            {
                return _timers.Count;
            }
        }
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _timestamp;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_lock)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        _now += by;
        _timestamp += by.Ticks;
        FireDue();
    }

    private void FireDue()
    {
        while (true)
        {
            ManualTimer? due = null;
            lock (_lock)
            {
                foreach (var timer in _timers)
                {
                    if (timer.Due is { } at && at <= _timestamp && (due is null || at < due.Due))
                    {
                        due = timer;
                    }
                }

                if (due is null)
                {
                    return;
                }

                due.Due = due.Period > TimeSpan.Zero ? due.Due + due.Period.Ticks : null;
            }

            due.Fire();
        }
    }

    private sealed class ManualTimer(TestTime owner, TimerCallback callback, object? state) : ITimer
    {
        public long? Due { get; set; }

        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._timestamp + dueTime.Ticks;
            }

            if (dueTime == TimeSpan.Zero)
            {
                owner.FireDue();
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._lock)
            {
                Due = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
