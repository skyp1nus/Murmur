namespace Murmur.Core.Tests.Fakes;

/// <summary>A <see cref="TimeProvider"/> that only moves on <see cref="Advance"/>; due timers fire inside it.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public int ActiveTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _ticks;
        }
    }

    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        long target;
        lock (_gate)
        {
            target = _ticks + by.Ticks;
        }

        while (true)
        {
            ManualTimer? due = null;
            lock (_gate)
            {
                foreach (var t in _timers)
                {
                    if (t.DueTicks <= target && (due is null || t.DueTicks < due.DueTicks))
                    {
                        due = t;
                    }
                }

                if (due is null)
                {
                    _ticks = target;
                    return;
                }

                _ticks = Math.Max(_ticks, due.DueTicks);
                if (due.PeriodTicks > 0)
                {
                    due.DueTicks += due.PeriodTicks;
                }
                else
                {
                    _timers.Remove(due);
                }
            }

            due.Fire();
        }
    }

    private void Schedule(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                return;
            }

            timer.DueTicks = _ticks + Math.Max(0, dueTime.Ticks);
            timer.PeriodTicks = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
            _timers.Add(timer);
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long DueTicks { get; set; }

        public long PeriodTicks { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Schedule(this, dueTime, period);
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
