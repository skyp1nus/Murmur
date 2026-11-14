using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Intelligence.Rag;

namespace Murmur.AiEval;

/// <summary>Outcome of scoring one question.</summary>
/// <param name="ExplicitCitation">The answer wrote at least one timestamp that resolved to an excerpt.</param>
/// <param name="MatchedExplicitly">Answerable: a citation within the tolerance was written by the model itself, not the fallback.</param>
/// <param name="ConveysFact">Full mode, answerable: the answer is non-empty, not a "not found" and contains one of the fact's stems.</param>
public sealed record QuestionScore(
    bool Pass,
    bool Skipped,
    IReadOnlyList<string> Cited,
    bool ExplicitCitation,
    bool? SaysNotFound,
    bool MatchedExplicitly = false,
    bool? ConveysFact = null);

/// <summary>Pass/fail rules of the evaluation (pure functions, unit-tested).</summary>
public static class Scoring
{
    public const double DefaultThreshold = 0.8;

    /// <summary>Retrieval modes: a hit "contains the fact" when the fact's start lies inside it, widened by this much.</summary>
    public static readonly TimeSpan RetrievalSlack = TimeSpan.FromSeconds(2);

    // Lower-case markers of "the recording does not say"; apostrophes are normalized to U+0027 first.
    private static readonly string[] NotFoundMarkers =
    [
        "немає", "нема ", "нема,", "нема.", "не згаду", "не знайд", "не знайш", "не знайти", "не обговорю", "не обговорювал", "не йдеться",
        "не було", "відсутн", "не міст", "не вказ", "не сказано", "не прозвуча", "невідомо", "не можу знайти",
        "not mentioned", "no mention", "not found", "no information", "not in the", "isn't mentioned",
        "is not mentioned", "does not contain", "doesn't contain", "do not contain", "don't contain", "not discussed",
        "no record", "don't have", "do not have", "not available", "cannot find", "can't find", "couldn't find",
        "could not find", "not specified", "not stated", "doesn't say", "does not say", "no details",
    ];

    /// <summary>Simple UA/EN keyword check: does the answer say the information is not in the recording?</summary>
    public static bool SaysNotFound(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var text = Normalize(answer) + " ";
        foreach (var marker in NotFoundMarkers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Does the answer contain one of the question's <see cref="EvalQuestion.FactStems"/> (case-insensitive)?</summary>
    public static bool ContainsAnyFactStem(string answer, EvalQuestion question)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(question);
        var text = Normalize(answer);
        foreach (var stem in question.FactStems)
        {
            if (stem.Length > 0 && text.Contains(Normalize(stem), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Does the answer actually state the fact? Non-empty, not a "not found" and mentions one of the fact's stems.
    /// Guards the fallback citation: it lands on the top excerpt whatever the model wrote.
    /// </summary>
    public static bool ConveysFact(string answer, EvalQuestion question) =>
        !string.IsNullOrWhiteSpace(answer) && !SaysNotFound(answer) && ContainsAnyFactStem(answer, question);

    public static bool WithinTolerance(TimeSpan cited, TimeSpan expected, TimeSpan tolerance) =>
        (cited - expected).Duration() <= tolerance;

    /// <summary>
    /// Full mode. Answerable: pass when the model itself wrote a timestamp within the tolerance of the fact, or when
    /// only the assistant's fallback citation of the top excerpt is within it (that is the chip the user clicks) and
    /// the answer conveys the fact (<see cref="ConveysFact"/>): the fallback is added whatever the model wrote, so an
    /// empty, "not found" or off-topic answer must not ride on good retrieval. Unanswerable: pass
    /// when the answer says it is not in the recording and wrote no timestamp of its own (the assistant always adds
    /// the top excerpt as a fallback citation, which is not a confident claim).
    /// </summary>
    public static QuestionScore ScoreAnswer(EvalQuestion question, string answer, IReadOnlyList<Citation> citations)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(citations);

        var written = new HashSet<TimeSpan>(CitationResolver.Parse(answer));
        var labels = new List<string>(citations.Count);
        var explicitCitation = false;
        foreach (var citation in citations)
        {
            labels.Add(citation.Label);
            explicitCitation |= written.Contains(citation.At);
        }

        if (!question.Answerable)
        {
            var notFound = SaysNotFound(answer);
            return new QuestionScore(notFound && !explicitCitation, false, labels, explicitCitation, notFound);
        }

        var expected = question.ExpectedAt ?? throw new ArgumentException($"Question {question.Id} has no expected time.", nameof(question));
        var withinFallback = false;
        var matchedExplicitly = false;
        foreach (var citation in citations)
        {
            var within = WithinTolerance(citation.At, expected, question.Tolerance);
            var byModel = written.Contains(citation.At);
            matchedExplicitly |= within && byModel;
            withinFallback |= within && !byModel;
        }

        var conveysFact = ConveysFact(answer, question);
        var pass = matchedExplicitly || (withinFallback && conveysFact);
        return new QuestionScore(pass, false, labels, explicitCitation, SaysNotFound(answer), matchedExplicitly, conveysFact);
    }

    /// <summary>
    /// Retrieval modes. Answerable: pass when one of the top hits contains the fact's start. Unanswerable: scored only
    /// in keyword mode (pass when nothing matches); hybrid search always returns the nearest vectors, so it is skipped.
    /// </summary>
    public static QuestionScore ScoreRetrieval(EvalQuestion question, IReadOnlyList<SearchHit> top, bool keywordMode)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(top);
        var labels = new List<string>(top.Count);
        foreach (var hit in top)
        {
            labels.Add(FixtureGenerator.Label(hit.Chunk.Start) + "–" + FixtureGenerator.Label(hit.Chunk.End));
        }

        if (!question.Answerable)
        {
            return keywordMode
                ? new QuestionScore(top.Count == 0, false, labels, false, null)
                : new QuestionScore(false, true, labels, false, null);
        }

        var expected = question.ExpectedAt ?? throw new ArgumentException($"Question {question.Id} has no expected time.", nameof(question));
        var pass = false;
        foreach (var hit in top)
        {
            pass |= expected >= hit.Chunk.Start - RetrievalSlack && expected <= hit.Chunk.End + RetrievalSlack;
        }

        return new QuestionScore(pass, false, labels, false, null);
    }

    private static string Normalize(string text) => text.Replace('’', '\'').Replace('ʼ', '\'').ToLowerInvariant();

    public static double Rate(int passed, int total) => total == 0 ? 0 : (double)passed / total;

    /// <summary>The run succeeds when at least one question was scored and the pass rate reaches the threshold.</summary>
    public static bool Succeeded(int passed, int total, double threshold) => total > 0 && Rate(passed, total) >= threshold;
}
