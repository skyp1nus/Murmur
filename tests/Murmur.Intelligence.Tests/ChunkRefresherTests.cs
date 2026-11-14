using Microsoft.Extensions.AI;
using Murmur.Core.Diagnostics;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;
using Murmur.Core.Transcription;
using Murmur.Intelligence.Rag;
using Murmur.Intelligence.Tests.Fakes;
using ManualTimeProvider = Murmur.Intelligence.Tests.Fakes.ManualTimeProvider;

namespace Murmur.Intelligence.Tests;

public sealed class ChunkRefresherTests : IDisposable
{
    private static readonly SessionId S1 = new(Guid.Parse("0190a000-0000-7000-8000-000000000001"));
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(10);

    private readonly FakeStorage _storage = new();
    private readonly FakeLlm _llm = new();
    private readonly ManualTimeProvider _time = new();
    private readonly RecordingAppLog _log = new();
    private readonly List<ChunkRefresher> _created = [];
    private bool _recording;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var refresher in _created)
        {
            refresher.Dispose();
        }
    }

    // Three chunks: the segments are two minutes apart, the chunker splits at 60 s.
    private static TranscriptSegment Seg(int second, string text) =>
        new(Speaker.Me, TimeSpan.FromSeconds(second), TimeSpan.FromSeconds(second + 5), text, []);

    private static Transcript Talk(string middle) =>
        new(S1, [Seg(0, "we start with the budget"), Seg(120, middle), Seg(240, "and finish with lunch")], "parakeet");

    /// <summary>The session as it is after the transcription: the transcript is there and indexed.</summary>
    private async Task<ChunkRefresher> IndexedAsync(string middle = "the zebre is here", bool withReader = true, Func<string, float[]>? vectorizer = null)
    {
        _storage.Transcripts[S1] = Talk(middle);
        _llm.Embeddings.Vectorizer = vectorizer;
        await new Assistant(_llm, _storage).IndexSessionAsync(S1, Ct);
        _llm.Embeddings.Batches.Clear();
        _storage.Replaced.Clear();
        return Create(withReader);
    }

    private ChunkRefresher Create(bool withReader = true)
    {
        var refresher = new ChunkRefresher(_llm, _storage, _time, () => _recording, withReader ? _storage.ReadChunksAsync : null, _log);
        _created.Add(refresher);
        return refresher;
    }

    private void FixMiddle(string text) => _storage.Transcripts[S1] = Talk(text);

    [Fact]
    public async Task OneFix_RefreshesOnceAfterTheDebounce()
    {
        var refresher = await IndexedAsync();
        FixMiddle("the zebra is here");

        refresher.Notify(S1);
        _time.Advance(Debounce - TimeSpan.FromMilliseconds(1));
        await refresher.WhenIdleAsync();
        Assert.Empty(_storage.Replaced);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await refresher.WhenIdleAsync();

        var replaced = Assert.Single(_storage.Replaced);
        Assert.Equal(S1, replaced.Session);
        Assert.Contains(replaced.Chunks, chunk => chunk.Text.Contains("zebra", StringComparison.Ordinal));
        Assert.Empty(_log.Events);
    }

    [Fact]
    public async Task ThreeQuickFixes_GiveOneRefresh()
    {
        var refresher = await IndexedAsync();

        FixMiddle("the zebra is here");
        refresher.Notify(S1);
        _time.Advance(TimeSpan.FromSeconds(6));
        FixMiddle("the zebra is there");
        refresher.Notify(S1);
        _time.Advance(TimeSpan.FromSeconds(6));
        FixMiddle("a zebra is there");
        refresher.Notify(S1);

        // 12 s after the first fix, but only 0 s after the last one.
        _time.Advance(TimeSpan.FromSeconds(9));
        await refresher.WhenIdleAsync();
        Assert.Empty(_storage.Replaced);

        _time.Advance(TimeSpan.FromSeconds(1));
        await refresher.WhenIdleAsync();
        var replaced = Assert.Single(_storage.Replaced);
        Assert.Contains(replaced.Chunks, chunk => chunk.Text.Contains("a zebra is there", StringComparison.Ordinal));

        _time.Advance(TimeSpan.FromMinutes(5));
        await refresher.WhenIdleAsync();
        Assert.Single(_storage.Replaced);
    }

    [Fact]
    public async Task WhileRecording_TheRefreshWaitsUntilTheRecordingStops()
    {
        var refresher = await IndexedAsync();
        var requests = _llm.EmbeddingRequests;
        FixMiddle("the zebra is here");
        _recording = true;

        refresher.Notify(S1);
        _time.Advance(Debounce);
        await refresher.WhenIdleAsync();
        _time.Advance(TimeSpan.FromMinutes(10));
        await refresher.WhenIdleAsync();
        Assert.Empty(_storage.Replaced);
        Assert.Equal(requests, _llm.EmbeddingRequests);

        _recording = false;
        refresher.NotifyRecordingStopped();
        await refresher.WhenIdleAsync();

        Assert.Single(_storage.Replaced);
    }

    [Fact]
    public async Task AFixDuringARecording_IsNotLostWhenTheStopComesBeforeTheTimer()
    {
        var refresher = await IndexedAsync();
        FixMiddle("the zebra is here");

        refresher.Notify(S1);
        refresher.NotifyRecordingStopped();
        await refresher.WhenIdleAsync();
        Assert.Empty(_storage.Replaced);

        _time.Advance(Debounce);
        await refresher.WhenIdleAsync();
        Assert.Single(_storage.Replaced);
    }

    [Fact]
    public async Task ARecordingThatStopsTheModelsUnderTheRefresh_PostponesInsteadOfFailing()
    {
        var refresher = await IndexedAsync();
        FixMiddle("the zebra is here");
        _llm.Paused = true;

        refresher.Notify(S1);
        _time.Advance(Debounce);
        await refresher.WhenIdleAsync();
        Assert.Empty(_storage.Replaced);
        Assert.Empty(_log.Events);

        _llm.Paused = false;
        refresher.NotifyRecordingStopped();
        await refresher.WhenIdleAsync();
        Assert.Single(_storage.Replaced);
    }

    [Fact]
    public async Task OnlyTheChangedChunkIsEmbedded_TheOthersKeepTheirStoredVectors()
    {
        var refresher = await IndexedAsync(vectorizer: HashWords);
        var before = _storage.Index[S1].Select(entry => entry.Vector.ToArray()).ToArray();
        FixMiddle("the zebra is here");

        refresher.Notify(S1);
        _time.Advance(Debounce);
        await refresher.WhenIdleAsync();

        var batch = Assert.Single(_llm.Embeddings.Batches);
        var embedded = Assert.Single(batch);
        Assert.Contains("the zebra is here", embedded, StringComparison.Ordinal);

        var replaced = Assert.Single(_storage.Replaced);
        Assert.Equal(3, replaced.Chunks.Count);
        Assert.Equal(before[0], replaced.Embeddings[0].ToArray());
        Assert.Equal(before[2], replaced.Embeddings[2].ToArray());
        Assert.NotEqual(before[1], replaced.Embeddings[1].ToArray());
    }

    [Fact]
    public async Task WithoutAStoredChunksReader_TheWholeSessionIsEmbeddedAgain()
    {
        var refresher = await IndexedAsync(withReader: false);
        FixMiddle("the zebra is here");

        refresher.Notify(S1);
        _time.Advance(Debounce);
        await refresher.WhenIdleAsync();

        Assert.Equal(3, Assert.Single(_llm.Embeddings.Batches).Length);
        Assert.Equal(3, Assert.Single(_storage.Replaced).Chunks.Count);
    }

    [Fact]
    public async Task ADifferentVectorSize_EmbedsEveryChunkAgain()
    {
        var refresher = await IndexedAsync();
        _llm.Embeddings.Vectorizer = _ => [1f, 2f, 3f]; // the embedding model was replaced by one with another size
        FixMiddle("the zebra is here");

        refresher.Notify(S1);
        _time.Advance(Debounce);
        await refresher.WhenIdleAsync();

        // The kept two-float vectors cannot sit next to a three-float one: everything was embedded again.
        Assert.Equal(2, _llm.Embeddings.Batches.Count);
        Assert.Single(_llm.Embeddings.Batches[0]);
        Assert.Equal(3, _llm.Embeddings.Batches[1].Length);
        Assert.All(Assert.Single(_storage.Replaced).Embeddings, vector => Assert.Equal(3, vector.Length));
    }

    [Fact]
    public async Task ATranscriptThatChunksAsBefore_WritesNothing()
    {
        var refresher = await IndexedAsync();

        refresher.Notify(S1);
        _time.Advance(Debounce);
        await refresher.WhenIdleAsync();

        Assert.Empty(_storage.Replaced);
        Assert.Empty(_llm.Embeddings.Batches);
    }

    [Fact]
    public async Task ASessionThatWasNeverIndexed_IsLeftToTheNormalIndexing()
    {
        _storage.Transcripts[S1] = Talk("the zebra is here");
        var refresher = Create();

        refresher.Notify(S1);
        _time.Advance(Debounce);
        await refresher.WhenIdleAsync();

        Assert.Empty(_storage.Replaced);
        Assert.Equal(0, _llm.EmbeddingRequests);
    }

    [Fact]
    public async Task AFailedReplace_IsLoggedAsAWarning_KeepsTheOldChunksAndIsNotRetried()
    {
        var refresher = await IndexedAsync();
        var before = _storage.Index[S1].Select(entry => entry.Chunk.Text).ToArray();
        FixMiddle("the zebra is here");
        _storage.ReplaceFailure = new IOException("disk full");

        refresher.Notify(S1);
        _time.Advance(Debounce);
        await refresher.WhenIdleAsync();
        _time.Advance(TimeSpan.FromMinutes(10));
        await refresher.WhenIdleAsync();

        var entry = Assert.Single(_log.Events);
        Assert.Equal((LogLevel.Warning, "ai", "chunks.refresh-failed"), (entry.Level, entry.Area, entry.Code));
        Assert.Contains("IOException", entry.Detail, StringComparison.Ordinal);
        Assert.Equal(before, _storage.Index[S1].Select(e => e.Chunk.Text).ToArray());
        Assert.Single(_llm.Embeddings.Batches);
    }

    [Fact]
    public async Task AFailedEmbedding_IsLoggedAndKeepsTheOldChunks()
    {
        var refresher = await IndexedAsync();
        var before = _storage.Index[S1].Select(entry => entry.Chunk.Text).ToArray();
        FixMiddle("the zebra is here");
        _llm.EmbeddingFailure = new InvalidOperationException("worker died");

        refresher.Notify(S1);
        _time.Advance(Debounce);
        await refresher.WhenIdleAsync();

        Assert.Equal("chunks.refresh-failed", Assert.Single(_log.Events).Code);
        Assert.Empty(_storage.Replaced);
        Assert.Equal(before, _storage.Index[S1].Select(e => e.Chunk.Text).ToArray());
    }

    [Fact]
    public async Task NoEmbeddingModel_IsASilentNoOp()
    {
        var refresher = await IndexedAsync();
        FixMiddle("the zebra is here");
        _llm.Status = LlmStatus.NotInstalled;

        refresher.Notify(S1);
        _time.Advance(Debounce);
        await refresher.WhenIdleAsync();

        Assert.Empty(_storage.Replaced);
        Assert.Empty(_log.Events);
    }

    [Fact]
    public async Task AMissingEmbeddingModelFile_IsASilentNoOp()
    {
        var refresher = await IndexedAsync();
        FixMiddle("the zebra is here");
        _llm.EmbeddingFailure = new FileNotFoundException("embeddinggemma.gguf");

        refresher.Notify(S1);
        _time.Advance(Debounce);
        await refresher.WhenIdleAsync();

        Assert.Empty(_storage.Replaced);
        Assert.Empty(_log.Events);
    }

    [Fact]
    public async Task Dispose_CancelsTheFixesThatAreStillWaiting()
    {
        var refresher = await IndexedAsync();
        FixMiddle("the zebra is here");

        refresher.Notify(S1);
        refresher.Dispose();
        _time.Advance(TimeSpan.FromMinutes(1));
        await refresher.WhenIdleAsync();

        Assert.Empty(_storage.Replaced);
    }

    [Fact]
    public async Task SearchingTheCorrectedWord_FindsTheSessionThroughTheVectorPath_OnlyAfterTheRefresh()
    {
        // A fake embedder that hashes the words of a text into 64 buckets (the prompt prefixes are not part of it).
        var refresher = await IndexedAsync(vectorizer: HashWords);
        _storage.CosineSearch = true;
        var assistant = new Assistant(_llm, _storage);

        Assert.Empty(await assistant.SearchAsync("zebra", null, 5, Ct));

        FixMiddle("the zebra is here");
        refresher.Notify(S1);
        _time.Advance(Debounce);
        await refresher.WhenIdleAsync();

        var hits = await assistant.SearchAsync("zebra", null, 5, Ct);
        var hit = Assert.Single(hits);
        Assert.Equal(S1, hit.Chunk.Session);
        Assert.NotEqual(SearchSource.Keyword, hit.Source);
        Assert.Contains("zebra", hit.Chunk.Text, StringComparison.Ordinal);
    }

    private static float[] HashWords(string prompt)
    {
        var at = prompt.IndexOf("query: ", StringComparison.Ordinal) is var q and >= 0 ? q + "query: ".Length : prompt.IndexOf("text: ", StringComparison.Ordinal) + "text: ".Length;
        var vector = new float[64];
        foreach (var word in prompt[at..].Split([' ', '\n', ':'], StringSplitOptions.RemoveEmptyEntries))
        {
            var hash = 17;
            foreach (var c in word.ToLowerInvariant())
            {
                hash = (hash * 31) + c;
            }

            vector[(hash & 0x7fffffff) % 64]++;
        }

        return vector;
    }
}
