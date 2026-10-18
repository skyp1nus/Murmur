using Microsoft.Extensions.AI;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;
using Murmur.Intelligence.Rag;
using Murmur.Intelligence.Tests.Fakes;

namespace Murmur.Intelligence.Tests;

public sealed class AssistantTests
{
    private static readonly SessionId S1 = new(Guid.Parse("0190a000-0000-7000-8000-000000000001"));
    private static readonly SessionId S2 = new(Guid.Parse("0190a000-0000-7000-8000-000000000002"));

    private readonly FakeStorage _storage = new();
    private readonly FakeLlm _llm = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SearchHit Hit(SessionId session, double start, double end, double score, SearchSource source, string text) =>
        new(new TextChunk(session, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text), score, source);

    private Assistant Create(AssistantOptions? options = null) => new(_llm, _storage, options);

    [Fact]
    public async Task IndexSession_ChunksEmbedsWithDocumentPromptInBatchesOf16AndReplaces()
    {
        // 40 one-minute-ish chunks: 80 segments of 30 s each.
        var segments = Enumerable.Range(0, 80)
            .Select(i => TranscriptChunkerTests.Seg(i * 30, (i * 30) + 30, $"речення {i}", i % 2 == 0 ? Speaker.Me : Speaker.Others))
            .ToArray();
        _storage.Transcripts[S1] = new Transcript(S1, segments, "parakeet");

        await Create().IndexSessionAsync(S1, Ct);

        Assert.Equal([16, 16, 8], _llm.Embeddings.Batches.Select(b => b.Length));
        Assert.All(_llm.Embeddings.Batches.SelectMany(b => b), input => Assert.StartsWith("title: none | text: Я: речення ", input, StringComparison.Ordinal));
        var (session, chunks, embeddings) = Assert.Single(_storage.Replaced);
        Assert.Equal(S1, session);
        Assert.Equal(40, chunks.Count);
        Assert.Equal(40, embeddings.Count);
        Assert.Equal(new TranscriptChunker().Chunk(_storage.Transcripts[S1]), chunks);
        for (var i = 0; i < chunks.Count; i++)
        {
            // The fake embeds an input as [input.Length, 1].
            Assert.Equal(EmbeddingPrompts.Document(chunks[i].Text).Length, embeddings[i].Span[0]);
        }
    }

    [Fact]
    public async Task IndexSession_EmptyTranscript_ClearsChunksWithoutLoadingTheModel()
    {
        _storage.Transcripts[S1] = new Transcript(S1, [], "parakeet");

        await Create().IndexSessionAsync(S1, Ct);

        Assert.Equal(0, _llm.EmbeddingRequests);
        var replaced = Assert.Single(_storage.Replaced);
        Assert.Empty(replaced.Chunks);
        Assert.Empty(replaced.Embeddings);
    }

