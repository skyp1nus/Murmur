namespace Murmur.Transcription.Bench;

/// <summary>A scored word: its confidence and whether it is wrong against the reference.</summary>
internal readonly record struct ScoredWord(float Confidence, bool Wrong);

/// <summary>"Flag the word when confidence &lt; threshold" evaluated as a detector of wrong words.</summary>
internal sealed record ThresholdPoint(float Threshold, int Flagged, int TruePositives, double Precision, double Recall, double F1);

internal sealed record HistogramBin(float From, float To, int Correct, int Wrong);

internal sealed record ScoreSummary
{
    public int Words { get; init; }

    public int Wrong { get; init; }

    /// <summary>Probability that a random wrong word scores lower than a random correct one (ROC AUC; 0.5 = useless).</summary>
    public double Auc { get; init; }

    public double MeanCorrect { get; init; }

    public double MeanWrong { get; init; }

    public double MedianCorrect { get; init; }

    public double MedianWrong { get; init; }

    public ThresholdPoint? BestF1 { get; init; }

    public IReadOnlyList<ThresholdPoint> Thresholds { get; init; } = [];

    public IReadOnlyList<HistogramBin> Histogram { get; init; } = [];
}

internal static class ConfidenceMetrics
{
    /// <summary>Thresholds 0.05, 0.10, ... 0.95 plus 0.97 and 0.99 (Parakeet is confident on most words).</summary>
    public static readonly float[] DefaultThresholds =
        [0.05f, 0.1f, 0.15f, 0.2f, 0.25f, 0.3f, 0.35f, 0.4f, 0.45f, 0.5f, 0.55f, 0.6f, 0.65f, 0.7f, 0.75f, 0.8f, 0.85f, 0.9f, 0.95f, 0.97f, 0.99f];

    public static ThresholdPoint At(IReadOnlyList<ScoredWord> words, float threshold)
    {
        ArgumentNullException.ThrowIfNull(words);
        int flagged = 0, tp = 0, wrong = 0;
        foreach (var w in words)
        {
            if (w.Wrong)
            {
                wrong++;
            }

            if (w.Confidence < threshold)
            {
                flagged++;
                if (w.Wrong)
                {
                    tp++;
                }
            }
        }

        var precision = flagged == 0 ? 0 : tp / (double)flagged;
        var recall = wrong == 0 ? 0 : tp / (double)wrong;
        var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        return new ThresholdPoint(threshold, flagged, tp, Math.Round(precision, 4), Math.Round(recall, 4), Math.Round(f1, 4));
    }

    /// <summary>Rank-based ROC AUC (Mann-Whitney U, ties count half); 0.5 when one class is empty.</summary>
    public static double Auc(IReadOnlyList<ScoredWord> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var sorted = words.OrderBy(w => w.Confidence).ToArray();
        long wrong = sorted.Count(w => w.Wrong);
        long correct = sorted.Length - wrong;
        if (wrong == 0 || correct == 0)
        {
            return 0.5;
        }

        // Sum of ranks of the correct words (1-based, average rank for ties).
        double rankSum = 0;
        var i = 0;
        while (i < sorted.Length)
        {
            var j = i;
            while (j + 1 < sorted.Length && sorted[j + 1].Confidence == sorted[i].Confidence)
            {
                j++;
            }

            var averageRank = ((i + 1) + (j + 1)) / 2.0;
            for (var k = i; k <= j; k++)
            {
                if (!sorted[k].Wrong)
                {
                    rankSum += averageRank;
                }
            }

            i = j + 1;
        }

        var u = rankSum - (correct * (correct + 1) / 2.0);
        return Math.Round(u / (correct * (double)wrong), 4);
    }

    public static ScoreSummary Summarize(IReadOnlyList<ScoredWord> words, IReadOnlyList<float>? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(words);
        var points = (thresholds ?? DefaultThresholds).Select(t => At(words, t)).ToArray();
        var best = points.Where(p => p.TruePositives > 0).OrderByDescending(p => p.F1).ThenBy(p => p.Threshold).FirstOrDefault();
        var correct = words.Where(w => !w.Wrong).Select(w => (double)w.Confidence).ToArray();
        var wrong = words.Where(w => w.Wrong).Select(w => (double)w.Confidence).ToArray();
        return new ScoreSummary
        {
            Words = words.Count,
            Wrong = wrong.Length,
            Auc = Auc(words),
            MeanCorrect = Math.Round(correct.DefaultIfEmpty().Average(), 4),
            MeanWrong = Math.Round(wrong.DefaultIfEmpty().Average(), 4),
            MedianCorrect = Math.Round(Median(correct), 4),
            MedianWrong = Math.Round(Median(wrong), 4),
            BestF1 = best,
            Thresholds = points,
            Histogram = Histogram(words, 10),
        };
    }

    public static IReadOnlyList<HistogramBin> Histogram(IReadOnlyList<ScoredWord> words, int bins)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentOutOfRangeException.ThrowIfLessThan(bins, 1);
        var correct = new int[bins];
        var wrong = new int[bins];
        foreach (var w in words)
        {
            var b = Math.Clamp((int)(w.Confidence * bins), 0, bins - 1);
            if (w.Wrong)
            {
                wrong[b]++;
            }
            else
            {
                correct[b]++;
            }
        }

        var result = new HistogramBin[bins];
        for (var b = 0; b < bins; b++)
        {
            result[b] = new HistogramBin(b / (float)bins, (b + 1) / (float)bins, correct[b], wrong[b]);
        }

        return result;
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        var s = values.Order().ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[(s.Length / 2) - 1] + s[s.Length / 2]) / 2;
    }
}
