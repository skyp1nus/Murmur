using Murmur.Transcription.Audio;
using Murmur.Transcription.Bench;
using Murmur.Transcription.Native;
using Murmur.Transcription.Pipeline;

namespace Murmur.Transcription.Tests;

public sealed class ConfidenceEvalTests
{
    private static float Ln(double p) => (float)Math.Log(p);

    private static RecognitionResult Result(string[] tokens, double[] probs) =>
        new(string.Concat(tokens).Trim(), tokens, [.. tokens.Select((_, i) => i * 0.2f)], [.. tokens.Select(_ => 0.16f)], string.Empty)
        {
            LogProbs = [.. probs.Select(Ln)],
        };

    private static readonly RecognitionResult English =
        Result([" Ask", " not", " what", " you", " country", " can", " do", "."], [0.99, 0.98, 0.97, 0.3, 0.95, 0.99, 0.99, 0.6]);

    private static readonly RecognitionResult EnglishFixed =
        Result([" Ask", " not", " what", " your", " country", " can", " do", "."], [0.99, 0.98, 0.97, 0.9, 0.95, 0.99, 0.99, 0.6]);

    private static readonly RecognitionResult Ukrainian =
        Result([" При", "віт", ",", " свят"], [0.97, 0.9, 0.5, 0.4]);

    /// <summary>Recognises a clip by its (constant) sample value; with hotwords the English clip comes out right.</summary>
    private sealed class FakeEvalRecognizer(RecognizerOptions options, List<string> disposed) : ISpeechRecognizer, IDisposable
    {
        public void Decode(ReadOnlySpan<ReadOnlyMemory<float>> utterances, Span<RecognitionResult> results)
        {
            for (var i = 0; i < utterances.Length; i++)
            {
                var english = utterances[i].Span[100] < 0.15f;
                results[i] = english ? (options.HotwordsFile is null ? English : EnglishFixed) : Ukrainian;
            }
        }

        public void Dispose() => disposed.Add(options.DecodingMethod + (options.HotwordsFile is null ? string.Empty : "+hotwords"));
    }

    private static (EvalReport Report, List<RecognizerOptions> Created, List<string> Disposed, string? Hotwords) Evaluate(TempDir dir, string? bpeVocab, int beamClips = 20)
    {
        WavFile.Write(dir.File("en.wav"), [.. Enumerable.Repeat(0.1f, 16_000)], 16_000);
        WavFile.Write(dir.File("uk.wav"), [.. Enumerable.Repeat(0.2f, 8_000)], 16_000);
        File.WriteAllLines(dir.File("clips.tsv"), ["# language\twav\treference", "en\ten.wav\tAsk not what your country can do.", "uk\tuk.wav\tПривіт, світ!"]);

        var created = new List<RecognizerOptions>();
        var disposed = new List<string>();
        string? hotwords = null;
        var report = ConfidenceEval.Run(
            new ConfidenceEvalOptions { ManifestPath = dir.File("clips.tsv"), BpeVocab = bpeVocab, BeamClips = beamClips, WorkDirectory = dir.Path },
            o =>
            {
                created.Add(o);
                if (o.HotwordsFile is not null)
                {
                    hotwords = File.ReadAllText(o.HotwordsFile);
                }

                return new FakeEvalRecognizer(o, disposed);
            },
            threads: 2,
            TextWriter.Null);
        return (report, created, disposed, hotwords);
    }

    [Fact]
    public void Run_LabelsWrongWords_AndScoresThem()
    {
        using var dir = new TempDir();

        var (report, _, _, _) = Evaluate(dir, bpeVocab: null);

        Assert.Equal(2, report.Clips.Count);
        var en = report.Clips[0];
        Assert.Equal(("en", 7, 1, 0, 0), (en.Language, en.ReferenceWords, en.Substitutions, en.Insertions, en.Deletions));
        Assert.Equal(["your"], en.Missed);
        Assert.Equal(7, en.Words.Count); // the final "." is punctuation only: not a word
        var you = en.Words[3];
        Assert.Equal(("you", WordLabel.Substitution), (you.Normalized, you.Label));
        Assert.Equal(0.3f, you.Min, 4);
        Assert.All(en.Words.Where(w => w != you), w => Assert.Equal(WordLabel.Correct, w.Label));

        var uk = report.Clips[1];
        Assert.Equal(["Привіт,", "свят"], uk.Words.Select(w => w.Text));
        Assert.Equal(0.9f, uk.Words[0].Min, 4); // the comma's 0.5 is left out
        Assert.Equal(WordLabel.Substitution, uk.Words[1].Label);
        Assert.Equal(["світ"], uk.Missed);

        // 3 aggregations x (all, en, uk)
        Assert.Equal(9, report.Summaries.Count);
        var all = report.Summaries.Single(s => s is { Aggregation: "Min", Language: "all" }).Summary;
        Assert.Equal((9, 2), (all.Words, all.Wrong));
        Assert.Equal(1.0, all.Auc); // both wrong words score below every correct one
        Assert.Equal(1.0, all.BestF1!.Recall);
    }

