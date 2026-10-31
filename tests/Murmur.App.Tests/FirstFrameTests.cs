using Avalonia.Controls;
using Avalonia.Headless;
using Murmur.App.Services;

namespace Murmur.App.Tests;

/// <summary>Start-up work that the first frame does not need runs after that frame, never from the window's Opened event.</summary>
public sealed class FirstFrameTests
{
    private static HeadlessUnitTestSession Session => UiHarness.Session;

    [Fact]
    public async Task Run_IsNotCalledBeforeTheWindowOpens_NorFromTheOpenedEvent_ButAfterTheFrames()
    {
        var (beforeShow, atOpened, afterShow, count) = await Session.Dispatch(
            () =>
            {
                var window = new Window { Width = 300, Height = 300, Content = new Border() };
                var runs = 0;
                var atOpenedRuns = -1;
                FirstFrame.Run(window, () => runs++);
                window.Opened += (_, _) => atOpenedRuns = runs;

                var early = runs;
                window.Show();
                UiHarness.Pump(10);
                var result = (early, atOpenedRuns, runs, runs);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, beforeShow);
        Assert.Equal(0, atOpened);
        Assert.Equal(1, afterShow);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Run_RunsTheWorkOnce_EvenWhenMoreFramesFollow()
    {
        var runs = await Session.Dispatch(
            () =>
            {
                var window = new Window { Width = 300, Height = 300, Content = new Border() };
                var count = 0;
                FirstFrame.Run(window, () => count++);
                window.Show();
                UiHarness.Pump(30);
                window.Close();
                return count;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Run_NeverRunsTheWork_WhileTheWindowIsNeverShown()
    {
        var runs = await Session.Dispatch(
            () =>
            {
                var window = new Window { Width = 300, Height = 300, Content = new Border() };
                var count = 0;
                FirstFrame.Run(window, () => count++);
                UiHarness.Pump(10);
                return count;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, runs);
    }
}
