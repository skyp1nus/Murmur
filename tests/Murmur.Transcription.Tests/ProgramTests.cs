namespace Murmur.Transcription.Tests;

public sealed class ProgramTests
{
    [Theory]
    [InlineData]
    [InlineData("--help")]
    [InlineData("transcribe", "file.wav")]
    public void Run_WithoutImplementedCommands_PrintsUsageToStderrAndReturns2(params string[] args)
    {
        using var error = new StringWriter();

        var exitCode = Program.Run(args, error);

        Assert.Equal(2, exitCode);
        Assert.Equal(Program.UsageExitCode, exitCode);
        Assert.Contains("usage:", error.ToString(), StringComparison.Ordinal);
    }
}
