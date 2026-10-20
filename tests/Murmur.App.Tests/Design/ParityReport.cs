using System.Globalization;
using System.Text;

namespace Murmur.App.Tests.Design;

/// <summary>What the comparison of one board found; stored next to the images so the report can be rebuilt from any run.</summary>
internal sealed record BoardResult(
    string Name,
    int Width,
    int Height,
    double Total,
    double Sidebar,
    double Content,
    double Ink,
    bool HasSidebar,
    long IgnoredPixels,
    bool SizeDiffers,
    double? Max,
    IReadOnlyList<string> Notes)
{
    public string Serialize()
    {
        var sb = new StringBuilder();
        void Line(string key, string value) => sb.Append(key).Append('=').Append(value).Append('\n');
        string D(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        Line("name", Name);
        Line("width", Width.ToString(CultureInfo.InvariantCulture));
        Line("height", Height.ToString(CultureInfo.InvariantCulture));
        Line("total", D(Total));
        Line("sidebar", D(Sidebar));
        Line("content", D(Content));
        Line("ink", D(Ink));
        Line("hasSidebar", HasSidebar ? "1" : "0");
        Line("ignored", IgnoredPixels.ToString(CultureInfo.InvariantCulture));
        Line("sizeDiffers", SizeDiffers ? "1" : "0");
        Line("max", Max is { } max ? D(max) : string.Empty);
        foreach (var note in Notes)
        {
            Line("note", note.Replace('\n', ' '));
        }

        return sb.ToString();
    }

    public static BoardResult Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var notes = new List<string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = line.IndexOf('=', StringComparison.Ordinal);
            var key = line[..at];
            var value = line[(at + 1)..];
            if (key == "note")
            {
                notes.Add(value);
            }
            else
            {
                values[key] = value;
            }
        }

        double D(string key) => double.Parse(values[key], CultureInfo.InvariantCulture);
        return new BoardResult(
            values["name"],
            int.Parse(values["width"], CultureInfo.InvariantCulture),
            int.Parse(values["height"], CultureInfo.InvariantCulture),
            D("total"),
            D("sidebar"),
            D("content"),
            D("ink"),
            values["hasSidebar"] == "1",
            long.Parse(values["ignored"], CultureInfo.InvariantCulture),
            values["sizeDiffers"] == "1",
            values["max"].Length == 0 ? null : D("max"),
            notes);
    }
}

/// <summary>Writes the images and the report of the parity run to <c>artifacts/design-parity</c>.</summary>
internal static class ParityReport
{
    private static readonly Lock Gate = new();

