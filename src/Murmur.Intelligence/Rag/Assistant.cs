using System.Collections.Concurrent;
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
        + "4. Якщо відповіді в записах немає, почни відповідь з маркера [NOT_FOUND] (лише його, без змін), а далі прямо скажи, "
        + "що в записах цього немає. У такій відповіді не став посилань на час. Якщо відповідь знайшлася, маркера не пиши.\n"
        + "У транскриптах «Я» — це користувач, «Інші» — інші учасники розмови.";

    /// <summary>
    /// What the model opens an answer with when the excerpts do not answer the question. The assistant strips it from the text and returns
    /// the answer without citations (no time chip, no sources). Not a timestamp, so it never reads as a citation.
    /// </summary>
    public const string NotFoundMarker = "[NOT_FOUND]";

    // Model input, not UI text: the title in an excerpt header of the (Ukrainian) prompt. Citations carry no title; the App words "untitled" itself.
    private const string UntitledSession = "Без назви";

    private readonly ILlm _llm;
    private readonly IStorage _storage;
    private readonly AssistantOptions _options;
    private readonly TranscriptChunker _chunker;

    // Per session with speaker names: how its chunks and segments look with them. Dropped when the session is indexed again.
    private readonly ConcurrentDictionary<SessionId, Relabeler> _relabelers = new();
    private const int MaxCachedRelabelers = 64;

    // Bumped whenever a session is indexed: a search that read the transcript before that must not cache what it built from it.
    private readonly ConcurrentDictionary<SessionId, int> _generations = new();

    private readonly Func<Speaker, string>? _displayLabel;

    /// <param name="displayLabel">
    /// The word search results show for a speaker without a name (the UI word, "You" / "Others"); <c>null</c> = the model labels. What the model
    /// reads (the excerpts of Ask AI, the index) always has the model labels.
    /// </param>
    public Assistant(ILlm llm, IStorage storage, AssistantOptions? options = null, Func<Speaker, string>? displayLabel = null)
    {
        ArgumentNullException.ThrowIfNull(llm);
        ArgumentNullException.ThrowIfNull(storage);
        _llm = llm;
        _storage = storage;
        _options = options ?? new AssistantOptions();
        _chunker = new TranscriptChunker(_options.ChunkMaxDuration);
        _displayLabel = displayLabel;
    }

    public async ValueTask IndexSessionAsync(SessionId session, CancellationToken cancellationToken)
    {
        var transcript = await _storage.GetTranscriptAsync(session, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Session {session} has no transcript to index.");
        // New indexing labels the lines with the names the user gave the speakers. Chunks indexed before a rename keep their old label in the
        // index until the next index run; search and answers relabel them on the way out (SearchAsync).
        Invalidate(session);
        var names = await _storage.GetSpeakerNamesAsync(session, cancellationToken).ConfigureAwait(false);
        var chunks = _chunker.Chunk(transcript, names);
        var texts = new string[chunks.Count];
        for (var i = 0; i < texts.Length; i++)
        {
            texts[i] = chunks[i].Text;
        }

        var embeddings = await DocumentEmbedder.EmbedAsync(_llm, texts, _options.EmbeddingBatchSize, cancellationToken).ConfigureAwait(false);
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
        return await ApplySpeakerNamesAsync(hits, display: true, cancellationToken).ConfigureAwait(false);
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

        // The excerpts are model input: the model labels for the speakers without a name, not the UI words of the search rows.
        IReadOnlyList<SearchHit> hits = [];
        if (!string.IsNullOrWhiteSpace(request.Question) && _options.AskExcerpts > 0)
        {
            hits = await SearchCoreAsync(request.Question, request.Scope, _options.AskExcerpts, cancellationToken).ConfigureAwait(false);
            hits = await ApplySpeakerNamesAsync(hits, display: false, cancellationToken).ConfigureAwait(false);
        }
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
        var marker = new NotFoundMarker();
        await foreach (var update in chat.GetStreamingResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false))
        {
            var delta = update.Text;
            if (string.IsNullOrEmpty(delta))
            {
                continue;
            }

            var shown = marker.Push(delta);
            if (shown.Length > 0)
            {
                answer.Append(shown);
                yield return new AssistantUpdate(shown, [], false);
            }
        }

        if (marker.Finish() is { Length: > 0 } tail)
        {
            answer.Append(tail);
            yield return new AssistantUpdate(tail, [], false);
        }

        // The model said nothing relevant was found: no chip and no sources, not even the top excerpt as a fallback.
        IReadOnlyList<Citation> citations = marker.Found
            ? []
            : CitationResolver.Resolve(answer.ToString(), excerpts, _options.CitationTolerance, _options.SnippetLength);
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
            prompt.Append("Уривків не знайдено: у записах немає нічого схожого на це запитання. Почни відповідь з маркера ")
                .Append(NotFoundMarker).Append(".\n\n");
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
        names = names.ForModel();
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
    /// Puts the current speaker names into the hits of the sessions that have names: a chunk is shown as the transcript would chunk it with the
    /// names (so chunks indexed before a rename show the new label), a single segment found by keyword gets its speaker's label in front (the
    /// name, or the default label of the speaker who has none). Hits of sessions without names are untouched. Best effort: a session whose names or
    /// transcript cannot be read keeps its hits as they are.
    /// </summary>
    private async ValueTask<IReadOnlyList<SearchHit>> ApplySpeakerNamesAsync(IReadOnlyList<SearchHit> hits, bool display, CancellationToken cancellationToken)
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
                relabeler = await TryGetRelabelerAsync(chunk.Session, cancellationToken).ConfigureAwait(false);
                relabelers[chunk.Session] = relabeler;
            }

            if (relabeler?.Relabel(chunk, display) is { } text && text != chunk.Text)
            {
                result ??= [.. hits];
                result[i] = hits[i] with { Chunk = chunk with { Text = text } };
            }
        }

        return result ?? hits;
    }

    /// <summary>
    /// The relabeler of a session with names: one query for the names on every search, the transcript only when the cached relabeler was made for
    /// other names (so a search does not read and chunk the transcripts again). <c>null</c> without names or when something cannot be read.
    /// </summary>
    private async ValueTask<Relabeler?> TryGetRelabelerAsync(SessionId session, CancellationToken cancellationToken)
    {
        try
        {
            var generation = _generations.GetValueOrDefault(session);
            var names = (await _storage.GetSpeakerNamesAsync(session, cancellationToken).ConfigureAwait(false)).ForModel();
            if (names.Me is null && names.Others is null)
            {
                _relabelers.TryRemove(session, out _);
                return null;
            }

            if (_relabelers.TryGetValue(session, out var cached) && cached.Names == names)
            {
                return cached;
            }

            var transcript = await _storage.GetTranscriptAsync(session, cancellationToken).ConfigureAwait(false);
            if (transcript is null)
            {
                return null;
            }

            var relabeler = new Relabeler(names, transcript, _chunker, _displayLabel);

            // Indexed meanwhile: what was built from the transcript read before is stale; use it for this search only.
            if (_generations.GetValueOrDefault(session) == generation)
            {
                if (_relabelers.Count >= MaxCachedRelabelers)
                {
                    _relabelers.Clear();
                }

                _relabelers[session] = relabeler;
            }

            return relabeler;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Forgets what was built from the transcript of <paramref name="session"/> (the speaker-name views of its chunks) and tells running searches not to
    /// keep what they build: call it when the transcript changed (a fixed word), so the next search reads it again.
    /// </summary>
    public void InvalidateSession(SessionId session) => Invalidate(session);

    private void Invalidate(SessionId session)
    {
        _generations.AddOrUpdate(session, 1, static (_, generation) => generation + 1);
        _relabelers.TryRemove(session, out _);
    }

    /// <summary>
    /// The chunks and segments of one transcript with the names of one moment, found by their start (and end) in whole milliseconds, as the
    /// index stores them. Kept per session (see <see cref="TryGetRelabelerAsync"/>), so it holds only what a hit needs: text and speaker.
    /// </summary>
    private sealed class Relabeler
    {
        private readonly Dictionary<(long Start, long End), string> _chunks = [];
        private readonly Dictionary<(long Start, long End), string> _displayChunks = [];
        private readonly Dictionary<long, (Speaker Speaker, string Text)> _segments = [];
        private readonly Func<Speaker, string>? _displayLabel;

        public Relabeler(SpeakerNames names, Transcript transcript, TranscriptChunker chunker, Func<Speaker, string>? displayLabel)
        {
            Names = names;
            _displayLabel = displayLabel;
            foreach (var chunk in chunker.Chunk(transcript, names))
            {
                _chunks[(Ms(chunk.Start), Ms(chunk.End))] = chunk.Text;
            }

            // The rows of the search show the UI word for a speaker without a name.
            foreach (var chunk in displayLabel is null ? [] : chunker.Chunk(transcript, names, displayLabel))
            {
                _displayChunks[(Ms(chunk.Start), Ms(chunk.End))] = chunk.Text;
            }

            foreach (var segment in transcript.Segments)
            {
                _segments.TryAdd(Ms(segment.Start), (segment.Speaker, segment.Text.Trim()));
            }
        }

        /// <summary>The names this relabeler was made for.</summary>
        public SpeakerNames Names { get; }

        /// <summary>The hit's text with the names, or <c>null</c> when it matches neither a chunk nor a segment.</summary>
        public string? Relabel(TextChunk hit, bool display)
        {
            var key = (Ms(hit.Start), Ms(hit.End));
            if ((display && _displayChunks.TryGetValue(key, out var shown)) || _chunks.TryGetValue(key, out shown))
            {
                return shown;
            }

            if (_segments.TryGetValue(Ms(hit.Start), out var segment) && segment.Text == hit.Text.Trim())
            {
                var label = Names[segment.Speaker] is { Length: > 0 } name
                    ? name
                    : display && _displayLabel is not null ? _displayLabel(segment.Speaker) : TranscriptChunker.Label(segment.Speaker);
                return label + ": " + segment.Text;
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
