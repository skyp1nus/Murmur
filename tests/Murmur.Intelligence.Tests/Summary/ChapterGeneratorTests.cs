using Microsoft.Extensions.AI;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;
using Murmur.Intelligence.Summary;
using Murmur.Intelligence.Tests.Fakes;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Murmur.Intelligence.Tests.Summary;

public sealed class ChapterGeneratorTests
{
    private static readonly SessionId Id = SessionId.Parse("0198a3b2c4d57e6f8091a2b3c4d5e6f7");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TranscriptSegment Segment(double start, double end, string text, Speaker speaker = Speaker.Me) =>
        new(speaker, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text, []);

    /// <summary>A talk of <paramref name="minutes"/> minutes: one segment a minute, the first at 0:07 (so "the first chapter starts at 0" is not just the first segment).</summary>
    private static Transcript Talk(int minutes, string? language = "en", double firstStart = 7)
    {
        var segments = new List<TranscriptSegment>();
        for (var minute = 0; minute < minutes; minute++)
        {
            var start = minute == 0 ? firstStart : minute * 60;
            segments.Add(Segment(start, minute * 60 + 50, "Line number " + minute + " of the talk.", minute % 2 == 0 ? Speaker.Me : Speaker.Others));
        }

        return new Transcript(Id, segments, "test", language);
    }

    private static FakeStorage StorageWith(Transcript transcript)
    {
        var storage = new FakeStorage();
        storage.Transcripts[transcript.Session] = transcript;
        return storage;
    }

    private static ChapterGenerator Create(ScriptedLlm llm, FakeStorage storage, SummarizerOptions? options = null) => new(llm, storage, options);

    private const string Answer = "- [00:07] Intro\n- [04:10] Roadmap and search\n- [12:00] PDF export\n- [17:30] Next steps\n";

