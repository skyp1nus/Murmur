using System.Globalization;

namespace Murmur.App.Tests.Design;

/// <summary>How far an app text may be from the reference one before it counts as a geometry error.</summary>
internal static class TextTolerance
{
    public const double OffsetPx = 2;
    public const double BaselinePx = 2;
    public const double WidthPercent = 3;

    /// <summary>A width within half a pixel is never an error: 3 % of a full stop is 0.14 px, less than the two rasterisers agree on.</summary>
    public const double WidthPx = 0.5;
    public const double FontSizePx = 0.5;

    /// <summary>The farthest an app text may be from the reference one and still be taken for it (duplicates, fragments).</summary>
    public const double MatchDistancePx = 200;
}

/// <summary>A canvas text that the app replaces with a real value (a placeholder like <c>[VERSION]</c>); it is left out of the gates.</summary>
internal sealed record ExcludedText(string Text, string Reason, bool Contains = false)
{
    public bool Covers(string text) => Contains ? text.Contains(Text, StringComparison.Ordinal) : string.Equals(text, Text, StringComparison.Ordinal);
}

/// <summary>How a reference text was found in the app.</summary>
internal enum MatchKind
{
    /// <summary>An app text with exactly this text.</summary>
    Exact,

    /// <summary>The reference text is a fragment of its block; the app has the whole block as one text.</summary>
    BlockText,

    /// <summary>The reference text is part of a longer app text.</summary>
    Fragment,
}

/// <summary>A reference text found in the app, with what differs.</summary>
internal sealed record TextMatch(TextItem Reference, TextItem App, IReadOnlyList<string> Problems, double Severity, MatchKind Via = MatchKind.Exact)
{
    public double DeltaX => App.First.X - Reference.First.X;

    /// <summary>Baseline for ordinary texts, vertical centre for placeholders.</summary>
    public double DeltaY => Reference.Placeholder || double.IsNaN(Reference.First.Baseline) ? App.First.CenterY - Reference.First.CenterY : App.First.Baseline - Reference.First.Baseline;

    public bool HasProblems => Problems.Count > 0;

    public string ViaText => Via switch
    {
        MatchKind.Exact => "text",
        MatchKind.BlockText => "block text",
        _ => "fragment",
    };
}

internal sealed class TextComparison
{
    public required IReadOnlyList<TextMatch> Matches { get; init; }

    /// <summary>In the reference, not in the app.</summary>
    public required IReadOnlyList<TextItem> Missing { get; init; }

    /// <summary>In the app, not in the reference (informational).</summary>
    public required IReadOnlyList<TextItem> Extra { get; init; }

    /// <summary>Canvas placeholders left out of the gates, with the reason.</summary>
    public IReadOnlyList<(TextItem Item, string Reason)> Excluded { get; init; } = [];

    public int ReferenceCount => Matches.Count + Missing.Count;

    public double MatchedPercent => ReferenceCount == 0 ? 100 : 100.0 * Matches.Count / ReferenceCount;

    public int GeometryErrors => Matches.Count(m => m.HasProblems);

    /// <summary>Matches with problems, worst first.</summary>
    public IEnumerable<TextMatch> BySeverity => Matches.Where(m => m.HasProblems).OrderByDescending(m => m.Severity);
}

