using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Murmur.App.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Murmur.App.Tests.Design;

/// <summary>The text side of the parity gate: reading the app's text lines, matching them with the canvas and the overlay.</summary>
[Trait("Category", "DesignParity")]
public sealed class TextGeometryTests
{
    private static TextItem Item(string text, double x, double centerY, double width = 100, double size = 14, int weight = 400, bool mono = false, int lines = 1, bool placeholder = false, string blockText = "", int blockId = -1, double? baseline = null)
    {
        // A 17 px line box whose baseline is 13 px below its top.
        var boxes = Enumerable.Range(0, lines).Select(i => new LineBox(x, centerY - 8.5 + (i * 17), width / lines, 17, placeholder ? double.NaN : baseline is { } b ? b + (i * 17) : centerY + 4.5 + (i * 17))).ToArray();
        return new TextItem(text, boxes, size, weight, mono, "test", placeholder, blockId, blockText);
    }

    private static Window Show(Control content, double width = 400, double height = 300)
    {
        var window = new Window
        {
            Width = width,
            Height = height,
            CanResize = false,
            Content = new Canvas { Background = Brushes.White, Children = { content } },
            RequestedThemeVariant = ThemeVariant.Light,
        };
        TextOptions.SetTextRenderingMode(window, TextRenderingMode.Antialias);
        window.Show();
        UiHarness.Pump(4);
        return window;
    }