    [Fact]
    public void TheMinimumDuration_IsTenMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), Create(new ScriptedLlm(_ => Answer), new FakeStorage()).MinimumDuration);
    }

    [Fact]
    public async Task ALongSession_BecomesChapters_FirstAtZero_TimesSnappedToSegmentStarts()
    {
        var llm = new ScriptedLlm(_ => Answer);
        var generator = Create(llm, StorageWith(Talk(20)));

        var chapters = await generator.GenerateChaptersAsync(Id, null, Ct);

        Assert.Equal(
            [
                new Chapter("Intro", TimeSpan.Zero),
                new Chapter("Roadmap and search", TimeSpan.FromSeconds(240)),
                new Chapter("PDF export", TimeSpan.FromSeconds(720)),
                new Chapter("Next steps", TimeSpan.FromSeconds(1020)),
            ],
            chapters);
        Assert.Single(llm.Requests);
    }

    [Fact]
    public async Task ATimeInsideASegment_SnapsToTheStartOfThatSegment()
    {
        // 04:40 is 40 s into the segment that starts at 04:00 (04:00-04:50); 11:20 is in the one at 11:00.
        var llm = new ScriptedLlm(_ => "- [00:00] Intro\n- [04:40] Middle\n- [11:20] End\n");

        var chapters = await Create(llm, StorageWith(Talk(20))).GenerateChaptersAsync(Id, null, Ct);

        Assert.Equal([TimeSpan.Zero, TimeSpan.FromSeconds(240), TimeSpan.FromSeconds(660)], chapters.Select(c => c.Start));
    }

    [Fact]
    public async Task AShortSession_GetsNoChapters_AndTheModelIsNotAsked()
    {
        var llm = new ScriptedLlm(_ => Answer);
        var progress = new RecordingProgress();

        var chapters = await Create(llm, StorageWith(Talk(9))).GenerateChaptersAsync(Id, progress, Ct);

        Assert.Empty(chapters);
        Assert.Empty(llm.Requests);
        Assert.Equal(0, llm.ClientRequests);
        Assert.Equal(1.0, progress.Values[^1]);
    }

    [Fact]
    public async Task ASessionExactlyAsLongAsTheMinimum_GetsChapters()
    {
        // The last segment of Talk(11) ends at 10:50, and a custom minimum of 10:50 counts it.
        var options = new SummarizerOptions { ChapterMinimumDuration = TimeSpan.FromSeconds(650) };

        var chapters = await Create(new ScriptedLlm(_ => "- [0:00] A\n- [5:00] B\n"), StorageWith(Talk(11)), options).GenerateChaptersAsync(Id, null, Ct);

        Assert.Equal(2, chapters.Count);
    }

    [Fact]
    public async Task NoTranscript_ThrowsInvalidOperation()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Create(new ScriptedLlm(_ => Answer), new FakeStorage()).GenerateChaptersAsync(Id, null, Ct));
    }

    [Fact]
    public async Task ATranscriptWithoutSpeech_ThrowsInvalidOperation()
    {
        var empty = new Transcript(Id, [Segment(0, 700, "   ")], "test", "en");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Create(new ScriptedLlm(_ => Answer), StorageWith(empty)).GenerateChaptersAsync(Id, null, Ct));
    }

    [Fact]
    public async Task ThePrompt_CarriesTheTimestampedTranscript_TheLanguageAndTheLimits()
    {
        var llm = new ScriptedLlm(_ => Answer);

        await Create(llm, StorageWith(Talk(20, "uk"))).GenerateChaptersAsync(Id, null, Ct);

        var request = Assert.Single(llm.Requests);
        Assert.Contains("Ukrainian", request.System);
        Assert.Contains("[00:07] Me: Line number 0 of the talk.", request.User);
        Assert.Contains("[01:00] Others: Line number 1 of the talk.", request.User);
        Assert.Contains("20 minutes", request.User);
        Assert.Contains("at most 12", request.User);
        Assert.Contains("48 characters", request.System);
    }

    [Fact]
    public async Task MarkdownNumberingBracketsAndPlaceholders_AreTolerated()
    {
        var llm = new ScriptedLlm(_ =>
            "Here are the chapters:\n"
            + "1. **[00:00]** \"Welcome and goals.\"\n"
            + "- 04:10 - Roadmap & search\n"
            + "* [12:00–14:00]: **PDF export**\n"
            + "- [mm:ss] <title>\n"
            + "- no time here\n");

        var chapters = await Create(llm, StorageWith(Talk(20))).GenerateChaptersAsync(Id, null, Ct);

        Assert.Equal(["Welcome and goals", "Roadmap & search", "PDF export"], chapters.Select(c => c.Title));
        Assert.Equal([0, 240, 720], chapters.Select(c => (int)c.Start.TotalSeconds));
    }

    [Fact]
    public async Task Titles_AreCutToFortyEightCharacters_AtAWordBoundary_WithoutATrailingPeriod()
    {
        var llm = new ScriptedLlm(_ =>
            "- [00:00] A very long chapter title that keeps going and going well past the limit of the strip\n"
            + "- [05:00] Short title.\n");

        var chapters = await Create(llm, StorageWith(Talk(20))).GenerateChaptersAsync(Id, null, Ct);

        Assert.All(chapters, c => Assert.True(c.Title.Length <= 48));
        Assert.Equal("A very long chapter title that keeps going and", chapters[0].Title);
        Assert.Equal("Short title", chapters[1].Title);
    }

    [Fact]
    public async Task UnsortedChapters_AreOrdered_AndTheFirstOneIsPutAtZero()
    {
        var llm = new ScriptedLlm(_ => "- [12:00] Last\n- [03:00] First\n- [07:00] Middle\n");

        var chapters = await Create(llm, StorageWith(Talk(20))).GenerateChaptersAsync(Id, null, Ct);

        Assert.Equal(["First", "Middle", "Last"], chapters.Select(c => c.Title));
        Assert.Equal(TimeSpan.Zero, chapters[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(420), chapters[1].Start);
    }

    [Fact]
    public async Task ChaptersOnTheSameSegment_OrTooCloseTogether_AreDropped_AndTimesInNoSegmentToo()
    {
        var llm = new ScriptedLlm(_ =>
            "- [00:00] One\n"
            + "- [00:05] Two, too close to the first\n"
            + "- [05:00] Three\n"
            + "- [05:20] Four, same segment as three\n"
            + "- [59:00] Five, after the end of the talk\n"
            + "- [09:00] Six\n");

        var chapters = await Create(llm, StorageWith(Talk(20))).GenerateChaptersAsync(Id, null, Ct);

        Assert.Equal(["One", "Three", "Six"], chapters.Select(c => c.Title));
    }

    [Fact]
    public async Task MoreThanTwelveChapters_AreThinnedOut_KeepingTheFirstAndTheLongOnes()
    {
        var talk = Talk(40);
        // 14 chapters: one a minute for the first twelve minutes... then three long ones.
        var lines = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            lines.Add($"- [{i:00}:00] Short {i}");
        }

        lines.Add("- [12:00] Long A");
        lines.Add("- [26:00] Long B");
        var llm = new ScriptedLlm(_ => string.Join('\n', lines));

        var chapters = await Create(llm, StorageWith(talk), new SummarizerOptions { MinChapterLength = TimeSpan.FromSeconds(30) }).GenerateChaptersAsync(Id, null, Ct);

        Assert.Equal(12, chapters.Count);
        Assert.Equal(TimeSpan.Zero, chapters[0].Start);
        Assert.Contains(chapters, c => c.Title == "Long A");
        Assert.Contains(chapters, c => c.Title == "Long B");
        Assert.Equal(chapters.OrderBy(c => c.Start), chapters);
    }

    [Fact]
    public async Task FewerThanTwoUsableChapters_IsAnUnusableAnswer()
    {
        var llm = new ScriptedLlm(_ => "- [00:00] The only one\n- [59:00] Beyond the end\nnothing else");

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await Create(llm, StorageWith(Talk(20))).GenerateChaptersAsync(Id, null, Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("I cannot split this transcript.")]
    [InlineData("- [mm:ss] <title>")]
    public async Task AnswerWithoutChapters_ThrowsInvalidData(string answer)
    {
        var llm = new ScriptedLlm(_ => answer);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await Create(llm, StorageWith(Talk(20))).GenerateChaptersAsync(Id, null, Ct));
    }

    [Fact]
    public async Task ALongTranscript_IsSplitInChunks_ThenMerged()
    {
        var llm = new ScriptedLlm(request => request.User.StartsWith("These are chapter candidates", StringComparison.Ordinal)
            ? "- [00:07] Beginning\n- [10:00] Middle\n- [16:00] End\n"
            : "- [" + Math.Floor(FirstMinute(request.User)) + ":00] Candidate\n");
        var options = new SummarizerOptions { ChunkCharacters = 400, ReduceCharacters = 100_000 };
        var progress = new RecordingProgress();

        var chapters = await Create(llm, StorageWith(Talk(20)), options).GenerateChaptersAsync(Id, progress, Ct);

        Assert.Equal(["Beginning", "Middle", "End"], chapters.Select(c => c.Title));
        Assert.True(llm.Requests.Count > 2);
        Assert.StartsWith("This is part 1 of", llm.Requests[0].User, StringComparison.Ordinal);
        Assert.StartsWith("These are chapter candidates", llm.Requests[^1].User, StringComparison.Ordinal);
        Assert.Contains("Candidate", llm.Requests[^1].User);
        Assert.Equal(progress.Values.Order().ToList(), progress.Values);
        Assert.Equal(1.0, progress.Values[^1]);
    }

    [Fact]
    public async Task TooManyCandidatesForOneRequest_AreCondensedInGroupsFirst()
    {
        var llm = new ScriptedLlm(request =>
            request.User.StartsWith("These are chapter candidates from consecutive parts of one meeting. Condense", StringComparison.Ordinal) ? "- [00:07] Condensed\n"
            : request.User.StartsWith("These are chapter candidates", StringComparison.Ordinal) ? "- [00:07] A\n- [10:00] B\n- [16:00] C\n"
            : "- [" + Math.Floor(FirstMinute(request.User)) + ":00] A rather long candidate title to fill the budget\n");
        var options = new SummarizerOptions { ChunkCharacters = 300, ReduceCharacters = 150 };

        var chapters = await Create(llm, StorageWith(Talk(20)), options).GenerateChaptersAsync(Id, null, Ct);

        Assert.Equal(3, chapters.Count);
        Assert.Contains(llm.Requests, r => r.User.Contains("Condense them", StringComparison.Ordinal));
        Assert.All(llm.Requests.Where(r => r.User.StartsWith("These are chapter candidates", StringComparison.Ordinal)), r => Assert.True(r.User.Length < 2000));
    }

    [Fact]
    public async Task Cancelling_StopsBetweenRequests_AndThrows()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var llm = new ScriptedLlm(_ =>
        {
            cts.Cancel();
            return "- [00:00] X\n";
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Create(llm, StorageWith(Talk(20)), new SummarizerOptions { ChunkCharacters = 400 }).GenerateChaptersAsync(Id, null, cts.Token));
        Assert.Single(llm.Requests);
    }

    [Fact]
    public async Task ACancelledToken_StartsNothing()
    {
        var llm = new ScriptedLlm(_ => Answer);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Create(llm, StorageWith(Talk(20))).GenerateChaptersAsync(Id, null, cts.Token));
        Assert.Empty(llm.Requests);
    }

    [Fact]
    public async Task APausedModel_ThrowsAiPaused()
    {
        var llm = new ScriptedLlm(_ => Answer) { Paused = true };

        await Assert.ThrowsAsync<AiPausedException>(async () =>
            await Create(llm, StorageWith(Talk(20))).GenerateChaptersAsync(Id, null, Ct));
    }

    [Fact]
    public async Task TheRequest_IsDeterministic_WithoutHiddenReasoning_AndWithinTheBudget()
    {
        var llm = new ScriptedLlm(_ => Answer);

        await Create(llm, StorageWith(Talk(20))).GenerateChaptersAsync(Id, null, Ct);

        var options = Assert.Single(llm.Requests).Options;
        Assert.NotNull(options);
        Assert.Equal(new SummarizerOptions().Temperature, options.Temperature);
        Assert.Equal(new SummarizerOptions().ChapterMaxOutputTokens, options.MaxOutputTokens);
        Assert.Equal(ReasoningEffort.None, options.Reasoning?.Effort);
    }

    /// <summary>The minute of the first "[mm:ss]" line of a request's transcript part.</summary>
    private static double FirstMinute(string user)
    {
        var open = user.IndexOf("\n[", StringComparison.Ordinal);
        return int.Parse(user.AsSpan(open + 2, 2), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    private sealed record Request(string System, string User, ChatOptions? Options);

    private sealed class ScriptedLlm(Func<Request, string> respond) : ILlm
    {
        public List<Request> Requests { get; } = [];

        private string Respond(Request request) => respond(request);

        public bool Paused { get; init; }

        public int ClientRequests { get; private set; }

        public LlmStatus Status => LlmStatus.Ready;

        public ValueTask<IChatClient> GetChatClientAsync(CancellationToken cancellationToken)
        {
            ClientRequests++;
            return Paused ? throw new AiPausedException() : ValueTask.FromResult<IChatClient>(new Client(this));
        }

        public ValueTask<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingGeneratorAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask UnloadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Client(ScriptedLlm owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var list = messages.ToList();
                var request = new Request(list.First(m => m.Role == ChatRole.System).Text, list.First(m => m.Role == ChatRole.User).Text, options);
                owner.Requests.Add(request);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, owner.Respond(request))));
            }

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }
}
