namespace Murmur.Transcription.Pipeline;

/// <summary>How the token probabilities of one word are folded into the word's confidence.</summary>
internal enum ConfidenceAggregation
{
    /// <summary>Probability of the least certain token (a single doubtful sub-word makes the word doubtful).</summary>
    Min,

    /// <summary>Arithmetic mean of the token probabilities.</summary>
    Mean,

    /// <summary>exp(mean log-probability): the per-token geometric mean.</summary>
    GeometricMean,
}

/// <summary>
/// Word confidence (0..1) from the per-token log-probabilities of sherpa-onnx (<c>ys_log_probs</c>, see
/// <see cref="Native.RecognitionResult.LogProbs"/>). Tokens are grouped into words exactly like
/// <see cref="WordBuilder"/> does (<see cref="WordBuilder.Spans"/>), so the result lines up one to one with the
/// words of a segment. Punctuation-only tokens ("," "." "?") are left out of a word's score, because the transcript
/// marks a word, not its punctuation, as uncertain; a word made only of such tokens is scored from all of them.
/// Diagnostic / spike code (#53): the production protocol does not carry confidence yet.
/// </summary>
internal static class WordConfidence
{
    public const ConfidenceAggregation DefaultAggregation = ConfidenceAggregation.Min;

    /// <summary>Per-word confidence, or an empty array when the log-probabilities do not match the tokens.</summary>
    public static float[] Compute(ReadOnlySpan<string> tokens, ReadOnlySpan<float> logProbs, ConfidenceAggregation aggregation = DefaultAggregation)
    {
        if (tokens.IsEmpty || logProbs.Length != tokens.Length)
        {
            return [];
        }

        var spans = WordBuilder.Spans(tokens);
        var result = new float[spans.Count];
        for (var w = 0; w < spans.Count; w++)
        {
            var (first, last, _) = spans[w];
            result[w] = ForWord(tokens[first..(last + 1)], logProbs[first..(last + 1)], aggregation);
        }

        return result;
    }

    /// <summary>Confidence of one word given its tokens and their log-probabilities (same length).</summary>
    public static float ForWord(ReadOnlySpan<string> tokens, ReadOnlySpan<float> logProbs, ConfidenceAggregation aggregation)
    {
        if (tokens.Length != logProbs.Length)
        {
            throw new ArgumentException("Tokens and log-probabilities differ in length.", nameof(logProbs));
        }

        var lexical = false;
        foreach (var t in tokens)
        {
            if (IsLexical(t))
            {
                lexical = true;
                break;
            }
        }

        var min = double.PositiveInfinity;
        var sumProb = 0.0;
        var sumLog = 0.0;
        var n = 0;
        for (var i = 0; i < tokens.Length; i++)
        {
            var lp = logProbs[i];
            if (float.IsNaN(lp) || (lexical && !IsLexical(tokens[i])))
            {
                continue;
            }

            var log = Math.Min(0.0, lp); // a log-probability is never positive; guard against rounding
            min = Math.Min(min, log);
            sumProb += Math.Exp(log);
            sumLog += log;
            n++;
        }

        if (n == 0)
        {
            return 0f;
        }

        var value = aggregation switch
        {
            ConfidenceAggregation.Min => Math.Exp(min),
            ConfidenceAggregation.Mean => sumProb / n,
            ConfidenceAggregation.GeometricMean => Math.Exp(sumLog / n),
            _ => throw new ArgumentOutOfRangeException(nameof(aggregation), aggregation, null),
        };
        return (float)Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>True when the token (without its word-boundary mark) has a letter or a digit.</summary>
    public static bool IsLexical(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        foreach (var c in token)
        {
            if (char.IsLetterOrDigit(c))
            {
                return true;
            }
        }

        return false;
    }
}
