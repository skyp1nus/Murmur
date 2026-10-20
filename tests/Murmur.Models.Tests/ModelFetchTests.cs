using Murmur.Core.Models;
using Murmur.Core.Transcription;
using Murmur.ModelFetch;
using Murmur.Models.Tests.Support;

namespace Murmur.Models.Tests;

public sealed class ModelFetchTests
{
    private static async Task<(int Code, string Output, string Error)> RunAsync(Harness h, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await FetchRunner.RunAsync(args, output, error, h.Sources, TestContext.Current.CancellationToken);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Parse_AcceptsAllOptions()
    {
        var options = FetchOptions.Parse(["--dir", "/m", "--ids", "a, b", "--head-only", "c", "--summary", "s.md"], out var error);

        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal("/m", options.Directory);
        Assert.Equal(["a", "b"], options.Install);
        Assert.Equal(["c"], options.HeadOnly);
        Assert.Equal("s.md", options.SummaryFile);
    }

    [Theory]
    [InlineData("--ids", "a")]
    [InlineData("--dir", "/m")]
    [InlineData("--dir", "/m", "--bogus", "x")]
    [InlineData("--dir")]
    public void Parse_RejectsIncompleteOrUnknownArguments(params string[] args)
    {
        Assert.Null(FetchOptions.Parse(args, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Run_InstallsItemsAndPrintsSizeAndSha256()
    {
        await using var h = new Harness();
        var vad = Harness.Bytes(5000, 1);
        h.Server.Add("/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx", vad);
        var summary = Path.Combine(h.Root, "summary.md");
        Directory.CreateDirectory(h.Root);

        var (code, output, error) = await RunAsync(h, "--dir", h.Models, "--ids", ModelCatalog.SileroVadId, "--summary", summary);

        Assert.Equal(0, code);
        Assert.Equal("", error);
        Assert.Contains("5000 bytes", output);
        Assert.Contains(Harness.Sha256(vad), output);
        Assert.True(new ModelLocator(h.Models).MissingAsrFiles().Count > 0); // only the VAD was asked for
        Assert.True(File.Exists(Path.Combine(h.Models, "silero_vad.onnx")));
        var markdown = await File.ReadAllTextAsync(summary, TestContext.Current.CancellationToken);
        Assert.Contains("| `silero-vad` | `silero_vad.onnx` | 5000 |", markdown);
        Assert.Contains(Harness.Sha256(vad), markdown);
    }

    [Fact]
    public async Task Run_HeadOnlyChecksTheUrlsWithoutDownloading()
    {
        await using var h = new Harness();
        h.Server.Add("/unsloth/gemma-4-E4B-it-GGUF/resolve/bfc15c382204943c3a8fff0c750b94ae2364d7a3/gemma-4-E4B-it-Q4_K_M.gguf", Harness.Bytes(7777));

        var (code, output, _) = await RunAsync(h, "--dir", h.Models, "--head-only", $"{ModelCatalog.ChatE4BId},{ModelCatalog.Chat26BId}");

        Assert.Equal(1, code); // the 26B guess does not exist
        Assert.Contains("Content-Length 7777", output);
        Assert.Contains("UNREACHABLE", output);
        Assert.False(Directory.Exists(h.Models));
    }

    [Fact]
    public async Task Run_HeadOnlyPrintsWhichSourceAnsweredAndWhichWereSkipped()
    {
        await using var h = new Harness();
        // E4B: the first source (unsloth) is missing, the second (bartowski) answers.
        h.Server.Add("/unsloth/gemma-4-E4B-it-GGUF/resolve/bfc15c382204943c3a8fff0c750b94ae2364d7a3/gemma-4-E4B-it-Q4_K_M.gguf", Harness.Bytes(1)).NotFound = true;
        h.Server.Add("/bartowski/google_gemma-4-E4B-it-GGUF/resolve/029e94146666900b08caf49a3b47b413dfa8ec66/google_gemma-4-E4B-it-Q4_K_M.gguf", Harness.Bytes(2468));
        var summary = Path.Combine(h.Root, "summary.md");
        Directory.CreateDirectory(h.Root);

        var (code, output, _) = await RunAsync(h, "--dir", h.Models, "--head-only", ModelCatalog.ChatE4BId, "--summary", summary);

        Assert.Equal(0, code);
        Assert.Contains("skipped " + h.Server.Url("/unsloth/gemma-4-E4B-it-GGUF/resolve/bfc15c382204943c3a8fff0c750b94ae2364d7a3/gemma-4-E4B-it-Q4_K_M.gguf"), output);
        Assert.Contains("404", output);
        Assert.Contains("bartowski/google_gemma-4-E4B-it-GGUF/resolve/029e94146666900b08caf49a3b47b413dfa8ec66/google_gemma-4-E4B-it-Q4_K_M.gguf: Content-Length 2468", output);
        Assert.Contains("(skipped:", await File.ReadAllTextAsync(summary, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Run_ReportsAFailedInstallAndKeepsGoing()
    {
        await using var h = new Harness();
        h.Server.Add("/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx", Harness.Bytes(100)).NotFound = true;

        var (code, output, _) = await RunAsync(h, "--dir", h.Models, "--ids", ModelCatalog.SileroVadId);

        Assert.Equal(1, code);
        Assert.Contains("FAILED", output);
    }

    [Fact]
    public async Task Run_RejectsUnknownIds()
    {
        await using var h = new Harness();

        var (code, _, error) = await RunAsync(h, "--dir", h.Models, "--ids", "nope");

        Assert.Equal(2, code);
        Assert.Contains("Unknown id", error);
    }

    [Fact]
    public async Task Run_PrintsUsageForBadArguments()
    {
        await using var h = new Harness();

        var (code, _, error) = await RunAsync(h, "--ids", "a");

        Assert.Equal(2, code);
        Assert.Contains("Usage:", error);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task Run_PrintsUsageForHelpAndExitsWithZero(string flag)
    {
        await using var h = new Harness();

        var (code, output, error) = await RunAsync(h, flag);

        Assert.Equal(0, code);
        Assert.Contains("Usage:", output);
        Assert.Equal("", error);
    }
}
