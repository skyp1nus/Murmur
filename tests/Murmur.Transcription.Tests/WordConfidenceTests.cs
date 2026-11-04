using Murmur.Core.Transcription.Protocol;
using Murmur.Transcription.Native;
using Murmur.Transcription.Pipeline;

namespace Murmur.Transcription.Tests;

public sealed class WordConfidenceTests
{
    private static float Ln(double p) => (float)Math.Log(p);

    [Fact]
    public void Compute_GroupsTokensLikeWordBuilder_AndUsesTheWeakestTokenByDefault()
    {
        // Shape of a Parakeet result: sherpa-onnx hands out word starts as a leading space.
        string[] tokens = [" Ask", " no", "t", " what", ",", " you"];
        float[] logProbs = [Ln(0.99), Ln(0.9), Ln(0.5), Ln(0.8), Ln(0.1), Ln(1.0)];
        float[] times = [0f, 0.4f, 0.56f, 0.8f, 1.04f, 1.2f];

        var confidence = WordConfidence.Compute(tokens, logProbs);
        var words = WordBuilder.Build(tokens, times, [], 0, 2);

        Assert.Equal(words.Count, confidence.Length);
        Assert.Equal(["Ask", "not", "what,", "you"], words.Select(w => w.Text));
        Assert.Equal(0.99f, confidence[0], 4);
        Assert.Equal(0.5f, confidence[1], 4);   // min of "no" + "t"
        Assert.Equal(0.8f, confidence[2], 4);   // the low-probability comma does not count
        Assert.Equal(1.0f, confidence[3], 4);
    }

    [Fact]
    public void Aggregations_MinMeanAndGeometricMean()
    {
        string[] tokens = ["▁hel", "lo"];
        float[] logProbs = [Ln(0.9), Ln(0.4)];

        Assert.Equal(0.4f, WordConfidence.ForWord(tokens, logProbs, ConfidenceAggregation.Min), 4);
        Assert.Equal(0.65f, WordConfidence.ForWord(tokens, logProbs, ConfidenceAggregation.Mean), 4);
        Assert.Equal(0.6f, WordConfidence.ForWord(tokens, logProbs, ConfidenceAggregation.GeometricMean), 4);
    }

    [Fact]
    public void PunctuationOnlyWord_IsScoredFromItsOwnTokens()
    {
        string[] tokens = ["▁-", "-"];
        float[] logProbs = [Ln(0.3), Ln(0.6)];

        Assert.Equal(0.3f, WordConfidence.ForWord(tokens, logProbs, ConfidenceAggregation.Min), 4);
    }

    [Fact]
    public void MissingOrMismatchedLogProbs_GiveNoConfidence()
    {
        string[] tokens = ["▁a", "▁b"];

        Assert.Empty(WordConfidence.Compute(tokens, []));
        Assert.Empty(WordConfidence.Compute(tokens, [0f]));
        Assert.Empty(WordConfidence.Compute([], []));
        Assert.Throws<ArgumentException>(() => WordConfidence.ForWord(tokens, [0f], ConfidenceAggregation.Min));
    }

    [Fact]
    public void NonFiniteAndPositiveLogProbs_AreClampedOrSkipped()
    {
        Assert.Equal(1f, WordConfidence.ForWord(["▁a"], [0.001f], ConfidenceAggregation.Min));            // rounding above 0
        Assert.Equal(0f, WordConfidence.ForWord(["▁a"], [float.NegativeInfinity], ConfidenceAggregation.Min));
        Assert.Equal(0.5f, WordConfidence.ForWord(["▁a", "b"], [float.NaN, Ln(0.5)], ConfidenceAggregation.Min), 4);
        Assert.Equal(0f, WordConfidence.ForWord(["▁a"], [float.NaN], ConfidenceAggregation.Min));       // nothing usable
    }

