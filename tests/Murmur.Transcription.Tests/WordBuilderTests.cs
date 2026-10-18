using Murmur.Transcription.Pipeline;

namespace Murmur.Transcription.Tests;

public sealed class WordBuilderTests
{
    [Fact]
    public void BoundaryTokens_StartWords_AndPiecesAndPunctuationExtendThem()
    {
        string[] tokens = ["▁Ask", "▁no", "t", "▁what", ",", "▁you"];
        float[] times = [0.00f, 0.40f, 0.56f, 0.80f, 1.04f, 1.20f];
        float[] durations = [0.32f, 0.16f, 0.08f, 0.24f, 0.08f, 0.40f];

        var words = WordBuilder.Build(tokens, times, durations, segmentStart: 10.0, segmentEnd: 12.0);

        Assert.Equal(["Ask", "not", "what,", "you"], words.Select(w => w.Text));
        Assert.Equal([10.0, 10.4, 10.8, 11.2], words.Select(w => w.Start));
        // end = last token time + its duration
        Assert.Equal([10.32, 10.64, 11.12, 11.6], words.Select(w => w.End));
    }

    [Fact]
    public void SpaceAlsoMarksAWordBoundary()
    {
        string[] tokens = [" hello", " wor", "ld"];
        float[] times = [0f, 0.5f, 0.7f];

        var words = WordBuilder.Build(tokens, times, [], 0, 2);

        Assert.Equal(["hello", "world"], words.Select(w => w.Text));
    }

    [Fact]
    public void WithoutDurations_EndIsNextWordStartOrSegmentEnd()
    {
        string[] tokens = ["▁a", "▁b", "c"];
        float[] times = [0.1f, 0.5f, 0.9f];

        var words = WordBuilder.Build(tokens, times, [], segmentStart: 2.0, segmentEnd: 3.5);

        Assert.Equal(2, words.Count);
        Assert.Equal((2.1, 2.5), (words[0].Start, words[0].End));
        Assert.Equal((2.5, 3.5), (words[1].Start, words[1].End));
    }

    [Fact]
    public void ZeroOrInvalidDurations_FallBackToNextWordStart()
    {
        string[] tokens = ["▁a", "▁b"];
        float[] times = [0.0f, 0.4f];
        float[] durations = [0f, float.NaN];

        var words = WordBuilder.Build(tokens, times, durations, 0, 1);

        Assert.Equal(0.4, words[0].End);
        Assert.Equal(1.0, words[1].End);
    }

    [Fact]
    public void EndsNeverOverlapTheNextWordOrLeaveTheSegment()
    {
        string[] tokens = ["▁a", "▁b"];
        float[] times = [0.0f, 0.3f];
        float[] durations = [0.5f, 5f]; // overlapping / too long

        var words = WordBuilder.Build(tokens, times, durations, 1, 2);

        Assert.Equal(1.3, words[0].End);
        Assert.Equal(2.0, words[1].End);
    }

    [Fact]
    public void Times_AreMonotonicEvenForUnorderedTimestamps()
    {
        string[] tokens = ["▁a", "▁b", "▁c"];
        float[] times = [0.5f, 0.2f, 0.9f];

        var words = WordBuilder.Build(tokens, times, [], 0, 1);

        for (var i = 0; i < words.Count; i++)
        {
            Assert.True(words[i].Start <= words[i].End);
            if (i > 0)
            {
                Assert.True(words[i - 1].End <= words[i].Start + 1e-9);
                Assert.True(words[i - 1].Start <= words[i].Start);
            }
        }
    }

    [Fact]
    public void LeadingPieceWithoutBoundary_StartsTheFirstWord_AndEmptyWordsAreDropped()
    {
        string[] tokens = ["ab", "c", "▁", "▁d"];
        float[] times = [0f, 0.1f, 0.2f, 0.3f];

        var words = WordBuilder.Build(tokens, times, [], 0, 1);

        Assert.Equal(["abc", "d"], words.Select(w => w.Text));
    }

    [Fact]
    public void MissingOrMismatchedTimestamps_YieldNoWords()
    {
        Assert.Empty(WordBuilder.Build(["▁a"], [], [], 0, 1));
        Assert.Empty(WordBuilder.Build(["▁a", "▁b"], [0f], [], 0, 1));
        Assert.Empty(WordBuilder.Build([], [], [], 0, 1));
    }

    [Fact]
    public void Times_AreRoundedToMilliseconds()
    {
        var words = WordBuilder.Build(["▁a"], [0.123456f], [0.1f], 100.0004, 101);

        Assert.Equal(100.124, words[0].Start); // 100.0004 + 0.123456
        Assert.Equal(100.224, words[0].End);
    }
}
