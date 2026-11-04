using Murmur.Core.Transcription.Protocol;
using Murmur.Transcription.Native;

namespace Murmur.Transcription.Tests;

public sealed class RecognizerOptionsTests
{
    [Fact]
    public void Default_IsTheProductionGreedyDecoder()
    {
        var d = RecognizerOptions.Default;

        Assert.Equal(RecognizerOptions.GreedySearch, d.DecodingMethod);
        Assert.Equal(4, d.MaxActivePaths);
        Assert.Null(d.HotwordsFile);
        Assert.Null(d.BpeVocab);
        OfflineRecognizer.ValidateDecoding(d);
    }

    [Fact]
    public void ValidateDecoding_RejectsWhatSherpaOnnxWouldIgnoreOrCrashOn()
    {
        using var dir = new TempDir();
        var hotwords = dir.File("hotwords.txt");
        var vocab = dir.File("bpe.vocab");
        File.WriteAllText(hotwords, "Murmur\n");
        File.WriteAllText(vocab, "▁Mur -1\n");
        var beam = new RecognizerOptions { DecodingMethod = RecognizerOptions.ModifiedBeamSearch };

        OfflineRecognizer.ValidateDecoding(beam);
        OfflineRecognizer.ValidateDecoding(beam with { HotwordsFile = hotwords, BpeVocab = vocab });
        Assert.Throws<ArgumentException>(() => OfflineRecognizer.ValidateDecoding(new RecognizerOptions { DecodingMethod = "attention_rescoring" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => OfflineRecognizer.ValidateDecoding(beam with { MaxActivePaths = 0 }));
        Assert.Throws<ArgumentException>(() => OfflineRecognizer.ValidateDecoding(beam with { HotwordsFile = hotwords }));                 // no vocabulary
        Assert.Throws<ArgumentException>(() => OfflineRecognizer.ValidateDecoding(RecognizerOptions.Default with { HotwordsFile = hotwords, BpeVocab = vocab })); // greedy
        Assert.Throws<FileNotFoundException>(() => OfflineRecognizer.ValidateDecoding(beam with { HotwordsFile = dir.File("none.txt"), BpeVocab = vocab }));
        Assert.Throws<FileNotFoundException>(() => OfflineRecognizer.ValidateDecoding(beam with { BpeVocab = dir.File("none.vocab") }));
    }

    [Fact]
    public void CreateParakeet_ValidatesTheDecoderBeforeLoadingAnything()
    {
        using var dir = new TempDir();

        Assert.Throws<ArgumentException>(() =>
            OfflineRecognizer.CreateParakeet(new ParakeetModelFiles(dir.Path), 1, new RecognizerOptions { DecodingMethod = "nope" }));
    }

    [Fact]
    public void FormatConfidence_PrintsEveryWordWithItsScore()
    {
        WorkerWord[] words = [new("Ask", 0, 0.3), new("not", 0.3, 0.5), new("what", 0.5, 0.8)];

        Assert.Equal("Ask(0.98) not(0.41) what(?)", Program.FormatConfidence(words, [0.981f, 0.406f]));
        Assert.Equal("Ask(?) not(?) what(?)", Program.FormatConfidence(words, []));
    }

    [Fact]
    public void ConfidenceEval_WithAMissingManifest_Returns1WithMessage()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = Program.Run(["--confidence-eval", "/nonexistent/clips.tsv", "--model-dir", "/nonexistent"], output, error);

        Assert.Equal(Program.FailureExitCode, exitCode);
        Assert.Contains("clips.tsv", error.ToString(), StringComparison.Ordinal);
    }
}
