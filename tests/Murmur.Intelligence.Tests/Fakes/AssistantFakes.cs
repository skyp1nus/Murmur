using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

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

    public ValueTask<IReadOnlyList<SearchHit>> SearchVectorAsync(ReadOnlyMemory<float> embedding, SessionId? scope, int limit, CancellationToken cancellationToken)
    {
        VectorCalls.Add((embedding, scope, limit));
        return ValueTask.FromResult<IReadOnlyList<SearchHit>>(VectorHits);
    }

    public ValueTask ReplaceChunksAsync(SessionId session, IReadOnlyList<TextChunk> chunks, IReadOnlyList<ReadOnlyMemory<float>> embeddings, CancellationToken cancellationToken)
    {
        Replaced.Add((session, chunks, embeddings));
        return ValueTask.CompletedTask;
    }

    public ValueTask<Transcript?> GetTranscriptAsync(SessionId id, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Transcripts.GetValueOrDefault(id));

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

    public ValueTask<SessionRecord> CreateSessionAsync(string title, DateTimeOffset startedAt, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask UpdateSessionAsync(SessionRecord session, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask RenameSessionAsync(SessionId id, string title, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<SessionRecord>> ListSessionsAsync(SessionQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask DeleteSessionAsync(SessionId id, CancellationToken cancellationToken) => throw new NotSupportedException();

    public SessionFiles GetFiles(SessionId id) => throw new NotSupportedException();
}

/// <summary>Embeds "…{n}" style inputs deterministically and records every batch.</summary>
internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public List<string[]> Batches { get; } = [];

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var batch = values.ToArray();
        Batches.Add(batch);
        var result = new GeneratedEmbeddings<Embedding<float>>(
            batch.Select(v => new Embedding<float>(new[] { v.Length, 1f })));
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

    public int EmbeddingRequests { get; private set; }

    public ValueTask<IChatClient> GetChatClientAsync(CancellationToken cancellationToken) =>
        Paused ? throw new AiPausedException() : ValueTask.FromResult<IChatClient>(Chat);

    public ValueTask<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingGeneratorAsync(CancellationToken cancellationToken)
    {
        EmbeddingRequests++;
        return Paused ? throw new AiPausedException() : ValueTask.FromResult<IEmbeddingGenerator<string, Embedding<float>>>(Embeddings);
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
