using System.Text;

namespace Murmur.App.ViewModels;

/// <summary>
/// What "Did you mean" offers for an uncertain word (#58): first the user's vocabulary words that are a small edit away from it, then the
/// local model's ideas, then vocabulary words that only start like it; at most <see cref="MaxAlternatives"/>, never the word itself and
/// never one twice (any case). The recogniser gives no alternatives of its own (spike #53, D-052).
/// </summary>
internal static class WordSuggestions
{
    /// <summary>The popover shows at most this many alternatives, plus "Keep".</summary>
    public const int MaxAlternatives = 3;

    /// <summary>A vocabulary word at most this many edits away (case-insensitive) is a close match.</summary>
    public const int MaxDistance = 2;

    /// <summary>The edit distance counts only when both words have at least this many characters (two edits turn "on" into "I").</summary>
    public const int MinDistanceLength = 4;

    /// <summary>A vocabulary word that starts with the same this many letters (case-insensitive) is offered too, after the model's ideas.</summary>
    public const int SharedPrefix = 3;

    /// <summary>
    /// The vocabulary words that look like <paramref name="word"/>. <c>Close</c>: at most <see cref="MaxDistance"/> edits away, both words at
    /// least <see cref="MinDistanceLength"/> long. <c>Prefix</c>: the others that share its first <see cref="SharedPrefix"/> letters. Each
    /// list the closest first, equally close ones in the order of the vocabulary (the newest first), at most <paramref name="max"/>.
    /// </summary>
    public static (List<string> Close, List<string> Prefix) FromVocabulary(string word, IReadOnlyList<string> vocabulary, int max = MaxAlternatives)
    {
        var target = word.ToLowerInvariant();
        var close = new List<(string Word, int Distance, int Order)>();
        var prefix = new List<(string Word, int Distance, int Order)>();
        for (var i = 0; i < vocabulary.Count; i++)
        {
            var candidate = vocabulary[i]?.Trim();
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }

            var lower = candidate.ToLowerInvariant();
            if (string.Equals(lower, target, StringComparison.Ordinal))
            {
                continue;
            }

            var distance = Distance(target, lower, Math.Max(target.Length, lower.Length));
            if (distance <= MaxDistance && target.Length >= MinDistanceLength && lower.Length >= MinDistanceLength)
            {
                close.Add((candidate, distance, i));
            }
            else if (SharesPrefix(target, lower))
            {
                prefix.Add((candidate, distance, i));
            }
        }

        return (Ranked(close, max), Ranked(prefix, max));
    }

    /// <summary>The sources in order: without <paramref name="word"/>, without repeats (any case), at most <see cref="MaxAlternatives"/>.</summary>
    public static List<string> Merge(string word, params IReadOnlyList<string>[] sources)
    {
        var result = new List<string>(MaxAlternatives);
        foreach (var source in sources)
        {
            foreach (var raw in source)
            {
                var candidate = raw?.Trim();
                if (result.Count == MaxAlternatives)
                {
                    return result;
                }

                if (!string.IsNullOrEmpty(candidate) && !string.Equals(candidate, word, StringComparison.OrdinalIgnoreCase) && !Contains(result, candidate))
                {
                    result.Add(candidate);
                }
            }
        }

        return result;
    }

    private static List<string> Ranked(List<(string Word, int Distance, int Order)> matches, int max)
    {
        matches.Sort(static (a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Order.CompareTo(b.Order));
        var result = new List<string>(Math.Min(max, matches.Count));
        foreach (var (candidate, _, _) in matches)
        {
            if (result.Count == max)
            {
                break;
            }

            if (!Contains(result, candidate))
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    /// <summary>
    /// Splits a word of the transcript into the punctuation in front, the word itself (letters and digits) and the punctuation after it:
    /// "(parquet?" → ("(", "parquet", "?"). A fix replaces the middle and keeps the rest.
    /// </summary>
    public static (string Prefix, string Core, string Suffix) Split(string text)
    {
        var trimmed = text.Trim();
        var start = 0;
        var end = trimmed.Length;
        while (start < end && !char.IsLetterOrDigit(trimmed[start]))
        {
            start++;
        }

        while (end > start && !char.IsLetterOrDigit(trimmed[end - 1]))
        {
            end--;
        }

        return start == end ? (string.Empty, trimmed, string.Empty) : (trimmed[..start], trimmed[start..end], trimmed[end..]);
    }

    /// <summary>
    /// <paramref name="text"/> with word <paramref name="index"/> replaced, the same way the store does it: the words are found in the text one
    /// after the other, so the spacing stays; when one cannot be found the text is rebuilt from the words joined by spaces.
    /// </summary>
    public static string ReplaceInText(string text, IReadOnlyList<Core.Transcription.TranscriptWord> words, int index, string replacement)
    {
        var at = 0;
        for (var i = 0; i <= index; i++)
        {
            var word = words[i].Text.AsSpan().Trim();
            if (word.IsEmpty)
            {
                if (i == index)
                {
                    break;
                }

                continue;
            }

            var found = text.AsSpan(at).IndexOf(word, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            found += at;
            if (i == index)
            {
                return string.Concat(text.AsSpan(0, found), replacement, text.AsSpan(found + word.Length));
            }

            at = found + word.Length;
        }

        var joined = new StringBuilder(text.Length + replacement.Length);
        for (var i = 0; i < words.Count; i++)
        {
            var word = i == index ? replacement.AsSpan() : words[i].Text.AsSpan().Trim();
            if (word.IsEmpty)
            {
                continue;
            }

            if (joined.Length > 0)
            {
                joined.Append(' ');
            }

            joined.Append(word);
        }

        return joined.ToString();
    }

    private static bool Contains(List<string> list, string candidate)
    {
        foreach (var item in list)
        {
            if (string.Equals(item, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SharesPrefix(string a, string b)
    {
        if (a.Length < SharedPrefix || b.Length < SharedPrefix)
        {
            return false;
        }

        for (var i = 0; i < SharedPrefix; i++)
        {
            if (a[i] != b[i] || !char.IsLetter(a[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Levenshtein distance, or <paramref name="limit"/> + 1 as soon as it is known to be larger.</summary>
    internal static int Distance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit)
        {
            return limit + 1;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var best = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                best = Math.Min(best, current[j]);
            }

            if (best > limit)
            {
                return limit + 1;
            }

            (previous, current) = (current, previous);
        }

        return Math.Min(previous[b.Length], limit + 1);
    }
}
