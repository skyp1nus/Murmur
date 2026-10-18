namespace Murmur.AiEval.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void Parse_RequiredArguments()
    {
        var parsed = CommandLine.Parse(["--fixture", "f.json", "--questions", "q.json", "--out", "o.json"]);
        Assert.Null(parsed.Error);
        var eval = Assert.IsType<EvalOptions>(parsed.Eval);
        Assert.Equal("f.json", eval.FixturePath);
        Assert.Equal("q.json", eval.QuestionsPath);
        Assert.Equal("o.json", eval.OutPath);
        Assert.Null(eval.DataDir);
        Assert.Null(eval.MarkdownPath);
        Assert.False(eval.RetrievalOnly);
        Assert.Equal(0.8, eval.Threshold);
        Assert.Equal(3, eval.TopK);
        Assert.Equal(512, eval.MaxOutputTokens);
        Assert.Null(eval.Threads);
    }

    [Fact]
    public void Parse_AllOptions()
    {
        var parsed = CommandLine.Parse(
        [
            "--retrieval-only", "--fixture", "f", "--questions", "q", "--out", "o", "--data-dir", "/tmp/x",
            "--markdown", "t.md", "--threshold", "0.75", "--top", "5", "--max-output-tokens", "256", "--threads", "4",
        ]);
        var eval = Assert.IsType<EvalOptions>(parsed.Eval);
        Assert.True(eval.RetrievalOnly);
        Assert.Equal("/tmp/x", eval.DataDir);
        Assert.Equal("t.md", eval.MarkdownPath);
        Assert.Equal(0.75, eval.Threshold);
        Assert.Equal(5, eval.TopK);
        Assert.Equal(256, eval.MaxOutputTokens);
        Assert.Equal(4, eval.Threads);
    }

    [Fact]
    public void Parse_GenerateFixture()
    {
        var parsed = CommandLine.Parse(["--generate-fixture", "tests/fixtures"]);
        Assert.Equal("tests/fixtures", parsed.GenerateFixtureDir);
        Assert.Null(parsed.Eval);
        Assert.Null(parsed.Error);
    }

    [Theory]
    [InlineData("--bogus", "Unknown option: --bogus")]
    [InlineData("--out", "--out needs a value.")]
    [InlineData("stray", "Unknown argument: stray")]
    public void Parse_ExplainsTheError(string arg, string error) => Assert.Equal(error, CommandLine.Parse([arg]).Error);

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public void Parse_Help(string arg) => Assert.True(CommandLine.Parse([arg]).Help);

    [Theory]
    [InlineData("")]
    [InlineData("--fixture f --questions q")]
    [InlineData("--fixture --questions q --out o")]
    [InlineData("--fixture f --questions q --out")]
    [InlineData("--fixture f --questions q --out o --bogus 1")]
    [InlineData("stray")]
    [InlineData("--fixture f --questions q --out o --threshold 1.5")]
    [InlineData("--fixture f --questions q --out o --threshold abc")]
    [InlineData("--fixture f --questions q --out o --top 0")]
    [InlineData("--fixture f --questions q --out o --max-output-tokens -1")]
    [InlineData("--fixture f --questions q --out o --threads 0")]
    public void Parse_RejectsBadInput(string commandLine)
    {
        var parsed = CommandLine.Parse(commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.NotNull(parsed.Error);
        Assert.Null(parsed.Eval);
    }
}
