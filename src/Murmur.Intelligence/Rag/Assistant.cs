using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Murmur.Intelligence.Rag;

/// <summary>
/// Retrieval-augmented search and Q&amp;A over transcripts: chunks are embedded with EmbeddingGemma, search fuses
/// BM25 and vector hits with Reciprocal Rank Fusion, and answers cite recording moments as <c>[12:34]</c>.
/// </summary>
public sealed class Assistant : IAssistant
{
    /// <summary>Ukrainian system prompt: answer only from the excerpts, in the question's language, with timestamps.</summary>
    public const string DefaultSystemPrompt =
        "Ти — помічник застосунку Murmur, який відповідає на запитання про записи мітингів.\n"
        + "Правила:\n"
        + "1. Відповідай лише на основі наданих уривків транскриптів. Нічого не вигадуй і не додавай знань ззовні.\n"
        + "2. Відповідай тією мовою, якою поставлене запитання.\n"
        + "3. Кожне твердження підкріплюй посиланням на час уривка у квадратних дужках, наприклад [12:34], "
        + "а для моментів після першої години — [1:02:03].\n"
        + "4. Якщо відповіді в записах немає, прямо скажи, що в записах цього немає.\n"
        + "У транскриптах «Я» — це користувач, «Інші» — інші учасники розмови.";

    // Model input, not UI text: the title in an excerpt header of the (Ukrainian) prompt. Citations carry no title; the App words "untitled" itself.
    private const string UntitledSession = "Без назви";

    private readonly ILlm _llm;
    private readonly IStorage _storage;
    private readonly AssistantOptions _options;
    private readonly TranscriptChunker _chunker;

    public Assistant(ILlm llm, IStorage storage, AssistantOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(llm);
        ArgumentNullException.ThrowIfNull(storage);
        _llm = llm;
        _storage = storage;
        _options = options ?? new AssistantOptions();
        _chunker = new TranscriptChunker(_options.ChunkMaxDuration);
    }

    public async ValueTask IndexSessionAsync(SessionId session, CancellationToken cancellationToken)
    {
        var transcript = await _storage.GetTranscriptAsync(session, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Session {session} has no transcript to index.");
        // New indexing labels the lines with the names the user gave the speakers. Chunks indexed before a rename keep their old label in the
        // index until the next index run; search and answers relabel them on the way out (SearchAsync).
        var names = await _storage.GetSpeakerNamesAsync(session, cancellationToken).ConfigureAwait(false);
        var chunks = _chunker.Chunk(transcript, names);
        var embeddings = new List<ReadOnlyMemory<float>>(chunks.Count);
        if (chunks.Count > 0)
        {
            var generator = await _llm.GetEmbeddingGeneratorAsync(cancellationToken).ConfigureAwait(false);
            var batchSize = Math.Max(1, _options.EmbeddingBatchSize);
            for (var offset = 0; offset < chunks.Count; offset += batchSize)
            {
                var count = Math.Min(batchSize, chunks.Count - offset);
                var inputs = new string[count];
                for (var i = 0; i < count; i++)
                {
                    inputs[i] = EmbeddingPrompts.Document(chunks[offset + i].Text);
                }

                var generated = await generator.GenerateAsync(inputs, options: null, cancellationToken).ConfigureAwait(false);
                if (generated.Count != count)
                {
                    throw new InvalidDataException($"Expected {count} embeddings, got {generated.Count}.");
                }

                foreach (var embedding in generated)
                {
                    embeddings.Add(embedding.Vector);
                }
            }
        }

        await _storage.ReplaceChunksAsync(session, chunks, embeddings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hybrid search. Falls back to BM25 only (<see cref="SearchSource.Keyword"/>) when the embedding model is not
    /// installed or AI is paused by a recording.
    /// </summary>
    public async ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string query, SessionId? scope, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return [];
        }

        var hits = await SearchCoreAsync(query, scope, limit, cancellationToken).ConfigureAwait(false);
        return await ApplySpeakerNamesAsync(hits, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<SearchHit>> SearchCoreAsync(string query, SessionId? scope, int limit, CancellationToken cancellationToken)
    {
        var keyword = await _storage.SearchKeywordAsync(query, scope, _options.KeywordCandidates, cancellationToken).ConfigureAwait(false);
        var queryEmbedding = await TryEmbedQueryAsync(query, cancellationToken).ConfigureAwait(false);
        if (queryEmbedding is not { } embedding)
        {
            var fused = RankFusion.Fuse(keyword, [], _options.RrfK, limit, scope);
            var keywordOnly = new SearchHit[fused.Count];
            for (var i = 0; i < fused.Count; i++)
            {
                keywordOnly[i] = fused[i] with { Source = SearchSource.Keyword };
            }

            return keywordOnly;
        }

        var vector = await _storage.SearchVectorAsync(embedding, scope, _options.VectorCandidates, cancellationToken).ConfigureAwait(false);
        return RankFusion.Fuse(keyword, vector, _options.RrfK, limit, scope);
    }

    public async IAsyncEnumerable<AssistantUpdate> AskAsync(AskRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Question);

        var hits = await SearchAsync(request.Question, request.Scope, _options.AskExcerpts, cancellationToken).ConfigureAwait(false);
        var excerpts = new TextChunk[hits.Count];
        for (var i = 0; i < hits.Count; i++)
        {
            excerpts[i] = hits[i].Chunk;
        }

        var messages = await BuildMessagesAsync(request, excerpts, cancellationToken).ConfigureAwait(false);
        var chat = await _llm.GetChatClientAsync(cancellationToken).ConfigureAwait(false);
        var chatOptions = new ChatOptions
        {
            Temperature = _options.Temperature,
            MaxOutputTokens = _options.MaxOutputTokens,
            Reasoning = new ReasoningOptions { Effort = _options.Reasoning },
        };

        var answer = new StringBuilder();
        await foreach (var update in chat.GetStreamingResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false))
        {
            var delta = update.Text;
            if (string.IsNullOrEmpty(delta))
            {
                continue;
            }

            answer.Append(delta);
            yield return new AssistantUpdate(delta, [], false);
        }

        var citations = CitationResolver.Resolve(answer.ToString(), excerpts, _options.CitationTolerance, _options.SnippetLength);
        yield return new AssistantUpdate(string.Empty, citations, true);
    }

    internal async ValueTask<List<ChatMessage>> BuildMessagesAsync(AskRequest request, IReadOnlyList<TextChunk> excerpts, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, _options.SystemPrompt ?? DefaultSystemPrompt) };
        if (request.History is { } history)
        {
            foreach (var turn in history)
            {
                messages.Add(new ChatMessage(turn.Role == TurnRole.User ? ChatRole.User : ChatRole.Assistant, turn.Text));
            }
        }

