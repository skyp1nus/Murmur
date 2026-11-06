using BenchmarkDotNet.Attributes;
using Murmur.App.ViewModels;
using Murmur.Core.Transcription;

namespace Murmur.Benchmarks;

/// <summary>
/// The word and line lookup that runs at the player's poll rate (about 30 Hz) while a recording plays with "Follow along" on:
/// a binary search over the start of every word. Budget: no allocation, microseconds even for a three-hour transcript.
/// </summary>
[MemoryDiagnoser]
public class FollowAlongBenchmarks
{
    private TranscriptTimeline _timeline = null!;
    private TimeSpan[] _positions = null!;
    private int _next;

    /// <summary>Words in the transcript: 10 000 is about an hour of speech, 100 000 a long day of meetings.</summary>
    [Params(10_000, 100_000)]
    public int Words { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        const int perSegment = 12;
        var segments = new List<TranscriptSegment>(Words / perSegment);
        var clock = TimeSpan.Zero;
        for (var s = 0; s < Words / perSegment; s++)
        {
            var words = new TranscriptWord[perSegment];
            var start = clock;
            for (var w = 0; w < perSegment; w++)
            {
                words[w] = new TranscriptWord("word", clock, clock + TimeSpan.FromMilliseconds(300));
                clock += TimeSpan.FromMilliseconds(350);
            }

            segments.Add(new TranscriptSegment(s % 2 == 0 ? Speaker.Me : Speaker.Others, start, clock, "word", words));
            clock += TimeSpan.FromMilliseconds(600);
        }

        _timeline = new TranscriptTimeline(segments);
        _positions = new TimeSpan[1024];
        var random = new Random(56);
        for (var i = 0; i < _positions.Length; i++)
        {
            _positions[i] = TimeSpan.FromTicks((long)(random.NextDouble() * clock.Ticks));
        }
    }

    /// <summary>One playhead update: the word being spoken, then its line.</summary>
    [Benchmark]
    public int FindWordAndSegment()
    {
        var position = _positions[_next++ & (_positions.Length - 1)];
        var word = _timeline.FindWord(position);
        return word >= 0 ? _timeline.SegmentOfWord(word) : _timeline.FindSegment(position);
    }
}
