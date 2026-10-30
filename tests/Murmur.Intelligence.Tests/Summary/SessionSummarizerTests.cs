using Microsoft.Extensions.AI;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;
using Murmur.Intelligence.Summary;
using Murmur.Intelligence.Tests.Fakes;

namespace Murmur.Intelligence.Tests.Summary;

public sealed class SessionSummarizerTests
{
    private static readonly SessionId Id = SessionId.Parse("0198a3b2c4d57e6f8091a2b3c4d5e6f7");
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TranscriptSegment Segment(Speaker speaker, double start, double end, string text) =>
        new(speaker, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text, []);

    private static Transcript Roadmap(string? language = "en") => new(
        Id,
        [
            Segment(Speaker.Me, 712, 727, "Okay, a quick run through the roadmap."),
            Segment(Speaker.Others, 728, 740, "Does search only match exact words?"),
            Segment(Speaker.Me, 741, 759, "Both: full-text plus semantic."),
            Segment(Speaker.Me, 772, 794, "PDF export moves to next sprint."),
            Segment(Speaker.Others, 804, 810, "Deal. I'll send the invite today."),
        ],
        "test",
        language);

    private static SessionSummarizer Create(ScriptedLlm llm, FakeStorage storage, SummarizerOptions? options = null) =>
        new(llm, storage, options, new FixedTime(Now));

    private static FakeStorage StorageWith(Transcript transcript)
    {
        var storage = new FakeStorage();
        storage.Transcripts[transcript.Session] = transcript;
        return storage;
    }

    private const string Answer =
        "OVERVIEW:\nWalked the client through the roadmap. PDF export moved to next sprint.\n"
        + "KEY POINTS:\n- [12:52] PDF export — next sprint\n- [12:09] Search is full-text plus semantic\n"
        + "ACTION ITEMS:\n- [13:24] You — send the demo invite\n";

    [Fact]
    public async Task OneRequest_BecomesTheSummary_WithCitedTimesSnappedToSegments()
    {
        var llm = new ScriptedLlm(_ => Answer) { ModelId = "gemma-4-E4B-it-Q4_K_M" };
        var summarizer = Create(llm, StorageWith(Roadmap()));

        var summary = await summarizer.SummarizeAsync(Id, null, Ct);

        Assert.Equal(Id, summary.Session);
        Assert.Equal("Walked the client through the roadmap. PDF export moved to next sprint.", summary.Overview);
        Assert.Equal("gemma-4-E4B-it-Q4_K_M", summary.ModelId);
        Assert.Equal(Now, summary.CreatedAt);
        Assert.Collection(
            summary.KeyPoints,
            item =>
            {
                Assert.Equal("PDF export — next sprint", item.Text);
                Assert.Equal(TimeSpan.FromSeconds(772), item.At);
            },
            item =>
            {
                // 12:09 is inside the second segment (12:08–12:20): the chip seeks to its start.
                Assert.Equal("Search is full-text plus semantic", item.Text);
                Assert.Equal(TimeSpan.FromSeconds(728), item.At);
            });
        var action = Assert.Single(summary.ActionItems);
        Assert.Equal("You — send the demo invite", action.Text);
        Assert.Equal(TimeSpan.FromSeconds(804), action.At);
    }

    [Fact]
    public async Task ThePrompt_CarriesTheTimestampedTranscript_AndAsksForTheLanguageOfTheTranscript()
    {
        var llm = new ScriptedLlm(_ => Answer);

        await Create(llm, StorageWith(Roadmap("uk"))).SummarizeAsync(Id, null, Ct);

        var request = Assert.Single(llm.Requests);
        Assert.Contains("[11:52] Me: Okay, a quick run through the roadmap.", request.User);
        Assert.Contains("[12:08] Others: Does search only match exact words?", request.User);
        Assert.Contains("Ukrainian", request.System);
        Assert.Equal(0.2f, request.Options?.Temperature);
        Assert.Equal(ReasoningEffort.None, request.Options?.Reasoning?.Effort);
    }

    [Theory]
    [InlineData("en", "English")]
    [InlineData("uk-UA", "Ukrainian")]
    [InlineData(null, "English or Ukrainian")]
    [InlineData("de", "English or Ukrainian")]
    public async Task TheLanguageFollowsTheTranscript(string? code, string expected)
    {
        var llm = new ScriptedLlm(_ => Answer);

        await Create(llm, StorageWith(Roadmap(code))).SummarizeAsync(Id, null, Ct);

        Assert.Contains(expected, llm.Requests[0].System);
    }