        var titles = new Dictionary<SessionId, string>();
        var speakerNotes = new List<string>();
        var prompt = new StringBuilder();
        if (excerpts.Count == 0)
        {
            prompt.Append("Уривків не знайдено: у записах немає нічого схожого на це запитання.\n\n");
        }
        else
        {
            foreach (var excerpt in excerpts)
            {
                if (titles.ContainsKey(excerpt.Session))
                {
                    continue;
                }

                var session = await _storage.GetSessionAsync(excerpt.Session, cancellationToken).ConfigureAwait(false);
                var title = string.IsNullOrWhiteSpace(session?.Title) ? UntitledSession : session.Title;
                titles[excerpt.Session] = title;
                var names = await _storage.GetSpeakerNamesAsync(excerpt.Session, cancellationToken).ConfigureAwait(false);
                if (SpeakerNote(title, names) is { } note)
                {
                    speakerNotes.Add(note);
                }
            }

            prompt.Append("Уривки із записів:\n\n");
            if (speakerNotes.Count > 0)
            {
                foreach (var note in speakerNotes)
                {
                    prompt.Append(note).Append('\n');
                }

                prompt.Append('\n');
            }

            for (var i = 0; i < excerpts.Count; i++)
            {
                var excerpt = excerpts[i];
                prompt.Append("Уривок ").Append(i + 1).Append(" — «").Append(titles[excerpt.Session]).Append("», ")
                    .Append(Timestamps.FormatRange(excerpt.Start, excerpt.End)).Append(":\n")
                    .Append(excerpt.Text.Trim()).Append("\n\n");
            }
        }

