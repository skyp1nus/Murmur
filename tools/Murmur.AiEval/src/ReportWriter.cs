using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Murmur.AiEval;

public static class ReportWriter
{
    public const int AnswerPreviewLength = 120;

    public static string ToJson(EvalReport report) =>
        JsonSerializer.Serialize(report, EvalJsonContext.Readable.EvalReport).ReplaceLineEndings("\n") + "\n";

    /// <summary>GitHub-flavoured markdown: a headline and one row per question.</summary>
    public static string ToMarkdown(EvalReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var inv = CultureInfo.InvariantCulture;
        var md = new StringBuilder();
        md.Append("### AI eval (").Append(ModeLabel(report.Mode)).Append("): ")
            .Append(report.Success ? "PASS" : "FAIL").Append(' ')
            .Append(report.Passed.ToString(inv)).Append('/').Append(report.Total.ToString(inv))
            .Append(" (").Append((report.Rate * 100).ToString("0", inv)).Append(" %, threshold ")
            .Append((report.Threshold * 100).ToString("0", inv)).Append(" %)\n\n");
        if (report.Mode == EvalMode.Full)
        {
            md.Append("Answerable passes with the right timestamp written by the model itself (not the top-excerpt fallback): ")
                .Append(report.ExplicitPassed.ToString(inv)).Append(".\n\n");
        }

        md.Append("Fixture `").Append(report.Fixture).Append("` (").Append(report.Segments.ToString(inv)).Append(" segments, ")
            .Append(report.Timings.Chunks.ToString(inv)).Append(" chunks). ");
        md.Append("Chat: ").Append(ModelLabel(report.Models.Chat)).Append(", embeddings: ").Append(ModelLabel(report.Models.Embedding)).Append(". ");
        md.Append("Index ").Append(Seconds(report.Timings.IndexMs)).Append(", chat load ").Append(Seconds(report.Timings.ChatLoadMs))
            .Append(", avg answer ").Append(Seconds(report.Timings.AvgAnswerMs)).Append(", total ").Append(Seconds(report.Timings.TotalMs))
            .Append(". ").Append(report.Machine.Processors.ToString(inv)).Append(" CPUs, ").Append(Cell(report.Machine.Os)).Append(".\n\n");

        md.Append("| # | Question | Expected | Cited | Pass | Latency, ms | Answer |\n");
        md.Append("|---|---|---|---|---|---:|---|\n");
        foreach (var q in report.PerQuestion)
        {
            md.Append("| ").Append(q.Id)
                .Append(" | ").Append(Cell(q.Question))
                .Append(" | ").Append(q.Expected is null ? "not in recording" : q.Expected + " ±" + q.ToleranceSeconds.ToString(inv) + " s")
                .Append(" | ").Append(q.Cited.Count == 0 ? "-" : Cell(string.Join(", ", q.Cited)))
                .Append(" | ").Append(q.Skipped ? "skip" : q.Pass ? "yes" : "**no**")
                .Append(" | ").Append(q.LatencyMs.ToString(inv))
                .Append(" | ").Append(Cell(Preview(q.Answer)))
                .Append(" |\n");
        }

        return md.ToString();
    }

    /// <summary>The first <see cref="AnswerPreviewLength"/> characters on one line.</summary>
    public static string Preview(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= AnswerPreviewLength ? line : line[..AnswerPreviewLength] + "…";
    }

    private static string Cell(string text) => text.ReplaceLineEndings(" ").Replace("|", "\\|", StringComparison.Ordinal);

    private static string Seconds(long ms) => (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";

    private static string ModelLabel(ModelFile? file) =>
        file is null ? "none" : file.Bytes is null ? Cell(file.Name) + " (missing)" : Cell(file.Name);

    private static string ModeLabel(EvalMode mode) => mode switch
    {
        EvalMode.Full => "full: Gemma answers",
        EvalMode.RetrievalHybrid => "retrieval only: hybrid search",
        _ => "retrieval only: keyword search",
    };
}
