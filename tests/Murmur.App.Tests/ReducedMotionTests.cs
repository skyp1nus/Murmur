using Avalonia.Controls;
using Avalonia.Headless;
using Murmur.App.Controls;
using Murmur.App.Services;
using Murmur.Core.Platform;

namespace Murmur.App.Tests;

/// <summary>Decorative animation holds still under the system's reduce-motion setting; meters keep updating.</summary>
public sealed class ReducedMotionTests
{
    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private sealed class FixedLevel(float level) : ILevelSource
    {
        public float Level { get; set; } = level;
    }

    private sealed class FakePreference(bool reduce) : IMotionPreference
    {
        public bool ReduceMotion { get; set; } = reduce;

        public event Action? Changed;

        public void Raise() => Changed?.Invoke();

        public bool HasSubscribers => Changed is not null;
    }

    private static Window Host(Control content, bool reduce)
    {
        var window = new Window { Width = 500, Height = 500, Content = content };
        Motion.SetReduceMotion(window, reduce);
        window.Show();
        UiHarness.Pump();
        return window;
    }

    // ---- GlassOrbControl ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlassOrb_Reduced_RunsNoTimer_AndShowsTheRestFrame(bool withLevelSource)
    {
        var (running, body, halo, frames) = await Session.Dispatch(
            () =>
            {
                var orb = new GlassOrbControl
                {
                    Width = 300,
                    Height = 300,
                    HaloBlur = 30,
                    IsActive = true,
                    LevelSource = withLevelSource ? new FixedLevel(0.9f) : null,
                };
                var window = Host(orb, reduce: true);
                var before = orb.AppliedFrames;
                UiHarness.Pump(10);

                // Even a direct step (what the timer would do) changes nothing.
                orb.Update(TimeSpan.FromSeconds(1));
                orb.Update(TimeSpan.FromSeconds(2));
                var result = (orb.IsFrameLoopRunning, orb.BodyScale, orb.HaloScale, orb.AppliedFrames - before);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.False(running);
        Assert.Equal(1.0, body, 6);
        Assert.Equal(1.0, halo, 6);
        Assert.Equal(0, frames);
    }

    [Fact]
    public async Task GlassOrb_TogglingAtRuntime_StopsAndRestartsTheAnimation()
    {
        var (before, reduced, restScale, after, afterScale) = await Session.Dispatch(
            () =>
            {
                var orb = new GlassOrbControl { Width = 300, Height = 300, HaloBlur = 30, IsActive = true };
                var window = Host(orb, reduce: false);
                var wasRunning = orb.IsFrameLoopRunning;

                // Breathe to the peak, then the user turns animation effects off.
                for (var i = 1; i <= 18; i++)
                {
                    orb.Update(TimeSpan.FromSeconds(0.1 * i));
                }

                var peak = orb.BodyScale;
                Motion.SetReduceMotion(window, true);
                var stopped = !orb.IsFrameLoopRunning;
                var rest = orb.BodyScale;

                // ... and on again: the timer runs and the orb breathes from rest.
                Motion.SetReduceMotion(window, false);
                var restarted = orb.IsFrameLoopRunning;
                orb.Update(TimeSpan.FromSeconds(100));
                for (var i = 1; i <= 18; i++)
                {
                    orb.Update(TimeSpan.FromSeconds(100 + (0.1 * i)));
                }

                var result = (wasRunning && peak > 1.04, stopped, rest, restarted, orb.BodyScale);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.True(before);
        Assert.True(reduced);
        Assert.Equal(1.0, restScale, 6);
        Assert.True(after);
        Assert.True(afterScale > 1.04, $"Body scale {afterScale} should breathe again.");
    }

    [Fact]
    public async Task GlassOrb_ActivatedWhileReduced_StaysStill_UntilMotionIsAllowed()
    {
        var (whileReduced, afterwards) = await Session.Dispatch(
            () =>
            {
                var orb = new GlassOrbControl { Width = 300, Height = 300 };
                var window = Host(orb, reduce: true);
                orb.IsActive = true;
                var still = !orb.IsFrameLoopRunning;
                Motion.SetReduceMotion(window, false);
                var result = (still, orb.IsFrameLoopRunning);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.True(whileReduced);
        Assert.True(afterwards);
    }

    // ---- OrbControl ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Orb_Reduced_RunsNoTimer_AndShowsTheStaticFrame()
    {
        var (running, smoothed, scale, frames) = await Session.Dispatch(
            () =>
            {
                var orb = new OrbControl { LevelSource = new FixedLevel(0.9f), IsActive = true };
                var window = Host(orb, reduce: true);
                var before = orb.AppliedFrames;
                UiHarness.Pump(10);
                orb.Update(TimeSpan.FromSeconds(1000));
                orb.Update(TimeSpan.FromSeconds(1001));
                var result = (orb.IsFrameLoopRunning, orb.SmoothedLevel, orb.BodyScale, orb.AppliedFrames - before);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.False(running);
        Assert.Equal(0, smoothed);
        Assert.Equal(0.56, scale, 3);
        Assert.Equal(0, frames);
    }

    [Fact]
    public async Task Orb_TogglingAtRuntime_StopsAndRestartsTheAnimation()
    {
        var (started, stopped, rest, restarted, grown) = await Session.Dispatch(
            () =>
            {
                var orb = new OrbControl { LevelSource = new FixedLevel(0.64f), IsActive = true };
                var window = Host(orb, reduce: false);
                var wasRunning = orb.IsFrameLoopRunning;
                orb.Update(TimeSpan.FromSeconds(1000));
                for (var i = 1; i <= 40; i++)
                {
                    orb.Update(TimeSpan.FromSeconds(1000 + (i * 0.05)));
                }

                Motion.SetReduceMotion(window, true);
                var isStopped = !orb.IsFrameLoopRunning;
                var restScale = orb.BodyScale;
                Motion.SetReduceMotion(window, false);
                var isRestarted = orb.IsFrameLoopRunning;
                orb.Update(TimeSpan.FromSeconds(2000));
                for (var i = 1; i <= 40; i++)
                {
                    orb.Update(TimeSpan.FromSeconds(2000 + (i * 0.05)));
                }

                var result = (wasRunning, isStopped, restScale, isRestarted, orb.BodyScale);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.True(started);
        Assert.True(stopped);
        Assert.Equal(0.56, rest, 3);
        Assert.True(restarted);
        Assert.True(grown > 0.56 * 1.1);
    }

    // ---- meters keep updating ------------------------------------------------------------------------------------------

    [Fact]
    public async Task LevelMeter_Reduced_StillPollsAndFollowsTheLevel()
    {
        var (polling, last) = await Session.Dispatch(
            () =>
            {
                var meter = new LevelMeterControl { Width = 200, Height = 24, LevelSource = new FixedLevel(0.8f), IsActive = true };
                var window = Host(meter, reduce: true);
                meter.Tick();
                meter.Tick();
                var levels = meter.Levels;
                var result = (meter.IsPolling, levels[^1]);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.True(polling);
        Assert.True(last > 0.1f, $"The meter should follow the level, got {last}.");
    }

    // ---- MotionFollower -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Follower_AppliesTheCurrentValue_AndEveryChangeOnThePostedThread()
    {
        var (initial, queued, afterPost, afterOff, detached) = await Session.Dispatch(
            () =>
            {
                var window = new Window();
                var preference = new FakePreference(reduce: true);
                var posted = new List<Action>();
                var follower = MotionFollower.Follow(window, preference, posted.Add);
                var first = Motion.GetReduceMotion(window);

                // The change is raised on some other thread: nothing is applied until the post runs on the UI thread.
                preference.ReduceMotion = false;
                preference.Raise();
                var notYet = Motion.GetReduceMotion(window);
                posted[0]();
                var applied = Motion.GetReduceMotion(window);

                preference.ReduceMotion = true;
                preference.Raise();
                posted[1]();
                var again = Motion.GetReduceMotion(window);

                follower.Dispose();
                var gone = !preference.HasSubscribers;
                return (first, notYet, applied, again, gone);
            },
            TestContext.Current.CancellationToken);

        Assert.True(initial);
        Assert.True(queued);
        Assert.False(afterPost);
        Assert.True(afterOff);
        Assert.True(detached);
    }

    [Fact]
    public async Task Follower_ReachesTheOrbsBelowTheWindow()
    {
        var running = await Session.Dispatch(
            () =>
            {
                var orb = new GlassOrbControl { Width = 300, Height = 300, IsActive = true };
                var window = Host(new Border { Child = orb }, reduce: false);
                var preference = new FakePreference(reduce: true);
                using var follower = MotionFollower.Follow(window, preference, static action => action());
                var stopped = !orb.IsFrameLoopRunning;
                preference.ReduceMotion = false;
                preference.Raise();
                var result = (stopped, orb.IsFrameLoopRunning);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.True(running.stopped);
        Assert.True(running.Item2);
    }

    [Fact]
    public void FullMotionPreference_NeverReducesMotion()
    {
        var preference = new FullMotionPreference();
        var raised = false;
        preference.Changed += () => raised = true;

        Assert.False(preference.ReduceMotion);
        Assert.False(raised);
    }

    [Fact]
    public async Task Follower_APostQueuedBeforeDispose_IsANoOpWhenItRunsAfterwards()
    {
        var value = await Session.Dispatch(
            () =>
            {
                var window = new Window();
                var preference = new FakePreference(reduce: false);
                var posted = new List<Action>();
                var follower = MotionFollower.Follow(window, preference, posted.Add);

                preference.ReduceMotion = true;
                preference.Raise();
                follower.Dispose();
                posted[0]();
                return Motion.GetReduceMotion(window);
            },
            TestContext.Current.CancellationToken);

        Assert.False(value);
    }

    [Fact]
    public async Task Attach_SetsTheValueAtOnce_AndSubscribesOnlyAfterTheFirstFrame()
    {
        var (initial, beforeShow, afterShow, afterClose) = await Session.Dispatch(
            () =>
            {
                var window = new Window { Width = 300, Height = 300, Content = new Border() };
                var preference = new FakePreference(reduce: true);

                MotionFollower.Attach(window, preference, static action => action());
                var first = Motion.GetReduceMotion(window);
                var early = preference.HasSubscribers;

                window.Show();
                UiHarness.Pump(10);
                var late = preference.HasSubscribers;

                window.Close();
                return (first, early, late, preference.HasSubscribers);
            },
            TestContext.Current.CancellationToken);

        Assert.True(initial);
        Assert.False(beforeShow);
        Assert.True(afterShow);
        Assert.False(afterClose);
    }

    [Fact]
    public async Task Attach_FollowsLaterChanges_OnceSubscribed()
    {
        var (reduced, allowed) = await Session.Dispatch(
            () =>
            {
                var orb = new GlassOrbControl { Width = 300, Height = 300, IsActive = true };
                var window = new Window { Width = 400, Height = 400, Content = orb };
                var preference = new FakePreference(reduce: false);
                MotionFollower.Attach(window, preference, static action => action());
                window.Show();
                UiHarness.Pump(10);

                preference.ReduceMotion = true;
                preference.Raise();
                var stopped = !orb.IsFrameLoopRunning;
                preference.ReduceMotion = false;
                preference.Raise();
                var result = (stopped, orb.IsFrameLoopRunning);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.True(reduced);
        Assert.True(allowed);
    }

    // ---- easing back in ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GlassOrb_MotionAllowedAgainMidVoice_EasesInFromRest()
    {
        var (first, later) = await Session.Dispatch(
            () =>
            {
                var orb = new GlassOrbControl { Width = 300, Height = 300, HaloBlur = 30, IsActive = true, LevelSource = new FixedLevel(0.64f) };
                var window = Host(orb, reduce: true);
                Motion.SetReduceMotion(window, false);
                orb.Update(TimeSpan.FromSeconds(100));
                var atStart = orb.BodyScale;
                for (var i = 1; i <= 40; i++)
                {
                    orb.Update(TimeSpan.FromSeconds(100 + (i * 0.1)));
                }

                var result = (atStart, orb.BodyScale);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1.0, first, 3);
        Assert.True(later > 1.05, $"The orb should follow the voice after the ease-in, got {later}.");
    }

    [Fact]
    public async Task Orb_MotionAllowedAgainMidVoice_EasesInFromRest()
    {
        var (first, later) = await Session.Dispatch(
            () =>
            {
                var orb = new OrbControl { LevelSource = new FixedLevel(0.64f), IsActive = true };
                var window = Host(orb, reduce: true);
                Motion.SetReduceMotion(window, false);
                orb.Update(TimeSpan.FromSeconds(1000));
                var atStart = orb.BodyScale;
                for (var i = 1; i <= 40; i++)
                {
                    orb.Update(TimeSpan.FromSeconds(1000 + (i * 0.05)));
                }

                var result = (atStart, orb.BodyScale);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0.56, first, 3);
        Assert.True(later > 0.56 * 1.1);
    }
}
