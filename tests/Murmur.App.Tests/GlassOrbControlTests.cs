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
