using Murmur.Core.Sessions;
using Murmur.Core.Transcription;
using Murmur.Intelligence.Rag;

namespace Murmur.Intelligence.Tests;

public sealed class TranscriptChunkerTests
{
    private static readonly SessionId Session = new(Guid.Parse("0190a000-0000-7000-8000-000000000001"));

    internal static TranscriptSegment Seg(double start, double end, string text, Speaker speaker = Speaker.Others) =>
        new(speaker, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text, []);

    private static Transcript Make(params TranscriptSegment[] segments) => new(Session, segments, "test");

    [Fact]
    public void Chunk_EvenSegments_FillsSixtySecondWindows()
    {
        var segments = Enumerable.Range(0, 30).Select(i => Seg(i * 10, (i * 10) + 10, $"s{i}")).ToArray();

        var chunks = new TranscriptChunker().Chunk(Make(segments));

        Assert.Equal(5, chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(TimeSpan.FromSeconds(i * 60), chunks[i].Start);
            Assert.Equal(TimeSpan.FromSeconds((i * 60) + 60), chunks[i].End);
            Assert.Equal(Session, chunks[i].Session);
        }

        Assert.Equal("Інші: s0 s1 s2 s3 s4 s5", chunks[0].Text);
    }

    [Fact]
    public void Chunk_UnevenSegments_StayWithinSixtySecondsAndUsuallyAboveThirty()
    {
        var segments = new List<TranscriptSegment>();
        var t = 0.0;
        var lengths = new[] { 7.0, 12.5, 3.2, 9.9, 15.0, 4.4, 11.1, 8.0, 13.3, 2.5, 6.6, 14.0, 5.5, 9.0, 10.0, 12.0 };
        foreach (var length in lengths.Concat(lengths))
        {
            segments.Add(Seg(t, t + length, "w"));
            t += length + 0.5;
        }

        var chunks = new TranscriptChunker().Chunk(Make([.. segments]));

        Assert.All(chunks, c => Assert.True(c.End - c.Start <= TimeSpan.FromSeconds(60)));
        Assert.All(chunks.SkipLast(1), c => Assert.True(c.End - c.Start >= TimeSpan.FromSeconds(30), $"{c.Start}-{c.End}"));
        Assert.Equal(segments.Count, chunks.Sum(c => c.Text.Split(' ').Count(w => w == "w")));
    }

    [Fact]
    public void Chunk_SegmentLongerThanLimit_BecomesItsOwnChunk()
    {
        var chunks = new TranscriptChunker().Chunk(Make(Seg(0, 10, "a"), Seg(10, 95, "long"), Seg(95, 100, "b")));

        Assert.Equal(3, chunks.Count);
        Assert.Equal((TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(10)), (chunks[0].Start, chunks[0].End));
        Assert.Equal((TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(95)), (chunks[1].Start, chunks[1].End));
        Assert.Equal("Інші: long", chunks[1].Text);
        Assert.Equal((TimeSpan.FromSeconds(95), TimeSpan.FromSeconds(100)), (chunks[2].Start, chunks[2].End));
    }

    [Fact]
    public void Chunk_PrefixesSpeakerLabelsAndMergesConsecutiveLinesOfOneSpeaker()
    {
        var chunks = new TranscriptChunker().Chunk(Make(
            Seg(0, 4, " Привіт ", Speaker.Me),
            Seg(4, 8, "як справи?", Speaker.Me),
            Seg(8, 12, "Добре.", Speaker.Others),
            Seg(12, 14, "А в тебе?", Speaker.Others),
            Seg(14, 16, "Теж.", Speaker.Me)));

        var chunk = Assert.Single(chunks);
        Assert.Equal("Я: Привіт як справи?\nІнші: Добре. А в тебе?\nЯ: Теж.", chunk.Text);
        Assert.Equal(TimeSpan.Zero, chunk.Start);
        Assert.Equal(TimeSpan.FromSeconds(16), chunk.End);
    }

    [Fact]
    public void Chunk_LongSilence_StartsANewChunk()
    {
        var chunks = new TranscriptChunker().Chunk(Make(Seg(0, 10, "a"), Seg(300, 310, "b")));

        Assert.Equal(2, chunks.Count);
        Assert.Equal(TimeSpan.FromSeconds(300), chunks[1].Start);
    }

    [Fact]
    public void Chunk_OverlappingTracks_EndIsTheLatestSegmentEnd()
    {
        var chunk = Assert.Single(new TranscriptChunker().Chunk(Make(
            Seg(0, 20, "me talking", Speaker.Me), Seg(5, 9, "uh-huh", Speaker.Others))));

        Assert.Equal(TimeSpan.FromSeconds(20), chunk.End);
        Assert.Equal("Я: me talking\nІнші: uh-huh", chunk.Text);
    }

    [Fact]
    public void Chunk_SkipsBlankSegmentsAndEmptyTranscripts()
    {
        Assert.Empty(new TranscriptChunker().Chunk(Make()));
        Assert.Empty(new TranscriptChunker().Chunk(Make(Seg(0, 5, "  "), Seg(5, 6, ""))));

        var chunk = Assert.Single(new TranscriptChunker().Chunk(Make(Seg(0, 5, " "), Seg(5, 9, "x"))));
        Assert.Equal(TimeSpan.FromSeconds(5), chunk.Start);
    }

    [Fact]
    public void Chunk_IsDeterministic()
    {
        var transcript = Make([.. Enumerable.Range(0, 50).Select(i => Seg(i * 7.3, (i * 7.3) + 6.1, $"t{i}", i % 3 == 0 ? Speaker.Me : Speaker.Others))]);

        var first = new TranscriptChunker().Chunk(transcript);
        var second = new TranscriptChunker().Chunk(transcript);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Chunk_CustomMaxDuration()
    {
        var segments = Enumerable.Range(0, 6).Select(i => Seg(i * 10, (i * 10) + 10, "x")).ToArray();

        var chunks = new TranscriptChunker(TimeSpan.FromSeconds(20)).Chunk(Make(segments));

        Assert.Equal(3, chunks.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TranscriptChunker(TimeSpan.Zero));
    }
}
