using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests.Fakes;

/// <summary>
/// Scriptable <see cref="IAssistant"/>. Every <see cref="AskAsync"/> call streams whatever the test pushes with
/// <see cref="Push"/>, <see cref="Finish"/> and <see cref="Fail"/>; searches return <see cref="SearchResult"/>.
/// </summary>
internal sealed class FakeAssistant : IAssistant
{
    private Channel<Func<AssistantUpdate>>? _answer;

    public List<AskRequest> Asks { get; } = [];

    public List<(string Query, SessionId? Scope, int Limit)> Searches { get; } = [];

    public IReadOnlyList<SearchHit> SearchResult { get; set; } = [];

    public Exception? SearchFailure { get; set; }

    /// <summary>Set when the running answer observed its cancellation.</summary>
    public bool AnswerCancelled { get; private set; }

    public ValueTask IndexSessionAsync(SessionId session, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string query, SessionId? scope, int limit, CancellationToken cancellationToken)
    {
        Searches.Add((query, scope, limit));
        if (SearchFailure is { } failure)
        {
            throw failure;
        }

        return ValueTask.FromResult(SearchResult);
    }

    public IAsyncEnumerable<AssistantUpdate> AskAsync(AskRequest request, CancellationToken cancellationToken)
    {
        Asks.Add(request);
        var channel = Channel.CreateUnbounded<Func<AssistantUpdate>>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _answer = channel;
        return Stream(channel.Reader, cancellationToken);
    }

    /// <summary>One text delta of the running answer.</summary>
    public void Push(string delta) => Writer.TryWrite(() => new AssistantUpdate(delta, [], false));

    /// <summary>The final update with the citations; ends the answer.</summary>
    public void Finish(params Citation[] citations)
    {
        Writer.TryWrite(() => new AssistantUpdate(string.Empty, citations, true));
        Writer.TryComplete();
    }

    /// <summary>The answer throws <paramref name="error"/> at this point of the stream.</summary>
    public void Fail(Exception error) => Writer.TryWrite(() => throw error);

    /// <summary>Streams a whole answer in one go: the deltas, then the citations.</summary>
    public void Answer(IEnumerable<string> deltas, params Citation[] citations)
    {
        foreach (var delta in deltas)
        {
            Push(delta);
        }

        Finish(citations);
    }

    private ChannelWriter<Func<AssistantUpdate>> Writer => (_answer ?? throw new InvalidOperationException("No question was asked.")).Writer;

    private async IAsyncEnumerable<AssistantUpdate> Stream(ChannelReader<Func<AssistantUpdate>> reader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            Func<AssistantUpdate> next;
            try
            {
                if (!await reader.WaitToReadAsync(cancellationToken))
                {
                    yield break;
                }

                if (!reader.TryRead(out next!))
                {
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                AnswerCancelled = true;
                throw;
            }

            yield return next();
        }
    }
}

/// <summary><see cref="ILlm"/> whose only job is to report <see cref="Status"/>.</summary>
internal sealed class FakeLlm : ILlm
{
    public LlmStatus Status { get; set; } = LlmStatus.Unloaded;

    public ValueTask<Microsoft.Extensions.AI.IChatClient> GetChatClientAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>> GetEmbeddingGeneratorAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask UnloadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary><see cref="ISearchIndex"/> with scripted BM25 hits.</summary>
internal sealed class FakeSearchIndex : ISearchIndex
{
    public List<string> Queries { get; } = [];

    public IReadOnlyList<SearchHit> KeywordResult { get; set; } = [];

    public ValueTask<IReadOnlyList<SearchHit>> SearchKeywordAsync(string query, SessionId? scope, int limit, CancellationToken cancellationToken)
    {
        Queries.Add(query);
        return ValueTask.FromResult(KeywordResult);
    }

    public ValueTask ReplaceChunksAsync(SessionId session, IReadOnlyList<TextChunk> chunks, IReadOnlyList<ReadOnlyMemory<float>> embeddings, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<IReadOnlyList<SearchHit>> SearchVectorAsync(ReadOnlyMemory<float> embedding, SessionId? scope, int limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal static class Hits
{
    public static SearchHit Make(SessionId session, double seconds, string text, SearchSource source = SearchSource.Hybrid) =>
        new(new TextChunk(session, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(seconds + 20), text), 1, source);

    public static Citation Cite(SessionId session, double seconds, string snippet = "…") =>
        new(session, TimeSpan.FromSeconds(seconds), Clock(TimeSpan.FromSeconds(seconds)), snippet);

    private static string Clock(TimeSpan at) =>
        at.TotalHours >= 1 ? $"{(int)at.TotalHours}:{at.Minutes:00}:{at.Seconds:00}" : $"{at.Minutes:00}:{at.Seconds:00}";
}
