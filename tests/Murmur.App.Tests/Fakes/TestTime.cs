namespace Murmur.App.Tests.Fakes;

/// <summary>
/// A clock that only moves when told to: the wall clock (in UTC, so date maths do not depend on the machine) and the
/// monotonic timestamp both advance only through <see cref="Advance"/>.
/// </summary>
internal sealed class TestTime : TimeProvider
{
    private DateTimeOffset _now;
    private long _timestamp;

    public TestTime(DateTimeOffset now) => _now = now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    /// <summary>Timestamps are counted in <see cref="TimeSpan"/> ticks.</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        _now += by;
        _timestamp += by.Ticks;
    }
}
