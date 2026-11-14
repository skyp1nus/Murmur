using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Murmur.Core.Import;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatMessage_ = Murmur.Core.Intelligence.ChatMessage;

namespace Murmur.Intelligence.Tests.Fakes;

internal sealed class FakeStorage : IStorage
{
    public Dictionary<SessionId, Transcript> Transcripts { get; } = [];

    public Dictionary<SessionId, SessionRecord> Sessions { get; } = [];

    public List<SearchHit> KeywordHits { get; set; } = [];

    public List<SearchHit> VectorHits { get; set; } = [];

    public List<(string Query, SessionId? Scope, int Limit)> KeywordCalls { get; } = [];

    public List<(ReadOnlyMemory<float> Embedding, SessionId? Scope, int Limit)> VectorCalls { get; } = [];

    public List<(SessionId Session, IReadOnlyList<TextChunk> Chunks, IReadOnlyList<ReadOnlyMemory<float>> Embeddings)> Replaced { get; } = [];

    public int SessionLookups { get; private set; }

    public string RootDirectory => Path.GetTempPath();

    public void AddSession(SessionId id, string title) =>
        Sessions[id] = new SessionRecord { Id = id, Title = title, StartedAt = DateTimeOffset.UnixEpoch };

    public ValueTask<IReadOnlyList<SearchHit>> SearchKeywordAsync(string query, SessionId? scope, int limit, CancellationToken cancellationToken)
    {
        KeywordCalls.Add((query, scope, limit));
        return ValueTask.FromResult<IReadOnlyList<SearchHit>>(KeywordHits);
    }

    /// <summary>The chunks stored through <see cref="ReplaceChunksAsync"/> with their vectors, per session.</summary>
    public Dictionary<SessionId, List<(TextChunk Chunk, ReadOnlyMemory<float> Vector)>> Index { get; } = [];

    /// <summary>When set, <see cref="SearchVectorAsync"/> ranks the stored <see cref="Index"/> by cosine similarity (hits with a positive score) instead of answering <see cref="VectorHits"/>.</summary>
    public bool CosineSearch { get; set; }

    /// <summary>Thrown by <see cref="ReplaceChunksAsync"/> when set (nothing is stored then).</summary>
    public Exception? ReplaceFailure { get; set; }

    public ValueTask<IReadOnlyList<SearchHit>> SearchVectorAsync(ReadOnlyMemory<float> embedding, SessionId? scope, int limit, CancellationToken cancellationToken)
    {
        VectorCalls.Add((embedding, scope, limit));
        if (!CosineSearch)
        {
            return ValueTask.FromResult<IReadOnlyList<SearchHit>>(VectorHits);
        }

        var hits = new List<SearchHit>();
        foreach (var (session, entries) in Index)
        {
            if (scope is { } only && only != session)
            {
                continue;
            }

            foreach (var (chunk, vector) in entries)
            {
                var score = Cosine(embedding.Span, vector.Span);
                if (score > 0)
                {
                    hits.Add(new SearchHit(chunk, score, SearchSource.Vector));
                }
            }
        }

        return ValueTask.FromResult<IReadOnlyList<SearchHit>>([.. hits.OrderByDescending(h => h.Score).Take(limit)]);
    }

    public ValueTask ReplaceChunksAsync(SessionId session, IReadOnlyList<TextChunk> chunks, IReadOnlyList<ReadOnlyMemory<float>> embeddings, CancellationToken cancellationToken)
    {
        if (ReplaceFailure is { } failure)
        {
            throw failure;
        }

        Replaced.Add((session, chunks, embeddings));
        Index[session] = [.. chunks.Select((chunk, i) => (chunk, embeddings[i]))];
        return ValueTask.CompletedTask;
    }