    [Fact]
    public async Task AppTexts_ReadsEveryLineOfATextBlock_WhereItIsDrawn()
    {
        var (items, ink) = await UiHarness.Session.Dispatch(
            () =>
            {
                var single = new TextBlock { Text = "  Client   demo\n— Q4 ", FontFamily = new FontFamily("fonts:Inter#Inter"), FontSize = 15, FontWeight = FontWeight.Medium };
                var wrapped = new TextBlock { Text = "A long paragraph that has to wrap over several lines in a narrow column.", Width = 120, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
                var hidden = new TextBlock { Text = "Hidden", IsVisible = false };
                var mono = new TextBlock { Text = "47:12", FontFamily = new FontFamily("avares://Murmur.App/Assets/Fonts#Geist Mono"), FontSize = 13 };
                var canvas = new Canvas { Background = Brushes.White, Width = 400, Height = 300 };
                Canvas.SetLeft(single, 30);
                Canvas.SetTop(single, 40);
                Canvas.SetLeft(wrapped, 30);
                Canvas.SetTop(wrapped, 120);
                Canvas.SetLeft(mono, 250);
                Canvas.SetTop(mono, 40);
                canvas.Children.Add(single);
                canvas.Children.Add(wrapped);
                canvas.Children.Add(hidden);
                canvas.Children.Add(mono);
                var window = new Window { Width = 400, Height = 300, CanResize = false, Content = canvas, RequestedThemeVariant = ThemeVariant.Light };
                TextOptions.SetTextRenderingMode(window, TextRenderingMode.Antialias);
                window.Show();
                UiHarness.Pump(4);
                var texts = AppTexts.Collect(window);
                var frame = Capture(window);
                window.Close();
                return (texts, frame);
            },
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(items, t => t.Text == "Hidden");
        var title = items.Single(t => t.Text == "Client demo — Q4");
        Assert.Equal(2, title.Lines.Count);
        Assert.Equal((15, 500, false), (title.FontSize, title.FontWeight, title.Mono));
        Assert.Equal(30, title.First.X, 1);
        Assert.Equal(40, title.First.Y, 1);
        var wrapped = items.Single(t => t.Text.StartsWith("A long paragraph", StringComparison.Ordinal));
        Assert.True(wrapped.Lines.Count >= 3, "the paragraph wraps");
        Assert.All(wrapped.Lines, l => Assert.True(l.W <= 120.5));
        Assert.True(items.Single(t => t.Text == "47:12").Mono);

        // The reported first line contains the ink of the glyphs that were really drawn.
        var (left, top, right, bottom) = InkBox(ink, 20, 30, 200, 80);
        Assert.InRange(left, title.First.X - 2, title.First.X + 3);
        Assert.InRange(top, title.First.Y - 1, title.First.CenterY);
        Assert.InRange(bottom, title.Lines[^1].CenterY, title.Lines[^1].Bottom + 1);
        Assert.InRange(right, title.Lines.Max(l => l.Right) - 4, title.Lines.Max(l => l.Right) + 2);
    }

    [Fact]
    public async Task AppTexts_FindsTheTextOfAStretchedBlockAndAPlaceholderWhereTheyAreDrawn()
    {
        var (items, ink) = await UiHarness.Session.Dispatch(
            () =>
            {
                var centered = new TextBlock { Text = "Centered", FontSize = 14, Height = 60, Width = 200, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, Padding = new Thickness(0, 21, 0, 0) };
                var box = new TextBox { Width = 200, PlaceholderText = "Search titles and text", FontSize = 14 };
                var panel = new StackPanel { Width = 200, Spacing = 20 };
                panel.Children.Add(centered);
                panel.Children.Add(box);
                var window = Show(panel);
                var texts = AppTexts.Collect(window);
                var frame = Capture(window);
                window.Close();
                return (texts, frame);
            },
            TestContext.Current.CancellationToken);

        var centeredItem = items.Single(t => t.Text == "Centered");
        var (_, top, _, bottom) = InkBox(ink, 0, 0, 400, 60);
        Assert.InRange(top, centeredItem.First.Y - 1, centeredItem.First.Bottom);
        Assert.InRange(bottom, centeredItem.First.Y, centeredItem.First.Bottom + 1);
        Assert.Contains(items, t => t.Text == "Search titles and text");
    }

    [Fact]
    public async Task HideText_MakesEveryGlyphInvisible_AndKeepsTheShapes()
    {
        var frames = await UiHarness.Session.Dispatch(
            () =>
            {
                var panel = new StackPanel { Width = 300, Spacing = 8, Background = Brushes.White };
                panel.Children.Add(new TextBlock { Text = "A text block", FontSize = 20 });
                panel.Children.Add(new TextBox { Text = "Typed text", Width = 200 });
                panel.Children.Add(new Button { Content = "A button" });
                panel.Children.Add(new CheckBox { Content = "A check box" });
                panel.Children.Add(new Border { Width = 100, Height = 10, Background = Brushes.Red });
                var window = Show(panel);
                var before = Capture(window);
                DesignScene.HideText(window);
                UiHarness.Pump(4);
                var after = Capture(window);
                window.Close();
                return (before, after);
            },
            TestContext.Current.CancellationToken);

        var (before, after) = frames;
        Assert.True(Count(before, p => p.R < 80 && p.G < 80 && p.B < 80) > 100, "the text was drawn dark before");
        Assert.Equal(0, Count(after, p => p.R < 80 && p.G < 80 && p.B < 80));
        Assert.True(Count(after, p => p.R > 200 && p.G < 60 && p.B < 60) >= 100 * 10, "the red shape stays");
    }

    [Fact]
    public void TheMatcher_PairsDuplicatesByNearestPosition_AndListsMissingAndExtra()
    {
        var reference = new[]
        {
            Item("Library", 56, 162),
            Item("Library", 273, 30),
            Item("Only in canvas", 400, 400),
        };
        var app = new[]
        {
            Item("Library", 270, 33),
            Item("Library", 60, 170),
            Item("Only in app", 10, 10),
            Item("library", 56, 162),
        };

        var result = TextMatcher.Compare(reference, app);

        Assert.Equal(2, result.Matches.Count);
        Assert.Equal(270, result.Matches.Single(m => m.Reference.First.X == 273).App.First.X);
        Assert.Equal(60, result.Matches.Single(m => m.Reference.First.X == 56).App.First.X);
        Assert.Equal(["Only in canvas"], result.Missing.Select(t => t.Text));
        Assert.Equal(["Only in app", "library"], result.Extra.Select(t => t.Text).Order(StringComparer.Ordinal));
        Assert.Equal(100.0 * 2 / 3, result.MatchedPercent, 6);
    }

    [Fact]
    public void TheMatcher_ReportsEachKindOfDifference_BeyondTheTolerance()
    {
        var reference = Item("Settings", 100, 100, width: 100, size: 14, weight: 500);

        Assert.False(TextMatcher.Measure(reference, Item("Settings", 102, 98, width: 102.9, size: 14.4, weight: 500)).HasProblems);
        Assert.True(TextMatcher.Measure(reference, Item("Settings", 100, 100, weight: 500, baseline: 107.1)).HasProblems, "the baseline, not the line box, is compared");
        Assert.False(TextMatcher.Measure(reference, Item("Settings", 100, 130, weight: 500, baseline: 104.5 + 1.9)).HasProblems, "a taller line box with the same baseline is fine");

        var shifted = TextMatcher.Measure(reference, Item("Settings", 103, 104.5, width: 100, size: 14, weight: 500));
        Assert.Equal(["x +3 px", "baseline +4.5 px"], shifted.Problems);
        Assert.Equal(3, shifted.DeltaX);

        var wide = TextMatcher.Measure(reference, Item("Settings", 100, 100, width: 104, size: 14, weight: 500));
        Assert.Equal(["width +4 %"], wide.Problems);

        var wrapped = TextMatcher.Measure(reference, Item("Settings", 100, 100, width: 100, size: 14, weight: 500, lines: 2));
        Assert.Equal(["lines 1 -> 2"], wrapped.Problems);

        var font = TextMatcher.Measure(reference, Item("Settings", 100, 100, width: 100, size: 16, weight: 400, mono: true));
        Assert.Equal(["size 14 -> 16", "weight 500 -> 400", "family Inter -> Geist Mono"], font.Problems);
        Assert.True(font.Severity > shifted.Severity - 100);
    }

    [Fact]
    public void TheComparison_OrdersTheErrorsBySeverity_AndCountsOnlyTheMatchedOnes()
    {
        var reference = new[] { Item("A", 10, 10), Item("B", 10, 40), Item("C", 10, 70), Item("D", 10, 100) };
        var app = new[] { Item("A", 10, 10), Item("B", 14, 40), Item("C", 40, 90), Item("E", 0, 0) };

        var result = TextMatcher.Compare(reference, app);

        Assert.Equal(2, result.GeometryErrors);
        Assert.Equal(["C", "B"], result.BySeverity.Select(m => m.Reference.Text));
        Assert.Single(result.Missing);
        Assert.Equal(75, result.MatchedPercent);
    }

    [Fact]
    public void TheReferenceTexts_AreReadFromTheCanvasJson_WithPlaceholdersAsBoxes()
    {
        var texts = ReferenceTexts.Load("Main");

        Assert.Contains(texts, t => t.Text == "What are we recording today?" && t.FontSize == 36 && t.FontWeight == 300 && !t.Mono);
        Assert.Contains(texts, t => t.Text == "47:12" && t.Mono);
        var placeholder = texts.Single(t => t.Text == "Search titles and text");
        Assert.True(placeholder.Placeholder);
        Assert.Single(placeholder.Lines);
        Assert.Equal("a b c", ReferenceTexts.Normalize("  a \n b\t c "));
    }

    [Fact]
    public void TheOverlay_DrawsReferenceBoxesBlue_AndAppBoxesRed()
    {
        var reference = PixelImage.Filled(200, 100, 255, 255, 255);
        var overlay = TextReport.Overlay(reference, [Item("a", 20, 30, width: 50)], [Item("a", 100, 60, width: 50)]);

        PixelColor At(int x, int y) => new(overlay.Rgba[((y * overlay.Width) + x) * 4], overlay.Rgba[((y * overlay.Width) + x) * 4 + 1], overlay.Rgba[((y * overlay.Width) + x) * 4 + 2]);
        Assert.Equal(new PixelColor(30, 90, 230), At(20, 30 - 9 + 0 + 1));
        Assert.Equal(new PixelColor(230, 30, 30), At(100, 60 - 9 + 1));
        Assert.Equal(new PixelColor(255, 255, 255), At(150, 90));
    }

    [Fact]
    public void TheTextReport_ListsTheWorstFirst_ThenMissingAndExtra()
    {
        var board = DesignBoards.Get("Main");
        var reference = new[] { Item("Near", 10, 10), Item("Far", 10, 40), Item("Gone", 10, 70) };
        var app = new[] { Item("Near", 13, 10), Item("Far", 60, 90), Item("New", 0, 0) };

        var markdown = TextReport.Markdown(board, TextMatcher.Compare(reference, app));

        Assert.Contains("matched: 2 (66.7 %)", markdown, StringComparison.Ordinal);
        Assert.True(markdown.IndexOf("| Far |", StringComparison.Ordinal) < markdown.IndexOf("| Near |", StringComparison.Ordinal));
        Assert.Contains("| Gone |", markdown, StringComparison.Ordinal);
        Assert.Contains("| New |", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(48, 48)]
    [InlineData(44, 48.4)]
    [InlineData(36, 42.12)]
    [InlineData(15, 18.15)]
    [InlineData(15, 30)]
    public async Task TheBaseline_OfALineHeightSmallerOrLargerThanTheFont_IsWhereTheGlyphsSit(double size, double lineHeight)
    {
        var (items, ink) = await UiHarness.Session.Dispatch(
            () =>
            {
                var block = new TextBlock { Text = "HEHE", FontFamily = new FontFamily("fonts:Inter#Inter"), FontSize = size, LineHeight = lineHeight, Foreground = Brushes.Black };
                Canvas.SetLeft(block, 20);
                Canvas.SetTop(block, 30);
                var window = Show(block, 400, 200);
                var texts = AppTexts.Collect(window);
                var frame = Capture(window);
                window.Close();
                return (texts, frame);
            },
            TestContext.Current.CancellationToken);

        var line = items.Single(t => t.Text == "HEHE").First;
        var (_, top, _, bottom) = InkBox(ink, 0, 0, 400, 200);

        // Capital letters stand on the baseline and are 0.727 em high (Inter).
        Assert.Equal(line.Baseline, bottom, 1.5);
        Assert.Equal(line.Baseline - (0.727 * size), top, 2.0);
        Assert.True(line.Y <= line.Baseline && line.Baseline <= line.Bottom + 1);
    }

    [Fact]
    public async Task AppTexts_ReadsInlinesAndSpans_AndResolvesAFragmentToItsRect()
    {
        var (items, ink) = await UiHarness.Session.Dispatch(
            () =>
            {
                var block = new TextBlock { FontFamily = new FontFamily("fonts:Inter#Inter"), FontSize = 15, Foreground = Brushes.Black };
                block.Inlines!.Add(new Run("Both: full-text plus semantic. Answers come back with "));
                var span = new Span();
                span.Inlines.Add(new Run("timestamps"));
                block.Inlines.Add(span);
                block.Inlines.Add(new Run(", so one click takes you there."));
                Canvas.SetLeft(block, 20);
                Canvas.SetTop(block, 30);
                var window = Show(block, 700, 100);
                var texts = AppTexts.Collect(window);
                var frame = Capture(window);
                window.Close();
                return (texts, frame);
            },
            TestContext.Current.CancellationToken);

        var whole = items.Single();
        Assert.Equal("Both: full-text plus semantic. Answers come back with timestamps, so one click takes you there.", whole.Text);
        var at = whole.Text.IndexOf("timestamps", StringComparison.Ordinal);
        var range = whole.Layout!.Range(at, "timestamps".Length);
        var box = Assert.Single(range);

        // Ink between the end of "with " and the comma: the rect of "timestamps" starts where its glyphs start.
        var (left, _, right, _) = InkBox(ink, (int)Math.Floor(box.X) - 3, 25, (int)Math.Ceiling(box.W) + 6, 30);
        Assert.InRange(left, box.X - 3, box.X + 3);
        Assert.InRange(right, box.Right - 3, box.Right + 4);
        Assert.Equal(whole.First.Baseline, box.Baseline, 0.5);
    }

    [Fact]
    public async Task AppTexts_SkipsTextOutOfAScrollArea_TransparentTextAndMarksTrimmedOnes()
    {
        var items = await UiHarness.Session.Dispatch(
            () =>
            {
                var rows = new StackPanel();
                for (var i = 0; i < 6; i++)
                {
                    rows.Children.Add(new TextBlock { Text = "Row " + i, Height = 30 });
                }

                var scroll = new ScrollViewer { Height = 70, Width = 200, Content = rows };
                var invisible = new TextBlock { Text = "Transparent", Opacity = 0 };
                var parent = new Border { Opacity = 0, Child = new TextBlock { Text = "Inside transparent parent" } };
                var trimmed = new TextBlock { Text = "A text that is far too long for its box", Width = 80, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
                var panel = new StackPanel { Width = 300 };
                panel.Children.Add(scroll);
                panel.Children.Add(invisible);
                panel.Children.Add(parent);
                panel.Children.Add(trimmed);
                var window = Show(panel, 400, 300);
                var texts = AppTexts.Collect(window);
                window.Close();
                return texts;
            },
            TestContext.Current.CancellationToken);

        Assert.Contains(items, t => t.Text == "Row 0");
        Assert.Contains(items, t => t.Text == "Row 1");
        Assert.DoesNotContain(items, t => t.Text == "Row 5");
        Assert.DoesNotContain(items, t => t.Text == "Transparent");
        Assert.DoesNotContain(items, t => t.Text == "Inside transparent parent");
        Assert.True(items.Single(t => t.Text.StartsWith("A text that", StringComparison.Ordinal)).Trimmed);
        Assert.False(items.Single(t => t.Text == "Row 0").Trimmed);
    }

    [Fact]
    public async Task AppTexts_ReadsTheTextPresenterOfATextBoxWithTypedText_WhereItIsDrawn()
    {
        var (items, ink) = await UiHarness.Session.Dispatch(
            () =>
            {
                var box = new TextBox { Text = "Typed words", Width = 240, FontSize = 14, FontFamily = new FontFamily("fonts:Inter#Inter"), Foreground = Brushes.Black };
                Canvas.SetLeft(box, 20);
                Canvas.SetTop(box, 20);
                var window = Show(box, 400, 100);
                var texts = AppTexts.Collect(window);
                var frame = Capture(window);
                window.Close();
                return (texts, frame);
            },
            TestContext.Current.CancellationToken);

        var typed = items.Single(t => t.Text == "Typed words");
        Assert.Contains("TextPresenter", typed.Source, StringComparison.Ordinal);
        var (left, top, right, bottom) = InkBox(ink, 25, 22, 230, 40);
        Assert.InRange(left, typed.First.X - 2, typed.First.X + 3);
        Assert.InRange(right, typed.First.Right - 3, typed.First.Right + 2);
        Assert.InRange(bottom, typed.First.Baseline - 1, typed.First.Baseline + 5);
        Assert.True(top < typed.First.Baseline);
    }

    [Fact]
    public async Task AppTexts_ReadsTheTranscriptControl_WhereItIsDrawn()
    {
        var (items, ink) = await UiHarness.Session.Dispatch(
            () =>
            {
                var control = new TranscriptTextControl
                {
                    Text = "Both: full-text plus semantic. Answers come back with timestamps, so one click takes you to the exact moment in the recording.",
                    FontFamily = new FontFamily("fonts:Inter#Inter"),
                    FontSize = 15,
                    LineHeight = 24,
                    Foreground = Brushes.Black,
                    Width = 300,
                };
                Canvas.SetLeft(control, 20);
                Canvas.SetTop(control, 20);
                var window = Show(control, 400, 200);
                var texts = AppTexts.Collect(window);
                var frame = Capture(window);
                window.Close();
                return (texts, frame);
            },
            TestContext.Current.CancellationToken);

        var item = items.Single();
        Assert.True(item.Lines.Count >= 3);
        var (left, top, right, bottom) = InkBox(ink, 0, 0, 400, 200);
        Assert.InRange(left, item.Lines.Min(l => l.X) - 1, item.Lines.Min(l => l.X) + 3);
        Assert.InRange(right, item.Lines.Max(l => l.Right) - 4, item.Lines.Max(l => l.Right) + 2);
        Assert.InRange(top, item.First.Y, item.First.Baseline);
        Assert.InRange(bottom, item.Lines[^1].Baseline - 1, item.Lines[^1].Bottom + 1);
    }

    [Fact]
    public void ThePlaceholder_IsComparedByItsTextXAndCentre_NotByWidthOrLines()
    {
        var reference = Item("Search transcript", 877.33, 237.16, width: 515, size: 14, placeholder: true);

        var good = TextMatcher.Measure(reference, Item("Search transcript", 878, 238.5, width: 120, size: 14));
        var wrong = TextMatcher.Measure(reference, Item("Search transcript", 900, 250, width: 120, size: 16, lines: 2));

        Assert.False(good.HasProblems);
        Assert.Equal(["x +22.7 px", "y (centre) +12.8 px", "size 14 -> 16"], wrong.Problems);
    }

    [Fact]
    public void ExcludedPlaceholders_AreListedButNeverCountedAsMissing()
    {
        var reference = new[] { Item("Settings", 10, 10), Item("[VERSION]", 10, 40), Item("Project license — [LICENSE]", 10, 70), Item(@"C:\Users\[USER]\Murmur", 10, 100) };
        var app = new[] { Item("Settings", 10, 10), Item("0.4.0", 10, 40) };
        ExcludedText[] excluded = [new("[VERSION]", "version"), new("[LICENSE]", "license", Contains: true), new("[USER]", "path", Contains: true)];

        var result = TextMatcher.Compare(reference, app, excluded);

        Assert.Equal(1, result.ReferenceCount);
        Assert.Equal(100, result.MatchedPercent);
        Assert.Empty(result.Missing);
        Assert.Equal(["[VERSION]", "Project license — [LICENSE]", @"C:\Users\[USER]\Murmur"], result.Excluded.Select(e => e.Item.Text));
        Assert.Equal(["version", "license", "path"], result.Excluded.Select(e => e.Reason));
        var markdown = TextReport.Markdown(DesignBoards.Get("Settings"), result);
        Assert.Contains("excluded: license", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ADuplicateBeyondTheDistanceLimit_IsMissingAndExtra_ButAUniqueTextIsFoundAnywhere()
    {
        var far = 162 + TextTolerance.MatchDistancePx + 10;
        var duplicates = TextMatcher.Compare([Item("Library", 56, 162), Item("Library", 273, 30)], [Item("Library", 56, far), Item("Library", 273, 30)]);
        var unique = TextMatcher.Compare([Item("Library", 56, 162)], [Item("Library", 56, far)]);

        Assert.Single(duplicates.Matches);
        Assert.Single(duplicates.Missing);
        Assert.Single(duplicates.Extra);
        Assert.Single(unique.Matches);
        Assert.Contains("baseline", unique.Matches[0].Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fragments_AreFoundInTheWholeBlockOrInALongerText_AndSayHow()
    {
        var (app, whole) = await UiHarness.Session.Dispatch(
            () =>
            {
                var block = new TextBlock { FontFamily = new FontFamily("fonts:Inter#Inter"), FontSize = 15, Foreground = Brushes.Black };
                block.Inlines!.Add(new Run("Both: full-text plus semantic. Answers come back with "));
                block.Inlines.Add(new Run("timestamps"));
                block.Inlines.Add(new Run(", so one click takes you there."));
                var other = new TextBlock { Text = "Great. What about PDF export? We need it for our reports.", FontFamily = new FontFamily("fonts:Inter#Inter"), FontSize = 15 };
                Canvas.SetLeft(block, 20);
                Canvas.SetTop(block, 30);
                Canvas.SetLeft(other, 20);
                Canvas.SetTop(other, 80);
                var canvas = new Canvas { Width = 700, Height = 150 };
                canvas.Children.Add(block);
                canvas.Children.Add(other);
                var window = Show(canvas, 700, 150);
                var texts = AppTexts.Collect(window);
                window.Close();
                return (texts, texts[0]);
            },
            TestContext.Current.CancellationToken);

        var block0 = whole;
        var at = block0.Text.IndexOf("timestamps", StringComparison.Ordinal);
        var rect = block0.Layout!.Range(at, 10)[0];
        var fullText = block0.Text;
        var reference = new[]
        {
            Item("Both: full-text plus semantic. Answers come back with", 20, block0.First.CenterY, width: rect.X - 20, size: 15, blockText: fullText, blockId: 7, baseline: block0.First.Baseline),
            Item("timestamps", rect.X, block0.First.CenterY, width: rect.W, size: 15, blockText: fullText, blockId: 7, baseline: block0.First.Baseline),
            Item("What about PDF export?", 20 + 55, 80 + 11, width: 150, size: 15, baseline: 80 + 15.5),
        };

        var result = TextMatcher.Compare(reference, app);

        Assert.Equal(3, result.Matches.Count);
        Assert.Equal([MatchKind.BlockText, MatchKind.BlockText, MatchKind.Fragment], result.Matches.Select(m => m.Via));
        Assert.Empty(result.Extra);
        var stamp = result.Matches[1];
        Assert.Equal(rect.X, stamp.App.First.X, 0.5);
        Assert.Equal("timestamps", stamp.App.Text);
        Assert.Equal(100, result.MatchedPercent);
    }

    [Fact]
    public async Task HideText_ReachesNestedSpans_AndLeavesAnIconAlone()
    {
        var (before, after) = await UiHarness.Session.Dispatch(
            () =>
            {
                var panel = new StackPanel { Width = 300, Spacing = 8, Background = Brushes.White };
                var block = new TextBlock { FontSize = 20, Foreground = Brushes.Black };
                var outer = new Span();
                var inner = new Span();
                inner.Inlines.Add(new Run("Deeply nested") { Foreground = Brushes.Black });
                outer.Inlines.Add(inner);
                block.Inlines!.Add(outer);
                panel.Children.Add(block);
                panel.Children.Add(new PathIcon { Data = Geometry.Parse("M0,0 L20,0 L20,20 L0,20 Z"), Foreground = Brushes.Red, Width = 20, Height = 20 });
                var window = Show(panel);
                var first = Capture(window);
                DesignScene.HideText(window);
                UiHarness.Pump(4);
                var second = Capture(window);
                window.Close();
                return (first, second);
            },
            TestContext.Current.CancellationToken);

        Assert.True(Count(before, p => p.R < 80 && p.G < 80 && p.B < 80) > 50, "the nested run was drawn dark before");
        Assert.Equal(0, Count(after, p => p.R < 80 && p.G < 80 && p.B < 80));
        Assert.True(Count(after, p => p.R > 200 && p.G < 60 && p.B < 60) > 200, "the icon keeps its colour");
    }

    [Fact]
    public async Task DisableTransitions_RemovesEveryTransitionOfTheTree()
    {
        var left = await UiHarness.Session.Dispatch(
            () =>
            {
                var button = new Button { Content = "Go" };
                var window = Show(button);
                DesignScene.DisableTransitions(window);
                var count = window.GetVisualDescendants().OfType<Avalonia.Animation.Animatable>().Count(a => a.Transitions is { Count: > 0 });
                window.Close();
                return count;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, left);
    }

    private readonly record struct PixelColor(byte R, byte G, byte B);

    private static int Count(PixelImage image, Func<PixelColor, bool> predicate)
    {
        var count = 0;
        for (var i = 0; i < image.Rgba.Length; i += 4)
        {
            if (predicate(new PixelColor(image.Rgba[i], image.Rgba[i + 1], image.Rgba[i + 2])))
            {
                count++;
            }
        }

        return count;
    }

    private static (int Left, int Top, int Right, int Bottom) InkBox(PixelImage image, int x, int y, int width, int height)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var py = y; py < y + height; py++)
        {
            for (var px = x; px < x + width; px++)
            {
                var o = ((py * image.Width) + px) * 4;
                if (image.Rgba[o] < 160 && image.Rgba[o + 1] < 160 && image.Rgba[o + 2] < 160)
                {
                    left = Math.Min(left, px);
                    top = Math.Min(top, py);
                    right = Math.Max(right, px + 1);
                    bottom = Math.Max(bottom, py + 1);
                }
            }
        }

        return (left, top, right, bottom);
    }

    private static PixelImage Capture(Window window)
    {
        using var frame = window.CaptureRenderedFrame()!;
        using var buffer = frame.Lock();
        var w = buffer.Size.Width;
        var h = buffer.Size.Height;
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(buffer.Address + (y * buffer.RowBytes), pixels, y * w * 4, w * 4);
        }

        return PixelImage.FromFrame(pixels, w, h, buffer.Format == Avalonia.Platform.PixelFormat.Bgra8888);
    }
}
