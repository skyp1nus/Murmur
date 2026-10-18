using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;

namespace Murmur.AiEval.Tests;

public sealed class ScoringTests
{
    private static readonly SessionId Session = SessionId.New();

    private static readonly EvalQuestion Answerable = new()
    {
        Id = "q01",
        Question = "Скільки грошей виділили на третій квартал?",
        Answerable = true,
        ExpectedMs = 462_000,
        Expected = "07:42",
        ToleranceSeconds = 60,
        Keywords = "бюджет Q3",
        FactStems = ["бюджет", "q3", "мільйон", "гривень", "1,2", "1.2"],
    };

    private static readonly EvalQuestion Unanswerable = new()
    {
        Id = "q11",
        Question = "Яку зарплату запропонували?",
        Answerable = false,
        Keywords = "зарплату",
    };

    [Theory]
    [InlineData("У записах цього немає.")]
    [InlineData("Про це в записі нічого не згадується.")]
    [InlineData("Я не знайшов такої інформації.")]
    [InlineData("Цю тему на зустрічі не обговорювали.")]
    [InlineData("Інформація про зарплату відсутня.")]
    [InlineData("Таке не прозвучало.")]
    [InlineData("This is not mentioned in the recording.")]
    [InlineData("I couldn't find anything about it.")]
    [InlineData("The excerpts DO NOT CONTAIN that.")]
    [InlineData("The recording doesn’t say.")]
    public void SaysNotFound_RecognizesUkrainianAndEnglish(string answer) => Assert.True(Scoring.SaysNotFound(answer));

    [Theory]
    [InlineData("Бюджет затвердили: 1,2 мільйона гривень [07:42].")]
    [InlineData("The team picked Redis [16:27].")]
    [InlineData("")]
    public void SaysNotFound_IgnoresRealAnswers(string answer) => Assert.False(Scoring.SaysNotFound(answer));

    [Theory]
    [InlineData(462, true)]
    [InlineData(402, true)]
    [InlineData(522, true)]
    [InlineData(401, false)]
    [InlineData(523, false)]
    public void Answerable_PassesWithinTolerance(int citedSeconds, bool pass)
    {
        var cited = TimeSpan.FromSeconds(citedSeconds);
        var answer = $"Бюджет на Q3 — 1,2 мільйона [{FixtureGenerator.Label(cited)}].";
        var score = Scoring.ScoreAnswer(Answerable, answer, [Cite(cited)]);
        Assert.Equal(pass, score.Pass);
        Assert.True(score.ExplicitCitation);
        Assert.Equal(pass, score.MatchedExplicitly);
        Assert.False(score.Skipped);
        Assert.Equal([FixtureGenerator.Label(cited)], score.Cited);
    }

    [Fact]
    public void Answerable_AnyMatchingCitationPasses()
    {
        var score = Scoring.ScoreAnswer(Answerable, "[01:00] і [07:40]", [Cite(TimeSpan.FromMinutes(1)), Cite(new TimeSpan(0, 7, 40))]);
        Assert.True(score.Pass);
        Assert.Equal(["01:00", "07:40"], score.Cited);
    }

    [Fact]
    public void Answerable_FallbackCitationCounts_ButNotAsExplicit()
    {
        // No timestamp in the text: the assistant cited the top excerpt's start.
        var score = Scoring.ScoreAnswer(Answerable, "Бюджет — 1,2 мільйона гривень.", [Cite(new TimeSpan(0, 7, 20))]);
        Assert.True(score.Pass);
        Assert.False(score.ExplicitCitation);
        Assert.False(score.MatchedExplicitly);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("В записах цього немає.")]
    [InlineData("Про бюджет на третій квартал у записі нічого не згадується.")]
    [InlineData("Говорили про Redis і кешування.")]
    public void Answerable_FallbackCitationAlone_FailsWhenTheAnswerDoesNotConveyTheFact(string answer)
    {
        // The fallback (top excerpt) lands on the fact whatever the model wrote: good retrieval must not hide a bad answer.
        var score = Scoring.ScoreAnswer(Answerable, answer, [Cite(new TimeSpan(0, 7, 20))]);
        Assert.False(score.Pass);
        Assert.False(score.MatchedExplicitly);
        Assert.False(score.ConveysFact);
    }

    [Fact]
    public void Answerable_ExplicitMatchingTimestamp_PassesWithoutStems()
    {
        var score = Scoring.ScoreAnswer(Answerable, "Про це сказали тут [07:42].", [Cite(new TimeSpan(0, 7, 42))]);
        Assert.True(score.Pass);
        Assert.True(score.MatchedExplicitly);
        Assert.False(score.ConveysFact);
    }

    [Fact]
    public void Answerable_FactStatedButFallbackOutsideTolerance_Fails()
    {
        var score = Scoring.ScoreAnswer(Answerable, "Бюджет — 1,2 мільйона гривень.", [Cite(TimeSpan.FromMinutes(30))]);
        Assert.False(score.Pass);
        Assert.True(score.ConveysFact);
    }