        prompt.Append("Запитання: ").Append(request.Question.Trim());
        messages.Add(new ChatMessage(ChatRole.User, prompt.ToString()));
        return messages;
    }

    /// <summary>
    /// Model input: the names the user gave the speakers of a recording, so that "Anna:" in an excerpt is known to be the user.
    /// <c>null</c> when the recording has no names.
    /// </summary>
    internal static string? SpeakerNote(string title, SpeakerNames names)
    {
        if (names.Me is null && names.Others is null)
        {
            return null;
        }

        var note = new StringBuilder("У записі «").Append(title).Append("» ");
        if (names.Me is not null)
        {
            note.Append('«').Append(names.Me).Append("» — користувач");
        }

        if (names.Others is not null)
        {
            note.Append(names.Me is not null ? ", " : string.Empty).Append('«').Append(names.Others).Append("» — інші учасники розмови");
        }

        return note.Append('.').ToString();
    }

    /// <summary>
    /// Puts the current speaker names into the hits: a chunk is rebuilt from the transcript with the names (so chunks indexed before a
    /// rename show the new label), a single segment found by keyword gets its speaker's name in front. Hits of sessions without names are untouched.
    /// </summary>
    private async ValueTask<IReadOnlyList<SearchHit>> ApplySpeakerNamesAsync(IReadOnlyList<SearchHit> hits, CancellationToken cancellationToken)
    {
        if (hits.Count == 0)
        {
            return hits;
        }

        var relabelers = new Dictionary<SessionId, Relabeler?>();
        SearchHit[]? result = null;
        for (var i = 0; i < hits.Count; i++)
        {
            var chunk = hits[i].Chunk;
            if (!relabelers.TryGetValue(chunk.Session, out var relabeler))
            {
                relabeler = await LoadRelabelerAsync(chunk.Session, cancellationToken).ConfigureAwait(false);
                relabelers[chunk.Session] = relabeler;
            }

            if (relabeler?.Relabel(chunk) is { } text && text != chunk.Text)
            {
                result ??= [.. hits];
                result[i] = hits[i] with { Chunk = chunk with { Text = text } };
            }
        }

        return result ?? hits;
    }

    private async ValueTask<Relabeler?> LoadRelabelerAsync(SessionId session, CancellationToken cancellationToken)
    {
        var names = await _storage.GetSpeakerNamesAsync(session, cancellationToken).ConfigureAwait(false);
        if (names.Me is null && names.Others is null)
        {
            return null;
        }

        var transcript = await _storage.GetTranscriptAsync(session, cancellationToken).ConfigureAwait(false);
        return transcript is null ? null : new Relabeler(names, transcript, _chunker);
    }

    /// <summary>The chunks and segments of one transcript with the current names, found by their start (and end) in whole milliseconds, as the index stores them.</summary>
    private sealed class Relabeler
    {
        private readonly SpeakerNames _names;
        private readonly Dictionary<(long Start, long End), string> _chunks = [];
        private readonly Dictionary<long, TranscriptSegment> _segments = [];

        public Relabeler(SpeakerNames names, Transcript transcript, TranscriptChunker chunker)
        {
            _names = names;
            foreach (var chunk in chunker.Chunk(transcript, names))
            {
                _chunks[(Ms(chunk.Start), Ms(chunk.End))] = chunk.Text;
            }

            foreach (var segment in transcript.Segments)
            {
                _segments.TryAdd(Ms(segment.Start), segment);
            }
        }

        /// <summary>The hit's text with the names, or <c>null</c> when it matches neither a chunk nor a named segment.</summary>
        public string? Relabel(TextChunk hit)
        {
            if (_chunks.TryGetValue((Ms(hit.Start), Ms(hit.End)), out var text))
            {
                return text;
            }

            if (_segments.TryGetValue(Ms(hit.Start), out var segment)
                && _names[segment.Speaker] is { } name
                && segment.Text.Trim() == hit.Text.Trim())
            {
                return name + ": " + hit.Text.Trim();
            }

            return null;
        }

        private static long Ms(TimeSpan time) => (long)time.TotalMilliseconds;
    }

    private async ValueTask<ReadOnlyMemory<float>?> TryEmbedQueryAsync(string query, CancellationToken cancellationToken)
    {
        if (_llm.Status == LlmStatus.NotInstalled)
        {
            return null;
        }

        IEmbeddingGenerator<string, Embedding<float>> generator;
        try
        {
            generator = await _llm.GetEmbeddingGeneratorAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AiPausedException)
        {
            return null;
        }
        catch (FileNotFoundException)
        {
            // Chat model installed, embedding model not.
            return null;
        }

        var embeddings = await generator.GenerateAsync([EmbeddingPrompts.Query(query)], options: null, cancellationToken).ConfigureAwait(false);
        return embeddings.Count > 0 ? embeddings[0].Vector : null;
    }
}
