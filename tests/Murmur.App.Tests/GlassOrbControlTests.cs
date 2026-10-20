using Avalonia.Controls;
using Avalonia.Headless;
using Murmur.App.Controls;

namespace Murmur.App.Tests;

public sealed class GlassOrbControlTests
{
    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private sealed class FixedLevel(float level) : ILevelSource
    {
        public float Level { get; set; } = level;
    }

    private static Window Host(GlassOrbControl orb)
    {
        var window = new Window { Width = 500, Height = 500, Content = orb };
        window.Show();
        UiHarness.Pump();
        return window;
    }

    [Fact]
    public async Task Halo_ReachesPastTheOrb_SoTheBlurIsNotClipped()
    {
        var bounds = await Session.Dispatch(
            () =>
            {
                var orb = new GlassOrbControl { Width = 300, Height = 300, HaloBlur = 30, BodyFraction = 0.77 };
                var window = Host(orb);
                var result = orb.HaloBounds;
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(-90, bounds.X);
        Assert.Equal(-90, bounds.Y);
        Assert.Equal(480, bounds.Width);
        Assert.Equal(480, bounds.Height);
    }
}

public sealed class GlassOrbControlBehaviourTests
{
    private static Avalonia.Headless.HeadlessUnitTestSession Session => UiHarness.Session;

    private sealed class FixedLevel(float level) : ILevelSource
    {
        public float Level { get; set; } = level;
    }

    private static Window Host(GlassOrbControl orb)
    {
        var window = new Window { Width = 500, Height = 500, Content = orb };
        window.Show();
        UiHarness.Pump();
        return window;
    }

    [Fact]
    public async Task Idle_ShowsOneStaticFrame_AndRunsNoTimer()
    {
        var (running, body, halo) = await Session.Dispatch(
            () =>
            {
                var orb = new GlassOrbControl { Width = 300, Height = 300, HaloBlur = 30, BodyFraction = 0.77 };
                var window = Host(orb);
                var result = (orb.IsFrameLoopRunning, orb.BodyScale, orb.HaloScale);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.False(running);
        Assert.Equal(1.0, body, 3);
        Assert.Equal(1.0, halo, 3);
    }

    [Fact]
    public async Task Breathing_GrowsTheSphereByFivePercentAndTheHaloBySixteen_AtThePeak()
    {
        var (start, peak, running) = await Session.Dispatch(
            () =>
            {
                var orb = new GlassOrbControl { Width = 300, Height = 300, HaloBlur = 30, BodyFraction = 0.77, IsActive = true };
                var window = Host(orb);
                var clock = new Murmur.App.Tests.Fakes.TestTime(UiHarness.Now);
                orb.Clock = clock;
                orb.Tick();
                var first = (orb.BodyScale, orb.HaloScale);

                // Half a period (1.8 s) in steps of 0.1 s: the breath is at its peak.
                for (var i = 1; i <= 18; i++)
                {
                    orb.Update(TimeSpan.FromSeconds(0.1 * i));
                }

                var result = (first, (orb.BodyScale, orb.HaloScale), orb.IsFrameLoopRunning);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.True(running);
        Assert.Equal(1.0, start.BodyScale, 3);
        Assert.Equal(1.05, peak.BodyScale, 2);
        Assert.Equal(1.16, peak.HaloScale, 2);
    }

    [Fact]
    public async Task WithALevelSource_TheSphereFollowsTheVoice()
    {
        var (quiet, loud) = await Session.Dispatch(
            () =>
            {
                var source = new FixedLevel(0f);
                var orb = new GlassOrbControl { Width = 168, Height = 168, HaloBlur = 20, BodyFraction = 0.78, IsActive = true, LevelSource = source };
                var window = Host(orb);
                orb.Clock = new Murmur.App.Tests.Fakes.TestTime(UiHarness.Now);
                orb.Update(TimeSpan.FromSeconds(100));
                var calm = orb.BodyScale;
                source.Level = 1f;
                for (var i = 1; i <= 40; i++)
                {
                    orb.Update(TimeSpan.FromSeconds(100 + (i * 0.1)));
                }

                var result = (calm, orb.BodyScale);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1.0, quiet, 3);
        Assert.True(loud > 1.08, $"the sphere grew to {loud}");
    }

    [Fact]
    public void SizeProgress_WritesTheSharedUnitOnce()
    {
        Assert.Equal("0.31 / 0.67 GB", Murmur.App.ViewModels.UiFormat.SizeProgress(310_000_000, 670_000_000));
        Assert.Equal("50 MB / 0.67 GB", Murmur.App.ViewModels.UiFormat.SizeProgress(50_000_000, 670_000_000));
    }

    [Theory]
    [InlineData("Gemma 4 26B A4B", "Gemma 4 26B")]
    [InlineData("Gemma 4 E4B", "Gemma 4 E4B")]
    [InlineData("Gemma 4 26B A4B (Q4_K_M)", "Gemma 4 26B A4B (Q4_K_M)")]
    [InlineData("Alpha", "Alpha")]
    [InlineData("Model AB", "Model AB")]
    public void TheAiCard_DropsTheActiveParameterMarker_OnlyAtTheEnd(string catalogName, string expected)
    {
        Assert.Equal(expected, Murmur.App.ViewModels.AiChoiceViewModel.WithoutActiveParameters(catalogName));
    }
}
