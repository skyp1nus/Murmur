using System.Text.Json;
using Murmur.Core.Transcription;

namespace Murmur.AiEval.Tests;

public sealed class FixtureGeneratorTests
{
    private static readonly FixtureTranscript Fixture = FixtureGenerator.GenerateTranscript();
    private static readonly QuestionSet Questions = FixtureGenerator.GenerateQuestions();

    [Fact]
    public void Generation_IsDeterministic()
    {
        var first = FixtureGenerator.SerializeTranscript(FixtureGenerator.GenerateTranscript());
        var second = FixtureGenerator.SerializeTranscript(FixtureGenerator.GenerateTranscript());
        Assert.Equal(first, second);
        Assert.Equal(FixtureGenerator.SerializeQuestions(FixtureGenerator.GenerateQuestions()), FixtureGenerator.SerializeQuestions(Questions));
    }

    [Fact]
    public void AnotherSeed_GivesAnotherTranscript_WithTheSameFacts()
    {
        var other = FixtureGenerator.GenerateTranscript(seed: 7);
        Assert.NotEqual(FixtureGenerator.SerializeTranscript(Fixture), FixtureGenerator.SerializeTranscript(other));
        foreach (var fact in FixtureContent.Facts)
        {
            Assert.Contains(other.Segments, s => s.StartMs == (long)fact.At.TotalMilliseconds && s.Text == fact.Text);
        }
    }

    [Fact]
    public void CommittedFiles_MatchTheGenerator()
    {
        // Regenerate with: dotnet run --project tools/Murmur.AiEval/src -- --generate-fixture tests/fixtures
        Assert.Equal(FixtureGenerator.SerializeTranscript(Fixture), Normalize(File.ReadAllText(RepoPaths.FixtureFile)));
        Assert.Equal(FixtureGenerator.SerializeQuestions(Questions), Normalize(File.ReadAllText(RepoPaths.QuestionsFile)));
    }