    /// <summary>What <see cref="ChunkRefresher"/> reads: the stored chunks with their vectors.</summary>
    public ValueTask<IReadOnlyList<(TextChunk Chunk, ReadOnlyMemory<float> Vector)>> ReadChunksAsync(SessionId session, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<(TextChunk Chunk, ReadOnlyMemory<float> Vector)>>(Index.TryGetValue(session, out var entries) ? [.. entries] : []);

    private static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            return 0;
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    /// <summary>How many times a transcript was read.</summary>
    public int TranscriptReads { get; private set; }

    /// <summary>Thrown by <see cref="GetTranscriptAsync"/> when set.</summary>
    public Exception? TranscriptFailure { get; set; }

    /// <summary>Runs once, while a transcript is being read (after the read started, before it is answered): lets a test do something in between.</summary>
    public Action? DuringNextTranscriptRead { get; set; }

    public ValueTask<Transcript?> GetTranscriptAsync(SessionId id, CancellationToken cancellationToken)
    {
        TranscriptReads++;
        var hook = DuringNextTranscriptRead;
        DuringNextTranscriptRead = null;
        hook?.Invoke();
        return TranscriptFailure is { } failure ? throw failure : ValueTask.FromResult(Transcripts.GetValueOrDefault(id));
    }

    public ValueTask<IReadOnlyList<TranscriptSummary>> GetTranscriptSummariesAsync(IReadOnlyList<SessionId> sessions, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<TranscriptSummary>>(
        [
            .. sessions.Where(Transcripts.ContainsKey).Select(id => new TranscriptSummary(id, Transcripts[id].Language, Transcripts[id].Segments.Count > 0 ? Transcripts[id].Segments[0].Text : null)),
        ]);

    public ValueTask<SessionRecord?> GetSessionAsync(SessionId id, CancellationToken cancellationToken)
    {
        SessionLookups++;
        return ValueTask.FromResult(Sessions.GetValueOrDefault(id));
    }

    public ValueTask SaveTranscriptAsync(Transcript transcript, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<SessionRecord> CreateSessionAsync(string title, DateTimeOffset startedAt, SessionSource source, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask UpdateSessionAsync(SessionRecord session, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask RenameSessionAsync(SessionId id, string title, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<SessionRecord>> ListSessionsAsync(SessionQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask DeleteSessionAsync(SessionId id, CancellationToken cancellationToken) => throw new NotSupportedException();

    public SessionFiles GetFiles(SessionId id) => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<Bookmark>> AddBookmarksAsync(SessionId session, IReadOnlyList<NewBookmark> bookmarks, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<Bookmark>> GetBookmarksAsync(SessionId session, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask UpdateBookmarkNoteAsync(long id, string note, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask DeleteBookmarkAsync(long id, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<ImportItem> SaveImportAsync(StoredImport import, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<StoredImport>> LoadImportsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask DeleteImportAsync(long id, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<ChatThread>> ListThreadsAsync(int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<ChatThread> CreateThreadAsync(string title, SessionId? scope, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<ChatMessage_>> GetMessagesAsync(ChatThreadId thread, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask AppendMessageAsync(ChatMessage_ message, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask RenameThreadAsync(ChatThreadId thread, string title, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask DeleteThreadAsync(ChatThreadId thread, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<SessionSummary?> GetSummaryAsync(SessionId session, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask SaveSummaryAsync(SessionSummary summary, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask DeleteSummaryAsync(SessionId session, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask SaveChaptersAsync(SessionId session, IReadOnlyList<Chapter> chapters, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<Chapter>> GetChaptersAsync(SessionId session, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Dictionary<SessionId, SpeakerNames> SpeakerNames { get; } = [];

    public ValueTask<SpeakerNames> GetSpeakerNamesAsync(SessionId session, CancellationToken cancellationToken) =>
        ValueTask.FromResult(SpeakerNames.GetValueOrDefault(session) ?? Murmur.Core.Sessions.SpeakerNames.Default);

    public ValueTask SetSpeakerNameAsync(SessionId session, Speaker speaker, string? name, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask ReplaceWordAsync(SessionId session, int segmentIndex, int wordIndex, string text, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<string>> GetVocabularyAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask AddVocabularyWordAsync(string word, CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>Embeds "…{n}" style inputs deterministically and records every batch.</summary>
internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public List<string[]> Batches { get; } = [];

    /// <summary>Turns an input into its vector; the default is <c>{length, 1}</c>.</summary>
    public Func<string, float[]>? Vectorizer { get; set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var batch = values.ToArray();
        Batches.Add(batch);
        var result = new GeneratedEmbeddings<Embedding<float>>(
            batch.Select(v => new Embedding<float>(Vectorizer?.Invoke(v) ?? [v.Length, 1f])));
        return Task.FromResult(result);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>Streams the configured pieces and records the messages it was given.</summary>
internal sealed class FakeChatClient : IChatClient
{
    public IReadOnlyList<string> Pieces { get; set; } = [];

    public List<ChatMessage> LastMessages { get; } = [];

    public ChatOptions? LastOptions { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Record(messages, options);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Concat(Pieces))));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Record(messages, options);
        foreach (var piece in Pieces)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, piece);
        }

        yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private void Record(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        LastMessages.Clear();
        LastMessages.AddRange(messages);
        LastOptions = options;
    }
}

internal sealed class FakeLlm : ILlm
{
    public FakeChatClient Chat { get; } = new();

    public FakeEmbeddingGenerator Embeddings { get; } = new();

    public LlmStatus Status { get; set; } = LlmStatus.Unloaded;

    public bool Paused { get; set; }

    /// <summary>Thrown when the embedding generator is requested, when set (a missing embedding model is a <see cref="FileNotFoundException"/>).</summary>
    public Exception? EmbeddingFailure { get; set; }

    public int EmbeddingRequests { get; private set; }

    public ValueTask<IChatClient> GetChatClientAsync(CancellationToken cancellationToken) =>
        Paused ? throw new AiPausedException() : ValueTask.FromResult<IChatClient>(Chat);

    public ValueTask<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingGeneratorAsync(CancellationToken cancellationToken)
    {
        EmbeddingRequests++;
        if (EmbeddingFailure is { } failure)
        {
            throw failure;
        }

        return Paused ? throw new AiPausedException() : ValueTask.FromResult<IEmbeddingGenerator<string, Embedding<float>>>(Embeddings);
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
