using System.Globalization;
using System.Text;

namespace Murmur.App.Tests.Design;

/// <summary>What the comparison of one board found; stored next to the images so the report can be rebuilt from any run.</summary>
internal sealed record BoardResult(
    string Name,
    int Width,
    int Height,
    bool HasSidebar,
    double ShapeDrawn,
    double ShapeDrawnSidebar,
    double ShapeDrawnContent,
    int TextReference,
    int TextMatched,
    int TextMissing,
    int TextExtra,
    int TextErrors,
    double PixelDrawn,
    double PixelDrawnSidebar,
    double PixelDrawnContent,
    double PixelDrawnLenient,
    double PixelTotal,
    double PixelSidebar,
    double PixelContent,
    long IgnoredPixels,
    bool SizeDiffers,
    double? MaxShape,
    double? MinText,
    int? MaxErrors,
    IReadOnlyList<string> Notes,
    string RunId = "")
{
    public double TextMatchedPercent => TextReference == 0 ? 100 : 100.0 * TextMatched / TextReference;

    public string Serialize()
    {
        var sb = new StringBuilder();
        void Line(string key, string value) => sb.Append(key).Append('=').Append(value).Append('\n');
        string D(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        string I(long v) => v.ToString(CultureInfo.InvariantCulture);
        Line("name", Name);
        Line("width", I(Width));
        Line("height", I(Height));
        Line("hasSidebar", HasSidebar ? "1" : "0");
        Line("shapeDrawn", D(ShapeDrawn));
        Line("shapeDrawnSidebar", D(ShapeDrawnSidebar));
        Line("shapeDrawnContent", D(ShapeDrawnContent));
        Line("textReference", I(TextReference));
        Line("textMatched", I(TextMatched));
        Line("textMissing", I(TextMissing));
        Line("textExtra", I(TextExtra));
        Line("textErrors", I(TextErrors));
        Line("pixelDrawn", D(PixelDrawn));
        Line("pixelDrawnSidebar", D(PixelDrawnSidebar));
        Line("pixelDrawnContent", D(PixelDrawnContent));
        Line("pixelDrawnLenient", D(PixelDrawnLenient));
        Line("pixelTotal", D(PixelTotal));
        Line("pixelSidebar", D(PixelSidebar));
        Line("pixelContent", D(PixelContent));
        Line("ignored", I(IgnoredPixels));
        Line("sizeDiffers", SizeDiffers ? "1" : "0");
        Line("maxShape", MaxShape is { } a ? D(a) : string.Empty);
        Line("minText", MinText is { } b ? D(b) : string.Empty);
        Line("maxErrors", MaxErrors is { } c ? I(c) : string.Empty);
        Line("runId", RunId);
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
        int I(string key) => int.Parse(values[key], CultureInfo.InvariantCulture);
        return new BoardResult(
            values["name"],
            I("width"),
            I("height"),
            values["hasSidebar"] == "1",
            D("shapeDrawn"),
            D("shapeDrawnSidebar"),
            D("shapeDrawnContent"),
            I("textReference"),
            I("textMatched"),
            I("textMissing"),
            I("textExtra"),
            I("textErrors"),
            D("pixelDrawn"),
            D("pixelDrawnSidebar"),
            D("pixelDrawnContent"),
            D("pixelDrawnLenient"),
            D("pixelTotal"),
            D("pixelSidebar"),
            D("pixelContent"),
            long.Parse(values["ignored"], CultureInfo.InvariantCulture),
            values["sizeDiffers"] == "1",
            values["maxShape"].Length == 0 ? null : D("maxShape"),
            values["minText"].Length == 0 ? null : D("minText"),
            values["maxErrors"].Length == 0 ? null : I("maxErrors"),
            notes,
            values.GetValueOrDefault("runId") ?? string.Empty);
    }
}

/// <summary>Everything one board produced: the renders, the comparisons and the texts.</summary>
internal sealed record BoardRun(
    DesignBoard Board,
    PixelImage Reference,
    PixelImage Actual,
    PixelImage ShapesReference,
    PixelImage ShapesActual,
    ImageComparison Pixels,
    ImageComparison PixelsLenient,
    ImageComparison Shapes,
    IReadOnlyList<TextItem> ReferenceTexts,
    IReadOnlyList<TextItem> AppTexts,
    TextComparison Texts,
    IReadOnlyList<string> Notes);

/// <summary>Writes the images and the report of the parity run to <c>artifacts/design-parity</c>.</summary>
internal static class ParityReport
{
    private static readonly Lock Gate = new();

    /// <summary>Tells the results of this process from the ones an earlier run left in the folder.</summary>
    private static readonly string RunId = Guid.NewGuid().ToString("N");

    /// <summary>Where the run writes; <c>MURMUR_PARITY_DIR</c> overrides the default <c>artifacts/design-parity</c> of the checkout.</summary>
    public static string Directory =>
        Environment.GetEnvironmentVariable("MURMUR_PARITY_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(RepoPaths.Root, "artifacts", "design-parity");

    /// <summary>Saves every image and the text report of a board and its result; then rebuilds <c>report.md</c>.</summary>
    public static BoardResult Write(BoardRun run)
    {
        var board = run.Board;
        System.IO.Directory.CreateDirectory(Directory);
        run.Actual.SavePng(Path.Combine(Directory, board.Name + ".app.png"));
        run.Pixels.Diff.SavePng(Path.Combine(Directory, board.Name + ".diff.png"));
        SideBySide(run.Reference, run.Actual, run.Pixels.Diff).SavePng(Path.Combine(Directory, board.Name + ".side.png"));
        SideBySide(run.ShapesReference, run.ShapesActual, run.Shapes.Diff).SavePng(Path.Combine(Directory, board.Name + ".shapes.side.png"));
        File.WriteAllText(Path.Combine(Directory, board.Name + ".text.md"), TextReport.Markdown(board, run.Texts));
        TextReport.Overlay(run.Reference, run.ReferenceTexts, run.AppTexts).SavePng(Path.Combine(Directory, board.Name + ".text.png"));

        var result = new BoardResult(
            board.Name,
            run.Reference.Width,
            run.Reference.Height,
            board.HasSidebar,
            run.Shapes.Drawn.Percent,
            run.Shapes.DrawnSidebar.Percent,
            run.Shapes.DrawnContent.Percent,
            run.Texts.ReferenceCount,
            run.Texts.Matches.Count,
            run.Texts.Missing.Count,
            run.Texts.Extra.Count,
            run.Texts.GeometryErrors,
            run.Pixels.Drawn.Percent,
            run.Pixels.DrawnSidebar.Percent,
            run.Pixels.DrawnContent.Percent,
            run.PixelsLenient.Drawn.Percent,
            run.Pixels.Total.Percent,
            run.Pixels.Sidebar.Percent,
            run.Pixels.Content.Percent,
            run.Pixels.IgnoredPixels,
            run.Pixels.SizeDiffers,
            board.MaxShapeMismatchPercent,
            board.MinTextMatchedPercent,
            board.MaxTextGeometryErrors,
            run.Notes,
            RunId);
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
        var newest = DateTime.MinValue;
        var newestRun = string.Empty;
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.result.txt"))
        {
            try
            {
                var result = BoardResult.Parse(File.ReadAllText(file));
                results[result.Name] = result;
                var written = File.GetLastWriteTimeUtc(file);
                if (written > newest)
                {
                    newest = written;
                    newestRun = result.RunId;
                }
            }
            catch (Exception ex) when (ex is KeyNotFoundException or FormatException or ArgumentOutOfRangeException)
            {
                // A result file of an older run with other fields: the board shows as "not rendered" until it runs again.
            }
        }

        string P(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        string P1(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("# Design parity: the app against the canvas\n\n");
        sb.Append("Every artboard of `docs/design/source` is rendered by the app headlessly (Avalonia.Headless + Skia, English, scale 1, the size and theme of the board) ")
          .Append("with the artboard's demo data (`DesignDemoData`) and compared with `docs/design/reference`. Text is judged by geometry, not by pixels (Skia and Chromium rasterise glyphs differently, see `calibration.md`).\n\n");
        sb.Append("- **Shape layer** (first columns): the app rendered with every glyph invisible against `<Board>.notext.png`, symmetric comparison at ")
          .Append(ImageComparer.DefaultTolerance.ToString(CultureInfo.InvariantCulture))
          .Append("/255 per channel in a 3x3 neighbourhood. **Shape drawn %** is the share of mismatching pixels among the pixels that are not page background in either image (total / sidebar / content; the sidebar is the strip x < ")
          .Append(ImageComparer.SidebarWidth.ToString(CultureInfo.InvariantCulture)).Append(" of the five main screens). Files: `<Board>.shapes.side.png` (reference | app | diff: red = only in the app, blue = only in the reference, purple = both).\n");
        sb.Append("- **Text** (`<Board>.text.md`, `<Board>.text.png`): every reference text node is looked up in the app by its text; **Text matched %** = found / in the reference; **geometry errors** = found texts whose position (|dx|, |dy centre| > ")
          .Append(TextTolerance.OffsetPx).Append(" px), total width (> ").Append(TextTolerance.WidthPercent).Append(" %), line count, font size, weight or family differ; **Missing** = in the reference, not in the app.\n");
        sb.Append("- **Pixels** (last columns, information only): the whole rendered frame against `<Board>.png`; text pixels cannot match exactly. Ignored rectangles are neither compared nor counted.\n\n");
        sb.Append("| Board | Size | Shape drawn % | Shape sidebar % | Shape content % | Text matched % | Text geometry errors | Missing texts | Gates (shape max / text min / errors max) | Pixel drawn % | Pixel drawn sidebar | Pixel drawn content | Pixel drawn at tol 24 | Pixel total % | Notes |\n");
        sb.Append("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | --- |\n");
        foreach (var board in DesignBoards.All)
        {
            if (!results.TryGetValue(board.Name, out var r))
            {
                sb.Append("| ").Append(board.Name).Append(" | ").Append(board.Width).Append('x').Append(board.Height).Append(" | not rendered | | | | | | | | | | | | |\n");
                continue;
            }

            var gates = (r.MaxShape is null && r.MinText is null && r.MaxErrors is null)
                ? "not enforced"
                : $"{(r.MaxShape is { } a ? P(a) : "—")} / {(r.MinText is { } b ? P1(b) : "—")} / {(r.MaxErrors is { } c ? c.ToString(CultureInfo.InvariantCulture) : "—")}";
            var stale = r.RunId != newestRun ? "STALE: from an earlier run" : null;
            var notes = string.Join("; ", r.Notes.Append(r.SizeDiffers ? "render size differs from the reference" : null!).Prepend(stale!).Where(n => n is not null));
            sb.Append("| ").Append(r.Name)
              .Append(" | ").Append(r.Width).Append('x').Append(r.Height)
              .Append(" | ").Append(P(r.ShapeDrawn))
              .Append(" | ").Append(r.HasSidebar ? P(r.ShapeDrawnSidebar) : "—")
              .Append(" | ").Append(P(r.ShapeDrawnContent))
              .Append(" | ").Append(P1(r.TextMatchedPercent)).Append(" (").Append(r.TextMatched).Append('/').Append(r.TextReference).Append(')')
              .Append(" | ").Append(r.TextErrors)
              .Append(" | ").Append(r.TextMissing)
              .Append(" | ").Append(gates)
              .Append(" | ").Append(P(r.PixelDrawn))
              .Append(" | ").Append(r.HasSidebar ? P(r.PixelDrawnSidebar) : "—")
              .Append(" | ").Append(P(r.PixelDrawnContent))
              .Append(" | ").Append(P(r.PixelDrawnLenient))
              .Append(" | ").Append(P(r.PixelTotal))
              .Append(" | ").Append(notes)
              .Append(" |\n");
        }

        sb.Append("\nFiles per board: `<Board>.app.png` (the app), `<Board>.diff.png` and `<Board>.side.png` (whole-pixel comparison), `<Board>.shapes.side.png`, `<Board>.text.md` (what to fix, worst first), `<Board>.text.png` (blue = reference line boxes, red = app line boxes).\n");

        sb.Append("\n## Ignored rectangles\n\n");
        sb.Append("Only content the app cannot reproduce (coordinates in the reference image). Rectangles marked *text only* are left out of the pixel comparison; the shape layer has no glyphs, so it does not need them.\n\n");
        foreach (var board in DesignBoards.All.Where(b => b.Ignored.Count > 0))
        {
            foreach (var rect in board.Ignored)
            {
                sb.Append("- `").Append(board.Name).Append("` (").Append(rect.X).Append(',').Append(rect.Y).Append(' ').Append(rect.Width).Append('x').Append(rect.Height).Append("): ").Append(rect.Reason).Append(rect.TextOnly ? " *(text only)*" : string.Empty).Append('\n');
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