    [Fact]
    public void SerializedTranscript_RoundTrips()
    {
        var json = FixtureGenerator.SerializeTranscript(Fixture);
        var parsed = JsonSerializer.Deserialize(json, EvalJsonContext.Default.FixtureTranscript)!;
        Assert.Equal(Fixture.Segments.Count, parsed.Segments.Count);
        Assert.Equal(Fixture.DurationMs, parsed.DurationMs);
        Assert.Equal(Fixture.Segments[100].Text, parsed.Segments[100].Text);
        Assert.Equal(Fixture.Segments[100].Words, parsed.Segments[100].Words);
        Assert.Contains("Бюджет на Q3", json, StringComparison.Ordinal); // readable Cyrillic, no \uXXXX
        Assert.DoesNotContain("\\u", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Transcript_IsAnHourOfShortSegments_FromBothSpeakers()
    {
        var segments = Fixture.Segments;
        Assert.True(segments.Count >= 600, $"{segments.Count} segments");
        Assert.Equal(3_600_000, Fixture.DurationMs);
        Assert.InRange(segments[^1].EndMs, 3_590_000, 3_600_000);
        Assert.Contains(segments, s => s.Speaker == Speaker.Me);
        Assert.Contains(segments, s => s.Speaker == Speaker.Others);

        long previousEnd = -1;
        foreach (var segment in segments)
        {
            Assert.InRange(segment.EndMs - segment.StartMs, FixtureGenerator.MinSegmentMs, FixtureGenerator.MaxSegmentMs);
            Assert.True(segment.StartMs > previousEnd, $"segment at {segment.StartMs} overlaps the previous one");
            Assert.DoesNotContain('{', segment.Text);
            previousEnd = segment.EndMs;
        }
    }

    [Fact]
    public void Words_AreEvenlySpreadInsideTheirSegment()
    {
        foreach (var segment in Fixture.Segments)
        {
            var tokens = segment.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(tokens, segment.Words.Select(w => w.Text));
            var slot = (double)(segment.EndMs - segment.StartMs) / segment.Words.Count;
            for (var i = 0; i < segment.Words.Count; i++)
            {
                var word = segment.Words[i];
                Assert.InRange(word.StartMs, segment.StartMs, segment.EndMs);
                Assert.InRange(word.EndMs, word.StartMs + 1, segment.EndMs);
                Assert.InRange(word.StartMs - segment.StartMs, (long)Math.Floor(i * slot) - 1, (long)Math.Ceiling(i * slot) + 1);
            }
        }
    }

    [Fact]
    public void Facts_ArePlantedAtTheirExactTimes_SpreadOverTheHour()
    {
        var facts = FixtureContent.Facts;
        Assert.True(facts.Count >= 12);
        Assert.Equal(facts.Count, facts.Select(f => f.At).Distinct().Count());
        var times = facts.Select(f => f.At).Order().ToList();
        Assert.True(times[0] < TimeSpan.FromMinutes(10));
        Assert.True(times[^1] > TimeSpan.FromMinutes(50));
        for (var i = 1; i < times.Count; i++)
        {
            Assert.True(times[i] - times[i - 1] >= TimeSpan.FromMinutes(3), $"facts at {times[i - 1]} and {times[i]} are too close");
        }

        foreach (var fact in facts)
        {
            var segment = Assert.Single(Fixture.Segments, s => s.StartMs == (long)fact.At.TotalMilliseconds);
            Assert.Equal(fact.Text, segment.Text);
            Assert.Equal(fact.Speaker, segment.Speaker);
        }
    }

    [Fact]
    public void FactStems_OccurOnlyInTheirOwnSegment()
    {
        foreach (var fact in FixtureContent.Facts)
        {
            foreach (var stem in fact.Stems)
            {
                var matches = Fixture.Segments.Where(s => HasWordStartingWith(s.Text, stem)).ToList();
                Assert.True(matches.Count == 1, $"stem «{stem}» of {fact.Id} occurs in {matches.Count} segments");
                var only = matches[0];
                Assert.Equal(fact.Text, only.Text);
            }
        }
    }

    [Fact]
    public void UnanswerableStems_OccurNowhere()
    {
        foreach (var question in FixtureContent.Unanswerable)
        {
            foreach (var stem in question.Stems)
            {
                Assert.DoesNotContain(Fixture.Segments, s => HasWordStartingWith(s.Text, stem));
            }

            Assert.Contains(question.Stems, stem => HasWordStartingWith(question.Keywords, stem));
        }
    }

    [Fact]
    public void Questions_AreTwelve_TwoUnanswerable_ParaphrasedWithKnownTimes()
    {
        var questions = Questions.Questions;
        Assert.Equal(12, questions.Count);
        Assert.Equal(2, questions.Count(q => !q.Answerable));
        Assert.Equal(questions.Count, questions.Select(q => q.Id).Distinct().Count());
        Assert.Equal(FixtureGenerator.FixtureFileName, Questions.Fixture);

        foreach (var question in questions)
        {
            Assert.Equal(60, question.ToleranceSeconds);
            if (!question.Answerable)
            {
                Assert.Null(question.ExpectedMs);
                continue;
            }

            var fact = Assert.Single(FixtureContent.Facts, f => f.Text == question.Fact);
            Assert.Equal((long)fact.At.TotalMilliseconds, question.ExpectedMs);
            Assert.Equal(FixtureGenerator.Label(fact.At), question.Expected);

            // Paraphrased: the question shares none of the fact's distinctive stems.
            Assert.DoesNotContain(fact.Stems, stem => HasWordStartingWith(question.Question, stem));

            // The search-box keywords are words of the fact sentence.
            foreach (var keyword in question.Keywords.Split(' '))
            {
                Assert.Contains(keyword, fact.Text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void AnswerableQuestions_CarryTheFactStems_NoneOfWhichEchoesTheQuestion()
    {
        foreach (var question in Questions.Questions)
        {
            if (!question.Answerable)
            {
                Assert.Empty(question.FactStems);
                continue;
            }

            var fact = Assert.Single(FixtureContent.Facts, f => f.Text == question.Fact);
            Assert.Equal(fact.AllAnswerStems, question.FactStems);
            Assert.NotEmpty(question.FactStems);

            // An answer that only repeats the question must not count as conveying the fact.
            foreach (var stem in question.FactStems)
            {
                Assert.Equal(stem.ToLowerInvariant(), stem);
                Assert.DoesNotContain(stem, question.Question, StringComparison.OrdinalIgnoreCase);
            }

            Assert.True(Scoring.ContainsAnyFactStem(fact.Text, question));
            Assert.False(Scoring.ContainsAnyFactStem(question.Question, question));
        }
    }

    [Fact]
    public void WriteFiles_WritesBothFiles()
    {
        var dir = Directory.CreateTempSubdirectory("aieval-gen-").FullName;
        try
        {
            var (fixture, questions) = FixtureGenerator.WriteFiles(dir);
            Assert.Equal(FixtureGenerator.SerializeTranscript(Fixture), File.ReadAllText(fixture));
            Assert.Equal(FixtureGenerator.SerializeQuestions(Questions), File.ReadAllText(questions));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(462, "07:42")]
    [InlineData(3599, "59:59")]
    [InlineData(3723, "1:02:03")]
    public void Label_FormatsLikeCitations(int seconds, string expected) =>
        Assert.Equal(expected, FixtureGenerator.Label(TimeSpan.FromSeconds(seconds)));

    private static string Normalize(string text) => text.ReplaceLineEndings("\n");

    /// <summary>Whether a word of <paramref name="text"/> (letters, digits, apostrophes) starts with <paramref name="stem"/>, ignoring case.</summary>
    private static bool HasWordStartingWith(string text, string stem)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inWord = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '\'');
            if (inWord && start < 0)
            {
                start = i;
            }
            else if (!inWord && start >= 0)
            {
                if (text.AsSpan(start, i - start).StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                start = -1;
            }
        }

        return false;
    }

    [Theory]
    [InlineData("Не будемо чекати.", "демо", false)]
    [InlineData("Нагадаю, що демо для клієнта", "демо", true)]
    [InlineData("через TLS-сертифікат", "tls", true)]
    [InlineData("минулої п'ятниці", "п'ятниц", true)]
    public void HasWordStartingWith_MatchesWordPrefixesOnly(string text, string stem, bool expected) =>
        Assert.Equal(expected, HasWordStartingWith(text, stem));
}
