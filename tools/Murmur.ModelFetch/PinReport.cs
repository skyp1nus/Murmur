using System.Text;
using System.Text.Json;

namespace Murmur.ModelFetch;

/// <summary>Formats resolved pins as a table, a paste-ready C# snippet and JSON (the same text goes to the console and the job summary).</summary>
internal static class PinReport
{
    public static string Render(IReadOnlyList<PinRow> rows)
    {
        var text = new StringBuilder();
        text.AppendLine("### Upstream pins (--resolve-pins)").AppendLine();
        text.AppendLine("| Item | Source | Revision | File | Size (bytes) | SHA-256 | Pin | Note |").AppendLine("|---|---|---|---|---:|---|---|---|");
        foreach (var row in rows)
        {
            text.AppendLine(
                $"| `{row.Item}` | {Cell(row.Source)} | {(row.Revision is null ? "" : $"`{row.Revision}`")} | `{row.File}` | {row.Size} | {(row.Sha256 is null ? "" : $"`{row.Sha256}`")} | {row.Status} | {Cell(row.Note ?? "")} |");
        }

        text.AppendLine();
        var drifted = rows.Count(r => r.Status == "DRIFT");
        var failed = rows.Count(r => r.Status == "ERROR");
        text.AppendLine(drifted == 0 ? "No pinned hash or size differs from upstream." : $"**{drifted} pinned source(s) DRIFTED from upstream** (see the Pin column).");
        if (failed > 0)
        {
            text.AppendLine($"**{failed} source(s) could not be resolved.**");
        }

        text.AppendLine().AppendLine("### Paste into ModelCatalog").AppendLine().AppendLine("```csharp");
        text.AppendLine("// ModelCatalog.HuggingFaceRevisions (a repository listed there is downloaded from resolve/<sha>)");
        foreach (var revision in rows.Where(r => r.Host == "huggingface" && r.Revision is not null && r.Status != "ERROR").Select(r => (r.Source, r.Revision)).Distinct().OrderBy(r => r.Source, StringComparer.Ordinal))
        {
            text.AppendLine($"[\"{revision.Source}\"] = \"{revision.Revision}\",");
        }

        text.AppendLine().AppendLine("// Pins: re-pin every file of a repository together with its revision");
        foreach (var row in rows.Where(r => r.Status != "ERROR" && r.Size is not null))
        {
            var sha = row.Sha256 is null ? "null" : $"\"{row.Sha256}\"";
            text.AppendLine($"// {row.Item}: {row.Source}@{row.Revision}");
            text.AppendLine(
                row.IsGguf ? $"new GgufSource(\"{row.Source}\", \"{row.File}\", {sha}, {Underscored(row.Size!.Value)})"
                : row.Host == "huggingface" ? $"\"{row.File}\" => ({sha}, {Underscored(row.Size!.Value)}),"
                : $"({sha}, {Underscored(row.Size!.Value)}) // {row.File}");
        }

        text.AppendLine("```").AppendLine().AppendLine("### JSON").AppendLine().AppendLine("```json");
        text.AppendLine(JsonSerializer.Serialize(rows.ToList(), PinJsonContext.Default.ListPinRow));
        text.AppendLine("```").AppendLine();
        return text.ToString();
    }

    private static string Underscored(long value) => value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', '_');

    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' ');
}
