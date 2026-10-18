using System.Globalization;
using Microsoft.Data.Sqlite;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Storage.Tests;

public sealed class SearchIndexTests : IDisposable
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

        _root.Dispose();
    }

    private SqliteStorage Open()
    {
        var storage = _root.Open();
        _opened.Add(storage);
        return storage;
    }

    private static TimeSpan Ms(long value) => TimeSpan.FromMilliseconds(value);

    private static async Task<SessionId> NewSession(SqliteStorage storage) =>
        (await storage.CreateSessionAsync("t", T0, Ct)).Id;

    private static async Task<SessionId> SessionWithSegments(SqliteStorage storage, params string[] texts)
    {
        var id = await NewSession(storage);
        var segments = new List<TranscriptSegment>();
        for (var i = 0; i < texts.Length; i++)
        {
            segments.Add(new TranscriptSegment(Speaker.Me, Ms(i * 1000), Ms((i * 1000) + 900), texts[i], []));
        }

        await storage.SaveTranscriptAsync(new Transcript(id, segments, "test", null), Ct);
        return id;
    }

    private static string[] Texts(IReadOnlyList<SearchHit> hits) => [.. hits.Select(h => h.Chunk.Text)];

    // ---- keyword search ----

    [Fact]
    public async Task Keyword_FindsSegmentWithPositionAndPositiveScore()
    {
        var storage = Open();
        var id = await SessionWithSegments(storage, "we ship the beta on friday", "lunch is at noon");

        var hits = await storage.SearchKeywordAsync("friday", null, 10, Ct);

        var hit = Assert.Single(hits);
        Assert.Equal(SearchSource.Keyword, hit.Source);
        Assert.Equal(id, hit.Chunk.Session);
        Assert.Equal(Ms(0), hit.Chunk.Start);
        Assert.Equal(Ms(900), hit.Chunk.End);
        Assert.Equal("we ship the beta on friday", hit.Chunk.Text);
        Assert.True(hit.Score > 0);
    }

    [Fact]
    public async Task Keyword_RanksTheDenserMatchFirst_HigherScoreIsBetter()
    {
        var storage = Open();
        await SessionWithSegments(
            storage,
            "budget is mentioned once among many other unrelated words here today",
            "budget budget budget",
            "nothing relevant");

        var hits = await storage.SearchKeywordAsync("budget", null, 10, Ct);

        Assert.Equal(2, hits.Count);
        Assert.Equal("budget budget budget", hits[0].Chunk.Text);
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public async Task Keyword_MultipleWords_UseAndSemantics()
    {
        var storage = Open();
        await SessionWithSegments(storage, "red apple pie", "red car", "apple tree");

        var hits = await storage.SearchKeywordAsync("red apple", null, 10, Ct);

        Assert.Equal(["red apple pie"], Texts(hits));
    }

    [Fact]
    public async Task Keyword_LastTokenIsAPrefix_EarlierTokensAreNot()
    {
        var storage = Open();
        await SessionWithSegments(storage, "the migration plan", "migrations are hard", "plan the migrate");

        Assert.Equal(3, (await storage.SearchKeywordAsync("migr", null, 10, Ct)).Count);
        Assert.Equal(["the migration plan"], Texts(await storage.SearchKeywordAsync("migration pl", null, 10, Ct)));
        Assert.Empty(await storage.SearchKeywordAsync("migr plan", null, 10, Ct));
    }

    [Fact]
    public async Task Keyword_Cyrillic_IsCaseAndDiacriticInsensitive_WithPrefix()
    {
        var storage = Open();
        await SessionWithSegments(storage, "Обговорили бюджет на наступний квартал", "Їжа та кава", "nothing");

        Assert.Single(await storage.SearchKeywordAsync("БЮДЖЕТ", null, 10, Ct));
        Assert.Single(await storage.SearchKeywordAsync("бюдж", null, 10, Ct));
        Assert.Single(await storage.SearchKeywordAsync("наступний квартал", null, 10, Ct));
        Assert.Single(await storage.SearchKeywordAsync("ЇЖА", null, 10, Ct));
        Assert.Empty(await storage.SearchKeywordAsync("квартали", null, 10, Ct));
    }

    [Fact]
    public async Task Keyword_DiacriticsAreFolded()
    {
        var storage = Open();
        await SessionWithSegments(storage, "We met at the café yesterday");

        Assert.Single(await storage.SearchKeywordAsync("cafe", null, 10, Ct));
        Assert.Single(await storage.SearchKeywordAsync("CAFÉ", null, 10, Ct));
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("\"unterminated")]
    [InlineData("foo AND")]
    [InlineData("NOT")]
    [InlineData("OR OR")]
    [InlineData("NEAR(a b)")]
    [InlineData("NEAR/3")]
    [InlineData("-foo")]
    [InlineData("foo -bar")]
    [InlineData("text:foo")]
    [InlineData("^foo")]
    [InlineData("foo*")]
    [InlineData("(foo")]
    [InlineData("foo)")]
    [InlineData("*")]
    [InlineData("'; DROP TABLE segments; --")]
    [InlineData("a\0b")]
    [InlineData("a\uD800b")]
    [InlineData("\uDC00")]
    [InlineData("foo\uD800")]
    public async Task Keyword_HostileInput_NeverThrows(string query)
    {
        var storage = Open();
        await SessionWithSegments(storage, "foo bar baz", "near and or not");

        var hits = await storage.SearchKeywordAsync(query, null, 10, Ct);

        Assert.NotNull(hits);
        // The segments table survived.
        Assert.Single(await storage.SearchKeywordAsync("baz", null, 10, Ct));
    }

    [Fact]
    public async Task Keyword_OperatorsAreLiteralWords()
    {
        var storage = Open();
        await SessionWithSegments(storage, "cats and dogs", "cats", "dogs");

        // "and" is a word here (AND semantics between all tokens), not an operator.
        Assert.Equal(["cats and dogs"], Texts(await storage.SearchKeywordAsync("cats AND dogs", null, 10, Ct)));
        Assert.Equal(["cats and dogs"], Texts(await storage.SearchKeywordAsync("cats and dogs", null, 10, Ct)));
        // '-' is a separator, not NOT.
        Assert.Equal(2, (await storage.SearchKeywordAsync("-cats", null, 10, Ct)).Count);
        // Punctuation splits words.
        Assert.Equal(["cats and dogs"], Texts(await storage.SearchKeywordAsync("cats, dogs!?", null, 10, Ct)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!! ??? ---")]
    [InlineData("\"\"")]
    public async Task Keyword_EmptyAfterSanitizing_ReturnsNothing(string query)
    {
        var storage = Open();
        await SessionWithSegments(storage, "foo bar");

        Assert.Empty(await storage.SearchKeywordAsync(query, null, 10, Ct));
    }

    [Fact]
    public async Task Keyword_NonPositiveLimit_ReturnsNothing()
    {
        var storage = Open();
        await SessionWithSegments(storage, "foo bar");

        Assert.Empty(await storage.SearchKeywordAsync("foo", null, 0, Ct));
        Assert.Empty(await storage.SearchKeywordAsync("foo", null, -3, Ct));
    }

    [Fact]
    public async Task Keyword_LimitCapsResults()
    {
        var storage = Open();
        await SessionWithSegments(storage, "alpha one", "alpha two", "alpha three");

        Assert.Equal(2, (await storage.SearchKeywordAsync("alpha", null, 2, Ct)).Count);
    }

    [Fact]
    public async Task Keyword_ScopeFiltersToOneSession()
    {
        var storage = Open();
        var a = await SessionWithSegments(storage, "shared topic in a");
        var b = await SessionWithSegments(storage, "shared topic in b");

        var all = await storage.SearchKeywordAsync("shared", null, 10, Ct);
        var onlyA = await storage.SearchKeywordAsync("shared", a, 10, Ct);
        var onlyB = await storage.SearchKeywordAsync("shared", b, 10, Ct);

        Assert.Equal(2, all.Count);
        Assert.Equal("shared topic in a", Assert.Single(onlyA).Chunk.Text);
        Assert.Equal(b, Assert.Single(onlyB).Chunk.Session);
        Assert.Empty(await storage.SearchKeywordAsync("shared", SessionId.New(), 10, Ct));
    }

    [Fact]
    public async Task Keyword_DeletedSessionNoLongerMatches()
    {
        var storage = Open();
        var id = await SessionWithSegments(storage, "ephemeral words");

        await storage.DeleteSessionAsync(id, Ct);

        Assert.Empty(await storage.SearchKeywordAsync("ephemeral", null, 10, Ct));
    }

    [Theory]
    [InlineData("hello world", "\"hello\" \"world\"*")]
    [InlineData("  Привіт,  світе! ", "\"Привіт\" \"світе\"*")]
    [InlineData("don't", "\"don\" \"t\"*")]
    [InlineData("a\"b", "\"a\" \"b\"*")]
    [InlineData("a\uD800b", "\"a\" \"b\"*")]
    [InlineData("foo\uD800", "\"foo\"*")]
    public void BuildFtsQuery_QuotesTokensAndPrefixesTheLast(string input, string expected) =>
        Assert.Equal(expected, SqliteStorage.BuildFtsQuery(input));

    [Fact]
    public void BuildFtsQuery_NoTokens_IsNull()
    {
        Assert.Null(SqliteStorage.BuildFtsQuery(""));
        Assert.Null(SqliteStorage.BuildFtsQuery("-- ** ()"));
    }

    // ---- chunks and vectors ----

    private static TextChunk Chunk(SessionId session, int i, string? text = null) =>
        new(session, Ms(i * 1000), Ms((i * 1000) + 800), text ?? $"chunk {i}");

    private static ReadOnlyMemory<float> Vec(params float[] values) => values;

    private static float[] RandomVector(Random random, int dim)
    {
        var v = new float[dim];
        for (var i = 0; i < dim; i++)
        {
            v[i] = (float)((random.NextDouble() * 2) - 1);
        }

        return v;
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }

        return dot / Math.Sqrt(na * nb);
    }

    private long Scalar(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root.Path, "murmur.db"),
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Vector_TopK_MatchesNaiveReference_AcrossSessions()
    {
        var storage = Open();
        var random = new Random(42);
        const int dim = 48;
        var sessions = new[] { await NewSession(storage), await NewSession(storage), await NewSession(storage) };
        var all = new List<(SessionId Session, int Index, float[] Vector)>();
        foreach (var session in sessions)
        {
            var count = 40;
            var chunks = new List<TextChunk>();
            var embeddings = new List<ReadOnlyMemory<float>>();
            for (var i = 0; i < count; i++)
            {
                var v = RandomVector(random, dim);
                chunks.Add(Chunk(session, i));
                embeddings.Add(v);
                all.Add((session, i, v));
            }

            await storage.ReplaceChunksAsync(session, chunks, embeddings, Ct);
        }

        for (var round = 0; round < 5; round++)
        {
            var query = RandomVector(random, dim);
            var expected = all
                .Select(x => (x.Session, x.Index, Score: Cosine(x.Vector, query)))
                .OrderByDescending(x => x.Score)
                .Take(10)
                .ToArray();

            var hits = await storage.SearchVectorAsync(query, null, 10, Ct);

            Assert.Equal(10, hits.Count);
            for (var i = 0; i < 10; i++)
            {
                Assert.Equal(SearchSource.Vector, hits[i].Source);
                Assert.Equal(expected[i].Session, hits[i].Chunk.Session);
                Assert.Equal($"chunk {expected[i].Index}", hits[i].Chunk.Text);
                Assert.Equal(expected[i].Score, hits[i].Score, 1e-5);
            }

            var scoped = await storage.SearchVectorAsync(query, sessions[1], 5, Ct);
            var expectedScoped = all
                .Where(x => x.Session == sessions[1])
                .Select(x => (x.Index, Score: Cosine(x.Vector, query)))
                .OrderByDescending(x => x.Score)
                .Take(5)
                .ToArray();
            Assert.Equal(5, scoped.Count);
            for (var i = 0; i < 5; i++)
            {
                Assert.Equal(sessions[1], scoped[i].Chunk.Session);
                Assert.Equal($"chunk {expectedScoped[i].Index}", scoped[i].Chunk.Text);
            }
        }
    }

    [Fact]
    public async Task Vector_ScoreIsCosine_IndependentOfMagnitude_AndPositionsRoundtrip()
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.ReplaceChunksAsync(
            id,
            [new TextChunk(id, Ms(1500), Ms(4200), "Привіт, світе")],
            [Vec(3, 4)],
            Ct);

        var same = await storage.SearchVectorAsync(Vec(30, 40), null, 5, Ct);
        var orthogonal = await storage.SearchVectorAsync(Vec(4, -3), null, 5, Ct);
        var opposite = await storage.SearchVectorAsync(Vec(-3, -4), null, 5, Ct);

        var hit = Assert.Single(same);
        Assert.Equal(1.0, hit.Score, 1e-6);
        Assert.Equal(id, hit.Chunk.Session);
        Assert.Equal(Ms(1500), hit.Chunk.Start);
        Assert.Equal(Ms(4200), hit.Chunk.End);
        Assert.Equal("Привіт, світе", hit.Chunk.Text);
        Assert.Equal(0.0, Assert.Single(orthogonal).Score, 1e-6);
        Assert.Equal(-1.0, Assert.Single(opposite).Score, 1e-6);
    }

    [Fact]
    public async Task Vector_StoredAsNormalizedLittleEndianFloat32Blob()
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.ReplaceChunksAsync(id, [Chunk(id, 0)], [Vec(3, 0, 4)], Ct);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root.Path, "murmur.db"),
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT dim, vector FROM chunks";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(3, reader.GetInt32(0));
        var blob = reader.GetFieldValue<byte[]>(1);
        Assert.Equal(12, blob.Length);
        Assert.Equal(0.6f, BitConverter.ToSingle(BitConverter.IsLittleEndian ? blob : [.. blob.Take(4).Reverse()], 0), 1e-6f);
        Assert.Equal(0f, BitConverter.ToSingle(blob, 4));
        Assert.Equal(0.8f, BitConverter.ToSingle(blob, 8), 1e-6f);
    }

    [Fact]
    public async Task Vector_FreshInstance_LoadsFromSqlite()
    {
        var first = Open();
        var id = await NewSession(first);
        await first.ReplaceChunksAsync(id, [Chunk(id, 0, "a"), Chunk(id, 1, "b")], [Vec(1, 0), Vec(0, 1)], Ct);

        var second = Open();
        var all = await second.SearchVectorAsync(Vec(0, 1), null, 10, Ct);
        var scoped = await second.SearchVectorAsync(Vec(1, 0), id, 1, Ct);

        Assert.Equal(["b", "a"], Texts(all));
        Assert.Equal("a", Assert.Single(scoped).Chunk.Text);
    }

    [Fact]
    public async Task Vector_LimitCapsResults_AndNonPositiveLimitIsEmpty()
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.ReplaceChunksAsync(id, [Chunk(id, 0), Chunk(id, 1), Chunk(id, 2)], [Vec(1, 0), Vec(1, 1), Vec(0, 1)], Ct);

        Assert.Equal(2, (await storage.SearchVectorAsync(Vec(1, 0), null, 2, Ct)).Count);
        Assert.Equal(3, (await storage.SearchVectorAsync(Vec(1, 0), null, 100, Ct)).Count);
        Assert.Empty(await storage.SearchVectorAsync(Vec(1, 0), null, 0, Ct));
    }

    [Fact]
    public async Task Vector_EmptyZeroOrNonFiniteQuery_ReturnsNothing()
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.ReplaceChunksAsync(id, [Chunk(id, 0)], [Vec(1, 0)], Ct);

        Assert.Empty(await storage.SearchVectorAsync(ReadOnlyMemory<float>.Empty, null, 5, Ct));
        Assert.Empty(await storage.SearchVectorAsync(Vec(0, 0), null, 5, Ct));
        Assert.Empty(await storage.SearchVectorAsync(Vec(float.NaN, 1), null, 5, Ct));
    }

    [Fact]
    public async Task Vector_QueryOfAnotherDimension_SkipsStaleChunks()
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.ReplaceChunksAsync(id, [Chunk(id, 0)], [Vec(1, 0)], Ct);

        Assert.Empty(await storage.SearchVectorAsync(Vec(1, 0, 0), null, 5, Ct));
        Assert.Empty(await storage.SearchVectorAsync(Vec(1, 0, 0), id, 5, Ct));
    }

    [Fact]
    public async Task Vector_UnknownOrEmptyScope_ReturnsNothing()
    {
        var storage = Open();
        var id = await NewSession(storage);

        Assert.Empty(await storage.SearchVectorAsync(Vec(1, 0), id, 5, Ct));
        Assert.Empty(await storage.SearchVectorAsync(Vec(1, 0), SessionId.New(), 5, Ct));
        Assert.Empty(await storage.SearchVectorAsync(Vec(1, 0), null, 5, Ct));
    }

    // ---- ReplaceChunks validation ----

    [Fact]
    public async Task Replace_CountMismatch_Throws_AndKeepsOldChunks()
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.ReplaceChunksAsync(id, [Chunk(id, 0, "old")], [Vec(1, 0)], Ct);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await storage.ReplaceChunksAsync(id, [Chunk(id, 0), Chunk(id, 1)], [Vec(1, 0)], Ct));

        Assert.Equal(["old"], Texts(await storage.SearchVectorAsync(Vec(1, 0), null, 5, Ct)));
        Assert.Equal(1, Scalar("SELECT count(*) FROM chunks"));
    }

    [Fact]
    public async Task Replace_DimensionMismatchWithinCall_Throws_AndKeepsOldChunks()
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.ReplaceChunksAsync(id, [Chunk(id, 0, "old")], [Vec(1, 0)], Ct);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await storage.ReplaceChunksAsync(id, [Chunk(id, 0), Chunk(id, 1)], [Vec(1, 0), Vec(1, 0, 0)], Ct));

        Assert.Equal(["old"], Texts(await storage.SearchVectorAsync(Vec(1, 0), null, 5, Ct)));
        Assert.Equal(1, Scalar("SELECT count(*) FROM chunks"));
    }

    [Fact]
    public async Task Replace_EmptyOrNonFiniteVector_Throws()
    {
        var storage = Open();
        var id = await NewSession(storage);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await storage.ReplaceChunksAsync(id, [Chunk(id, 0)], [ReadOnlyMemory<float>.Empty], Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await storage.ReplaceChunksAsync(id, [Chunk(id, 0)], [Vec(1, float.PositiveInfinity)], Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await storage.ReplaceChunksAsync(id, [Chunk(id, 0)], [Vec(float.NaN, 1)], Ct));

        Assert.Equal(0, Scalar("SELECT count(*) FROM chunks"));
    }

    [Fact]
    public async Task Replace_UnknownSession_Throws()
    {
        var storage = Open();
        var ghost = SessionId.New();

        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await storage.ReplaceChunksAsync(ghost, [Chunk(ghost, 0)], [Vec(1, 0)], Ct));
    }

    [Fact]
    public async Task Replace_WithEmptyLists_ClearsTheSessionChunks()
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.ReplaceChunksAsync(id, [Chunk(id, 0)], [Vec(1, 0)], Ct);

        await storage.ReplaceChunksAsync(id, [], [], Ct);

        Assert.Empty(await storage.SearchVectorAsync(Vec(1, 0), null, 5, Ct));
        Assert.Equal(0, Scalar("SELECT count(*) FROM chunks"));
    }

    // ---- cache invalidation and cascade ----

    [Fact]
    public async Task Cache_IsInvalidatedByReplace()
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.ReplaceChunksAsync(id, [Chunk(id, 0, "first")], [Vec(1, 0)], Ct);
        Assert.Equal(["first"], Texts(await storage.SearchVectorAsync(Vec(1, 0), null, 5, Ct)));
        Assert.Equal(["first"], Texts(await storage.SearchVectorAsync(Vec(1, 0), id, 5, Ct)));

        await storage.ReplaceChunksAsync(id, [Chunk(id, 0, "second"), Chunk(id, 1, "third")], [Vec(1, 0, 0), Vec(0, 1, 0)], Ct);

        Assert.Empty(await storage.SearchVectorAsync(Vec(1, 0), null, 5, Ct));
        Assert.Equal(["second", "third"], Texts(await storage.SearchVectorAsync(Vec(1, 0, 0), null, 5, Ct)));
        Assert.Equal(["second", "third"], Texts(await storage.SearchVectorAsync(Vec(1, 0, 0), id, 5, Ct)));
        Assert.Equal(2, Scalar("SELECT count(*) FROM chunks"));
    }

    [Fact]
    public async Task Cache_IsInvalidatedByDelete_AndRowsCascade()
    {
        var storage = Open();
        var keep = await NewSession(storage);
        var drop = await NewSession(storage);
        await storage.ReplaceChunksAsync(keep, [Chunk(keep, 0, "keep")], [Vec(1, 0)], Ct);
        await storage.ReplaceChunksAsync(drop, [Chunk(drop, 0, "drop")], [Vec(1, 0)], Ct);
        Assert.Equal(2, (await storage.SearchVectorAsync(Vec(1, 0), null, 5, Ct)).Count);
        Assert.Single(await storage.SearchVectorAsync(Vec(1, 0), drop, 5, Ct));

        await storage.DeleteSessionAsync(drop, Ct);

        Assert.Equal(["keep"], Texts(await storage.SearchVectorAsync(Vec(1, 0), null, 5, Ct)));
        Assert.Empty(await storage.SearchVectorAsync(Vec(1, 0), drop, 5, Ct));
        Assert.Equal(1, Scalar("SELECT count(*) FROM chunks"));
        Assert.Equal(0, Scalar($"SELECT count(*) FROM chunks WHERE session_id = '{drop}'"));
    }

    [Fact]
    public async Task Cache_RepeatedQueries_DoNotReadSqlite()
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.ReplaceChunksAsync(id, [Chunk(id, 0, "cached")], [Vec(1, 0)], Ct);
        Assert.Single(await storage.SearchVectorAsync(Vec(1, 0), null, 5, Ct));

        // Remove the rows behind the storage's back: a cache hit still answers, proving SQLite is not consulted.
        Scalar("DELETE FROM chunks");

        Assert.Equal(["cached"], Texts(await storage.SearchVectorAsync(Vec(1, 0), null, 5, Ct)));
        Assert.Equal(["cached"], Texts(await storage.SearchVectorAsync(Vec(1, 0), id, 5, Ct)));
    }

    [Fact]
    public async Task Cascade_DeletingSession_RemovesChunksAndKeywordRows()
    {
        var storage = Open();
        var id = await SessionWithSegments(storage, "cascade check");
        await storage.ReplaceChunksAsync(id, [Chunk(id, 0)], [Vec(1, 0)], Ct);
        Assert.Equal(1, Scalar("SELECT count(*) FROM chunks"));

        await storage.DeleteSessionAsync(id, Ct);

        Assert.Equal(0, Scalar("SELECT count(*) FROM chunks"));
        Assert.Empty(await storage.SearchKeywordAsync("cascade", null, 5, Ct));
    }

    [Fact]
    public async Task ConcurrentReplaceAndSearch_StayConsistent()
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.ReplaceChunksAsync(id, [Chunk(id, 0, "v0")], [Vec(1, 0)], Ct);

        var writer = Task.Run(async () =>
        {
            for (var i = 1; i <= 30; i++)
            {
                await storage.ReplaceChunksAsync(id, [Chunk(id, 0, "v" + i)], [Vec(1, 0)], Ct);
            }
        }, Ct);
        var reader = Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                Assert.Single(await storage.SearchVectorAsync(Vec(1, 0), null, 5, Ct));
            }
        }, Ct);

        await Task.WhenAll(writer, reader);

        Assert.Equal(["v30"], Texts(await storage.SearchVectorAsync(Vec(1, 0), null, 5, Ct)));
    }

    // ---- cancellation and robustness ----

    [Fact]
    public async Task CancelledToken_CancelsKeywordVectorAndReplace()
    {
        var storage = Open();
        var id = await SessionWithSegments(storage, "hello world");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await storage.SearchKeywordAsync("hello", null, 5, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await storage.SearchVectorAsync(Vec(1, 0), null, 5, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await storage.ReplaceChunksAsync(id, [Chunk(id, 0)], [Vec(1, 0)], cts.Token));

        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM chunks"));
    }

    [Fact]
    public async Task Keyword_LoneSurrogateInQuery_DoesNotThrow_AndStillMatches()
    {
        var storage = Open();
        await SessionWithSegments(storage, "alpha beta");

        var hits = await storage.SearchKeywordAsync("alpha\uD800 be\uDC00", null, 5, Ct);

        Assert.Single(hits);
    }

    [Fact]
    public async Task Keyword_NullQuery_Throws()
    {
        var storage = Open();
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await storage.SearchKeywordAsync(null!, null, 5, Ct));
    }

    [Fact]
    public async Task Vector_ReplaceKeepsOtherSessionsCached_AndScopedHitsStayInScope()
    {
        var storage = Open();
        var a = await NewSession(storage);
        var b = await NewSession(storage);
        await storage.ReplaceChunksAsync(a, [Chunk(a, 0, "a0")], [Vec(1, 0)], Ct);
        await storage.ReplaceChunksAsync(b, [Chunk(b, 0, "b0")], [Vec(1, 0)], Ct);
        await storage.ReplaceChunksAsync(a, [Chunk(a, 0, "a1")], [Vec(0, 1)], Ct);

        var all = await storage.SearchVectorAsync(Vec(1, 0), null, 5, Ct);
        var scoped = await storage.SearchVectorAsync(Vec(1, 0), a, 5, Ct);

        Assert.Equal("b0", all[0].Chunk.Text);
        Assert.Equal(2, all.Count);
        Assert.Single(scoped);
        Assert.Equal("a1", scoped[0].Chunk.Text);
    }
}
