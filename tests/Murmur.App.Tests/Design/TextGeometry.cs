using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using Murmur.App.Controls;

namespace Murmur.App.Tests.Design;

/// <summary>A line box of a text, in page (window) pixels; <c>Baseline</c> is the y of the baseline (NaN when unknown).</summary>
internal readonly record struct LineBox(double X, double Y, double W, double H, double Baseline = double.NaN)
{
    public double CenterY => Y + (H / 2);

    public double Right => X + W;

    public double Bottom => Y + H;
}

/// <summary>
/// The layout of an app text, kept to measure parts of it (a fragment of a paragraph) later: the raw text, where each character of
/// the whitespace-collapsed text came from, and the position of the layout in the window.
/// </summary>
internal sealed class AppTextLayout
{
    private readonly int[] _normalizedToRaw;
    private readonly Point _origin;
    private readonly Func<LineBox, bool> _isVisible;
    private readonly Rect[][] _characters;
    private readonly (double Top, double Height, double Baseline)[] _lines;

    /// <summary>
    /// The control re-creates (and disposes) its layout when the scene changes after the texts were collected (the shape pass), so the
    /// rectangles of every character and the lines are taken now.
    /// </summary>
    public AppTextLayout(string raw, int[] normalizedToRaw, TextLayout layout, Point origin, Func<LineBox, bool> isVisible, Func<int, int>? toLayout = null)
    {
        Raw = raw;
        _normalizedToRaw = normalizedToRaw;
        _origin = origin;
        _isVisible = isVisible;
        _characters = new Rect[raw.Length][];
        for (var i = 0; i < raw.Length; i++)
        {
            var at = toLayout?.Invoke(i) ?? i;
            _characters[i] = [.. layout.HitTestTextRange(at, 1)];
        }

        var top = 0.0;
        var lines = new List<(double, double, double)>();
        foreach (var line in layout.TextLines)
        {
            lines.Add((top, line.Height, line.Baseline));
            top += line.Height;
        }

        _lines = [.. lines];
    }

    public string Raw { get; }

    /// <summary>The line boxes of the part [start, start + length) of the normalised text, one per line it touches.</summary>
    public IReadOnlyList<LineBox> Range(int start, int length)
    {
        if (length <= 0 || start < 0 || start + length > _normalizedToRaw.Length)
        {
            return [];
        }

        var rawStart = _normalizedToRaw[start];
        var rawEnd = _normalizedToRaw[start + length - 1] + 1;

        // The rectangles of one line (one per character) are joined.
        var lineRects = new List<Rect>();
        for (var i = rawStart; i < rawEnd; i++)
        {
            foreach (var rect in _characters[i])
            {
                var at = lineRects.FindIndex(r => Math.Abs(r.Y - rect.Y) < 1);
                if (at >= 0)
                {
                    lineRects[at] = lineRects[at].Union(rect);
                }
                else
                {
                    lineRects.Add(rect);
                }
            }
        }

        var result = new List<LineBox>();
        foreach (var rect in lineRects.OrderBy(r => r.Y))
        {
            var (lineTop, baseline) = LineOf(rect.Y + (rect.Height / 2));
            var box = new LineBox(_origin.X + rect.X, _origin.Y + rect.Y, rect.Width, rect.Height, _origin.Y + lineTop + baseline);
            if (_isVisible(box))
            {
                result.Add(box);
            }
        }

        return result;
    }

    private (double Top, double Baseline) LineOf(double y)
    {
        foreach (var (top, height, baseline) in _lines)
        {
            if (y < top + height)
            {
                return (top, baseline);
            }
        }

        return _lines.Length == 0 ? (0, 0) : (_lines[^1].Top, _lines[^1].Baseline);
    }
}

