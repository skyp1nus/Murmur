using Murmur.Transcription.Bench;

namespace Murmur.Transcription.Tests;

public sealed class WordAlignmentTests
{
    [Theory]
    [InlineData("Country,", "country")]
    [InlineData("«Київ»", "київ")]
    [InlineData("пам’ять", "пам'ять")]
    [InlineData("пам'ять", "пам'ять")]
    [InlineData("будь-який", "будь-який")]
    [InlineData("'quoted'", "quoted")]
    [InlineData("--", "")]
    [InlineData("...", "")]
    [InlineData("42%", "42")]
    [InlineData(null, "")]
    public void Normalize_LowerCasesAndStripsPunctuation(string? word, string expected) =>
        Assert.Equal(expected, WordAlignment.Normalize(word));

    [Fact]
    public void NormalizeText_DropsPunctuationOnlyWords() =>
        Assert.Equal(["ask", "not", "what"], WordAlignment.NormalizeText("Ask not — what?"));

    [Fact]
    public void Align_LabelsSubstitutionsInsertionsAndCountsDeletions()
    {
        //          hyp: the  cat  sat  on   a    the  mat
        //          ref: the  bat  sat  on        the  mat  today
        var a = WordAlignment.Align(
            ["the", "cat", "sat", "on", "a", "the", "mat"],
            ["the", "bat", "sat", "on", "the", "mat", "today"]);

        Assert.Equal(
            [WordLabel.Correct, WordLabel.Substitution, WordLabel.Correct, WordLabel.Correct, WordLabel.Insertion, WordLabel.Correct, WordLabel.Correct],
            a.Labels);
        Assert.Equal(1, a.Substitutions);
        Assert.Equal(1, a.Insertions);
        Assert.Equal(1, a.Deletions);
        Assert.Equal(3, a.Errors);
        Assert.Equal(7, a.ReferenceWords);
        Assert.Equal([1, 6], a.MissedReference); // "bat" (substituted), "today" (deleted)
    }

    [Fact]
    public void Align_HandlesEmptySides()
    {
        var none = WordAlignment.Align([], ["a", "b"]);
        Assert.Empty(none.Labels);
        Assert.Equal(2, none.Deletions);

        var extra = WordAlignment.Align(["a", "b"], []);
        Assert.Equal([WordLabel.Insertion, WordLabel.Insertion], extra.Labels);
        Assert.Equal(0, extra.ReferenceWords);
    }

    [Fact]
    public void Manifest_ParsesTabSeparatedLines_AndResolvesRelativePaths()
    {
        var clips = EvalManifest.Parse(
            ["# comment", "", "en\tclips/a.wav\tHello world.", "uk\t/abs/b.wav\tПривіт\tсвіт"],
            "/base");

        Assert.Equal(2, clips.Count);
        Assert.Equal(new EvalClip("en", Path.Combine("/base", "clips/a.wav"), "Hello world."), clips[0]);
        Assert.Equal("/abs/b.wav", clips[1].WavPath);
        Assert.Equal("Привіт світ", clips[1].Reference);
    }

    [Fact]
    public void Manifest_RejectsIncompleteLines()
    {
        var ex = Assert.Throws<FormatException>(() => EvalManifest.Parse(["en\ta.wav"], "/"));
        Assert.Contains("line 1", ex.Message, StringComparison.Ordinal);
    }
}

public sealed class ConfidenceMetricsTests
{
    private static ScoredWord W(float c, bool wrong) => new(c, wrong);

    [Fact]
    public void At_CountsFlaggedWordsBelowTheThreshold()
    {
        ScoredWord[] words = [W(0.1f, true), W(0.3f, false), W(0.4f, true), W(0.9f, false), W(0.95f, true)];

        var p = ConfidenceMetrics.At(words, 0.5f);

        Assert.Equal(3, p.Flagged);
        Assert.Equal(2, p.TruePositives);
        Assert.Equal(0.6667, p.Precision);
        Assert.Equal(0.6667, p.Recall);
        Assert.Equal(0.6667, p.F1);
    }

    [Fact]
    public void At_WithNothingFlaggedOrNothingWrong_IsZeroNotNaN()
    {
        var p = ConfidenceMetrics.At([W(0.9f, false)], 0.5f);

        Assert.Equal((0, 0, 0.0, 0.0, 0.0), (p.Flagged, p.TruePositives, p.Precision, p.Recall, p.F1));
    }

    [Fact]
    public void Auc_IsOneForPerfectSeparation_ZeroForInverted_HalfForTies()
    {
        Assert.Equal(1.0, ConfidenceMetrics.Auc([W(0.1f, true), W(0.2f, true), W(0.8f, false), W(0.9f, false)]));
        Assert.Equal(0.0, ConfidenceMetrics.Auc([W(0.9f, true), W(0.1f, false)]));
        Assert.Equal(0.5, ConfidenceMetrics.Auc([W(0.5f, true), W(0.5f, false)]));
        Assert.Equal(0.5, ConfidenceMetrics.Auc([W(0.5f, false)])); // one class only
        Assert.Equal(0.75, ConfidenceMetrics.Auc([W(0.2f, true), W(0.6f, true), W(0.4f, false), W(0.8f, false)]));
    }

    [Fact]
    public void Summarize_PicksTheBestF1Threshold_AndBuildsAHistogram()
    {
        ScoredWord[] words = [W(0.05f, true), W(0.12f, true), W(0.6f, false), W(0.97f, false), W(1f, false)];

        var s = ConfidenceMetrics.Summarize(words, [0.1f, 0.2f, 0.7f]);

        Assert.Equal(5, s.Words);
        Assert.Equal(2, s.Wrong);
        Assert.Equal(1.0, s.Auc);
        Assert.Equal(0.2f, s.BestF1!.Threshold);
        Assert.Equal((1.0, 1.0), (s.BestF1.Precision, s.BestF1.Recall));
        Assert.Equal(3, s.Thresholds.Count);
        Assert.Equal(10, s.Histogram.Count);
        Assert.Equal((0, 1), (s.Histogram[0].Correct, s.Histogram[0].Wrong));
        Assert.Equal((2, 0), (s.Histogram[9].Correct, s.Histogram[9].Wrong)); // 0.97 and 1.0 share the last bin
        Assert.Equal(0.97, s.MedianCorrect, 4);
        Assert.Equal(0.085, s.MedianWrong, 4);
    }
}
