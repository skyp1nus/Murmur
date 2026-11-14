using Murmur.Core.Diagnostics;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;

namespace Murmur.Intelligence.Rag;

/// <summary>Reads the embedded chunks of a session with their stored vectors (empty for a session that has none).</summary>
public delegate ValueTask<IReadOnlyList<(TextChunk Chunk, ReadOnlyMemory<float> Vector)>> StoredChunksReader(SessionId session, CancellationToken cancellationToken);

/// <summary>
/// Brings the embedded chunks of a session (vector search, Ask AI) up to date after the user fixed a word in its transcript. The
/// full-text index is already correct at that moment; the chunks keep the old wording until this runs.
/// <list type="bullet">
/// <item>Lazy and batched: <see cref="Notify"/> starts (or restarts) a per-session timer of <see cref="DefaultDebounce"/>, so several fixes in a
/// row give one refresh, made when the user has stopped fixing.</item>
/// <item>Never while recording: a refresh that is due during a recording waits and runs on <see cref="NotifyRecordingStopped"/>; a refresh that
/// was interrupted by the start of a recording (the models are stopped then) waits the same way.</item>
/// <item>Narrow: the transcript is chunked as <see cref="Assistant.IndexSessionAsync"/> chunks it, the new chunks are compared with the stored
/// ones by text, and only the chunks whose text is new are embedded; the vectors of the others are reused (needs the <c>storedChunks</c> reader;
/// without it the whole session is embedded again). The result replaces the chunks of the session with
/// <see cref="ISearchIndex.ReplaceChunksAsync"/>, which is one transaction.</item>
/// <item>A session that has no stored chunks is left to the normal indexing (it was never indexed, so there is nothing stale to refresh).</item>
/// <item>Without the embedding model nothing happens, silently: the full-text index stays the source of truth.</item>
/// <item>A failure is logged (<c>ai chunks.refresh-failed</c>, warning), the old chunks stay, and nothing is retried until the next fix.</item>
/// </list>
/// </summary>
public sealed class ChunkRefresher : IDisposable
{
    /// <summary>How long after the last fix of a session its chunks are refreshed.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(10);

    private readonly ILlm _llm;
    private readonly IStorage _storage;
    private readonly TimeProvider _time;
    private readonly Func<bool> _isRecording;
    private readonly StoredChunksReader? _storedChunks;
    private readonly IAppLog _log;
    private readonly TimeSpan _debounce;
    private readonly AssistantOptions _options;
    private readonly TranscriptChunker _chunker;

    private readonly Lock _gate = new();
    private readonly Dictionary<SessionId, ITimer> _timers = [];
    private readonly HashSet<SessionId> _postponed = [];
    private readonly List<Task> _runs = [];
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <param name="llm">The embedding model (never loaded here while <paramref name="isRecording"/> says a recording runs).</param>
    /// <param name="storage">Transcripts, speaker names and the chunk index.</param>
    /// <param name="time">The clock of the debounce.</param>
    /// <param name="isRecording">Whether a recording is in progress.</param>
    /// <param name="storedChunks">Reads the stored chunks with their vectors, to embed only the chunks that changed; <c>null</c> = embed the whole session.</param>
    /// <param name="log">Where a failure is reported (a warning); <c>null</c> = nowhere.</param>
    /// <param name="debounce">Quiet time after the last fix, <see cref="DefaultDebounce"/> when <c>null</c>.</param>
    /// <param name="options">The same options the <see cref="Assistant"/> indexes with (chunk length, batch size).</param>
    public ChunkRefresher(
        ILlm llm,
        IStorage storage,
        TimeProvider time,
        Func<bool> isRecording,
        StoredChunksReader? storedChunks = null,
        IAppLog? log = null,
        TimeSpan? debounce = null,
        AssistantOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(llm);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(isRecording);
        _llm = llm;
        _storage = storage;
        _time = time;
        _isRecording = isRecording;
        _storedChunks = storedChunks;
        _log = log ?? NullAppLog.Instance;
        _debounce = debounce ?? DefaultDebounce;
        ArgumentOutOfRangeException.ThrowIfLessThan(_debounce, TimeSpan.Zero, nameof(debounce));
        _options = options ?? new AssistantOptions();
        _chunker = new TranscriptChunker(_options.ChunkMaxDuration);
    }

