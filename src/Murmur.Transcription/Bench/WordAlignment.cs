using System.Text;

namespace Murmur.Transcription.Bench;

/// <summary>How a recognised word compares with the reference transcript.</summary>
internal enum WordLabel
{
    Correct,

    /// <summary>The word stands where the reference has a different word.</summary>
    Substitution,

    /// <summary>The word has no counterpart in the reference.</summary>
    Insertion,
}

/// <summary>Result of aligning hypothesis words with reference words (Levenshtein, unit costs).</summary>
/// <param name="Labels">One label per hypothesis word (same order).</param>
/// <param name="Deletions">Reference words with no hypothesis counterpart (cannot be flagged by confidence).</param>
/// <param name="ReferenceWords">Number of reference words (WER denominator).</param>
/// <param name="MissedReference">Reference words that were substituted or deleted, in reference order.</param>
internal sealed record AlignmentResult(WordLabel[] Labels, int Deletions, int ReferenceWords, IReadOnlyList<int> MissedReference)
{
    public int Substitutions => Labels.Count(l => l == WordLabel.Substitution);

    public int Insertions => Labels.Count(l => l == WordLabel.Insertion);

    public int Errors => Substitutions + Insertions + Deletions;
}

/// <summary>
/// Word normalisation and alignment for the word-confidence spike (#53): a recognised word is "wrong" when, after
/// lower-casing and stripping punctuation on both sides, it is a substitution or an insertion against the reference.
/// </summary>
internal static class WordAlignment
{
    /// <summary>
    /// Lower-case, apostrophes unified to U+0027, every character other than a letter, digit, apostrophe or hyphen
    /// removed, then apostrophes and hyphens trimmed from both ends. Empty for punctuation-only input.
    /// </summary>
    public static string Normalize(string? word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(word.Length);
        foreach (var raw in word)
        {
            var c = raw switch
            {
                '’' or '‘' or 'ʼ' or '`' or '´' => '\'',
                '‐' or '‑' or '–' => '-',
                _ => raw,
            };
            if (char.IsLetterOrDigit(c) || c is '\'' or '-')
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString().Trim('\'', '-');
    }

    /// <summary>
    /// Splits text into words: on whitespace, and on an em / en dash between two letters ("government—with" is two
    /// words, as in the reference transcripts). Hyphens stay inside words ("будь-який").
    /// </summary>
    public static string[] SplitWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var chars = text.ToCharArray();
        for (var i = 1; i + 1 < chars.Length; i++)
        {
            if (chars[i] is '—' or '–' && char.IsLetter(chars[i - 1]) && char.IsLetter(chars[i + 1]))
            {
                chars[i] = ' ';
            }
        }

        return new string(chars).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Splits with <see cref="SplitWords"/> and normalises; punctuation-only words are dropped.</summary>
    public static List<string> NormalizeText(string? text)
    {
        var words = new List<string>();
        foreach (var w in SplitWords(text))
        {
            var n = Normalize(w);
            if (n.Length > 0)
            {
                words.Add(n);
            }
        }

        return words;
    }

    /// <summary>Aligns normalised hypothesis words with normalised reference words (both non-empty strings).</summary>
    public static AlignmentResult Align(IReadOnlyList<string> hypothesis, IReadOnlyList<string> reference)
    {
        ArgumentNullException.ThrowIfNull(hypothesis);
        ArgumentNullException.ThrowIfNull(reference);
        int h = hypothesis.Count, r = reference.Count;
        var d = new int[h + 1, r + 1];
        for (var i = 0; i <= h; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= r; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= h; i++)
        {
            for (var j = 1; j <= r; j++)
            {
                var same = string.Equals(hypothesis[i - 1], reference[j - 1], StringComparison.Ordinal);
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (same ? 0 : 1));
            }
        }

        var labels = new WordLabel[h];
        var missed = new List<int>();
        var deletions = 0;
        int a = h, b = r;
        while (a > 0 || b > 0)
        {
            if (a > 0 && b > 0)
            {
                var same = string.Equals(hypothesis[a - 1], reference[b - 1], StringComparison.Ordinal);
                if (d[a, b] == d[a - 1, b - 1] + (same ? 0 : 1))
                {
                    labels[a - 1] = same ? WordLabel.Correct : WordLabel.Substitution;
                    if (!same)
                    {
                        missed.Add(b - 1);
                    }

                    a--;
                    b--;
                    continue;
                }
            }

            if (a > 0 && d[a, b] == d[a - 1, b] + 1)
            {
                labels[a - 1] = WordLabel.Insertion;
                a--;
            }
            else
            {
                deletions++;
                missed.Add(b - 1);
                b--;
            }
        }

        missed.Reverse();
        return new AlignmentResult(labels, deletions, r, missed);
    }
}

/// <summary>One evaluation clip: a 16 kHz-convertible WAV with its reference transcript.</summary>
internal sealed record EvalClip(string Language, string WavPath, string Reference);

internal static class EvalManifest
{
    /// <summary>
    /// Tab-separated lines <c>language, wav path, reference text</c>; blank lines and lines starting with <c>#</c>
    /// are skipped; relative WAV paths are resolved against <paramref name="baseDirectory"/>.
    /// </summary>
    /// <exception cref="FormatException">A line does not have three non-empty fields.</exception>
    public static List<EvalClip> Parse(IEnumerable<string> lines, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var clips = new List<EvalClip>();
        var number = 0;
        foreach (var line in lines)
        {
            number++;
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length < 3 || parts[0].Trim().Length == 0 || parts[1].Trim().Length == 0 || parts[2].Trim().Length == 0)
            {
                throw new FormatException($"manifest line {number}: expected 'language<TAB>wav<TAB>reference'");
            }

            var wav = parts[1].Trim();
            clips.Add(new EvalClip(parts[0].Trim(), Path.IsPathRooted(wav) ? wav : Path.Combine(baseDirectory, wav), string.Join(' ', parts[2..]).Trim()));
        }

        return clips;
    }
}
