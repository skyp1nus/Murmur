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
    [InlineData("--confidence-eval", "clips.tsv")]
    [InlineData("--confidence-eval", "clips.tsv", "--model-dir", "/m", "--beam-clips", "-1")]
    [InlineData("--confidence-eval", "clips.tsv", "--model-dir", "/m", "--max-active-paths", "0")]
    [InlineData("--confidence-eval", "clips.tsv", "--model-dir", "/m", "--hotwords-score", "0")]
    [InlineData("--confidence-eval", "clips.tsv", "--transcribe", "a.wav", "--model-dir", "/m", "--vad", "/v")]
    public void InvalidCommandLines_AreRejectedWithAMessage(params string[] args)
    {
        Assert.False(CliOptions.TryParse(args, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ConfidenceEval_NeedsOnlyTheModel_AndParsesItsOptions()
    {
        Assert.True(CliOptions.TryParse(
            ["--confidence-eval", "clips.tsv", "--model-dir", "/m", "--beam-clips", "0", "--max-active-paths", "8", "--bpe-vocab", "bpe.vocab", "--hotwords-score", "2.5", "--out", "r.json"],
            out var o,
            out var error), error);

        Assert.Equal(WorkerCommand.ConfidenceEval, o.Command);
        Assert.Equal("clips.tsv", o.ConfidenceManifest);
        Assert.Equal((0, 8, "bpe.vocab", 2.5f), (o.BeamClips, o.MaxActivePaths, o.BpeVocab, o.HotwordsScore));
        Assert.Null(o.VadModelPath);
    }

    [Fact]
    public void WordConfidence_IsOffByDefault()
    {
        Assert.True(CliOptions.TryParse(["--transcribe", "a.wav", "--model-dir", "/m", "--vad", "/v"], out var o, out _));
        Assert.False(o.WordConfidence);
        Assert.Equal((20, 4, 1.5f), (o.BeamClips, o.MaxActivePaths, o.HotwordsScore));

        Assert.True(CliOptions.TryParse(["--transcribe", "a.wav", "--model-dir", "/m", "--vad", "/v", "--word-confidence"], out o, out _));
        Assert.True(o.WordConfidence);
    }
}
