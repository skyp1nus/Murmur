using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Activity;

namespace Murmur.App.Tests;

/// <summary>The real <see cref="ActivityFeed"/> (coalescing timer included) feeding the status bar view model, with fake sources.</summary>
public sealed class StatusBarFeedIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the status bar.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ModelDownloadAndRecording_FlowFromTheRealFeed_ToTheBar_AndBackToUpToDate()
    {
        var recorder = new FakeRecorder();
        var models = new FakeModelManager();
        using var feed = new ActivityFeed(recorder, null, null, models, TimeSpan.FromMilliseconds(30));
        var bar = new StatusBarViewModel(feed, recorder, action => action());

        Assert.Equal("Up to date", bar.SummaryText);
        Assert.Empty(bar.Activity.Rows);

        _ = models.InstallAsync(FakeModelManager.ChatLargeId, TestContext.Current.CancellationToken);
        models.Progress(FakeModelManager.ChatLargeId, 42, 100);
        await WaitUntilAsync(() => bar.SummaryText == "Downloading 42%");
        Assert.Single(bar.Activity.Rows);

        recorder.ForceRecording(Sessions.Make("Standup", DateTimeOffset.UtcNow, TimeSpan.Zero));
        await WaitUntilAsync(() => bar.IsRecording);
        Assert.Equal(2, bar.Activity.Rows.Count);

        models.Complete(FakeModelManager.ChatLargeId);
        recorder.FailBySelf();
        await WaitUntilAsync(() => bar.IsUpToDate && !bar.IsRecording);
        Assert.Equal("Up to date", bar.SummaryText);
        Assert.Empty(bar.Activity.Rows);
        Assert.True(bar.Activity.IsEmpty);
    }

    [Fact]
    public async Task FastDownloadProgress_ReachesTheBarCoalesced()
    {
        var recorder = new FakeRecorder();
        var models = new FakeModelManager();
        using var feed = new ActivityFeed(recorder, null, null, models);
        var bar = new StatusBarViewModel(feed, recorder, action => action());
        var changes = 0;
        feed.Changed += () => Interlocked.Increment(ref changes);

        _ = models.InstallAsync(FakeModelManager.ChatLargeId, TestContext.Current.CancellationToken);
        for (var i = 1; i <= 100; i++)
        {
            models.Progress(FakeModelManager.ChatLargeId, i, 100);
        }

        await Task.Delay(1100, TestContext.Current.CancellationToken);

        Assert.InRange(Volatile.Read(ref changes), 1, 3);
        Assert.Equal("Downloading 100%", bar.SummaryText);
    }
}
