using Murmur.Core.Sessions;
using Murmur.Intelligence.Rag;

namespace Murmur.Intelligence.Tests;

public sealed class TimestampsTests
{
    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(5, "00:05")]
    [InlineData(754, "12:34")]
    [InlineData(3599.9, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3723, "1:02:03")]
    [InlineData(36000 + 61, "10:01:01")]
    [InlineData(-3, "00:00")]
    public void Format(double seconds, string expected) =>
        Assert.Equal(expected, Timestamps.Format(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void FormatRange() =>
        Assert.Equal("[59:30–1:00:30]", Timestamps.FormatRange(TimeSpan.FromSeconds(3570), TimeSpan.FromSeconds(3630)));
}

public sealed class CitationResolverTests
{
    private static readonly SessionId S1 = new(Guid.Parse("0190a000-0000-7000-8000-000000000001"));
    private static readonly SessionId S2 = new(Guid.Parse("0190a000-0000-7000-8000-000000000002"));

    private static TextChunk Chunk(SessionId session, double start, double end, string text = "текст") =>
        new(session, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text);

    [Fact]
    public void Parse_FindsMinutesHoursAndRangeStarts()
    {
        var times = CitationResolver.Parse("A [12:34], B [1:02:03] and [0:05], C [12:00–12:45], D [3:15-3:40], E [75:10].");

        Assert.Equal(
            [
                new TimeSpan(0, 12, 34), new TimeSpan(1, 2, 3), new TimeSpan(0, 0, 5),
                new TimeSpan(0, 12, 0), new TimeSpan(0, 3, 15), new TimeSpan(1, 15, 10),
            ],
            times);
    }

    [Fact]
    public void Parse_IgnoresMalformedTimestamps()
    {
        Assert.Empty(CitationResolver.Parse("[12:75] [1:60:00] [123:45] [12:3] 12:34 [ 12:34 ] [ab:cd] (12:34)"));
    }

    [Fact]
    public void Resolve_MapsTimesToBestRankedExcerptWithTolerance()
    {
        TextChunk[] excerpts =
        [
            Chunk(S1, 0, 60, "перший уривок"),
            Chunk(S2, 30, 90, "другий уривок"),
            Chunk(S1, 3700, 3760, "після години"),
        ];

        var citations = CitationResolver.Resolve(
            "Так [00:45]. Ще [01:20]. Межа [01:14]. Година [1:02:03]. Поза [30:00]. Повтор [00:45].",
            excerpts, TimeSpan.FromSeconds(15), 120);

        Assert.Equal(4, citations.Count);
        Assert.Equal((S1, TimeSpan.FromSeconds(45), "00:45"), (citations[0].Session, citations[0].At, citations[0].Label));
        Assert.Equal((S2, TimeSpan.FromSeconds(80)), (citations[1].Session, citations[1].At));
        Assert.Equal("другий уривок", citations[1].Snippet);

        // 74 s lies inside S1's [0, 60] widened by the 15 s tolerance.
        Assert.Equal((S1, TimeSpan.FromSeconds(74)), (citations[2].Session, citations[2].At));
        Assert.Equal((S1, new TimeSpan(1, 2, 3), "1:02:03"), (citations[3].Session, citations[3].At, citations[3].Label));
        Assert.Equal("після години", citations[3].Snippet);
    }

    [Fact]
    public void Resolve_SkipsExcerptsThatDoNotContainTheTimeAndDedupes()
    {
        // [00:40] falls only in S1's widened range (best-ranked S2 does not contain it); the repeat is deduped.
        TextChunk[] excerpts = [Chunk(S2, 200, 260), Chunk(S1, 0, 30)];

        var citations = CitationResolver.Resolve("[00:40] [00:40] [3:30]", excerpts, TimeSpan.FromSeconds(15), 120);

        Assert.Equal([(S1, TimeSpan.FromSeconds(40)), (S2, TimeSpan.FromSeconds(210))], citations.Select(c => (c.Session, c.At)));
    }

    [Fact]
    public void Resolve_NothingResolves_FallsBackToTopExcerptStart()
    {
        TextChunk[] excerpts = [Chunk(S2, 125, 180, "головний"), Chunk(S1, 0, 60)];

        var withoutTimestamps = CitationResolver.Resolve("Відповідь без посилань.", excerpts, TimeSpan.FromSeconds(15), 120);
        var outOfRange = CitationResolver.Resolve("Дивно [45:00].", excerpts, TimeSpan.FromSeconds(15), 120);

        foreach (var citations in new[] { withoutTimestamps, outOfRange })
        {
            var citation = Assert.Single(citations);
            Assert.Equal(S2, citation.Session);
            Assert.Equal(TimeSpan.FromSeconds(125), citation.At);
            Assert.Equal("02:05", citation.Label);
            Assert.Equal("головний", citation.Snippet);
        }
    }

    [Fact]
    public void Resolve_NoExcerpts_NoCitations()
    {
        Assert.Empty(CitationResolver.Resolve("[00:10]", [], TimeSpan.FromSeconds(15), 120));
    }

    [Fact]
    public void Snippet_LongText_IsAboutTheRequestedLengthAroundTheMoment()
    {
        var words = string.Join(' ', Enumerable.Range(0, 200).Select(i => $"w{i:D3}"));
        var chunk = new TextChunk(S1, TimeSpan.Zero, TimeSpan.FromSeconds(100), words);

        var start = CitationResolver.Snippet(chunk, TimeSpan.Zero, 120);
        var middle = CitationResolver.Snippet(chunk, TimeSpan.FromSeconds(50), 120);
        var end = CitationResolver.Snippet(chunk, TimeSpan.FromSeconds(100), 120);

        Assert.StartsWith("w000", start, StringComparison.Ordinal);
        Assert.EndsWith("…", start, StringComparison.Ordinal);
        Assert.StartsWith("…", middle, StringComparison.Ordinal);
        Assert.EndsWith("…", middle, StringComparison.Ordinal);
        Assert.Contains("w100", middle, StringComparison.Ordinal);
        Assert.EndsWith("w199", end, StringComparison.Ordinal);
        foreach (var snippet in new[] { start, middle, end })
        {
            Assert.InRange(snippet.Length, 60, 122);
            Assert.DoesNotMatch(@"(^|…)\d", snippet); // never starts mid-word
        }
    }

    [Fact]
    public void Snippet_FlattensLines()
    {
        var chunk = new TextChunk(S1, TimeSpan.Zero, TimeSpan.FromSeconds(10), "Я: так\nІнші: ні");

        Assert.Equal("Я: так Інші: ні", CitationResolver.Snippet(chunk, TimeSpan.Zero, 120));
    }
}

public sealed class RankFusionTests
{
    private static readonly SessionId S1 = new(Guid.Parse("0190a000-0000-7000-8000-000000000001"));
    private static readonly SessionId S2 = new(Guid.Parse("0190a000-0000-7000-8000-000000000002"));

    private static SearchHit Hit(SessionId session, double start, double end, double score, SearchSource source, string text = "") =>
        new(new TextChunk(session, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text), score, source);

    [Fact]
    public void Fuse_RanksByReciprocalRankAndMergesOverlappingHits()
    {
        var a = Hit(S1, 0, 60, 0.9, SearchSource.Vector, "A");
        var b = Hit(S1, 60, 120, 0.8, SearchSource.Vector, "B");
        var c = Hit(S1, 120, 180, 0.7, SearchSource.Vector, "C");
        var inC = Hit(S1, 130, 135, 12.0, SearchSource.Keyword, "seg in C");
        var d = Hit(S1, 300, 305, 9.0, SearchSource.Keyword, "D");

        var fused = RankFusion.Fuse([inC, d], [a, b, c], k: 60, limit: 10);

        Assert.Equal(["C", "A", "B", "D"], fused.Select(h => h.Chunk.Text));
        Assert.Equal((1.0 / 63) + (1.0 / 61), fused[0].Score, 12);
        Assert.Equal(1.0 / 61, fused[1].Score, 12);
        Assert.Equal(1.0 / 62, fused[2].Score, 12);
        Assert.Equal(1.0 / 62, fused[3].Score, 12);
        Assert.All(fused, h => Assert.Equal(SearchSource.Hybrid, h.Source));
    }

    [Fact]
    public void Fuse_DoesNotMergeAcrossSessions_AndCountsEachListOncePerResult()
    {
        var vector = Hit(S1, 0, 60, 0.9, SearchSource.Vector, "S1 chunk");
        var otherSession = Hit(S2, 10, 20, 5, SearchSource.Keyword, "S2 seg");
        var first = Hit(S1, 10, 15, 4, SearchSource.Keyword, "S1 seg a");
        var second = Hit(S1, 20, 25, 3, SearchSource.Keyword, "S1 seg b");

        var fused = RankFusion.Fuse([otherSession, first, second], [vector], k: 60, limit: 10);

        Assert.Equal(["S1 chunk", "S2 seg"], fused.Select(h => h.Chunk.Text));
        Assert.Equal((1.0 / 61) + (1.0 / 62), fused[0].Score, 12);
        Assert.Equal(1.0 / 61, fused[1].Score, 12);
    }

    [Fact]
    public void Fuse_SortsInputsByScoreAndAppliesLimitAndScope()
    {
        var low = Hit(S1, 0, 5, 1, SearchSource.Keyword, "low");
        var high = Hit(S1, 100, 105, 9, SearchSource.Keyword, "high");
        var foreign = Hit(S2, 0, 5, 99, SearchSource.Keyword, "foreign");

        var fused = RankFusion.Fuse([low, foreign, high], [], k: 60, limit: 1, scope: S1);

        Assert.Equal("high", Assert.Single(fused).Chunk.Text);
        Assert.Empty(RankFusion.Fuse([low], [], 60, 0));
    }

    [Fact]
    public void Fuse_DeduplicatesIdenticalHits()
    {
        var chunk = Hit(S1, 0, 60, 0.9, SearchSource.Vector, "X");

        var fused = RankFusion.Fuse([], [chunk, chunk with { Score = 0.5 }], 60, 10);

        Assert.Single(fused);
    }
}
