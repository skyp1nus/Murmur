using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.VisualTree;
using Murmur.App.Controls;
using Murmur.App.Views;

namespace Murmur.App.Tests;

/// <summary>Every onboarding step in the smallest window (900 x 600): the compact layout, the footer, no overlap and no clipped halo.</summary>
public sealed class OnboardingMinimumSizeTests
{
    private const int MinWidth = 900;
    private const int MinHeight = 600;

    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private sealed record Result(
        bool Compact,
        Rect Footer,
        double ContentExtent,
        double ContentViewport,
        double ContentWidthExtent,
        double ContentWidthViewport,
        IReadOnlyList<(string Text, Rect Bounds)> Texts);

    private static Rect InWindow(Visual visual, Visual window)
    {
        var origin = visual.TranslatePoint(default, window) ?? default;
        return new Rect(origin, visual.Bounds.Size);
    }

    private static async Task<Result> MeasureAsync(int step)
    {
        return await Session.Dispatch(
            async () =>
            {
                var rig = new OnboardingRig();
                var window = new MainWindow { DataContext = rig.Shell, Width = MinWidth, Height = MinHeight };
                window.Show();
                UiHarness.Pump(6);
                if (step > 1)
                {
                    await rig.GoToStepAsync(step);
                    await rig.Onboarding.Audio.Settled;
                }

                UiHarness.Pump(10);
                var view = window.GetVisualDescendants().OfType<OnboardingView>().Single();
                var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First();
                var footer = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ContinueButton");
                var texts = window.GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text))
                    .Select(t => (t.Text!, InWindow(t, window)))
                    .ToArray();
                var result = new Result(
                    view.Classes.Contains("compact"),
                    InWindow(footer, window),
                    scroll.Extent.Height,
                    scroll.Viewport.Height,
                    scroll.Extent.Width,
                    scroll.Viewport.Width,
                    texts);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task EveryStep_FitsTheSmallestWindow_WithoutScrollingOrOverlap(int step)
    {
        var result = await MeasureAsync(step);

        Assert.True(result.Compact, "below 720 px of height the page is compact");

        // The footer button is inside the window, above the bottom margin.
        Assert.True(result.Footer.Bottom <= MinHeight, $"the footer button ends at {result.Footer.Bottom}");
        Assert.True(result.Footer.Right <= MinWidth);
        Assert.True(result.Footer.Width > 0 && result.Footer.Height > 0);

        // Nothing needs scrolling.
        Assert.True(result.ContentExtent <= result.ContentViewport + 1, $"step {step}: {result.ContentExtent} px of content in {result.ContentViewport} px");
        Assert.True(result.ContentWidthExtent <= result.ContentWidthViewport + 1);

        // No two texts overlap.
        for (var i = 0; i < result.Texts.Count; i++)
        {
            for (var j = i + 1; j < result.Texts.Count; j++)
            {
                var overlap = result.Texts[i].Bounds.Intersect(result.Texts[j].Bounds);
                Assert.False(
                    overlap.Width > 1 && overlap.Height > 1,
                    $"step {step}: \"{result.Texts[i].Text}\" overlaps \"{result.Texts[j].Text}\"");
            }
        }

        // Every text is inside the window.
        foreach (var (text, bounds) in result.Texts)
        {
            Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= MinWidth && bounds.Bottom <= MinHeight, $"step {step}: \"{text}\" is at {bounds}");
        }
    }

    [Fact]
    public async Task TheWelcomeHalo_IsNotCutOffByTheScrollArea()
    {
        // Compare the two pixel rows on either side of the top edge of the scroll area, in the middle of the orb: a clipped halo
        // would jump there; a halo that has faded out by then does not (the compact orb is sized so that it has: at most 2 levels
        // per channel, summed over three channels).
        var (jump, edge) = await Session.Dispatch(
            () =>
            {
                var rig = new OnboardingRig();
                var window = new MainWindow { DataContext = rig.Shell, Width = MinWidth, Height = MinHeight };
                window.Show();
                UiHarness.Pump(10);
                var view = window.GetVisualDescendants().OfType<OnboardingView>().Single();
                var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First();
                var orb = view.GetVisualDescendants().OfType<GlassOrbControl>().Last();
                var orbBounds = InWindow(orb, window);
                var top = (int)Math.Round(InWindow(scroll, window).Top);
                var x = (int)Math.Round(orbBounds.Center.X);

                using var frame = window.CaptureRenderedFrame();
                using var buffer = frame!.Lock();
                int Pixel(int px, int py)
                {
                    var bytes = new byte[4];
                    System.Runtime.InteropServices.Marshal.Copy(buffer.Address + (py * buffer.RowBytes) + (px * 4), bytes, 0, 4);
                    return bytes[0] + bytes[1] + bytes[2];
                }

                var result = (Math.Abs(Pixel(x, top - 1) - Pixel(x, top)), top);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.True(jump <= 6, $"the halo jumps by {jump} at y={edge}, where the scroll area starts");
    }

    [Theory]
    [InlineData(123_456_000_000L, true)]
    [InlineData(long.MaxValue, false)]
    public async Task TheModelsStep_ShowsTheFreeSpaceInATooltip_OnlyWhenTheDriveCanBeRead(long freeBytes, bool expected)
    {
        (bool Shown, string? Tip) shown = await Session.Dispatch(
            async () =>
            {
                using var scope = Murmur.App.Localization.UiCulture.Override("en");
                var rig = new OnboardingRig();
                rig.Manager.AvailableFreeSpaceBytes = freeBytes;
                var window = new MainWindow { DataContext = rig.Shell, Width = MinWidth, Height = MinHeight };
                window.Show();
                UiHarness.Pump(6);
                await rig.GoToStepAsync(3);
                UiHarness.Pump(10);
                var note = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "BackgroundNote");
                var result = (ToolTip.GetTip(note) is string text, ToolTip.GetTip(note) as string);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, shown.Shown);
        if (expected)
        {
            Assert.Contains("123", shown.Tip, StringComparison.Ordinal);
        }
    }
}
