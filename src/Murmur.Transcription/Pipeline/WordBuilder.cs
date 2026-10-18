using System.Text;
using Murmur.Core.Transcription.Protocol;

namespace Murmur.Transcription.Pipeline;

/// <summary>
/// Groups SentencePiece tokens into words with absolute times. A token starting with U+2581 ("▁") or a space begins
/// a new word; other tokens (sub-words, punctuation) extend the current one. Word start = time of its first token;
/// word end = last token time + its duration, or — without a usable duration — the next word's start / segment end.
/// Ends never overlap the next word and never leave the segment, so times are monotonic.
/// </summary>
internal static class WordBuilder
{
    public const char WordBoundary = '▁';

    /// <param name="timestamps">Token start times in seconds relative to the segment (same length as tokens).</param>
    /// <param name="durations">Token durations in seconds, or empty when the model does not provide them.</param>
    /// <param name="segmentStart">Absolute start of the segment in seconds.</param>
    /// <param name="segmentEnd">Absolute end of the segment in seconds.</param>
    public static List<WorkerWord> Build(
        ReadOnlySpan<string> tokens,
        ReadOnlySpan<float> timestamps,
        ReadOnlySpan<float> durations,
        double segmentStart,
        double segmentEnd)
    {
        var words = new List<WorkerWord>();
        if (tokens.IsEmpty || timestamps.Length != tokens.Length)
        {
            return words; // no per-token times: words cannot be placed
        }

        var haveDurations = durations.Length == tokens.Length;
        var segmentLength = Math.Max(0.0, segmentEnd - segmentStart);

        // Pass 1: word spans as [first token, last token] plus text.
        var spans = new List<(int First, int Last, string Text)>();
        var text = new StringBuilder();
        var first = -1;
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i] ?? string.Empty;
            var boundary = token.Length > 0 && (token[0] == WordBoundary || token[0] == ' ');
            var piece = boundary ? token.TrimStart(WordBoundary, ' ') : token;
            if (boundary || first < 0)
            {
                Flush(spans, text, first, i - 1);
                first = i;
            }

            text.Append(piece);
        }

        Flush(spans, text, first, tokens.Length - 1);

        // Pass 2: times.
        var previousStart = 0.0;
        for (var w = 0; w < spans.Count; w++)
        {
            var (f, l, t) = spans[w];
            var start = Math.Clamp(Time(timestamps[f], previousStart), previousStart, segmentLength);
            var nextStart = w + 1 < spans.Count ? Math.Clamp(Time(timestamps[spans[w + 1].First], start), start, segmentLength) : segmentLength;

            double end;
            if (haveDurations && durations[l] > 0 && float.IsFinite(durations[l]) && float.IsFinite(timestamps[l]))
            {
                end = timestamps[l] + (double)durations[l];
            }
            else
            {
                end = nextStart;
            }

            end = Math.Clamp(end, start, nextStart);
            previousStart = start;
            words.Add(new WorkerWord(t, Round(segmentStart + start), Round(segmentStart + end)));
        }

        return words;
    }

    private static double Time(float value, double fallback) => float.IsFinite(value) ? value : fallback;

    private static void Flush(List<(int, int, string)> spans, StringBuilder text, int first, int last)
    {
        if (first >= 0 && last >= first)
        {
            var s = text.ToString().Trim();
            if (s.Length > 0)
            {
                spans.Add((first, last, s));
            }
        }

        text.Clear();
    }

    /// <summary>Millisecond precision keeps the JSON compact; sub-millisecond detail is noise for an 80 ms frame model.</summary>
    public static double Round(double seconds) => Math.Round(seconds, 3, MidpointRounding.AwayFromZero);
}