    [Fact]
    public async Task ALongTranscript_IsSummarisedPerChunk_ThenMerged()
    {
        var segments = Enumerable.Range(0, 40)
            .Select(i => Segment(i % 2 == 0 ? Speaker.Me : Speaker.Others, i * 30, (i * 30) + 25, $"Sentence number {i} about the roadmap and the plan."))
            .ToList();
        var transcript = new Transcript(Id, segments, "test", "en");
        var options = new SummarizerOptions { ChunkCharacters = 400, ReduceCharacters = 100_000 };
        var llm = new ScriptedLlm(request => request.User.StartsWith("This is part", StringComparison.Ordinal) ? "OVERVIEW:\nPart.\nKEY POINTS:\n- [00:30] A point\n" : Answer);
        var progress = new RecordingProgress();

        var summary = await Create(llm, StorageWith(transcript), options).SummarizeAsync(Id, progress, Ct);

        var notes = llm.Requests.Where(r => r.User.StartsWith("This is part", StringComparison.Ordinal)).ToList();
        Assert.True(notes.Count > 2, "the transcript is cut into several chunks");
        Assert.Equal(notes.Count + 1, llm.Requests.Count);
        Assert.Contains("part 1 of " + notes.Count, notes[0].User);
        Assert.Contains($"Sentence number 0 ", notes[0].User);
        Assert.DoesNotContain("Sentence number 39 ", notes[0].User);
        Assert.Contains("Sentence number 39 ", notes[^1].User);

        // The last request merges the notes of every chunk.
        var merge = llm.Requests[^1];
        Assert.Contains("consecutive parts of one meeting", merge.User);
        Assert.Equal(notes.Count, merge.User.Split("A point").Length - 1);
        Assert.Equal("PDF export — next sprint", summary.KeyPoints[0].Text);
        Assert.Equal(1.0, progress.Values[^1]);
        Assert.Equal(progress.Values.Order(), progress.Values);
        Assert.True(progress.Values.Count >= notes.Count + 1);
    }

    [Fact]
    public async Task NotesThatAreTooLongForOneRequest_AreCondensedInGroupsFirst()
    {
        var segments = Enumerable.Range(0, 60)
            .Select(i => Segment(Speaker.Me, i * 30, (i * 30) + 25, $"Sentence number {i} about the roadmap and the plan."))
            .ToList();
        var options = new SummarizerOptions { ChunkCharacters = 300, ReduceCharacters = 700 };
        var bulky = "OVERVIEW:\n" + new string('x', 150) + "\nKEY POINTS:\n- [00:30] A point\n";
        var llm = new ScriptedLlm(request =>
            request.User.StartsWith("This is part", StringComparison.Ordinal) ? bulky
            : request.User.StartsWith("These are notes on consecutive parts of one meeting. Condense", StringComparison.Ordinal) ? "OVERVIEW:\nShort.\nKEY POINTS:\n- [00:30] A point\n"
            : Answer);
        var progress = new RecordingProgress();

        await Create(llm, StorageWith(new Transcript(Id, segments, "test", "en")), options).SummarizeAsync(Id, progress, Ct);

        var condense = llm.Requests.Count(r => r.User.Contains("Condense them", StringComparison.Ordinal));
        Assert.True(condense > 0, "the notes were condensed before the merge");
        Assert.Contains("consecutive parts of one meeting, in order", llm.Requests[^1].User);
        Assert.Equal(1.0, progress.Values[^1]);
        Assert.Equal(progress.Values.Order(), progress.Values);
    }

