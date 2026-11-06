using System.Text;

namespace Murmur.App.ViewModels;

/// <summary>
/// What "Did you mean" offers for an uncertain word (#58): first the user's vocabulary (words they picked or kept before), then the local
/// model's ideas, at most <see cref="MaxAlternatives"/>, never the word itself and never one twice (any case). The recogniser gives no
/// alternatives of its own (spike #53, D-052).
/// </summary>
internal static class WordSuggestions
{
    /// <summary>The popover shows at most this many alternatives, plus "Keep".</summary>
    public const int MaxAlternatives = 3;

    /// <summary>A vocabulary word at most this many edits away (case-insensitive) is offered.</summary>
    public const int MaxDistance = 2;

    /// <summary>A vocabulary word that starts with the same this many letters (case-insensitive) is offered too.</summary>
    public const int SharedPrefix = 3;

    /// <summary>
    /// The vocabulary words that look like <paramref name="word"/>: at most <see cref="MaxDistance"/> edits away, or sharing its first
    /// <see cref="SharedPrefix"/> letters. The closest first; equally close ones in the order of the vocabulary (the newest first).
    /// </summary>
    public static List<string> FromVocabulary(string word, IReadOnlyList<string> vocabulary, int max = MaxAlternatives)
    {
        var target = word.ToLowerInvariant();
        var matches = new List<(string Word, int Distance, int Order)>();
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

            var close = Distance(target, lower, MaxDistance) <= MaxDistance;
            if (close || SharesPrefix(target, lower))
            {
                // The order needs the real distance of a word that only shares the first letters.
                matches.Add((candidate, close ? Distance(target, lower, MaxDistance) : Distance(target, lower, Math.Max(target.Length, lower.Length)), i));
            }
        }

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

    /// <summary><paramref name="first"/>, then <paramref name="then"/>: without <paramref name="word"/>, without repeats (any case), at most <paramref name="max"/>.</summary>
    public static List<string> Merge(string word, IReadOnlyList<string> first, IReadOnlyList<string> then, int max = MaxAlternatives)
    {
        var result = new List<string>(max);
        Add(first);
        Add(then);
        return result;

        void Add(IReadOnlyList<string> source)
        {
            foreach (var raw in source)
            {
                var candidate = raw?.Trim();
                if (result.Count == max)
                {
                    return;
                }

                if (!string.IsNullOrEmpty(candidate) && !string.Equals(candidate, word, StringComparison.OrdinalIgnoreCase) && !Contains(result, candidate))
                {
                    result.Add(candidate);
                }
            }
        }
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
