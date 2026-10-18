using Murmur.Core.Sessions;

namespace Murmur.Core.Tests.Sessions;

public sealed class SessionIdTests
{
    [Fact]
    public void New_CreatesVersion7Guid()
    {
        var id = SessionId.New();

        Assert.Equal(7, id.Value.Version);
        Assert.NotEqual(Guid.Empty, id.Value);
    }

    [Fact]
    public void New_EmbedsCurrentUnixTimestamp()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var id = SessionId.New();
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var timestamp = UnixMilliseconds(id);

        Assert.InRange(timestamp, before, after);
    }

    [Fact]
    public void New_IdsFromLaterMillisecondsSortAfterEarlierOnes()
    {
        var first = SessionId.New();
        var firstMs = UnixMilliseconds(first);

        var second = SessionId.New();
        var spin = new SpinWait();
        while (UnixMilliseconds(second) <= firstMs)
        {
            spin.SpinOnce();
            second = SessionId.New();
        }

        Assert.True(first.Value.CompareTo(second.Value) < 0);
        Assert.True(string.CompareOrdinal(first.ToString(), second.ToString()) < 0);
    }

    [Fact]
    public void New_ReturnsDistinctIds()
    {
        var ids = new HashSet<SessionId>();
        for (var i = 0; i < 1_000; i++)
        {
            Assert.True(ids.Add(SessionId.New()));
        }
    }

    [Fact]
    public void ToString_Is32LowercaseHexDigitsWithoutDashes()
    {
        var text = SessionId.New().ToString();

        Assert.Equal(32, text.Length);
        Assert.All(text, c => Assert.True(char.IsAsciiHexDigitLower(c) || char.IsAsciiDigit(c), $"unexpected '{c}'"));
    }

    [Fact]
    public void Parse_RoundTripsToString()
    {
        var id = SessionId.New();

        var parsed = SessionId.Parse(id.ToString());

        Assert.Equal(id, parsed);
    }

    [Fact]
    public void Parse_RejectsDashedFormat()
    {
        var dashed = SessionId.New().Value.ToString("D");

        Assert.Throws<FormatException>(() => SessionId.Parse(dashed));
    }

    /// <summary>UUIDv7 stores Unix epoch milliseconds in the first 48 bits (big-endian).</summary>
    private static long UnixMilliseconds(SessionId id)
    {
        Span<byte> bytes = stackalloc byte[16];
        Assert.True(id.Value.TryWriteBytes(bytes, bigEndian: true, out _));
        long ms = 0;
        for (var i = 0; i < 6; i++)
        {
            ms = (ms << 8) | bytes[i];
        }

        return ms;
    }
}