    [Fact]
    public async Task ATimeThatIsInNoSegment_IsDropped_NotInvented()
    {
        var llm = new ScriptedLlm(_ => "OVERVIEW:\nShort.\nKEY POINTS:\n- [59:59] Something at a moment that is not in the recording\n- [12:10] Inside\n");

        var summary = await Create(llm, StorageWith(Roadmap())).SummarizeAsync(Id, null, Ct);

        Assert.Null(summary.KeyPoints[0].At);
        Assert.Equal("Something at a moment that is not in the recording", summary.KeyPoints[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(728), summary.KeyPoints[1].At);
    }

    [Fact]
    public async Task ATimeNearASegment_SnapsToItWithinTheTolerance()
    {
        var llm = new ScriptedLlm(_ => "OVERVIEW:\nShort.\nKEY POINTS:\n- [13:20] After the last segment\n- [13:40] Too far after it\n");

        var summary = await Create(llm, StorageWith(Roadmap())).SummarizeAsync(Id, null, Ct);

        // The last segment is 13:24-13:30: 13:20 is four seconds before it, 13:40 is ten seconds after it, both within the 15 s tolerance.
        Assert.Equal(TimeSpan.FromSeconds(804), summary.KeyPoints[0].At);
        Assert.Equal(TimeSpan.FromSeconds(804), summary.KeyPoints[1].At);

        var far = new ScriptedLlm(_ => "OVERVIEW:\nShort.\nKEY POINTS:\n- [14:00] Too far\n");
        var farSummary = await Create(far, StorageWith(Roadmap())).SummarizeAsync(Id, null, Ct);
        Assert.Null(farSummary.KeyPoints[0].At);
    }

    [Fact]
    public async Task TheLimitsOfTheOptions_CapTheLists()
    {
        var points = string.Join('\n', Enumerable.Range(0, 20).Select(i => $"- [12:52] Point {i}"));
        var llm = new ScriptedLlm(_ => $"OVERVIEW:\nShort.\nKEY POINTS:\n{points}\nACTION ITEMS:\n{points}");

        var summary = await Create(llm, StorageWith(Roadmap()), new SummarizerOptions { MaxKeyPoints = 3, MaxActionItems = 2 }).SummarizeAsync(Id, null, Ct);

        Assert.Equal(3, summary.KeyPoints.Count);
        Assert.Equal(2, summary.ActionItems.Count);
        Assert.Equal("Point 0", summary.KeyPoints[0].Text);
    }

    [Fact]
    public async Task Cancelling_StopsBetweenRequests_AndThrows()
    {
        var segments = Enumerable.Range(0, 40)
            .Select(i => Segment(Speaker.Me, i * 30, (i * 30) + 25, $"Sentence number {i} about the roadmap and the plan."))
            .ToList();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        ScriptedLlm? llm = null;
        llm = new ScriptedLlm(_ =>
        {
            if (llm!.Requests.Count == 2)
            {
                cts.Cancel();
            }

            return "OVERVIEW:\nPart.\nKEY POINTS:\n- [00:30] A point\n";
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Create(llm, StorageWith(new Transcript(Id, segments, "test", "en")), new SummarizerOptions { ChunkCharacters = 400 }).SummarizeAsync(Id, null, cts.Token));

        Assert.Equal(2, llm.Requests.Count);
    }

    [Fact]
    public async Task ACancelledToken_StartsNothing()
    {
        var llm = new ScriptedLlm(_ => Answer);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Create(llm, StorageWith(Roadmap())).SummarizeAsync(Id, null, cts.Token));

        Assert.Empty(llm.Requests);
        Assert.Equal(0, llm.ClientRequests);
    }

    [Fact]
    public async Task ASessionWithoutATranscript_ThrowsBeforeTheModelIsLoaded()
    {
        var llm = new ScriptedLlm(_ => Answer);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Create(llm, new FakeStorage()).SummarizeAsync(Id, null, Ct));

        Assert.Equal(0, llm.ClientRequests);
    }

