using System.Globalization;
using System.Text.RegularExpressions;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;

namespace Murmur.Intelligence.Rag;

/// <summary>Turns <c>[12:34]</c> / <c>[1:02:03]</c> timestamps in an answer into <see cref="Citation"/>s.</summary>
public static partial class CitationResolver
{
    /// <summary>
    /// <c>[m:ss]</c>, <c>[mm:ss]</c> or <c>[h:mm:ss]</c>; a copied excerpt range such as <c>[12:00–12:45]</c> cites its start.
    /// </summary>
    [GeneratedRegex(@"\[(?:(\d+):)?(\d{1,2}):(\d{2})(?:\s*[–-]\s*(?:\d+:)?\d{1,2}:\d{2})?\]", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();

    /// <summary>Every well-formed timestamp in <paramref name="answer"/>, in order of appearance (duplicates kept).</summary>
    public static IReadOnlyList<TimeSpan> Parse(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var times = new List<TimeSpan>();
        foreach (Match match in TimestampPattern().Matches(answer))
        {
            var hours = match.Groups[1].Success ? int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture) : 0;
            var minutes = int.Parse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
            var seconds = int.Parse(match.Groups[3].ValueSpan, CultureInfo.InvariantCulture);
            if (seconds >= 60 || (match.Groups[1].Success && minutes >= 60) || hours > 999)
            {
                continue;
            }

            times.Add(new TimeSpan(hours, minutes, seconds));
        }

        return times;
    }

    /// <summary>
    /// Resolves each cited time to the best-ranked excerpt whose [Start − tolerance, End + tolerance] contains it,
    /// dropping times that match no excerpt and duplicates. When nothing resolves, the top excerpt's start is cited.
    /// </summary>
    /// <param name="excerpts">Retrieved chunks, best first.</param>
    public static IReadOnlyList<Citation> Resolve(string answer, IReadOnlyList<TextChunk> excerpts, TimeSpan tolerance, int snippetLength)
    {
        ArgumentNullException.ThrowIfNull(excerpts);
        var citations = new List<Citation>();
        var seen = new HashSet<(SessionId, TimeSpan)>();
        foreach (var at in Parse(answer))
        {
            foreach (var excerpt in excerpts)
            {
                if (at < excerpt.Start - tolerance || at > excerpt.End + tolerance)
                {
                    continue;
                }

                if (seen.Add((excerpt.Session, at)))
                {
                    citations.Add(new Citation(excerpt.Session, at, Timestamps.Format(at), Snippet(excerpt, at, snippetLength)));
                }

                break;
            }
        }

        if (citations.Count == 0 && excerpts.Count > 0)
        {
            var top = excerpts[0];
            citations.Add(new Citation(top.Session, top.Start, Timestamps.Format(top.Start), Snippet(top, top.Start, snippetLength)));
        }

        return citations;
    }

    /// <summary>
    /// About <paramref name="length"/> characters of the chunk around <paramref name="at"/>; the position is estimated
    /// proportionally to the time inside the chunk and snapped to word boundaries.
    /// </summary>
    public static string Snippet(TextChunk chunk, TimeSpan at, int length)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var text = chunk.Text.ReplaceLineEndings(" ").Trim();
        if (length <= 0 || text.Length <= length)
        {
            return text;
        }

        var duration = chunk.End - chunk.Start;
        var fraction = duration > TimeSpan.Zero ? Math.Clamp((at - chunk.Start) / duration, 0, 1) : 0;
        var center = (int)(fraction * text.Length);
        var from = Math.Clamp(center - (length / 2), 0, text.Length - length);
        var to = from + length;

        if (from > 0)
        {
            var space = text.IndexOf(' ', from);
            if (space >= 0 && space < from + (length / 4))
            {
                from = space + 1;
            }
        }

        if (to < text.Length)
        {
            var space = text.LastIndexOf(' ', to);
            if (space > from + (length / 2))
            {
                to = space;
            }
        }

        return (from > 0 ? "…" : string.Empty) + text[from..to].Trim() + (to < text.Length ? "…" : string.Empty);
    }
}