    /// <summary>Where the run writes; <c>MURMUR_PARITY_DIR</c> overrides the default <c>artifacts/design-parity</c> of the checkout.</summary>
    public static string Directory =>
        Environment.GetEnvironmentVariable("MURMUR_PARITY_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(RepoPaths.Root, "artifacts", "design-parity");

    /// <summary>Saves the app render, the diff and the side-by-side of a board, and its result; then rebuilds <c>report.md</c>.</summary>
    public static BoardResult Write(DesignBoard board, PixelImage reference, PixelImage actual, ImageComparison comparison, IReadOnlyList<string> notes)
    {
        System.IO.Directory.CreateDirectory(Directory);
        actual.SavePng(Path.Combine(Directory, board.Name + ".app.png"));
        comparison.Diff.SavePng(Path.Combine(Directory, board.Name + ".diff.png"));
        SideBySide(reference, actual, comparison.Diff).SavePng(Path.Combine(Directory, board.Name + ".side.png"));

        var result = new BoardResult(
            board.Name,
            reference.Width,
            reference.Height,
            comparison.Total.Percent,
            comparison.Sidebar.Percent,
            comparison.Content.Percent,
            comparison.Ink.Percent,
            board.HasSidebar,
            comparison.IgnoredPixels,
            comparison.SizeDiffers,
            board.MaxMismatchPercent,
            notes);
        lock (Gate)
        {
            File.WriteAllText(Path.Combine(Directory, board.Name + ".result.txt"), result.Serialize());
            File.WriteAllText(Path.Combine(Directory, "report.md"), BuildReport(Directory));
        }

        return result;
    }

    /// <summary>The reference, the app and the diff next to each other, 12 px apart.</summary>
    public static PixelImage SideBySide(PixelImage reference, PixelImage actual, PixelImage diff)
    {
        const int Gap = 12;
        var height = Math.Max(reference.Height, Math.Max(actual.Height, diff.Height));
        var width = reference.Width + actual.Width + diff.Width + (2 * Gap);
        var sheet = PixelImage.Filled(width, height, 120, 120, 120);
        Blit(sheet, reference, 0);
        Blit(sheet, actual, reference.Width + Gap);
        Blit(sheet, diff, reference.Width + actual.Width + (2 * Gap));
        return sheet;
    }

    private static void Blit(PixelImage target, PixelImage source, int left)
    {
        for (var y = 0; y < source.Height; y++)
        {
            Buffer.BlockCopy(source.Rgba, y * source.Width * 4, target.Rgba, ((y * target.Width) + left) * 4, source.Width * 4);
        }
    }

    /// <summary>Builds the markdown report from the result files of <paramref name="directory"/>, boards in the order of the table.</summary>
    public static string BuildReport(string directory)
    {
        var results = new Dictionary<string, BoardResult>(StringComparer.Ordinal);
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.result.txt"))
        {
            try
            {
                var result = BoardResult.Parse(File.ReadAllText(file));
                results[result.Name] = result;
            }
            catch (Exception ex) when (ex is KeyNotFoundException or FormatException or ArgumentOutOfRangeException)
            {
                // A result file of an older run with other fields: the board shows as "not rendered" until it runs again.
            }
        }

        string P(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("# Design parity: the app against the canvas\n\n");
        sb.Append("Every artboard of `docs/design/source` is rendered by the app headlessly (Avalonia.Headless + Skia, English, scale 1, the size and theme of the board) ")
          .Append("with the artboard's demo data (`DesignDemoData`) and compared with `docs/design/reference/<Board>.png`.\n\n");
        sb.Append("A pixel is a mismatch when no pixel of the reference's 3x3 neighbourhood is within ")
          .Append(ImageComparer.DefaultTolerance.ToString(CultureInfo.InvariantCulture))
          .Append(" per channel (of 255) of the app pixel, so 1 px shifts and anti-aliasing do not count. ")
          .Append("The sidebar is the strip x < ").Append(ImageComparer.SidebarWidth.ToString(CultureInfo.InvariantCulture))
          .Append(" of the five main screens; the onboarding has none (content only). Ignored rectangles are neither compared nor counted. ")
          .Append("\"Of drawn pixels\" counts only the pixels that are not page background in the reference or in the app (a screen is mostly empty, so the total is small even for a screen that is wrong).\n\n");
        sb.Append("| Board | Size | Mismatch total % | Sidebar % | Content % | Of drawn pixels % | Ignored | Limit | Notes |\n");
        sb.Append("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |\n");
        foreach (var board in DesignBoards.All)
        {
            if (!results.TryGetValue(board.Name, out var r))
            {
                sb.Append("| ").Append(board.Name).Append(" | ").Append(board.Width).Append('x').Append(board.Height).Append(" | not rendered | | | | | | |\n");
                continue;
            }

            var ignored = r.IgnoredPixels == 0 ? "—" : (100.0 * r.IgnoredPixels / ((double)r.Width * r.Height)).ToString("0.00", CultureInfo.InvariantCulture) + " %";
            var notes = string.Join("; ", r.Notes.Append(r.SizeDiffers ? "render size differs from the reference" : null!).Where(n => n is not null));
            sb.Append("| ").Append(r.Name)
              .Append(" | ").Append(r.Width).Append('x').Append(r.Height)
              .Append(" | ").Append(P(r.Total))
              .Append(" | ").Append(r.HasSidebar ? P(r.Sidebar) : "—")
              .Append(" | ").Append(P(r.Content))
              .Append(" | ").Append(P(r.Ink))
              .Append(" | ").Append(ignored)
              .Append(" | ").Append(r.Max is { } max ? P(max) : "not enforced")
              .Append(" | ").Append(notes)
              .Append(" |\n");
        }

        sb.Append("\nFiles per board: `<Board>.app.png` (the app), `<Board>.diff.png` (reference greyed, mismatches red, ignored rectangles blue), `<Board>.side.png` (reference | app | diff).\n");

        sb.Append("\n## Ignored rectangles\n\n");
        sb.Append("Only content the app cannot reproduce (coordinates in the reference image).\n\n");
        foreach (var board in DesignBoards.All.Where(b => b.Ignored.Count > 0))
        {
            foreach (var rect in board.Ignored)
            {
                sb.Append("- `").Append(board.Name).Append("` (").Append(rect.X).Append(',').Append(rect.Y).Append(' ').Append(rect.Width).Append('x').Append(rect.Height).Append("): ").Append(rect.Reason).Append('\n');
            }
        }

        sb.Append("\n## Not rendered\n\n");
        foreach (var name in DesignBoards.Excluded)
        {
            sb.Append("- `").Append(name).Append("`: component sheet, not a screen.\n");
        }

        sb.Append("\n## Demo data the app cannot show yet\n\n");
        foreach (var (screen, feature) in DesignDemoData.NotRepresented)
        {
            sb.Append("- **").Append(screen).Append("**: ").Append(feature).Append('\n');
        }

        return sb.ToString();
    }
}
