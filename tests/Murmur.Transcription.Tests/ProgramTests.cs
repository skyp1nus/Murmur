namespace Murmur.Transcription.Tests;

public sealed class ProgramTests
{
    [Theory]
    [InlineData]
    [InlineData("transcribe", "file.wav")]
    [InlineData("--bogus")]
    [InlineData("--bench", "--model-dir", "m")]
    public void Run_WithoutAUsableCommand_PrintsUsageToStderrAndReturns2(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = Program.Run(args, output, error);

        Assert.Equal(Program.UsageExitCode, exitCode);
        Assert.Equal(2, exitCode);
        Assert.Contains("usage:", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public void Run_Help_PrintsUsageToStdoutAndReturns0()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = Program.Run(["--help"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("--serve", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_TranscribeOfMissingFile_Returns1WithMessage()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = Program.Run(["--transcribe", "/nonexistent/x.wav", "--model-dir", "/nonexistent", "--vad", "/nonexistent/v.onnx"], output, error);

        Assert.Equal(Program.FailureExitCode, exitCode);
        Assert.Contains("not found", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EngineOptionsFrom_SplitsTheThreadBudgetIntoWorkers()
    {
        Assert.True(CliOptions.TryParse(["--serve", "--model-dir", "m", "--vad", "v", "--threads", "4"], out var o, out _));
        var e = Program.EngineOptionsFrom(o);
        Assert.Equal(4, e.Pipeline.Parallelism);
        Assert.Equal(1, e.Threads);

        Assert.True(CliOptions.TryParse(["--serve", "--model-dir", "m", "--vad", "v", "--threads", "4", "--parallel", "2", "--max-speech", "10"], out o, out _));
        e = Program.EngineOptionsFrom(o);
        Assert.Equal(2, e.Pipeline.Parallelism);
        Assert.Equal(2, e.Threads);
        Assert.Equal(10f, e.Pipeline.Vad.MaxSpeechSeconds);
        Assert.Equal(0.5f, e.Pipeline.Vad.Threshold);
    }
}
