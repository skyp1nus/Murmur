using System.Globalization;
using System.Text.Json;
using Murmur.Intelligence;

namespace Murmur.AiEval.Tests;

public sealed class EvalRunnerTests
{
    private static readonly ModelFiles NoModels = new(null, null, null);

    private static EvalOptions Options(bool retrievalOnly, string? dataDir = null) => new()
    {
        FixturePath = RepoPaths.FixtureFile,
        QuestionsPath = RepoPaths.QuestionsFile,
        OutPath = "unused.json",
        DataDir = dataDir,
        RetrievalOnly = retrievalOnly,
    };

    private static IReadOnlyList<EvalQuestion> CommittedQuestions() =>
        JsonSerializer.Deserialize(File.ReadAllText(RepoPaths.QuestionsFile), EvalJsonContext.Default.QuestionSet)!.Questions;

    [Fact]
    public async Task RetrievalOnly_KeywordMode_FindsEveryPlantedFact_WithTheRealLocalLlmNotInstalled()
    {
        // The same path as the CLI on a machine without models: LocalLlm reports NotInstalled, search is BM25 only.
        var missing = Path.Combine(Path.GetTempPath(), "murmur-aieval-no-models-" + Guid.NewGuid().ToString("N"));
        var llmOptions = new LocalLlmOptions
        {
            ChatModelPath = Path.Combine(missing, "chat.gguf"),
            EmbeddingModelPath = Path.Combine(missing, "embed.gguf"),
            LlamaServerPath = Path.Combine(missing, "llama-server"),
        };
        await using var llm = new LocalLlm(llmOptions, static () => false);

        var report = await EvalRunner.RunAsync(
            Options(retrievalOnly: true), llm, LlmAvailability.From(llmOptions), LlmAvailability.Describe(llmOptions), TextWriter.Null,
            TestContext.Current.CancellationToken);

        Assert.Equal(EvalMode.RetrievalKeyword, report.Mode);
        Assert.Equal(12, report.Total);
        Assert.Equal(12, report.Passed);
        Assert.Equal(1.0, report.Rate);
        Assert.True(report.Success);
        Assert.Equal(0, report.Timings.IndexMs);
        Assert.True(report.Segments >= 600);
        Assert.All(report.PerQuestion.Where(q => q.Answerable), q => Assert.Single(q.Cited));
        Assert.All(report.PerQuestion.Where(q => !q.Answerable), q => Assert.Equal("(no hits)", q.Answer));
        Assert.Null(report.Models.Chat!.Bytes);
    }

    [Fact]
    public async Task RetrievalOnly_HybridMode_IndexesAndSkipsUnanswerable()
    {
        var questions = CommittedQuestions();
        var llm = new FakeLlm(new ExcerptCitingChatClient(new HashSet<string>()), new ConceptEmbeddingGenerator(questions));

        var report = await EvalRunner.RunAsync(
            Options(retrievalOnly: true), llm, new LlmAvailability(Chat: false, Embedding: true), NoModels, TextWriter.Null,
            TestContext.Current.CancellationToken);

        Assert.Equal(EvalMode.RetrievalHybrid, report.Mode);
        Assert.Equal(10, report.Total);
        Assert.Equal(10, report.Passed);
        Assert.Equal(2, report.PerQuestion.Count(q => q.Skipped));
        Assert.True(report.Timings.Chunks > 0);
        Assert.Equal(0, llm.ChatWarmUps);
        Assert.All(report.PerQuestion, q => Assert.Equal(q.Question, q.Query));
    }

