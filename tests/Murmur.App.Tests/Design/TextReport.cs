using System.Globalization;
using System.Text;

namespace Murmur.App.Tests.Design;

/// <summary>The per-board text report (<c>&lt;Board&gt;.text.md</c>) and the overlay of reference and app line boxes.</summary>
internal static class TextReport
{
    public static string Markdown(DesignBoard board, TextComparison comparison)
    {
        string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("# Text geometry: ").Append(board.Name).Append("\n\n");
        sb.Append("Reference texts: ").Append(comparison.ReferenceCount)
          .Append(" · matched: ").Append(comparison.Matches.Count).Append(" (").Append(N(comparison.MatchedPercent)).Append(" %)")
          .Append(" · missing in the app: ").Append(comparison.Missing.Count)
          .Append(" · geometry errors: ").Append(comparison.GeometryErrors)
          .Append(" · extra in the app (informational): ").Append(comparison.Extra.Count)
          .Append(" · excluded placeholders: ").Append(comparison.Excluded.Count).Append("\n\n");
        var byKind = comparison.Matches.GroupBy(m => m.Via).ToDictionary(g => g.Key, g => g.Count());
        sb.Append("Found by text: ").Append(byKind.GetValueOrDefault(MatchKind.Exact))
          .Append(" · as part of the block text: ").Append(byKind.GetValueOrDefault(MatchKind.BlockText))
          .Append(" · as a fragment of a longer app text: ").Append(byKind.GetValueOrDefault(MatchKind.Fragment)).Append("\n\n");
        sb.Append("Texts are matched by whitespace-collapsed, case-sensitive text; duplicates by the nearest position. A matched text is an error when its first line is more than ")
          .Append(TextTolerance.OffsetPx).Append(" px off in x, or the baseline of its first line more than ").Append(TextTolerance.BaselinePx).Append(" px off (placeholders: the vertical centre; no width or line check), its total line width differs by more than ")
          .Append(TextTolerance.WidthPercent).Append(" %, the line count differs, or the font size, weight or family (Inter / Geist Mono) differs. ")
          .Append("`<Board>.text.png`: blue = reference line boxes, red = app line boxes.\n\n");

        sb.Append("## Geometry errors (worst first)\n\n");
        if (comparison.GeometryErrors == 0)
        {
            sb.Append("None.\n\n");
        }
        else
        {
            sb.Append("| Text | Problems | Reference (x, baseline, font) | App (x, baseline, font) | Found by | App source |\n| --- | --- | --- | --- | --- | --- |\n");
            foreach (var m in comparison.BySeverity)
            {
                sb.Append("| ").Append(Cell(m.Reference.Text))
                  .Append(" | ").Append(string.Join("; ", m.Problems))
                  .Append(" | ").Append(N(m.Reference.First.X)).Append(", ").Append(N(Vertical(m.Reference))).Append(", ").Append(m.Reference.FontDescription)
                  .Append(" | ").Append(N(m.App.First.X)).Append(", ").Append(N(Vertical(m.App, m.Reference.Placeholder))).Append(", ").Append(m.App.FontDescription)
                  .Append(" | ").Append(m.ViaText)
                  .Append(" | ").Append(m.App.Source).Append(m.App.Trimmed ? " (trimmed)" : string.Empty)
                  .Append(" |\n");
            }

            sb.Append('\n');
        }

        sb.Append("## Missing in the app (in the reference only)\n\n");
        if (comparison.Missing.Count == 0)
        {
            sb.Append("None.\n\n");
        }
        else
        {
            sb.Append("| Text | Reference (x, centre y) | Font | Lines |\n| --- | --- | --- | ---: |\n");
            foreach (var t in comparison.Missing.OrderBy(t => t.First.Y).ThenBy(t => t.First.X))
            {
                sb.Append("| ").Append(Cell(t.Text)).Append(t.Placeholder ? " (placeholder)" : string.Empty)
                  .Append(" | ").Append(N(t.First.X)).Append(", ").Append(N(t.First.CenterY))
                  .Append(" | ").Append(t.FontDescription).Append(" | ").Append(t.Lines.Count).Append(" |\n");
            }

            sb.Append('\n');
        }

        sb.Append("## Compared by line centre (the reference has no baseline)\n\n");
        var centred = comparison.Matches.Where(m => !m.Reference.Placeholder && double.IsNaN(m.Reference.First.Baseline)).ToArray();
        if (centred.Length == 0)
        {
            sb.Append("None.\n\n");
        }
        else
        {
            foreach (var m in centred)
            {
                sb.Append("- ").Append(Cell(m.Reference.Text)).Append(" (").Append(N(m.Reference.First.X)).Append(", ").Append(N(m.Reference.First.CenterY)).Append(")\n");
            }

            sb.Append('\n');
        }

        sb.Append("## Excluded placeholders (not in the gates)\n\n");
        if (comparison.Excluded.Count == 0)
        {
            sb.Append("None.\n\n");
        }
        else
        {
            sb.Append("| Text | Reference (x, centre y) | Reason |\n| --- | --- | --- |\n");
            foreach (var (item, reason) in comparison.Excluded)
            {
                sb.Append("| ").Append(Cell(item.Text)).Append(" | ").Append(N(item.First.X)).Append(", ").Append(N(item.First.CenterY)).Append(" | excluded: ").Append(reason).Append(" |\n");
            }

            sb.Append('\n');
        }

        sb.Append("## Extra in the app (informational)\n\n");
        if (comparison.Extra.Count == 0)
        {
            sb.Append("None.\n");
        }
        else
        {
            sb.Append("| Text | App (x, centre y) | Font | App source |\n| --- | --- | --- | --- |\n");
            foreach (var t in comparison.Extra.OrderBy(t => t.First.Y).ThenBy(t => t.First.X))
            {
                sb.Append("| ").Append(Cell(t.Text)).Append(" | ").Append(N(t.First.X)).Append(", ").Append(N(t.First.CenterY))
                  .Append(" | ").Append(t.FontDescription).Append(" | ").Append(t.Source).Append(" |\n");
            }
        }

        return sb.ToString();
    }

    /// <summary>The reference render, faded, with every reference line box in blue and every app line box in red.</summary>
    public static PixelImage Overlay(PixelImage reference, IReadOnlyList<TextItem> referenceTexts, IReadOnlyList<TextItem> appTexts)
    {
        var image = reference.Faded(35);
        foreach (var text in referenceTexts)
        {
            foreach (var line in text.Lines)
            {
                image.DrawRect(line.X, line.Y, line.W, line.H, 30, 90, 230);
            }
        }

        foreach (var text in appTexts)
        {
            foreach (var line in text.Lines)
            {
                image.DrawRect(line.X, line.Y, line.W, line.H, 230, 30, 30);
            }
        }

        return image;
    }

    private static double Vertical(TextItem item, bool centre = false) => centre || item.Placeholder || double.IsNaN(item.First.Baseline) ? item.First.CenterY : item.First.Baseline;

    private static string Cell(string text)
    {
        var clipped = text.Length > 70 ? text[..67] + "..." : text;
        return clipped.Replace("|", "\\|", StringComparison.Ordinal);
    }
}
