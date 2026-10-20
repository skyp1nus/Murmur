using System.Globalization;

namespace Murmur.App.Tests.Design;

/// <summary>How far an app text may be from the reference one before it counts as a geometry error.</summary>
internal static class TextTolerance
{
    public const double OffsetPx = 2;
    public const double WidthPercent = 3;
    public const double FontSizePx = 0.5;
}

/// <summary>A reference text found in the app, with what differs.</summary>
internal sealed record TextMatch(TextItem Reference, TextItem App, IReadOnlyList<string> Problems, double Severity)
{
    public double DeltaX => App.First.X - Reference.First.X;

    public double DeltaCenterY => App.First.CenterY - Reference.First.CenterY;

    public double WidthPercent => Reference.TotalWidth <= 0 ? 0 : 100 * (App.TotalWidth - Reference.TotalWidth) / Reference.TotalWidth;

    public bool HasProblems => Problems.Count > 0;
}

internal sealed class TextComparison
{
    public required IReadOnlyList<TextMatch> Matches { get; init; }

    /// <summary>In the reference, not in the app.</summary>
    public required IReadOnlyList<TextItem> Missing { get; init; }

    /// <summary>In the app, not in the reference (informational).</summary>
    public required IReadOnlyList<TextItem> Extra { get; init; }

    public int ReferenceCount => Matches.Count + Missing.Count;

    public double MatchedPercent => ReferenceCount == 0 ? 100 : 100.0 * Matches.Count / ReferenceCount;

    public int GeometryErrors => Matches.Count(m => m.HasProblems);

    /// <summary>Matches with problems, worst first.</summary>
    public IEnumerable<TextMatch> BySeverity => Matches.Where(m => m.HasProblems).OrderByDescending(m => m.Severity);
}

/// <summary>
/// Matches the texts of the canvas with the texts of the app by normalised text (case-sensitive, whitespace collapsed) and
/// measures each pair: first-line left edge and vertical centre, line count, total line width, font size, weight and family.
/// </summary>
internal static class TextMatcher
{
    public static TextComparison Compare(IReadOnlyList<TextItem> reference, IReadOnlyList<TextItem> app)
    {
        var pairs = new List<(int Reference, int App, double Distance)>();
        var byText = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var j = 0; j < app.Count; j++)
        {
            if (!byText.TryGetValue(app[j].Text, out var list))
            {
                byText[app[j].Text] = list = [];
            }

            list.Add(j);
        }

        for (var i = 0; i < reference.Count; i++)
        {
            if (!byText.TryGetValue(reference[i].Text, out var candidates))
            {
                continue;
            }

            foreach (var j in candidates)
            {
                var dx = app[j].First.X - reference[i].First.X;
                var dy = app[j].First.CenterY - reference[i].First.CenterY;
                pairs.Add((i, j, Math.Sqrt((dx * dx) + (dy * dy))));
            }
        }

        // Duplicates ("Library" in the sidebar and in the breadcrumb): the closest pairs are taken first.
        var referenceTaken = new bool[reference.Count];
        var appTaken = new bool[app.Count];
        var matched = new TextMatch?[reference.Count];
        foreach (var (i, j, _) in pairs.OrderBy(p => p.Distance).ThenBy(p => p.Reference).ThenBy(p => p.App))
        {
            if (referenceTaken[i] || appTaken[j])
            {
                continue;
            }

            referenceTaken[i] = true;
            appTaken[j] = true;
            matched[i] = Measure(reference[i], app[j]);
        }

        return new TextComparison
        {
            Matches = matched.Where(m => m is not null).Select(m => m!).ToArray(),
            Missing = reference.Where((_, i) => !referenceTaken[i]).ToArray(),
            Extra = app.Where((_, j) => !appTaken[j]).ToArray(),
        };
    }

    public static TextMatch Measure(TextItem reference, TextItem app)
    {
        var problems = new List<string>();
        var severity = 0.0;
        var dx = app.First.X - reference.First.X;
        var dy = app.First.CenterY - reference.First.CenterY;
        if (Math.Abs(dx) > TextTolerance.OffsetPx)
        {
            problems.Add(Format($"x {dx:+0.#;-0.#} px"));
            severity += Math.Abs(dx) / TextTolerance.OffsetPx;
        }

        if (Math.Abs(dy) > TextTolerance.OffsetPx)
        {
            problems.Add(Format($"y {dy:+0.#;-0.#} px"));
            severity += Math.Abs(dy) / TextTolerance.OffsetPx;
        }

        if (app.Lines.Count != reference.Lines.Count)
        {
            problems.Add($"lines {reference.Lines.Count} -> {app.Lines.Count}");
            severity += 5 * Math.Abs(app.Lines.Count - reference.Lines.Count);
        }

        var widthPercent = reference.TotalWidth <= 0 ? 0 : 100 * (app.TotalWidth - reference.TotalWidth) / reference.TotalWidth;
        if (Math.Abs(widthPercent) > TextTolerance.WidthPercent)
        {
            problems.Add(Format($"width {widthPercent:+0.#;-0.#} %"));
            severity += Math.Abs(widthPercent) / TextTolerance.WidthPercent;
        }

        if (Math.Abs(app.FontSize - reference.FontSize) > TextTolerance.FontSizePx)
        {
            problems.Add(Format($"size {reference.FontSize:0.#} -> {app.FontSize:0.#}"));
            severity += 3;
        }

        if (app.FontWeight != reference.FontWeight)
        {
            problems.Add($"weight {reference.FontWeight} -> {app.FontWeight}");
            severity += 3;
        }

        if (app.Mono != reference.Mono)
        {
            problems.Add(reference.Mono ? "family Geist Mono -> Inter" : "family Inter -> Geist Mono");
            severity += 3;
        }

        return new TextMatch(reference, app, problems, severity);
    }

    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
