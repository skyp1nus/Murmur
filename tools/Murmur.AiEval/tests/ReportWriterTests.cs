using System.Text.Json;

namespace Murmur.AiEval.Tests;

public sealed class ReportWriterTests
{
    private static EvalReport Report(EvalMode mode = EvalMode.Full) => new()
    {
        Mode = mode,
        Passed = 1,
        Total = 2,
        Rate = 0.5,
        ExplicitPassed = 1,
        Threshold = 0.8,
        Success = false,
        Fixture = "meeting-60min.json",
        Segments = 651,
        Models = new ModelFiles(new ModelFile("gemma.gguf", "/m/gemma.gguf", 42), null, null),
        Timings = new EvalTimings { IndexMs = 12_345, AvgAnswerMs = 2_000, Chunks = 63 },
        Machine = new MachineInfo("Linux", "X64", 4, ".NET 11"),
        PerQuestion =
        [
            new QuestionResult
            {
                Id = "q01",
                Question = "Скільки | грошей?",
                Answerable = true,
                Expected = "07:42",
                ExpectedMs = 462_000,
                ToleranceSeconds = 60,
                Query = "Скільки | грошей?",
                Cited = ["07:30"],
                Pass = true,
                LatencyMs = 1500,
                Answer = new string('а', 200) + "\nкінець",
            },
            new QuestionResult
            {
                Id = "q11",
                Question = "Зарплата?",
                Answerable = false,
                ToleranceSeconds = 60,
                Query = "Зарплата?",
                Cited = [],
                Pass = false,
                LatencyMs = 2500,
                Answer = "Запропонували 3000.",
            },
        ],
    };

    [Fact]
    public void Markdown_HasAHeadlineAndOneRowPerQuestion()
    {
        var md = ReportWriter.ToMarkdown(Report());
        var lines = md.Split('\n');
        Assert.StartsWith("### AI eval (full: Gemma answers): FAIL 1/2 (50 %, threshold 80 %)", md, StringComparison.Ordinal);
        Assert.Contains("| # | Question | Expected | Cited | Pass | Latency, ms | Answer |", lines);
        Assert.Contains(lines, l => l.StartsWith("| q01 | Скільки \\| грошей? | 07:42 ±60 s | 07:30 | yes | 1500 | ", StringComparison.Ordinal));
        Assert.Contains("| q11 | Зарплата? | not in recording | - | **no** | 2500 | Запропонували 3000. |", lines);
        Assert.Contains("Index 12.3 s", md, StringComparison.Ordinal);
        Assert.Contains("gemma.gguf", md, StringComparison.Ordinal);
        Assert.Contains("written by the model itself", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_RetrievalMode_SaysSo()
    {
        var md = ReportWriter.ToMarkdown(Report(EvalMode.RetrievalKeyword));
        Assert.StartsWith("### AI eval (retrieval only: keyword search)", md, StringComparison.Ordinal);
        Assert.DoesNotContain("written by the model itself", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_IsTheFirst120CharactersOnOneLine()
    {
        var preview = ReportWriter.Preview(new string('а', 200) + "\nкінець");
        Assert.Equal(new string('а', 120) + "…", preview);
        Assert.Equal("коротко і ясно", ReportWriter.Preview(" коротко\nі ясно "));
    }

    [Fact]
    public void Json_HasTheContractFields_AndRoundTrips()
    {
        var json = ReportWriter.ToJson(Report());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("passed").GetInt32());
        Assert.Equal(2, root.GetProperty("total").GetInt32());
        Assert.Equal(0.5, root.GetProperty("rate").GetDouble());
        Assert.Equal("Full", root.GetProperty("mode").GetString());
        Assert.Equal(2, root.GetProperty("perQuestion").GetArrayLength());
        Assert.Equal(12_345, root.GetProperty("timings").GetProperty("indexMs").GetInt64());
        Assert.Equal(2_000, root.GetProperty("timings").GetProperty("avgAnswerMs").GetInt64());
        Assert.Equal("gemma.gguf", root.GetProperty("models").GetProperty("chat").GetProperty("name").GetString());
        Assert.Contains("Скільки", json, StringComparison.Ordinal);

        var parsed = JsonSerializer.Deserialize(json, EvalJsonContext.Default.EvalReport)!;
        Assert.Equal("q11", parsed.PerQuestion[1].Id);
    }
}
