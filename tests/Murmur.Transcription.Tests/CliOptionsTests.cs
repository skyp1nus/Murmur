namespace Murmur.Transcription.Tests;

public sealed class CliOptionsTests
{
    [Fact]
    public void Serve_ParsesAllOptions()
    {
        Assert.True(CliOptions.TryParse(
            ["--serve", "--model-dir", "/m", "--vad", "/v.onnx", "--threads", "3", "--idle-exit-seconds", "5"],
            out var o,
            out var error), error);

        Assert.Equal(WorkerCommand.Serve, o.Command);
        Assert.Equal("/m", o.ModelDirectory);
        Assert.Equal("/v.onnx", o.VadModelPath);
        Assert.Equal(3, o.Threads);
        Assert.Equal(5, o.IdleExitSeconds);
        Assert.Equal(3, o.EffectiveParallelism);
        Assert.Equal(1, o.OrtThreadsPerWorker);
    }

    [Fact]
    public void Defaults_FollowTheSpec()
    {
        Assert.True(CliOptions.TryParse(["--serve", "--model-dir", "/m", "--vad", "/v"], out var o, out _));

        Assert.Equal(Environment.ProcessorCount, o.Threads);
        Assert.Equal(120, o.IdleExitSeconds);
        Assert.Equal(1, o.BatchSize);
        Assert.Null(o.Parallelism);
    }

    [Fact]
    public void Bench_SplitsTheWavList()
    {
        Assert.True(CliOptions.TryParse(
            ["--bench", "--model-dir", "/m", "--vad", "/v", "--wav", "a.wav, b.wav,,c.wav", "--seconds", "60", "--out", "r.json"],
            out var o,
            out _));

        Assert.Equal(["a.wav", "b.wav", "c.wav"], o.BenchWavs);
        Assert.Equal(60, o.BenchSeconds);
        Assert.Equal("r.json", o.OutputPath);
    }

    [Fact]
    public void ParallelIsCappedByTheThreadBudget()
    {
        Assert.True(CliOptions.TryParse(["--serve", "--model-dir", "/m", "--vad", "/v", "--threads", "2", "--parallel", "8"], out var o, out _));

        Assert.Equal(2, o.EffectiveParallelism);
        Assert.Equal(1, o.OrtThreadsPerWorker);
    }

    [Theory]
    [InlineData("--serve", "--model-dir", "/m")]
    [InlineData("--serve", "--vad", "/v")]
    [InlineData("--serve", "--bench", "--model-dir", "/m", "--vad", "/v")]
    [InlineData("--serve", "--model-dir", "/m", "--vad", "/v", "--threads", "0")]
    [InlineData("--serve", "--model-dir", "/m", "--vad", "/v", "--threads", "x")]
    [InlineData("--serve", "--model-dir", "/m", "--vad", "/v", "--idle-exit-seconds", "-1")]
    [InlineData("--serve", "--model-dir")]
    [InlineData("--serve", "--model-dir", "--vad", "/v")]
    [InlineData("--bench", "--model-dir", "/m", "--vad", "/v")]
    [InlineData("--frobnicate")]
    public void InvalidCommandLines_AreRejectedWithAMessage(params string[] args)
    {
        Assert.False(CliOptions.TryParse(args, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
