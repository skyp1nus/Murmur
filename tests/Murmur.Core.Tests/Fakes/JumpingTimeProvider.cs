namespace Murmur.Core.Tests.Fakes;

/// <summary>A clock that stands still until it is jumped (a slow call, or time passing, without waiting for it).</summary>
internal sealed class JumpingTimeProvider : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Volatile.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());

    public void Jump(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public TimeSpan Between(long from, long to) => GetElapsedTime(from, to);
}
