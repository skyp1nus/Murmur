using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using Murmur.App.Controls;

namespace Murmur.App.Tests.Design;

/// <summary>A line box of a text, in page (window) pixels.</summary>
internal readonly record struct LineBox(double X, double Y, double W, double H)
{
    public double CenterY => Y + (H / 2);

    public double Right => X + W;

    public double Bottom => Y + H;
}

/// <summary>One text of the canvas (a reference text node) or of the app (a text control): its line boxes and its font.</summary>
internal sealed record TextItem(string Text, IReadOnlyList<LineBox> Lines, double FontSize, int FontWeight, bool Mono, string Source, bool Placeholder = false)
{
    public LineBox First => Lines[0];

    public double TotalWidth => Lines.Sum(l => l.W);

    public string FontDescription => $"{(Mono ? "Geist Mono" : "Inter")} {FontSize:0.#}/{FontWeight}";
}

/// <summary>Reads the texts of the canvas from <c>docs/design/reference/&lt;Board&gt;.text.json</c>.</summary>
internal static partial class ReferenceTexts
{
    public static string Normalize(string? text) => Whitespace().Replace(text ?? string.Empty, " ").Trim();

    public static IReadOnlyList<TextItem> Load(string board)
    {
        var path = Path.Combine(RepoPaths.Root, "docs", "design", "reference", board + ".text.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var result = new List<TextItem>();
        foreach (var node in document.RootElement.GetProperty("texts").EnumerateArray())
        {
            var lines = new List<LineBox>();
            var placeholder = node.TryGetProperty("placeholder", out var p) && p.GetBoolean();
            if (placeholder && node.TryGetProperty("box", out var box))
            {
                lines.Add(ReadBox(box));
            }
            else
            {
                foreach (var line in node.GetProperty("lines").EnumerateArray())
                {
                    lines.Add(ReadBox(line));
                }
            }

            if (lines.Count == 0)
            {
                continue;
            }

            var family = node.GetProperty("fontFamily").GetString() ?? string.Empty;
            result.Add(new TextItem(
                Normalize(node.GetProperty("text").GetString()),
                lines,
                node.GetProperty("fontSize").GetDouble(),
                node.GetProperty("fontWeight").GetInt32(),
                family.Contains("Geist Mono", StringComparison.OrdinalIgnoreCase),
                "reference",
                placeholder));
        }

        return result;
    }

    private static LineBox ReadBox(JsonElement e) => new(e.GetProperty("x").GetDouble(), e.GetProperty("y").GetDouble(), e.GetProperty("w").GetDouble(), e.GetProperty("h").GetDouble());

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>Collects the texts the app draws from the visual tree: every line of every text control, in window pixels.</summary>
internal static class AppTexts
{
    public static IReadOnlyList<TextItem> Collect(Window window)
    {
        var result = new List<TextItem>();
        var size = new Rect(0, 0, window.Bounds.Width, window.Bounds.Height);
        foreach (var visual in window.GetVisualDescendants())
        {
            TextItem? item = visual switch
            {
                TextBlock block => FromTextBlock(window, block),
                TextPresenter presenter => FromPresenter(window, presenter),
                TranscriptTextControl transcript => FromTranscript(window, transcript),
                _ => null,
            };
            if (item is null)
            {
                continue;
            }

            // Lines scrolled out of the window are not on the board.
            var inside = item.Lines.Where(l => new Rect(l.X, l.Y, l.W, l.H).Intersects(size)).ToArray();
            if (inside.Length > 0)
            {
                result.Add(item with { Lines = inside });
            }
        }

        return result;
    }

    private static TextItem? FromTextBlock(Window window, TextBlock block)
    {
        if (!block.IsEffectivelyVisible || string.IsNullOrWhiteSpace(block.Text) || block.Bounds.Width <= 0)
        {
            return null;
        }

        return Build(window, block, block.Text!, block.TextLayout, block.Padding, block.FontSize, (int)block.FontWeight, block.FontFamily, Describe(block));
    }

    private static TextItem? FromPresenter(Window window, TextPresenter presenter)
    {
        if (!presenter.IsEffectivelyVisible || string.IsNullOrWhiteSpace(presenter.Text) || presenter.Bounds.Width <= 0)
        {
            return null;
        }

        return Build(window, presenter, presenter.Text!, presenter.TextLayout, default, presenter.FontSize, (int)presenter.FontWeight, presenter.FontFamily, Describe(presenter));
    }

    /// <summary>The control keeps its layout private: build the same one (same text, font, width) to read its lines.</summary>
    private static TextItem? FromTranscript(Window window, TranscriptTextControl control)
    {
        if (!control.IsEffectivelyVisible || string.IsNullOrWhiteSpace(control.Text) || control.Bounds.Width <= 0)
        {
            return null;
        }

        using var layout = new TextLayout(
            control.Text!,
            new Typeface(control.FontFamily, FontStyle.Normal, control.FontWeight),
            control.FontSize,
            Brushes.Black,
            TextAlignment.Left,
            TextWrapping.Wrap,
            TextTrimming.None,
            null,
            FlowDirection.LeftToRight,
            control.Bounds.Width,
            double.PositiveInfinity,
            control.LineHeight,
            control.LetterSpacing);
        return Build(window, control, control.Text!, layout, default, control.FontSize, (int)control.FontWeight, control.FontFamily, Describe(control));
    }

    private static TextItem? Build(Window window, Visual owner, string text, TextLayout layout, Thickness padding, double size, int weight, FontFamily family, string source)
    {
        var lines = new List<LineBox>();
        var y = padding.Top;
        foreach (var line in layout.TextLines)
        {
            if (line.Width > 0)
            {
                var origin = owner.TranslatePoint(new Point(padding.Left + line.Start, y), window);
                if (origin is { } point)
                {
                    lines.Add(new LineBox(point.X, point.Y, line.Width, line.Height));
                }
            }

            y += line.Height;
        }

        return lines.Count == 0
            ? null
            : new TextItem(ReferenceTexts.Normalize(text), lines, size, weight, family.Name.Contains("Geist Mono", StringComparison.OrdinalIgnoreCase), source);
    }

    private static string Describe(Visual visual)
    {
        var name = visual is Control { Name: { Length: > 0 } n } ? "#" + n : string.Empty;
        var parent = visual.GetVisualParent()?.GetType().Name;
        return visual.GetType().Name + name + (parent is null ? string.Empty : " in " + parent);
    }
}