    [Theory]
    [InlineData("▁hello", true)]
    [InlineData(" 42", true)]
    [InlineData("ї", true)]
    [InlineData(",", false)]
    [InlineData("▁", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLexical_MeansHasALetterOrDigit(string? token, bool expected) =>
        Assert.Equal(expected, WordConfidence.IsLexical(token));

    [Fact]
    public void LoneBoundaryTokens_DoNotShiftTheAlignment()
    {
        string[] tokens = ["▁", "▁a", "▁", "▁b"];
        float[] logProbs = [Ln(0.1), Ln(0.9), Ln(0.1), Ln(0.8)];

        var confidence = WordConfidence.Compute(tokens, logProbs);

        Assert.Equal(WordBuilder.Build(tokens, [0f, 0.1f, 0.2f, 0.3f], [], 0, 1).Count, confidence.Length);
        Assert.Equal([0.9f, 0.8f], confidence.Select(c => MathF.Round(c, 4)));
    }

    [Fact]
    public void Pipeline_ReportsConfidence_OnlyWhenTheHookIsSet()
    {
        var recognizer = new ScriptedRecognizer(new RecognitionResult("hi there", ["▁hi", "▁the", "re"], [0f, 0.3f, 0.5f], [0.2f, 0.2f, 0.2f], "en")
        {
            LogProbs = [Ln(0.95), Ln(0.7), Ln(0.2)],
        });
        float[] samples = [.. Enumerable.Repeat(0f, 8_000), .. Enumerable.Repeat(0.1f, 16_000), .. Enumerable.Repeat(0f, 8_000)];

        var calls = new List<(WorkerSegment Segment, float[] Confidence)>();
        var segments = new List<WorkerSegment>();
        new TranscriptionPipeline(recognizer, () => new FakeVad(), new PipelineOptions { OnWordConfidence = (s, c) => calls.Add((s, c)) })
            .Run(new ConcatenatedLikeSource(samples), segments.Add, null, CancellationToken.None);

        var (segment, confidence) = Assert.Single(calls);
        Assert.Same(segments[0], segment);
        Assert.Equal(["hi", "there"], segment.Words.Select(w => w.Text));
        Assert.Equal([0.95f, 0.2f], confidence.Select(c => MathF.Round(c, 4)));

        // Default options: no hook, the segment is the same.
        var plain = new List<WorkerSegment>();
        new TranscriptionPipeline(recognizer, () => new FakeVad(), new PipelineOptions())
            .Run(new ConcatenatedLikeSource(samples), plain.Add, null, CancellationToken.None);
        Assert.Equal(segments[0].Words, plain[0].Words);
        Assert.Null(new PipelineOptions().OnWordConfidence);
    }

    [Fact]
    public void Pipeline_GivesEmptyConfidence_WhenTheModelHasNoLogProbs()
    {
        var recognizer = new ScriptedRecognizer(new RecognitionResult("hi", ["▁hi"], [0f], [0.2f], "en"));
        float[] samples = [.. Enumerable.Repeat(0.1f, 16_000), .. Enumerable.Repeat(0f, 8_000)];
        float[]? confidence = null;

        new TranscriptionPipeline(recognizer, () => new FakeVad(), new PipelineOptions { OnWordConfidence = (_, c) => confidence = c })
            .Run(new ConcatenatedLikeSource(samples), _ => { }, null, CancellationToken.None);

        Assert.NotNull(confidence);
        Assert.Empty(confidence);
    }
}

/// <summary>Returns the same result for every utterance.</summary>
internal sealed class ScriptedRecognizer(RecognitionResult result) : ISpeechRecognizer
{
    public List<int> Lengths { get; } = [];

    public void Decode(ReadOnlySpan<ReadOnlyMemory<float>> utterances, Span<RecognitionResult> results)
    {
        for (var i = 0; i < utterances.Length; i++)
        {
            Lengths.Add(utterances[i].Length);
            results[i] = result;
        }
    }
}
