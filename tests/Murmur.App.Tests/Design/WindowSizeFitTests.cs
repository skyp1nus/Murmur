using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace Murmur.App.Tests.Design;

/// <summary>
/// Every screen of the design (both themes) in the smallest window (900 x 600) and in the default one (1100 x 720): the primary controls
/// stay inside the window, none overlaps another, nothing scrolls sideways, and what is taller than the window scrolls.
/// PNGs go to <c>MURMUR_SIZEFIT_DIR</c> (default: the temp folder, murmur-sizefit).
/// </summary>
public sealed class WindowSizeFitTests
{
    private static readonly string[] Boards =
    [
        "Main", "Recording", "Session", "Chat", "Settings",
        "Onb1Welcome", "Onb2Audio", "Onb3Models", "Onb3ModelsReady", "Onb4Preferences", "Onb5FirstRecording",
    ];

    public static TheoryData<string, bool, int, int> Cases()
    {
        var data = new TheoryData<string, bool, int, int>();
        foreach (var name in Boards)
        {
            foreach (var dark in new[] { false, true })
            {
                data.Add(name, dark, 900, 600);
                data.Add(name, dark, 1100, 720);
            }
        }

        return data;
    }

    private static Rect InWindow(Visual visual, Visual window) => new(visual.TranslatePoint(default, window) ?? default, visual.Bounds.Size);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Screen_FitsTheWindow_WithoutClippingOrOverlap(string name, bool dark, int width, int height)
    {
        var board = DesignBoards.Get(name + (dark ? "Dark" : "")) with { Width = width, Height = height };
        var dir = Environment.GetEnvironmentVariable("MURMUR_SIZEFIT_DIR") is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "murmur-sizefit");
        var png = Path.Combine(dir, $"{board.Name}-{width}x{height}.png");

        var problems = await UiHarness.Session.Dispatch(
            async () =>
            {
                var found = new List<string>();
                using var scene = await DesignScene.BuildAsync(board);
                var window = scene.Window;
                UiHarness.Save(window, png);

                var scrollers = window.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.IsEffectivelyVisible).ToArray();
                foreach (var scroller in scrollers)
                {
                    if (scroller.Extent.Width > scroller.Viewport.Width + 0.5)
                    {
                        found.Add($"{scroller.Name ?? "ScrollViewer"} scrolls sideways: {scroller.Extent.Width:0} in {scroller.Viewport.Width:0}");
                    }

                    if (scroller.Extent.Height > scroller.Viewport.Height + 1)
                    {
                        scroller.Offset = new Vector(0, scroller.Extent.Height);
                        UiHarness.Pump(2);
                        if (scroller.Offset.Y <= 0)
                        {
                            found.Add($"{scroller.Name ?? "ScrollViewer"} holds {scroller.Extent.Height:0} px in {scroller.Viewport.Height:0} px but does not scroll");
                        }

                        scroller.Offset = default;
                    }
                }

                // Primary controls that do not scroll away must be fully inside the window.
                var controls = window.GetVisualDescendants()
                    .OfType<Control>()
                    .Where(c => (c is Button || c is TextBox || c is ToggleButton) && c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0)
                    .Where(c => c.FindAncestorOfType<ScrollViewer>() is null)
                    .ToArray();
                foreach (var c in controls)
                {
                    var r = InWindow(c, window);
                    if (r.Left < -0.5 || r.Top < -0.5 || r.Right > width + 0.5 || r.Bottom > height + 0.5)
                    {
                        found.Add($"{c.GetType().Name} '{c.Name}' at {r} leaves the window");
                    }
                }

                // Text outside the scrollers must be inside the window too.
                foreach (var t in window.GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text) && t.FindAncestorOfType<ScrollViewer>() is null))
                {
                    var r = InWindow(t, window);
                    if (r.Right > width + 0.5 || r.Bottom > height + 0.5 || r.Left < -0.5 || r.Top < -0.5)
                    {
                        found.Add($"text '{t.Text}' at {r} leaves the window");
                    }
                }

                // Controls of the same scroll context do not overlap (nested controls are not counted).
                var all = window.GetVisualDescendants().OfType<Control>()
                    .Where(c => (c is Button || c is TextBox) && c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0)
                    .ToArray();
                for (var i = 0; i < all.Length; i++)
                {
                    for (var j = i + 1; j < all.Length; j++)
                    {
                        if (all[i].FindAncestorOfType<ScrollViewer>() != all[j].FindAncestorOfType<ScrollViewer>()
                            || all[i].IsVisualAncestorOf(all[j]) || all[j].IsVisualAncestorOf(all[i]))
                        {
                            continue;
                        }

                        var overlap = InWindow(all[i], window).Intersect(InWindow(all[j], window));
                        if (overlap.Width > 1 && overlap.Height > 1)
                        {
                            found.Add($"{all[i].GetType().Name} '{all[i].Name}' overlaps {all[j].GetType().Name} '{all[j].Name}' by {overlap}");
                        }
                    }
                }

                return found;
            },
            TestContext.Current.CancellationToken);

        Assert.True(File.Exists(png) && new FileInfo(png).Length > 5_000, "Screenshot looks empty.");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
