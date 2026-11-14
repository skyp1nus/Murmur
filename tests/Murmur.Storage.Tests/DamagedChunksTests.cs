using Microsoft.Data.Sqlite;
using Murmur.Core.Diagnostics;
using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Storage.Tests;

// A session whose stored vectors are damaged is skipped (and reported once) instead of failing the search of the whole library.
public sealed class DamagedChunksTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 14, 9, 26, 53, TimeSpan.Zero);
    private readonly TempDataRoot _root = new();
    private readonly List<SqliteStorage> _opened = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var storage in _opened)
        {
            storage.Dispose();
        }

        SqliteConnection.ClearAllPools();
        _root.Dispose();
    }

    private SqliteStorage Open(IAppLog? log = null)
    {
        var storage = new SqliteStorage(new StoragePaths(_root.Path), null, log);
        _opened.Add(storage);
        return storage;
    }

    private static TextChunk Chunk(SessionId session, int i, string text) =>
        new(session, TimeSpan.FromSeconds(i), TimeSpan.FromSeconds(i + 0.8), text);

    private static async Task<SessionId> Indexed(SqliteStorage storage, string text, params float[][] vectors)
    {
        var id = (await storage.CreateSessionAsync("t", T0, Ct)).Id;
        var chunks = new List<TextChunk>();
        var embeddings = new List<ReadOnlyMemory<float>>();
        for (var i = 0; i < vectors.Length; i++)
        {
            chunks.Add(Chunk(id, i, $"{text}{i}"));
            embeddings.Add(vectors[i]);
        }

        await storage.ReplaceChunksAsync(id, chunks, embeddings, Ct);
        return id;
    }

    private void Raw(string sql, Action<SqliteCommand>? bind = null)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root.Path, "murmur.db"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind?.Invoke(command);
        command.ExecuteNonQuery();
    }

    private void SetVector(SessionId session, int idx, byte[] blob, int? dim = null) =>
        Raw("UPDATE chunks SET vector = $v" + (dim is null ? "" : ", dim = $d") + " WHERE session_id = $s AND idx = $i", c =>
        {
            c.Parameters.AddWithValue("$v", blob);
            c.Parameters.AddWithValue("$s", session.ToString());
            c.Parameters.AddWithValue("$i", idx);
            if (dim is { } d)
            {
                c.Parameters.AddWithValue("$d", d);
            }
        });

    private static byte[] Blob(params float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        for (var i = 0; i < values.Length; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * sizeof(float)), values[i]);
        }

        return bytes;
    }

    private static string[] Texts(IReadOnlyList<SearchHit> hits) => [.. hits.Select(h => h.Chunk.Text)];

    private async Task<(RecordingAppLog Log, SqliteStorage Storage, SessionId Healthy, SessionId Damaged)> Setup(Action<SessionId> damage)
    {
        var writer = Open();
        var damaged = await Indexed(writer, "bad", [1f, 0f], [0f, 1f]);
        var healthy = await Indexed(writer, "good", [1f, 0f], [0f, 1f]);
        damage(damaged);
        var log = new RecordingAppLog();
        return (log, Open(log), healthy, damaged);
    }

    [Fact]
    public async Task TruncatedBlob_SkipsThatSession_AndFindsTheHealthyOne()
    {
        var (log, storage, healthy, damaged) = await Setup(id => SetVector(id, 1, new byte[5]));

        var hits = await storage.SearchVectorAsync(new float[] { 1, 0 }, null, 10, Ct);

        Assert.Equal(["good0", "good1"], Texts(hits));
        Assert.All(hits, h => Assert.Equal(healthy, h.Chunk.Session));
        var warning = Assert.Single(log.Events);
        Assert.Equal((LogLevel.Warning, "search", "chunks.damaged", damaged.ToString()), warning);
    }

    [Fact]
    public async Task LongerBlobThanTheDimension_IsDamaged()
    {
        var (log, storage, healthy, _) = await Setup(id => SetVector(id, 0, Blob(1, 0, 0)));

        var hits = await storage.SearchVectorAsync(new float[] { 1, 0 }, null, 10, Ct);

        Assert.All(hits, h => Assert.Equal(healthy, h.Chunk.Session));
        Assert.Equal(2, hits.Count);
        Assert.Single(log.Events);
    }

    [Fact]
    public async Task MixedDimensionsInOneSession_SkipsThatSession()
    {
        var (log, storage, healthy, _) = await Setup(id => SetVector(id, 1, Blob(1, 0, 0), dim: 3));

        var hits = await storage.SearchVectorAsync(new float[] { 1, 0 }, null, 10, Ct);

        Assert.All(hits, h => Assert.Equal(healthy, h.Chunk.Session));
        Assert.Equal(2, hits.Count);
        Assert.Single(log.Events);
    }

    [Fact]
    public async Task ZeroDimension_IsDamaged()
    {
        var (log, storage, healthy, _) = await Setup(id => SetVector(id, 0, [], dim: 0));

        var hits = await storage.SearchVectorAsync(new float[] { 1, 0 }, null, 10, Ct);

        Assert.All(hits, h => Assert.Equal(healthy, h.Chunk.Session));
        Assert.Single(log.Events);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public async Task NonFiniteVector_SkipsThatSession(float bad)
    {
        var (log, storage, healthy, _) = await Setup(id => SetVector(id, 1, Blob(bad, 0)));

        var hits = await storage.SearchVectorAsync(new float[] { 1, 0 }, null, 10, Ct);

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h =>
        {
            Assert.Equal(healthy, h.Chunk.Session);
            Assert.True(double.IsFinite(h.Score));
        });
        Assert.Single(log.Events);
    }

    [Fact]
    public async Task QueryOfAnotherDimension_SkipsEverySession_WithoutAWarning()
    {
        var (log, storage, _, _) = await Setup(_ => { });

        Assert.Empty(await storage.SearchVectorAsync(new float[] { 1, 0, 0 }, null, 10, Ct));
        Assert.Empty(log.Events);
    }

    [Fact]
    public async Task SessionOfAnotherDimension_IsSkippedForTheQuery_ButNotDamaged()
    {
        var writer = Open();
        await Indexed(writer, "old", [1f, 0f, 0f]);
        var current = await Indexed(writer, "new", [1f, 0f]);
        var log = new RecordingAppLog();
        var storage = Open(log);

        var hits = await storage.SearchVectorAsync(new float[] { 1, 0 }, null, 10, Ct);

        Assert.Equal(["new0"], Texts(hits));
        Assert.Equal(current, hits[0].Chunk.Session);
        Assert.Empty(log.Events);
    }

    [Fact]
    public async Task Warning_IsLoggedOncePerSessionAndRun_ForAllAndScopedSearches()
    {
        var (log, storage, healthy, damaged) = await Setup(id => SetVector(id, 0, new byte[3]));

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(2, (await storage.SearchVectorAsync(new float[] { 1, 0 }, null, 10, Ct)).Count);
            Assert.Empty(await storage.SearchVectorAsync(new float[] { 1, 0 }, damaged, 10, Ct));
            Assert.Equal(2, (await storage.SearchVectorAsync(new float[] { 1, 0 }, healthy, 10, Ct)).Count);
        }

        Assert.Single(log.Events);
    }

    [Fact]
    public async Task ScopedSearchOfADamagedSession_ReturnsNothing_AndLogsOnce()
    {
        var (log, storage, _, damaged) = await Setup(id => SetVector(id, 1, new byte[1]));

        Assert.Empty(await storage.SearchVectorAsync(new float[] { 1, 0 }, damaged, 10, Ct));
        Assert.Empty(await storage.SearchVectorAsync(new float[] { 1, 0 }, damaged, 10, Ct));
        Assert.Equal(2, (await storage.SearchVectorAsync(new float[] { 1, 0 }, null, 10, Ct)).Count);

        Assert.Equal(damaged.ToString(), Assert.Single(log.Events).Detail);
    }

    [Fact]
    public async Task TwoDamagedSessions_AreEachLoggedOnce()
    {
        var writer = Open();
        var a = await Indexed(writer, "a", [1f, 0f]);
        var b = await Indexed(writer, "b", [1f, 0f]);
        var c = await Indexed(writer, "c", [1f, 0f]);
        SetVector(a, 0, new byte[2]);
        SetVector(b, 0, Blob(float.NaN, 0));
        var log = new RecordingAppLog();
        var storage = Open(log);

        var hits = await storage.SearchVectorAsync(new float[] { 1, 0 }, null, 10, Ct);
        await storage.SearchVectorAsync(new float[] { 1, 0 }, null, 10, Ct);

        Assert.Equal(c, Assert.Single(hits).Chunk.Session);
        Assert.Equal(
            new[] { a.ToString(), b.ToString() }.Order(StringComparer.Ordinal),
            log.Events.Select(e => e.Detail!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GetChunks_OfADamagedSession_IsEmpty_SoARefreshReindexesIt()
    {
        var (_, storage, healthy, damaged) = await Setup(id => SetVector(id, 0, new byte[3]));

        Assert.Empty(await storage.GetChunksAsync(damaged, Ct));
        Assert.Equal(2, (await storage.GetChunksAsync(healthy, Ct)).Count);
    }

    [Fact]
    public async Task IndexingTheDamagedSessionAgain_MakesItSearchable()
    {
        var (log, storage, _, damaged) = await Setup(id => SetVector(id, 0, new byte[3]));
        Assert.Equal(2, (await storage.SearchVectorAsync(new float[] { 1, 0 }, null, 10, Ct)).Count);

        await storage.ReplaceChunksAsync(damaged, [Chunk(damaged, 0, "fixed")], [new float[] { 1, 0 }], Ct);

        var hits = await storage.SearchVectorAsync(new float[] { 1, 0 }, null, 10, Ct);
        Assert.Equal(3, hits.Count);
        Assert.Contains("fixed", Texts(hits));
        Assert.Single(log.Events);
    }

    [Fact]
    public async Task WithoutALog_DamagedChunksStillDoNotThrow()
    {
        var writer = Open();
        var bad = await Indexed(writer, "bad", [1f, 0f]);
        var good = await Indexed(writer, "good", [1f, 0f]);
        SetVector(bad, 0, new byte[1]);
        var storage = Open();

        var hit = Assert.Single(await storage.SearchVectorAsync(new float[] { 1, 0 }, null, 10, Ct));
        Assert.Equal(good, hit.Chunk.Session);
    }
}