    [Fact]
    public async Task IndexSession_NoTranscript_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().IndexSessionAsync(S1, Ct).AsTask());
    }

    [Fact]
    public async Task Search_FusesKeywordAndVectorHitsByReciprocalRank()
    {
        _storage.VectorHits =
        [
            Hit(S1, 0, 60, 0.9, SearchSource.Vector, "A"),
            Hit(S1, 60, 120, 0.8, SearchSource.Vector, "B"),
            Hit(S1, 120, 180, 0.7, SearchSource.Vector, "C"),
        ];
        _storage.KeywordHits =
        [
            Hit(S1, 130, 135, 12, SearchSource.Keyword, "seg in C"),
            Hit(S2, 300, 305, 9, SearchSource.Keyword, "D"),
        ];

        var hits = await Create().SearchAsync("бюджет", scope: null, limit: 3, Ct);

        Assert.Equal(["C", "A", "B"], hits.Select(h => h.Chunk.Text));
        Assert.All(hits, h => Assert.Equal(SearchSource.Hybrid, h.Source));
        Assert.Equal(("бюджет", (SessionId?)null, 20), Assert.Single(_storage.KeywordCalls));
        var vectorCall = Assert.Single(_storage.VectorCalls);
        Assert.Equal(20, vectorCall.Limit);
        Assert.Equal(["task: search result | query: бюджет"], Assert.Single(_llm.Embeddings.Batches));
        Assert.Equal(EmbeddingPrompts.Query("бюджет").Length, vectorCall.Embedding.Span[0]);
    }

    [Fact]
    public async Task Search_Scope_IsPassedToBothIndexesAndForeignHitsAreDropped()
    {
        _storage.VectorHits = [Hit(S2, 0, 60, 0.99, SearchSource.Vector, "foreign"), Hit(S1, 0, 60, 0.5, SearchSource.Vector, "mine")];
        _storage.KeywordHits = [Hit(S2, 10, 12, 50, SearchSource.Keyword, "foreign seg")];

        var hits = await Create().SearchAsync("q", S1, 10, Ct);

        Assert.Equal(["mine"], hits.Select(h => h.Chunk.Text));
        Assert.Equal(S1, Assert.Single(_storage.KeywordCalls).Scope);
        Assert.Equal(S1, Assert.Single(_storage.VectorCalls).Scope);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Search_WithoutEmbeddings_FallsBackToKeywordOnly(bool notInstalled, bool paused)
    {
        _llm.Status = notInstalled ? LlmStatus.NotInstalled : LlmStatus.Unloaded;
        _llm.Paused = paused;
        _storage.KeywordHits = [Hit(S1, 1, 2, 1, SearchSource.Keyword, "b"), Hit(S1, 5, 6, 3, SearchSource.Keyword, "a")];

        var hits = await Create().SearchAsync("q", null, 10, Ct);

        Assert.Equal(["a", "b"], hits.Select(h => h.Chunk.Text));
        Assert.All(hits, h => Assert.Equal(SearchSource.Keyword, h.Source));
        Assert.Empty(_storage.VectorCalls);
    }

    [Fact]
    public async Task Search_BlankQuery_ReturnsNothing()
    {
        Assert.Empty(await Create().SearchAsync("  ", null, 10, Ct));
        Assert.Empty(await Create().SearchAsync("q", null, 0, Ct));
        Assert.Empty(_storage.KeywordCalls);
    }

    [Fact]
    public async Task Ask_StreamsDeltasThenResolvedCitations()
    {
        _storage.AddSession(S1, "Планування спринту");
        _storage.AddSession(S2, "Дзвінок з клієнтом");
        _storage.VectorHits =
        [
            Hit(S1, 0, 60, 0.9, SearchSource.Vector, "Я: бюджет на квартал сто тисяч"),
            Hit(S2, 3690, 3750, 0.8, SearchSource.Vector, "Інші: клієнт погодив бюджет"),
            Hit(S1, 600, 660, 0.7, SearchSource.Vector, "Інші: інше"),
        ];
        _llm.Chat.Pieces = ["Бюджет — сто тисяч [00:", "42]. Клієнт погодив [1:01:40]", ". Повтор [00:42], поза [30:00]."];

        var updates = new List<AssistantUpdate>();
        await foreach (var update in Create().AskAsync(new AskRequest("Який бюджет?"), Ct))
        {
            updates.Add(update);
        }

        Assert.Equal(_llm.Chat.Pieces, updates.SkipLast(1).Select(u => u.TextDelta));
        Assert.All(updates.SkipLast(1), u => Assert.False(u.IsFinal));
        Assert.All(updates.SkipLast(1), u => Assert.Empty(u.Citations));
        var final = updates[^1];
        Assert.True(final.IsFinal);
        Assert.Equal("", final.TextDelta);
        Assert.Equal(
            [(S1, TimeSpan.FromSeconds(42), "00:42"), (S2, new TimeSpan(1, 1, 40), "1:01:40")],
            final.Citations.Select(c => (c.Session, c.At, c.Label)));
        Assert.Equal("Інші: клієнт погодив бюджет", final.Citations[1].Snippet);
    }

    [Fact]
    public async Task Ask_BuildsPromptWithSystemRulesHistoryAndTitledExcerpts()
    {
        _storage.AddSession(S1, "Ретро");
        _storage.VectorHits =
        [
            Hit(S1, 754, 800, 0.9, SearchSource.Vector, "Я: перший"),
            Hit(S2, 3570, 3630, 0.8, SearchSource.Vector, "Інші: другий"),
            Hit(S1, 0, 30, 0.7, SearchSource.Vector, "Я: третій"),
        ];
        _llm.Chat.Pieces = ["ok"];
        var request = new AskRequest(
            "What was decided?",
            Scope: null,
            History: [new ChatTurn(TurnRole.User, "Привіт"), new ChatTurn(TurnRole.Assistant, "Вітаю")]);

        await foreach (var _ in Create().AskAsync(request, Ct))
        {
        }

        var messages = _llm.Chat.LastMessages;
        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User], messages.Select(m => m.Role));
        Assert.Equal(Assistant.DefaultSystemPrompt, messages[0].Text);
        Assert.Contains("мовою", messages[0].Text, StringComparison.Ordinal);
        Assert.Contains("[12:34]", messages[0].Text, StringComparison.Ordinal);
        Assert.Contains("[1:02:03]", messages[0].Text, StringComparison.Ordinal);
        Assert.Equal("Привіт", messages[1].Text);
        Assert.Equal("Вітаю", messages[2].Text);

        var prompt = messages[3].Text;
        Assert.Contains("Уривок 1 — «Ретро», [12:34–13:20]:\nЯ: перший", prompt, StringComparison.Ordinal);
        Assert.Contains("Уривок 2 — «Без назви», [59:30–1:00:30]:\nІнші: другий", prompt, StringComparison.Ordinal);
        Assert.Contains("Уривок 3 — «Ретро», [00:00–00:30]:\nЯ: третій", prompt, StringComparison.Ordinal);
        Assert.EndsWith("Запитання: What was decided?", prompt, StringComparison.Ordinal);
        Assert.Equal(2, _storage.SessionLookups); // titles are cached per session

        Assert.Equal(0.2f, _llm.Chat.LastOptions?.Temperature);
        Assert.Equal(1024, _llm.Chat.LastOptions?.MaxOutputTokens);
    }

    [Fact]
    public async Task Ask_RetrievesSixExcerptsWithinScope()
    {
        _storage.AddSession(S1, "Один");
        _storage.VectorHits = [.. Enumerable.Range(0, 10).Select(i => Hit(S1, i * 60, (i * 60) + 60, 1.0 - (i * 0.01), SearchSource.Vector, $"chunk {i}"))];
        _storage.VectorHits.Add(Hit(S2, 0, 60, 2.0, SearchSource.Vector, "foreign"));
        _llm.Chat.Pieces = ["x"];

        await foreach (var _ in Create().AskAsync(new AskRequest("q", S1), Ct))
        {
        }

        var prompt = _llm.Chat.LastMessages[^1].Text;
        Assert.Contains("Уривок 6 —", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Уривок 7 —", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("foreign", prompt, StringComparison.Ordinal);
        Assert.Equal(S1, Assert.Single(_storage.KeywordCalls).Scope);
    }

    [Fact]
    public async Task Ask_AnswerWithoutTimestamps_GetsTopExcerptAsFallbackCitation()
    {
        _storage.VectorHits = [Hit(S2, 125, 180, 0.9, SearchSource.Vector, "top"), Hit(S1, 0, 60, 0.5, SearchSource.Vector, "second")];
        _llm.Chat.Pieces = ["Відповідь без посилань."];

        var final = await LastAsync(Create().AskAsync(new AskRequest("q"), Ct));

        var citation = Assert.Single(final.Citations);
        Assert.Equal((S2, TimeSpan.FromSeconds(125), "02:05", "top"), (citation.Session, citation.At, citation.Label, citation.Snippet));
    }

    [Fact]
    public async Task Ask_NoExcerpts_TellsTheModelAndReturnsNoCitations()
    {
        _llm.Chat.Pieces = ["У записах цього немає."];

        var final = await LastAsync(Create().AskAsync(new AskRequest("q"), Ct));

        Assert.Empty(final.Citations);
        Assert.Contains("Уривків не знайдено", _llm.Chat.LastMessages[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ask_WhileRecording_ThrowsTheRecordingGuard()
    {
        _llm.Paused = true;
        _storage.KeywordHits = [Hit(S1, 0, 5, 1, SearchSource.Keyword, "k")];

        var ex = await Assert.ThrowsAsync<AiPausedException>(() => LastAsync(Create().AskAsync(new AskRequest("q"), Ct)));

        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Contains("paused while recording", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ask_BlankQuestion_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => LastAsync(Create().AskAsync(new AskRequest(" "), Ct)));
    }

    private static async Task<AssistantUpdate> LastAsync(IAsyncEnumerable<AssistantUpdate> updates)
    {
        AssistantUpdate? last = null;
        await foreach (var update in updates)
        {
            last = update;
        }

        return last ?? throw new InvalidOperationException("No updates.");
    }
}