    [Fact]
    public async Task ATranscriptWithoutSpeech_Throws()
    {
        var empty = new Transcript(Id, [Segment(Speaker.Me, 1, 2, "   ")], "test", "en");

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Create(new ScriptedLlm(_ => Answer), StorageWith(empty)).SummarizeAsync(Id, null, Ct));
    }

    [Fact]
    public async Task AnAnswerWithoutAnySection_IsTheOverview()
    {
        var llm = new ScriptedLlm(_ => "The team agreed to move PDF export to the next sprint.");

        var summary = await Create(llm, StorageWith(Roadmap())).SummarizeAsync(Id, null, Ct);

        Assert.Equal("The team agreed to move PDF export to the next sprint.", summary.Overview);
        Assert.Empty(summary.KeyPoints);
    }

    [Fact]
    public async Task AnEmptyAnswer_ThrowsInvalidData()
    {
        var llm = new ScriptedLlm(_ => "   \n ");

        await Assert.ThrowsAsync<InvalidDataException>(async () => await Create(llm, StorageWith(Roadmap())).SummarizeAsync(Id, null, Ct));
    }

    [Fact]
    public async Task WhileRecording_TheModelRefuses_AndTheErrorPropagates()
    {
        var llm = new ScriptedLlm(_ => Answer) { Paused = true };

        await Assert.ThrowsAsync<AiPausedException>(async () => await Create(llm, StorageWith(Roadmap())).SummarizeAsync(Id, null, Ct));
    }

    [Fact]
    public async Task WithoutAModelName_TheFallbackIdIsUsed()
    {
        var llm = new ScriptedLlm(_ => Answer);

        var summary = await Create(llm, StorageWith(Roadmap()), new SummarizerOptions { FallbackModelId = "gemma" }).SummarizeAsync(Id, null, Ct);

        Assert.Equal("gemma", summary.ModelId);
    }

    [Fact]
    public void Parser_ReadsMarkdownNumberingParenthesesAndRanges()
    {
        const string answer =
            "Here is the summary:\n\n**Overview:** The team **agreed** on the plan.\nSecond sentence.\n\n"
            + "## Key points\n1. [12:52] PDF export waits.\n2. [12:00–12:45] Range cited.\n* Plain bullet without a time\n"
            + "### ACTION ITEMS\n- none\n";

        var parsed = SessionSummarizer.SummaryParser.Parse(answer);

        Assert.Equal("The team agreed on the plan. Second sentence.", parsed.Overview);
        Assert.Collection(
            parsed.KeyPoints,
            item =>
            {
                Assert.Equal("PDF export waits.", item.Text);
                Assert.Equal(new TimeSpan(0, 12, 52), item.At);
            },
            item =>
            {
                Assert.Equal("Range cited.", item.Text);
                Assert.Equal(new TimeSpan(0, 12, 0), item.At);
            },
            item =>
            {
                Assert.Equal("Plain bullet without a time", item.Text);
                Assert.Null(item.At);
            });
        Assert.Empty(parsed.ActionItems);
    }

    [Fact]
    public void Parser_AcceptsACitationOnlyAtTheStartOfAnItem()
    {
        var parsed = SessionSummarizer.SummaryParser.Parse("KEY POINTS:\n- Meet at (10:30) sharp\n- The demo is at [12:52] next week\n- (10:30) Lead in parentheses\n");

        Assert.All(parsed.KeyPoints, item => Assert.Null(item.At));
        Assert.Equal("Meet at (10:30) sharp", parsed.KeyPoints[0].Text);
        Assert.Equal("The demo is at next week", parsed.KeyPoints[1].Text);
        Assert.Equal("(10:30) Lead in parentheses", parsed.KeyPoints[2].Text);
    }

    [Fact]
    public void Parser_DropsItemsThatEchoTheFormOfThePrompt()
    {
        const string answer =
            "OVERVIEW:\n<two to four sentences>\nKEY POINTS:\n- [mm:ss] <one point>\n- <one point>\n- [12:52] A real point\n"
            + "ACTION ITEMS:\n- [mm:ss] <who does what>\n";

        var parsed = SessionSummarizer.SummaryParser.Parse(answer);

        Assert.Equal(string.Empty, parsed.Overview);
        Assert.Equal("A real point", Assert.Single(parsed.KeyPoints).Text);
        Assert.Empty(parsed.ActionItems);
    }

    [Theory]
    [InlineData("ОГЛЯД", "КЛЮЧОВІ ПУНКТИ", "ЗАВДАННЯ")]
    [InlineData("Підсумок", "Ключові моменти", "Дії")]
    [InlineData("**Огляд:**", "## Рішення", "Наступні кроки:")]
    public void Parser_KnowsTheUkrainianHeadings(string overview, string points, string actions)
    {
        var answer = $"{overview}\nКоманда погодила план.\n{points}\n- [12:52] Експорт PDF — наступний спринт\n{actions}\n- [13:24] Надіслати запрошення\n";

        var parsed = SessionSummarizer.SummaryParser.Parse(answer);

        Assert.Equal("Команда погодила план.", parsed.Overview);
        Assert.Equal("Експорт PDF — наступний спринт", Assert.Single(parsed.KeyPoints).Text);
        Assert.Equal("Надіслати запрошення", Assert.Single(parsed.ActionItems).Text);
    }

    [Fact]
    public async Task ACitedLabel_FindsTheSegmentWithThatLabel_NotALongerSegmentOfAnotherSpeakerThatContainsTheSecond()
    {
        var transcript = new Transcript(
            Id,
            [
                Segment(Speaker.Others, 730.0, 750.0, "A long answer of the other side."),
                Segment(Speaker.Me, 741.75, 745.0, "Me interrupting."),
                Segment(Speaker.Me, 741.2, 742.0, "Earlier but the same second."),
            ],
            "test",
            "en");
        var llm = new ScriptedLlm(_ => "OVERVIEW:\nShort.\nKEY POINTS:\n- [12:21] Cited the interruption\n");

        var summary = await Create(llm, StorageWith(transcript)).SummarizeAsync(Id, null, Ct);

        // The prompt labels the Me lines [12:21]; the latest of them is the cited one, not the Others line that contains 12:21.000.
        Assert.Equal(TimeSpan.FromSeconds(741.75), summary.KeyPoints[0].At);
    }

    [Fact]
    public void Snap_PrefersTheLabelOverContainment_AndFallsBackToPositionWithoutOne()
    {
        (TimeSpan, TimeSpan)[] spans =
        [
            (TimeSpan.FromSeconds(730), TimeSpan.FromSeconds(750)),
            (TimeSpan.FromSeconds(741.75), TimeSpan.FromSeconds(745)),
        ];

        Assert.Equal(TimeSpan.FromSeconds(741.75), SessionSummarizer.Snap(TimeSpan.FromSeconds(741), spans, TimeSpan.FromSeconds(15)));
        Assert.Equal(TimeSpan.FromSeconds(741.75), SessionSummarizer.Snap(TimeSpan.FromSeconds(741.9), spans, TimeSpan.FromSeconds(15)));
        Assert.Equal(TimeSpan.FromSeconds(730), SessionSummarizer.Snap(TimeSpan.FromSeconds(735), spans, TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void ForContext_KeepsAChunkAndTheAnswerInsideTheContextWindow()
    {
        var options = SummarizerOptions.ForContext(8192);

        // Ukrainian text is 2-3 characters per token: the chunk, the answer and the instructions together stay below the window.
        var worstCaseTokens = (options.ChunkCharacters / 2.0) + options.SummaryMaxOutputTokens + 768;
        Assert.True(worstCaseTokens < 8192, $"{worstCaseTokens} tokens");
        Assert.Equal(options.ChunkCharacters, options.ReduceCharacters);
        Assert.True(SummarizerOptions.ForContext(2048).ChunkCharacters >= 1_500);
    }

    [Fact]
    public async Task TheMergeRequest_NeverExceedsTheReduceLimit()
    {
        var segments = Enumerable.Range(0, 60)
            .Select(i => Segment(Speaker.Me, i * 30, (i * 30) + 25, $"Sentence number {i} about the roadmap and the plan."))
            .ToList();
        var options = new SummarizerOptions { ChunkCharacters = 300, ReduceCharacters = 700 };
        var bulky = "OVERVIEW:\n" + new string('x', 400) + "\nKEY POINTS:\n- [00:30] A point\n";
        var llm = new ScriptedLlm(request => request.User.StartsWith("This is part", StringComparison.Ordinal) ? bulky : Answer);

        await Create(llm, StorageWith(new Transcript(Id, segments, "test", "en")), options).SummarizeAsync(Id, null, Ct);

        var merge = llm.Requests[^1].User;
        var notes = merge[(merge.IndexOf("Notes:\n", StringComparison.Ordinal) + "Notes:\n".Length)..];
        Assert.True(notes.Length <= 700, $"{notes.Length} characters of notes");
    }

    [Fact]
    public void Parser_ReadsHourTimes_AndIgnoresImpossibleOnes()
    {
        var parsed = SessionSummarizer.SummaryParser.Parse("KEY POINTS:\n- [1:02:03] Late point\n- [12:75] Impossible time\n");

        Assert.Equal(new TimeSpan(1, 2, 3), parsed.KeyPoints[0].At);
        Assert.Null(parsed.KeyPoints[1].At);
        Assert.Equal("Impossible time", parsed.KeyPoints[1].Text);
    }

    [Fact]
    public void Snap_PicksTheSegmentThatContainsTheTime_AndTheLaterOneOfTwoOverlappingSegments()
    {
        (TimeSpan, TimeSpan)[] spans =
        [
            (TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)),
            (TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40)),
        ];

        Assert.Equal(TimeSpan.FromSeconds(20), SessionSummarizer.Snap(TimeSpan.FromSeconds(25), spans, TimeSpan.FromSeconds(15)));
        Assert.Equal(TimeSpan.FromSeconds(10), SessionSummarizer.Snap(TimeSpan.FromSeconds(12), spans, TimeSpan.FromSeconds(15)));
        Assert.Null(SessionSummarizer.Snap(TimeSpan.FromSeconds(100), spans, TimeSpan.FromSeconds(15)));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    /// <summary>One request the summarizer sent: the system text, the user text and the options.</summary>
    private sealed record Request(string System, string User, ChatOptions? Options);

    private sealed class ScriptedLlm(Func<Request, string> respond) : ILlm
    {
        public List<Request> Requests { get; } = [];

        public string? ModelId { get; init; }

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

            public object? GetService(Type serviceType, object? serviceKey = null) =>
                serviceType == typeof(ChatClientMetadata) && owner.ModelId is { } id ? new ChatClientMetadata("test", null, id) : null;

            public void Dispose()
            {
            }
        }

        private string Respond(Request request) => respond(request);
    }
}
