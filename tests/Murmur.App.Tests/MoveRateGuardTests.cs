using Murmur.App.Views;

namespace Murmur.App.Tests;

/// <summary>The guard that keeps the floating orb from moving itself in a loop on mixed-DPI screens (#46).</summary>
public sealed class MoveRateGuardTests
{
    [Fact]
    public void UpToTheLimit_EveryMoveIsAllowed()
    {
        var guard = new MoveRateGuard(4, TimeSpan.FromSeconds(1));

        Assert.All([0L, 10, 20, 30], now => Assert.True(guard.TryEnter(now)));
    }

    [Fact]
    public void OneMoreWithinTheWindow_IsRefused_AndAllowedOnceTheOldestHasLeftIt()
    {
        var guard = new MoveRateGuard(4, TimeSpan.FromSeconds(1));
        foreach (var now in new long[] { 0, 10, 20, 30 })
        {
            guard.TryEnter(now);
        }

        Assert.False(guard.TryEnter(40));
        Assert.False(guard.TryEnter(999));
        Assert.True(guard.TryEnter(1000));
        Assert.False(guard.TryEnter(1001));
        Assert.True(guard.TryEnter(1010));
    }

    [Fact]
    public void ALoopOfMovesEveryFrame_IsCutToTheLimitPerSecond()
    {
        var guard = new MoveRateGuard(4, TimeSpan.FromSeconds(1));
        var allowed = 0;
        for (long now = 0; now < 3000; now += 16)
        {
            if (guard.TryEnter(now))
            {
                allowed++;
            }
        }

        Assert.InRange(allowed, 9, 12);
    }

    [Fact]
    public void RefusedMoves_AreNotCounted()
    {
        var guard = new MoveRateGuard(1, TimeSpan.FromSeconds(1));

        Assert.True(guard.TryEnter(0));
        Assert.False(guard.TryEnter(500));
        Assert.True(guard.TryEnter(1000));
    }
}