/// <summary>One text of the canvas (a reference text node) or of the app (a text control): its line boxes and its font.</summary>
internal sealed record TextItem(
    string Text,
    IReadOnlyList<LineBox> Lines,
    double FontSize,
    int FontWeight,
    bool Mono,
    string Source,
    bool Placeholder = false,
    int BlockId = -1,
    string BlockText = "",
    bool Trimmed = false,
    AppTextLayout? Layout = null)
{
    public LineBox First => Lines[0];

    public double TotalWidth => Lines.Sum(l => l.W);

    public string FontDescription => $"{(Mono ? "Geist Mono" : "Inter")} {FontSize:0.#}/{FontWeight}";
}

/// <summary>Whitespace-collapsed text with the raw index of every character.</summary>
internal static class TextNormalizer
{
    public static string Normalize(string? text) => Normalize(text ?? string.Empty, out _);

    public static string Normalize(string raw, out int[] map)
    {
        var sb = new StringBuilder(raw.Length);
        var indices = new List<int>(raw.Length);
        var pendingSpace = -1;
        for (var i = 0; i < raw.Length; i++)
        {
            if (char.IsWhiteSpace(raw[i]))
            {
                if (sb.Length > 0 && pendingSpace < 0)
                {
                    pendingSpace = i;
                }

                continue;
            }

            if (pendingSpace >= 0)
            {
                sb.Append(' ');
                indices.Add(pendingSpace);
                pendingSpace = -1;
            }

            sb.Append(raw[i]);
            indices.Add(i);
        }

        map = [.. indices];
        return sb.ToString();
    }
}

/// <summary>Reads the texts of the canvas from <c>docs/design/reference/&lt;Board&gt;.text.json</c>.</summary>
internal static class ReferenceTexts
{
    public static string Normalize(string? text) => TextNormalizer.Normalize(text);

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
                // The text starts at the content box (textX) and is centred on centerY; the box height is the line height.
                var b = ReadBox(box, double.NaN);
                var x = node.TryGetProperty("textX", out var tx) ? tx.GetDouble() : b.X;
                var centerY = node.TryGetProperty("centerY", out var cy) ? cy.GetDouble() : b.CenterY;
                lines.Add(new LineBox(x, centerY - (b.H / 2), b.W, b.H));
            }
            else
            {
                var baseline = node.TryGetProperty("baseline", out var bl) && bl.ValueKind == JsonValueKind.Number ? bl.GetDouble() : double.NaN;
                var first = true;
                foreach (var line in node.GetProperty("lines").EnumerateArray())
                {
                    lines.Add(ReadBox(line, first ? baseline : double.NaN));
                    first = false;
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
                placeholder,
                node.TryGetProperty("blockId", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt32() : -1,
                node.TryGetProperty("blockText", out var bt) ? Normalize(bt.GetString()) : string.Empty));
        }

        return result;
    }

    private static LineBox ReadBox(JsonElement e, double baseline) =>
        new(e.GetProperty("x").GetDouble(), e.GetProperty("y").GetDouble(), e.GetProperty("w").GetDouble(), e.GetProperty("h").GetDouble(), baseline);
}

