using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Transcription;

namespace Murmur.App.Tests;

public sealed class TranscriptTimelineTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    // Words (start, end): 10.0-10.4, 10.5-10.9, 11.0-11.4, 11.5-11.9 | 20.0-20.4, 20.5-20.9, 21.0-21.4
    private static TranscriptTimeline Short() =>
        new(
        [
            TranscriptFactory.Segment(Speaker.Me, 10, "a", "b", "c", "d"),
            TranscriptFactory.Segment(Speaker.Others, 20, "e", "f", "g"),
        ]);

    [Fact]
    public void FindWord_BeforeTheFirstWord_IsNone()
    {
        var timeline = Short();

        Assert.Equal(-1, timeline.FindWord(TimeSpan.Zero));
        Assert.Equal(-1, timeline.FindWord(S(9.999)));
    }

    [Fact]
    public void FindWord_ExactlyAtAWordStart_IsThatWord()
    {
        var timeline = Short();

        Assert.Equal(0, timeline.FindWord(S(10.0)));
        Assert.Equal(1, timeline.FindWord(S(10.5)));
        Assert.Equal(3, timeline.FindWord(S(11.5)));
        Assert.Equal(4, timeline.FindWord(S(20.0)));
    }

    [Fact]
    public void FindWord_JustBeforeTheNextStart_IsStillThePreviousWord()
    {
        var timeline = Short();

        Assert.Equal(0, timeline.FindWord(S(10.4999)));
        Assert.Equal(3, timeline.FindWord(S(12.0)));
    }

    [Fact]
    public void FindWord_ShortPauseAfterAWord_KeepsTheHighlight_LongPauseClearsIt()
    {
        var timeline = Short();

        // Word "d" ends at 11.9; the highlight holds for one more second.
        Assert.Equal(3, timeline.FindWord(S(12.899)));
        Assert.Equal(-1, timeline.FindWord(S(12.9)));
        Assert.Equal(-1, timeline.FindWord(S(15)));
    }

    [Fact]
    public void FindWord_AfterTheLastWordHold_IsNone()
    {
        var timeline = Short();

        Assert.Equal(6, timeline.FindWord(S(21.4)));
        Assert.Equal(-1, timeline.FindWord(S(22.4)));
        Assert.Equal(-1, timeline.FindWord(S(3600)));
    }

    [Fact]
    public void Words_AreNumberedAcrossSegments()
    {
        var timeline = Short();

        Assert.Equal(7, timeline.WordCount);
        Assert.Equal(0, timeline.FirstWordOfSegment(0));
        Assert.Equal(4, timeline.FirstWordOfSegment(1));
        Assert.Equal(1, timeline.SegmentOfWord(6));
        Assert.Equal(S(21.0), timeline.WordStart(6));
    }

    [Fact]
    public void FindSegment_IsTheLastSegmentThatStarted()
    {
        var timeline = Short();

        Assert.Equal(-1, timeline.FindSegment(S(9.9)));
        Assert.Equal(0, timeline.FindSegment(S(10.0)));
        Assert.Equal(0, timeline.FindSegment(S(19.999)));
        Assert.Equal(1, timeline.FindSegment(S(20.0)));
        Assert.Equal(1, timeline.FindSegment(S(9999)));
    }

    [Fact]
    public void OverlappingSpeakers_UseTheLatestStartedWord()
    {
        // Speech of both tracks overlaps: word starts are not increasing in transcript order.
        var timeline = new TranscriptTimeline(
        [
            new TranscriptSegment(
                Speaker.Me, S(0), S(2), "a b", [new TranscriptWord("a", S(0), S(0.4)), new TranscriptWord("b", S(1.0), S(1.4))]),
            new TranscriptSegment(
                Speaker.Others, S(0.5), S(1.0), "c d", [new TranscriptWord("c", S(0.5), S(0.7)), new TranscriptWord("d", S(0.75), S(0.9))]),
        ]);

        Assert.Equal(0, timeline.FindWord(S(0.1)));
        Assert.Equal(2, timeline.FindWord(S(0.6)));
        Assert.Equal(3, timeline.FindWord(S(0.8)));
        Assert.Equal(1, timeline.FindWord(S(1.0)));
    }

    [Fact]
    public void EmptyTranscript_FindsNothing()
    {
        var timeline = new TranscriptTimeline([]);

        Assert.Equal(-1, timeline.FindWord(S(1)));
        Assert.Equal(-1, timeline.FindSegment(S(1)));
    }

    [Fact]
    public void Segments_WithoutWords_AreFoundBySegmentStartOnly()
    {
        var timeline = new TranscriptTimeline([new TranscriptSegment(Speaker.Me, S(5), S(8), "no timings", [])]);

        Assert.Equal(0, timeline.WordCount);
        Assert.Equal(-1, timeline.FindWord(S(6)));
        Assert.Equal(0, timeline.FindSegment(S(6)));
    }

    [Fact]
    public void FindWord_OnTenThousandWords_FindsEveryWordAtItsStart()
    {
        var transcript = TranscriptFactory.Make(Murmur.Core.Sessions.SessionId.New(), 10_000);
        var timeline = new TranscriptTimeline(transcript.Segments);

        var id = 0;
        foreach (var segment in transcript.Segments)
        {
            foreach (var word in segment.Words)
            {
                Assert.Equal(id, timeline.FindWord(word.Start));
                id++;
            }
        }

        Assert.Equal(10_000, id);
    }
}