    [Theory]
    [InlineData("Виділили 1.2 млн.", true)]
    [InlineData("ГРИВЕНЬ було небагато", true)]
    [InlineData("Q3: so", true)]
    [InlineData("Скільки грошей виділили на третій квартал?", false)]
    [InlineData("", false)]
    public void ContainsAnyFactStem_IsACaseInsensitiveSubstringCheck(string answer, bool expected) =>
        Assert.Equal(expected, Scoring.ContainsAnyFactStem(answer, Answerable));

    [Fact]
    public void ContainsAnyFactStem_NormalizesApostrophes()
    {
        var question = Answerable with { FactStems = ["п'ятнадцят"] };
        Assert.True(Scoring.ContainsAnyFactStem("П’ятнадцять відсотків", question));
        Assert.True(Scoring.ContainsAnyFactStem("пʼятнадцять", question));
        Assert.False(Scoring.ContainsAnyFactStem("anything", Answerable with { FactStems = [] }));
    }

    [Fact]
    public void Answerable_WithoutCitations_Fails() =>
        Assert.False(Scoring.ScoreAnswer(Answerable, "Не знаю.", []).Pass);

    [Fact]
    public void Unanswerable_PassesWhenNotFoundAndOnlyTheFallbackIsCited()
    {
        var score = Scoring.ScoreAnswer(Unanswerable, "У записах цього немає.", [Cite(TimeSpan.FromMinutes(12))]);
        Assert.True(score.Pass);
        Assert.True(score.SaysNotFound);
        Assert.False(score.ExplicitCitation);
    }

    [Fact]
    public void Unanswerable_FailsWithAConfidentCitation()
    {
        var score = Scoring.ScoreAnswer(Unanswerable, "Цього немає, хоча про найм говорили [12:00].", [Cite(TimeSpan.FromMinutes(12))]);
        Assert.False(score.Pass);
        Assert.True(score.ExplicitCitation);
    }

    [Fact]
    public void Unanswerable_FailsWhenTheModelMakesSomethingUp()
    {
        var score = Scoring.ScoreAnswer(Unanswerable, "Запропонували 3000 доларів.", [Cite(TimeSpan.FromMinutes(5))]);
        Assert.False(score.Pass);
        Assert.False(score.SaysNotFound);
    }

    [Fact]
    public void Retrieval_PassesWhenAHitContainsTheFact()
    {
        var hits = new[] { Hit(0, 30), Hit(450, 470), Hit(900, 930) };
        var score = Scoring.ScoreRetrieval(Answerable, hits, keywordMode: false);
        Assert.True(score.Pass);
        Assert.Equal(["00:00–00:30", "07:30–07:50", "15:00–15:30"], score.Cited);
    }

    [Fact]
    public void Retrieval_FailsWhenNoHitContainsTheFact()
    {
        // 07:42 is 2 s + 1 ms after the hit ends: just outside the slack.
        var hits = new[] { Hit(0, 30), Hit(TimeSpan.FromMilliseconds(400_000), TimeSpan.FromMilliseconds(459_999)) };
        Assert.False(Scoring.ScoreRetrieval(Answerable, hits, keywordMode: true).Pass);
        Assert.False(Scoring.ScoreRetrieval(Answerable, [], keywordMode: true).Pass);
    }

    [Fact]
    public void Retrieval_Unanswerable_KeywordModeNeedsNoHits_HybridIsSkipped()
    {
        Assert.True(Scoring.ScoreRetrieval(Unanswerable, [], keywordMode: true).Pass);
        Assert.False(Scoring.ScoreRetrieval(Unanswerable, [Hit(0, 10)], keywordMode: true).Pass);
        var hybrid = Scoring.ScoreRetrieval(Unanswerable, [Hit(0, 10)], keywordMode: false);
        Assert.True(hybrid.Skipped);
        Assert.False(hybrid.Pass);
    }

    [Theory]
    [InlineData(10, 12, 0.8, true)]
    [InlineData(8, 10, 0.8, true)]
    [InlineData(9, 12, 0.8, false)]
    [InlineData(0, 0, 0.8, false)]
    [InlineData(0, 3, 0, true)]
    public void Succeeded_ComparesTheRateWithTheThreshold(int passed, int total, double threshold, bool expected) =>
        Assert.Equal(expected, Scoring.Succeeded(passed, total, threshold));

    [Fact]
    public void Rate_OfNothingIsZero() => Assert.Equal(0, Scoring.Rate(0, 0));

    private static Citation Cite(TimeSpan at) => new(Session, at, FixtureGenerator.Label(at), "snippet");

    private static SearchHit Hit(int startSeconds, int endSeconds) =>
        Hit(TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(endSeconds));

    private static SearchHit Hit(TimeSpan start, TimeSpan end) =>
        new(new TextChunk(Session, start, end, "text"), 1, SearchSource.Hybrid);
}
