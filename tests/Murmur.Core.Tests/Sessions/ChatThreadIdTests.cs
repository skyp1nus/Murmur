using Murmur.Core.Intelligence;

namespace Murmur.Core.Tests.Sessions;

public sealed class ChatThreadIdTests
{
    [Fact]
    public void New_MakesDistinctTimeOrderedIds_OfThirtyTwoHexDigits()
    {
        var first = ChatThreadId.New();
        Thread.Sleep(5);
        var second = ChatThreadId.New();

        Assert.NotEqual(first, second);
        Assert.Equal(32, first.Value.Length);
        Assert.True(string.CompareOrdinal(first.Value, second.Value) < 0, "ids sort by creation time");
    }

    [Fact]
    public void ToString_IsTheValue_AndEqualityIsByValue()
    {
        var id = new ChatThreadId("abc");

        Assert.Equal("abc", id.ToString());
        Assert.Equal(id, new ChatThreadId("abc"));
    }
}