/// <summary>Collects the texts the app draws from the visual tree: every line of every text control, in window pixels.</summary>
internal static class AppTexts
{
    public static IReadOnlyList<TextItem> Collect(Window window)
    {
        var result = new List<TextItem>();
        foreach (var visual in window.GetVisualDescendants())
        {
            TextItem? item = visual switch
            {
                TextBlock block => FromTextBlock(window, block),
                TextPresenter presenter => FromPresenter(window, presenter),
                TranscriptTextControl transcript => FromTranscript(window, transcript),
                _ => null,
            };
            if (item is not null)
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static TextItem? FromTextBlock(Window window, TextBlock block)
    {
        var text = string.IsNullOrEmpty(block.Text) ? block.Inlines?.Text : block.Text;
        if (!IsShown(block) || string.IsNullOrWhiteSpace(text) || block.Bounds.Width <= 0)
        {
            return null;
        }

        var layout = block.TextLayout;
        var trimmed = block.TextTrimming != TextTrimming.None && layout.TextLines.Any(l => l.HasCollapsed);
        return Build(window, block, text, layout, block.Padding, block.FontSize, (int)block.FontWeight, block.FontFamily, trimmed);
    }

    private static TextItem? FromPresenter(Window window, TextPresenter presenter)
    {
        if (!IsShown(presenter) || string.IsNullOrWhiteSpace(presenter.Text) || presenter.Bounds.Width <= 0)
        {
            return null;
        }

        return Build(window, presenter, presenter.Text!, presenter.TextLayout, default, presenter.FontSize, (int)presenter.FontWeight, presenter.FontFamily, false);
    }

    /// <summary>The control keeps its layout private: it hands out the layout it draws and the map of the positions of its text.</summary>
    private static TextItem? FromTranscript(Window window, TranscriptTextControl control)
    {
        if (!IsShown(control) || string.IsNullOrWhiteSpace(control.Text) || control.Bounds.Width <= 0 || control.CurrentLayout is not { } layout)
        {
            return null;
        }

        return Build(window, control, control.Text!, layout, default, control.FontSize, (int)control.FontWeight, control.FontFamily, false, control.LaidOutIndex);
    }

    /// <summary>Visible, and neither it nor an ancestor is fully transparent.</summary>
    private static bool IsShown(Visual visual) =>
        visual.IsEffectivelyVisible && visual.Opacity > 0 && !visual.GetVisualAncestors().Any(a => a.Opacity <= 0);

    private static TextItem? Build(Window window, Visual owner, string raw, TextLayout layout, Thickness padding, double size, int weight, FontFamily family, bool trimmed, Func<int, int>? toLayout = null)
    {
        var originPoint = owner.TranslatePoint(new Point(padding.Left, padding.Top), window);
        if (originPoint is not { } origin)
        {
            return null;
        }

        // Lines outside the window, or outside an ancestor that clips (a scroll area), are not on the board.
        var clip = ClipOf(window, owner);
        bool Visible(LineBox box) => new Rect(box.X, box.Y, box.W, box.H).Intersects(clip);

        var lines = new List<LineBox>();
        var y = origin.Y;
        foreach (var line in layout.TextLines)
        {
            if (line.Width > 0)
            {
                var box = new LineBox(origin.X + line.Start, y, line.Width, line.Height, y + line.Baseline);
                if (Visible(box))
                {
                    lines.Add(box);
                }
            }

            y += line.Height;
        }

        if (lines.Count == 0)
        {
            return null;
        }

        var normalized = TextNormalizer.Normalize(raw, out var map);
        return new TextItem(
            normalized,
            lines,
            size,
            weight,
            family.Name.Contains("Geist Mono", StringComparison.OrdinalIgnoreCase),
            Describe(owner),
            Trimmed: trimmed,
            Layout: new AppTextLayout(raw, map, layout, origin, Visible, toLayout));
    }

    /// <summary>The window, narrowed by every ancestor that clips its content.</summary>
    private static Rect ClipOf(Window window, Visual owner)
    {
        var clip = new Rect(0, 0, window.Bounds.Width, window.Bounds.Height);
        foreach (var ancestor in owner.GetVisualAncestors())
        {
            if (ancestor is Control { ClipToBounds: true } or ScrollViewer or ScrollContentPresenter && ancestor != window)
            {
                var corner = ancestor.TranslatePoint(default, window);
                if (corner is { } point)
                {
                    clip = clip.Intersect(new Rect(point, ancestor.Bounds.Size));
                }
            }
        }

        return clip;
    }

    private static string Describe(Visual visual)
    {
        var name = visual is Control { Name: { Length: > 0 } n } ? "#" + n : string.Empty;
        var parent = visual.GetVisualParent()?.GetType().Name;
        return visual.GetType().Name + name + (parent is null ? string.Empty : " in " + parent);
    }
}