    /// <summary>
    /// A word of <paramref name="session"/> was fixed: refresh its chunks <see cref="DefaultDebounce"/> after the last call for it (and, if a
    /// recording runs by then, after the recording). Returns at once.
    /// </summary>
    public void Notify(SessionId session)
    {
        lock (_gate)
        {
            if (_disposed || _postponed.Contains(session))
            {
                // Already waiting for the end of a recording: that refresh reads the transcript as it is then.
                return;
            }

            if (_timers.TryGetValue(session, out var timer))
            {
                timer.Change(_debounce, Timeout.InfiniteTimeSpan);
                return;
            }

            _timers[session] = _time.CreateTimer(OnDue, session, _debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Call when a recording has ended: runs the refreshes that had to wait for it.</summary>
    public void NotifyRecordingStopped()
    {
        SessionId[] waiting;
        lock (_gate)
        {
            if (_disposed || _postponed.Count == 0)
            {
                return;
            }

            waiting = [.. _postponed];
            _postponed.Clear();
        }

        foreach (var session in waiting)
        {
            Start(session);
        }
    }

    /// <summary>Completes when no refresh is running (the timers that have not fired yet are not waited for). For tests and for shutdown.</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] running;
            lock (_gate)
            {
                _runs.RemoveAll(static task => task.IsCompleted);
                running = [.. _runs];
            }

            if (running.Length == 0)
            {
                return;
            }

            await Task.WhenAll(running).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        ITimer[] timers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            timers = [.. _timers.Values];
            _timers.Clear();
            _postponed.Clear();
        }

        _cts.Cancel();
        foreach (var timer in timers)
        {
            timer.Dispose();
        }
    }

    private void OnDue(object? state)
    {
        var session = (SessionId)state!;
        lock (_gate)
        {
            if (_disposed || !_timers.Remove(session, out var timer))
            {
                return;
            }

            timer.Dispose();
        }

        Start(session);
    }

    private void Start(SessionId session)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _runs.RemoveAll(static task => task.IsCompleted);
            _runs.Add(RunAsync(session));
        }
    }

    private void Postpone(SessionId session)
    {
        lock (_gate)
        {
            if (!_disposed && !_timers.ContainsKey(session))
            {
                _postponed.Add(session);
            }
        }
    }

    private async Task RunAsync(SessionId session)
    {
        var cancellationToken = _cts.Token;
        try
        {
            await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_isRecording())
                {
                    Postpone(session);
                    return;
                }

                await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _oneAtATime.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex) when (ex is AiPausedException || _isRecording())
        {
            // A recording began: the models were stopped under the refresh. Nothing was replaced; try again when it ends.
            Postpone(session);
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Warning, "ai", "chunks.refresh-failed", LogText.Error(ex));
        }
    }

    private async Task RefreshAsync(SessionId session, CancellationToken cancellationToken)
    {
        if (_llm.Status == LlmStatus.NotInstalled)
        {
            return;
        }

        IReadOnlyList<(TextChunk Chunk, ReadOnlyMemory<float> Vector)> stored = [];
        if (_storedChunks is not null)
        {
            stored = await _storedChunks(session, cancellationToken).ConfigureAwait(false);
            if (stored.Count == 0)
            {
                return;
            }
        }

        var transcript = await _storage.GetTranscriptAsync(session, cancellationToken).ConfigureAwait(false);
        if (transcript is null)
        {
            return;
        }

        var names = await _storage.GetSpeakerNamesAsync(session, cancellationToken).ConfigureAwait(false);
        var chunks = _chunker.Chunk(transcript, names);

        // What can be reused: the vector of a stored chunk, found by its text.
        var reusable = new Dictionary<string, ReadOnlyMemory<float>>(StringComparer.Ordinal);
        foreach (var (chunk, vector) in stored)
        {
            reusable.TryAdd(chunk.Text, vector);
        }

        if (IsUnchanged(chunks, stored))
        {
            return;
        }

        var vectors = new ReadOnlyMemory<float>[chunks.Count];
        var changed = new List<int>();
        var changedTexts = new List<string>();
        var queued = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < chunks.Count; i++)
        {
            if (reusable.TryGetValue(chunks[i].Text, out var kept))
            {
                vectors[i] = kept;
            }
            else
            {
                changed.Add(i);
                if (queued.Add(chunks[i].Text))
                {
                    changedTexts.Add(chunks[i].Text);
                }
            }
        }

        if (changedTexts.Count > 0)
        {
            var embedded = await EmbedAsync(changedTexts, cancellationToken).ConfigureAwait(false);
            if (embedded is null)
            {
                return;
            }

            var byText = new Dictionary<string, ReadOnlyMemory<float>>(changedTexts.Count, StringComparer.Ordinal);
            for (var i = 0; i < changedTexts.Count; i++)
            {
                byText[changedTexts[i]] = embedded[i];
            }

            foreach (var i in changed)
            {
                vectors[i] = byText[chunks[i].Text];
            }

            // Another embedding model than the one the kept vectors came from: they cannot share an index with the new ones.
            if (reusable.Count > 0 && HasMixedDimensions(vectors))
            {
                var all = new string[chunks.Count];
                for (var i = 0; i < all.Length; i++)
                {
                    all[i] = chunks[i].Text;
                }

                embedded = await EmbedAsync(all, cancellationToken).ConfigureAwait(false);
                if (embedded is null)
                {
                    return;
                }

                for (var i = 0; i < vectors.Length; i++)
                {
                    vectors[i] = embedded[i];
                }
            }
        }

        await _storage.ReplaceChunksAsync(session, chunks, vectors, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The embeddings of <paramref name="texts"/>, or <c>null</c> when the embedding model is not installed.</summary>
    private async ValueTask<List<ReadOnlyMemory<float>>?> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        try
        {
            return await DocumentEmbedder.EmbedAsync(_llm, texts, _options.EmbeddingBatchSize, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            // The chat model is installed, the embedding model is not: the full-text index stays the source of truth.
            return null;
        }
    }

    private static bool HasMixedDimensions(ReadOnlyMemory<float>[] vectors)
    {
        for (var i = 1; i < vectors.Length; i++)
        {
            if (vectors[i].Length != vectors[0].Length)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the stored chunks are exactly the new ones (same positions, same text): nothing to write.</summary>
    private static bool IsUnchanged(IReadOnlyList<TextChunk> chunks, IReadOnlyList<(TextChunk Chunk, ReadOnlyMemory<float> Vector)> stored)
    {
        if (chunks.Count != stored.Count)
        {
            return false;
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            var old = stored[i].Chunk;
            if (old.Text != chunks[i].Text || (long)old.Start.TotalMilliseconds != (long)chunks[i].Start.TotalMilliseconds
                || (long)old.End.TotalMilliseconds != (long)chunks[i].End.TotalMilliseconds)
            {
                return false;
            }
        }

        return true;
    }
}
