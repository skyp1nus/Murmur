using Murmur.Core.Transcription;

namespace Murmur.App.ViewModels;

/// <summary>
/// Position-to-word lookup over a transcript. Words are numbered 0..n-1 in transcript order (segment by segment);
/// <see cref="FindWord"/> is a binary search over their start times, so it costs O(log n) per player tick.
/// </summary>
/// <remarks>
/// Segments of the two tracks can overlap in time, so word starts are not always increasing in transcript order. The
/// search therefore runs over a start-sorted copy (identity when the transcript is already ordered, which is the common case).
/// </remarks>
internal sealed class TranscriptTimeline
{
    /// <summary>How long after its end a word stays highlighted, so short pauses do not flicker the highlight off.</summary>
    public static readonly TimeSpan HighlightHold = TimeSpan.FromSeconds(1);

    private readonly TimeSpan[] _starts;
    private readonly TimeSpan[] _ends;
    private readonly int[] _wordSegment;
    private readonly int[] _segmentFirstWord;
    private readonly TimeSpan[] _segmentStarts;

    // Sorted view for the search: _sortedStarts[i] is the start of word _sortedIds[i].
    private readonly TimeSpan[] _sortedStarts;
    private readonly int[] _sortedIds;

    public TranscriptTimeline(IReadOnlyList<TranscriptSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var total = 0;
        foreach (var segment in segments)
        {
            total += segment.Words.Count;
        }

        _starts = new TimeSpan[total];
        _ends = new TimeSpan[total];
        _wordSegment = new int[total];
        _segmentFirstWord = new int[segments.Count];
        _segmentStarts = new TimeSpan[segments.Count];

        var id = 0;
        var ordered = true;
        for (var s = 0; s < segments.Count; s++)
        {
            var segment = segments[s];
            _segmentFirstWord[s] = id;
            _segmentStarts[s] = segment.Start;
            for (var w = 0; w < segment.Words.Count; w++)
            {
                var word = segment.Words[w];
                _starts[id] = word.Start;
                _ends[id] = word.End;
                _wordSegment[id] = s;
                if (id > 0 && word.Start < _starts[id - 1])
                {
                    ordered = false;
                }

                id++;
            }
        }

        _sortedIds = new int[total];
        for (var i = 0; i < total; i++)
        {
            _sortedIds[i] = i;
        }

        if (ordered)
        {
            _sortedStarts = _starts;
        }
        else
        {
            _sortedStarts = (TimeSpan[])_starts.Clone();
            Array.Sort(_sortedStarts, _sortedIds);
        }
    }

    public int WordCount => _starts.Length;

    public int SegmentCount => _segmentStarts.Length;

    public TimeSpan WordStart(int word) => _starts[word];

    public int SegmentOfWord(int word) => _wordSegment[word];

    public int FirstWordOfSegment(int segment) => _segmentFirstWord[segment];

    /// <summary>
    /// The word being spoken at <paramref name="position"/>: the last word that started at or before it, as long as
    /// the position is less than <see cref="HighlightHold"/> past its end. -1 before the first word or in a long pause.
    /// </summary>
    public int FindWord(TimeSpan position)
    {
        var index = LastAtOrBefore(_sortedStarts, position);
        if (index < 0)
        {
            return -1;
        }

        var word = _sortedIds[index];
        return position < _ends[word] + HighlightHold ? word : -1;
    }

    /// <summary>The last segment that starts at or before <paramref name="position"/> (segments are sorted by start); -1 before the first.</summary>
    public int FindSegment(TimeSpan position) => LastAtOrBefore(_segmentStarts, position);

    private static int LastAtOrBefore(TimeSpan[] sorted, TimeSpan position)
    {
        int low = 0, high = sorted.Length - 1, found = -1;
        while (low <= high)
        {
            var mid = low + ((high - low) >> 1);
            if (sorted[mid] <= position)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found;
    }
}