/// <summary>
/// Matches the texts of the canvas with the texts of the app and measures each pair: first-line left edge and baseline, line count,
/// total line width, font size, weight and family. Texts are matched by normalised text (case-sensitive, whitespace collapsed);
/// a canvas text the app has no exact twin for is looked for as a part of its block, then as a part of any app text. Duplicated
/// texts and fragments are only paired within <see cref="TextTolerance.MatchDistancePx"/>; a text that is unique on both sides is
/// found wherever it is.
/// </summary>
internal static class TextMatcher
{
    public static TextComparison Compare(IReadOnlyList<TextItem> reference, IReadOnlyList<TextItem> app, IReadOnlyList<ExcludedText>? excluded = null)
    {
        var kept = new List<TextItem>();
        var left = new List<(TextItem, string)>();
        foreach (var item in reference)
        {
            var rule = excluded?.FirstOrDefault(e => e.Covers(item.Text));
            if (rule is null)
            {
                kept.Add(item);
            }
            else
            {
                left.Add((item, rule.Reason));
            }
        }

        reference = kept;
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

        var referenceCounts = reference.GroupBy(r => r.Text, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        for (var i = 0; i < reference.Count; i++)
        {
            if (!byText.TryGetValue(reference[i].Text, out var candidates))
            {
                continue;
            }

            // A text that is alone on both sides is that text, wherever it is (the geometry tells how far off); a duplicate
            // is only taken for its twin when it is near, otherwise it is another text.
            var unique = candidates.Count == 1 && referenceCounts[reference[i].Text] == 1;
            foreach (var j in candidates)
            {
                var distance = Distance(reference[i].First, app[j].First);
                if (unique || distance <= TextTolerance.MatchDistancePx)
                {
                    pairs.Add((i, j, distance));
                }
            }
        }

        // Duplicates ("Library" in the sidebar and in the breadcrumb): the closest pairs are taken first; beyond the limit it is another text.
        var referenceTaken = new bool[reference.Count];
        var appUsed = new bool[app.Count];
        var matched = new TextMatch?[reference.Count];
        foreach (var (i, j, _) in pairs.OrderBy(p => p.Distance).ThenBy(p => p.Reference).ThenBy(p => p.App))
        {
            if (referenceTaken[i] || appUsed[j])
            {
                continue;
            }

            referenceTaken[i] = true;
            appUsed[j] = true;
            matched[i] = Measure(reference[i], app[j]);
        }

        // The rest, in document order: as a part of its block, then as a part of any app text.
        var cursors = new Dictionary<int, int>();
        for (var i = 0; i < reference.Count; i++)
        {
            if (referenceTaken[i])
            {
                continue;
            }

            var found = FromBlock(reference[i], app, cursors) ?? FromFragment(reference[i], app);
            if (found is { } hit)
            {
                referenceTaken[i] = true;
                appUsed[hit.AppIndex] = true;
                matched[i] = hit.Match;
            }
        }

        return new TextComparison
        {
            Matches = matched.Where(m => m is not null).Select(m => m!).ToArray(),
            Missing = reference.Where((_, i) => !referenceTaken[i]).ToArray(),
            Extra = app.Where((_, j) => !appUsed[j]).ToArray(),
            Excluded = left,
        };
    }

    /// <summary>(a) The whole block of the reference text is one app text: the fragment is where its words are in that text.</summary>
    private static (int AppIndex, TextMatch Match)? FromBlock(TextItem reference, IReadOnlyList<TextItem> app, Dictionary<int, int> cursors)
    {
        if (reference.Placeholder || reference.BlockText.Length == 0 || reference.BlockText == reference.Text)
        {
            return null;
        }

        var from = cursors.GetValueOrDefault(reference.BlockId);
        var at = reference.BlockText.IndexOf(reference.Text, from, StringComparison.Ordinal);
        if (at < 0)
        {
            at = reference.BlockText.IndexOf(reference.Text, StringComparison.Ordinal);
        }

        if (at < 0)
        {
            return null;
        }

        (int AppIndex, TextMatch Match)? best = null;
        var bestDistance = double.MaxValue;
        for (var j = 0; j < app.Count; j++)
        {
            if (app[j].Layout is null || app[j].Text != reference.BlockText)
            {
                continue;
            }

            var lines = app[j].Layout!.Range(at, reference.Text.Length);
            if (lines.Count == 0)
            {
                continue;
            }

            var distance = Distance(reference.First, lines[0]);
            if (distance <= TextTolerance.MatchDistancePx && distance < bestDistance)
            {
                bestDistance = distance;
                best = (j, Measure(reference, app[j] with { Text = reference.Text, Lines = lines }, MatchKind.BlockText));
            }
        }

        if (best is not null)
        {
            cursors[reference.BlockId] = at + reference.Text.Length;
        }

        return best;
    }

    /// <summary>(b) The reference text is a part of a longer app text: the nearest occurrence within the limit.</summary>
    private static (int AppIndex, TextMatch Match)? FromFragment(TextItem reference, IReadOnlyList<TextItem> app)
    {
        if (reference.Placeholder)
        {
            return null;
        }

        (int AppIndex, TextMatch Match)? best = null;
        var bestDistance = double.MaxValue;
        for (var j = 0; j < app.Count; j++)
        {
            if (app[j].Layout is null || app[j].Text.Length <= reference.Text.Length)
            {
                continue;
            }

            for (var at = app[j].Text.IndexOf(reference.Text, StringComparison.Ordinal); at >= 0; at = app[j].Text.IndexOf(reference.Text, at + 1, StringComparison.Ordinal))
            {
                var lines = app[j].Layout!.Range(at, reference.Text.Length);
                if (lines.Count == 0)
                {
                    continue;
                }

                var distance = Distance(reference.First, lines[0]);
                if (distance <= TextTolerance.MatchDistancePx && distance < bestDistance)
                {
                    bestDistance = distance;
                    best = (j, Measure(reference, app[j] with { Text = reference.Text, Lines = lines }, MatchKind.Fragment));
                }
            }
        }

        return best;
    }

    private static double Distance(LineBox a, LineBox b)
    {
        var dx = a.X - b.X;
        var dy = a.CenterY - b.CenterY;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    public static TextMatch Measure(TextItem reference, TextItem app, MatchKind via = MatchKind.Exact)
    {
        var problems = new List<string>();
        var severity = 0.0;
        var dx = app.First.X - reference.First.X;
        if (Math.Abs(dx) > TextTolerance.OffsetPx)
        {
            problems.Add(Format($"x {dx:+0.#;-0.#} px"));
            severity += Math.Abs(dx) / TextTolerance.OffsetPx;
        }

        if (reference.Placeholder || double.IsNaN(reference.First.Baseline) || double.IsNaN(app.First.Baseline))
        {
            // A placeholder is centred on its input; there is no line to take a baseline of.
            var dy = app.First.CenterY - reference.First.CenterY;
            if (Math.Abs(dy) > TextTolerance.OffsetPx)
            {
                problems.Add(Format($"y (centre) {dy:+0.#;-0.#} px"));
                severity += Math.Abs(dy) / TextTolerance.OffsetPx;
            }
        }
        else
        {
            var dy = app.First.Baseline - reference.First.Baseline;
            if (Math.Abs(dy) > TextTolerance.BaselinePx)
            {
                problems.Add(Format($"baseline {dy:+0.#;-0.#} px"));
                severity += Math.Abs(dy) / TextTolerance.BaselinePx;
            }
        }

        if (!reference.Placeholder)
        {
            if (app.Lines.Count != reference.Lines.Count)
            {
                problems.Add($"lines {reference.Lines.Count} -> {app.Lines.Count}");
                severity += 5 * Math.Abs(app.Lines.Count - reference.Lines.Count);
            }

            // A trimmed text ends where the space ends: its width is not the text's.
            var widthPercent = reference.TotalWidth <= 0 ? 0 : 100 * (app.TotalWidth - reference.TotalWidth) / reference.TotalWidth;
            if (!app.Trimmed && Math.Abs(widthPercent) > TextTolerance.WidthPercent && Math.Abs(app.TotalWidth - reference.TotalWidth) > TextTolerance.WidthPx)
            {
                problems.Add(Format($"width {widthPercent:+0.#;-0.#} %"));
                severity += Math.Abs(widthPercent) / TextTolerance.WidthPercent;
            }
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

        return new TextMatch(reference, app, problems, severity, via);
    }

    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