    [Fact]
    public void Run_ComparesGreedyWithBeamAndOracleHotwords()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("bpe.vocab"), "▁your -1\n");

        var (report, created, disposed, hotwords) = Evaluate(dir, dir.File("bpe.vocab"));

        Assert.Equal([RecognizerOptions.GreedySearch, RecognizerOptions.ModifiedBeamSearch, RecognizerOptions.ModifiedBeamSearch], created.Select(o => o.DecodingMethod));
        Assert.Equal(4, created[1].MaxActivePaths);
        Assert.Equal(dir.File("bpe.vocab"), created[2].BpeVocab);
        Assert.Equal(["greedy_search", "modified_beam_search", "modified_beam_search+hotwords"], disposed); // each disposed after its run
        Assert.Equal(["your", "світ"], report.HotwordsUsed);
        Assert.Equal("your\nсвіт\n", hotwords!.ReplaceLineEndings("\n"));

        Assert.Equal(3, report.Decoding.Count);
        var (greedy, beam, hot) = (report.Decoding[0], report.Decoding[1], report.Decoding[2]);
        Assert.Equal((2, 9, 2), (greedy.Clips, greedy.ReferenceWords, greedy.Errors));
        Assert.Equal((2, 0), (beam.Errors, beam.ChangedWordsVsGreedy));
        Assert.Equal((1, 1, 2), (hot.Errors, hot.ChangedWordsVsGreedy, hot.Hotwords));
        Assert.Equal(0.1111, hot.Wer);
        Assert.Equal(1.5, report.Decoding.Sum(r => r.AudioSeconds) / 3, 3);
    }

    [Fact]
    public void Run_WithoutBeamClips_DecodesGreedyOnly()
    {
        using var dir = new TempDir();

        var (report, created, _, _) = Evaluate(dir, dir.File("missing.vocab"), beamClips: 0);

        Assert.Single(created);
        Assert.Equal(0, Assert.Single(report.Decoding).Clips);
    }

    [Fact]
    public void Report_SerialisesToJsonAndMarkdown()
    {
        using var dir = new TempDir();
        var (report, _, _, _) = Evaluate(dir, bpeVocab: null);

        var json = ConfidenceEval.ToJson(report);
        var markdown = ConfidenceEval.ToMarkdown(report);

        Assert.Contains("\"label\": \"Substitution\"", json, StringComparison.Ordinal);
        Assert.Contains("\"logProbs\"", json, StringComparison.Ordinal);
        Assert.Contains("| Min | all | 9 | 2 | 1.000 |", markdown, StringComparison.Ordinal);
        Assert.Contains("### Thresholds, Min, uk", markdown, StringComparison.Ordinal);
        Assert.Contains("| en | you | Substitution | 0.300 |", markdown, StringComparison.Ordinal);
        Assert.Contains("- uk: 1 clips, 0.5 s, 2 reference words, WER 50.0%", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_RejectsAnEmptyManifest()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("empty.tsv"), "# nothing\n");

        Assert.Throws<InvalidDataException>(() => ConfidenceEval.Run(
            new ConfidenceEvalOptions { ManifestPath = dir.File("empty.tsv") }, _ => throw new InvalidOperationException("not reached"), 1, TextWriter.Null));
    }

    [Fact]
    public void ReferenceWords_KeepTheWrittenFormIndexForIndex()
    {
        var (normalized, raw) = ConfidenceEval.ReferenceWords("«Київ» — столиця, (2024)!");

        Assert.Equal(["київ", "столиця", "2024"], normalized);
        Assert.Equal(["Київ", "столиця", "2024"], raw);
    }

    [Fact]
    public void Score_WithoutLogProbs_GivesZeroConfidence()
    {
        var clip = new EvalClip("en", "a.wav", "hello world");
        var result = new RecognitionResult("hello world", [" hello", " world"], [0f, 0.4f], [], string.Empty);

        var report = ConfidenceEval.Score(clip, 16_000, result, 12.5);

        Assert.All(report.Words, w => Assert.Equal(0f, w.Min));
        Assert.All(report.Words, w => Assert.Equal(WordLabel.Correct, w.Label));
        Assert.Equal(1.0, report.AudioSeconds);
    }
}