    [Fact]
    public async Task Full_WithAnAssistantThatCitesTheRightExcerpt_PassesEveryQuestion()
    {
        var questions = CommittedQuestions();
        var notFound = questions.Where(q => !q.Answerable).Select(q => q.Question).ToHashSet();
        var chat = new ExcerptCitingChatClient(notFound);
        var llm = new FakeLlm(chat, new ConceptEmbeddingGenerator(questions));
        var dataDir = Directory.CreateTempSubdirectory("aieval-full-").FullName;
        try
        {
            var report = await EvalRunner.RunAsync(
                Options(retrievalOnly: false, dataDir), llm, new LlmAvailability(true, true), NoModels, TextWriter.Null,
                TestContext.Current.CancellationToken);

            Assert.Equal(EvalMode.Full, report.Mode);
            Assert.Equal(12, report.Total);
            Assert.Equal(12, report.Passed);
            Assert.Equal(10, report.ExplicitPassed);
            Assert.True(report.Success);
            Assert.Equal(12, chat.Requests);
            Assert.Equal(1 + 12, llm.ChatWarmUps); // the runner's warm-up, then AskAsync once per question
            Assert.All(report.PerQuestion, q => Assert.NotNull(q.FirstTokenMs));
            Assert.All(report.PerQuestion.Where(q => !q.Answerable), q => Assert.True(q.SaysNotFound));

            // An explicit --data-dir is kept (the database is inspectable afterwards).
            Assert.True(File.Exists(Path.Combine(dataDir, "murmur.db")));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task Full_WithUselessRetrievalAndAnswers_FailsTheThreshold()
    {
        var questions = CommittedQuestions();
        var llm = new FakeLlm(
            new ExcerptCitingChatClient(new HashSet<string>(), fixedAnswer: "Про це говорили на самому початку [00:01]."),
            new ConceptEmbeddingGenerator(questions, constant: true));

        var report = await EvalRunner.RunAsync(
            Options(retrievalOnly: false), llm, new LlmAvailability(true, true), NoModels, TextWriter.Null,
            TestContext.Current.CancellationToken);

        Assert.False(report.Success);
        Assert.True(report.Rate < 0.8);
        Assert.All(report.PerQuestion.Where(q => !q.Answerable), q => Assert.False(q.Pass));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("У записах цього немає.", 2)] // only the two unanswerable questions pass
    public async Task Full_WithGoodRetrievalButEmptyOrNotFoundAnswers_FailsTheThreshold(string fixedAnswer, int expectedPassed)
    {
        // Retrieval puts the fact into the top excerpt, so the assistant's fallback citation is on the right minute;
        // the chat model, however, says nothing useful. The gate must still go red.
        var questions = CommittedQuestions();
        var llm = new FakeLlm(new ExcerptCitingChatClient(new HashSet<string>(), fixedAnswer), new ConceptEmbeddingGenerator(questions));

        var report = await EvalRunner.RunAsync(
            Options(retrievalOnly: false), llm, new LlmAvailability(true, true), NoModels, TextWriter.Null,
            TestContext.Current.CancellationToken);

        Assert.Equal(12, report.Total);
        Assert.Equal(expectedPassed, report.Passed);
        Assert.False(report.Success);
        Assert.Equal(0, report.ExplicitPassed);
        foreach (var q in report.PerQuestion.Where(q => q.Answerable))
        {
            Assert.False(q.Pass);
            Assert.False(q.ConveysFact);
            Assert.False(q.MatchedExplicitly);

            // The fallback citation is within the tolerance: the old rule would have passed this question.
            Assert.Contains(q.Cited, label =>
                Math.Abs(TimeSpan.ParseExact(label, @"mm\:ss", CultureInfo.InvariantCulture).TotalMilliseconds - q.ExpectedMs!.Value)
                <= q.ToleranceSeconds * 1000);
        }
    }

    [Fact]
    public async Task Full_WithoutModels_IsASetupError()
    {
        var llm = new FakeLlm(new ExcerptCitingChatClient(new HashSet<string>()), new ConceptEmbeddingGenerator([]));
        await Assert.ThrowsAsync<EvalSetupException>(async () => await EvalRunner.RunAsync(
            Options(retrievalOnly: false), llm, new LlmAvailability(Chat: true, Embedding: false), NoModels, TextWriter.Null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingFixture_IsASetupError()
    {
        var llm = new FakeLlm(new ExcerptCitingChatClient(new HashSet<string>()), new ConceptEmbeddingGenerator([]));
        var options = Options(retrievalOnly: true) with { FixturePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json") };
        await Assert.ThrowsAsync<EvalSetupException>(async () => await EvalRunner.RunAsync(
            options, llm, new LlmAvailability(false, false), NoModels, TextWriter.Null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Availability_NeedsTheServerAndTheModelFile()
    {
        var dir = Directory.CreateTempSubdirectory("aieval-models-").FullName;
        try
        {
            var server = Path.Combine(dir, "llama-server");
            var embed = Path.Combine(dir, "embed.gguf");
            File.WriteAllText(embed, "x");
            var options = new LocalLlmOptions { LlamaServerPath = server, EmbeddingModelPath = embed, ChatModelPath = Path.Combine(dir, "chat.gguf") };
            Assert.Equal(new LlmAvailability(false, false), LlmAvailability.From(options));

            File.WriteAllText(server, "x");
            Assert.Equal(new LlmAvailability(false, true), LlmAvailability.From(options));

            var files = LlmAvailability.Describe(options);
            Assert.Equal("embed.gguf", files.Embedding!.Name);
            Assert.Equal(1, files.Embedding.Bytes);
            Assert.Null(files.Chat!.Bytes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
