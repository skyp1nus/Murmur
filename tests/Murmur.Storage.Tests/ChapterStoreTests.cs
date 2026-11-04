using Microsoft.Data.Sqlite;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Storage.Tests;

public sealed class ChapterStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 10, 30, 0, TimeSpan.Zero);
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

    private static async Task<SessionId> NewSession(SqliteStorage storage) =>
        (await storage.CreateSessionAsync("t", T0, Ct)).Id;

    private static List<Chapter> Sample() =>
    [
        new("Intro", TimeSpan.Zero),
        new("Roadmap & search", TimeSpan.FromMilliseconds(250_250)),
        new("Наступні кроки", new TimeSpan(1, 2, 5)),
    ];

    [Fact]
    public async Task ASessionWithoutChapters_HasNone()
    {
        var storage = Open();
        var id = await NewSession(storage);

        Assert.Empty(await storage.GetChaptersAsync(id, Ct));
    }

    [Fact]
    public async Task SavedChapters_ComeBackInTimelineOrder_WithTitlesAndStarts()
    {
        var storage = Open();
        var id = await NewSession(storage);
        var chapters = Sample();

        // Saved out of order: read back ordered by start.
        await storage.SaveChaptersAsync(id, [chapters[2], chapters[0], chapters[1]], Ct);
        var loaded = await storage.GetChaptersAsync(id, Ct);

        Assert.Equal(chapters, loaded);
    }

    [Fact]
    public async Task Chapters_SurviveReopeningTheDatabase()
    {
        var id = SessionId.New();
        using (var first = _root.Open())
        {
            id = (await first.CreateSessionAsync("t", T0, Ct)).Id;
            await first.SaveChaptersAsync(id, Sample(), Ct);
        }

        Assert.Equal(Sample(), await Open().GetChaptersAsync(id, Ct));
    }

    [Fact]
    public async Task SavingAgain_ReplacesTheEarlierChapters_AndAnEmptyListRemovesThem()
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.SaveChaptersAsync(id, Sample(), Ct);

        await storage.SaveChaptersAsync(id, [new Chapter("Only", TimeSpan.Zero)], Ct);
        Assert.Equal([new Chapter("Only", TimeSpan.Zero)], await storage.GetChaptersAsync(id, Ct));

        await storage.SaveChaptersAsync(id, [], Ct);
        Assert.Empty(await storage.GetChaptersAsync(id, Ct));
        Assert.Equal(0L, Scalar(storage, "SELECT count(*) FROM chapters"));
    }

    [Fact]
    public async Task ChaptersOfDifferentSessions_AreKeptApart()
    {
        var storage = Open();
        var first = await NewSession(storage);
        var second = await NewSession(storage);
        await storage.SaveChaptersAsync(first, [new Chapter("one", TimeSpan.Zero)], Ct);
        await storage.SaveChaptersAsync(second, [new Chapter("two", TimeSpan.Zero)], Ct);

        Assert.Equal("one", Assert.Single(await storage.GetChaptersAsync(first, Ct)).Title);
        Assert.Equal("two", Assert.Single(await storage.GetChaptersAsync(second, Ct)).Title);
    }

    [Fact]
    public async Task SavingForAnUnknownSession_Throws_AndStoresNothing()
    {
        var storage = Open();

        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await storage.SaveChaptersAsync(SessionId.New(), Sample(), Ct));
        Assert.Equal(0L, Scalar(storage, "SELECT count(*) FROM chapters"));
    }

    [Fact]
    public async Task DeletingTheSession_DeletesItsChapters_AndOnlyThose()
    {
        var storage = Open();
        var id = await NewSession(storage);
        var other = await NewSession(storage);
        await storage.SaveChaptersAsync(id, Sample(), Ct);
        await storage.SaveChaptersAsync(other, Sample(), Ct);

        await storage.DeleteSessionAsync(id, Ct);

        Assert.Empty(await storage.GetChaptersAsync(id, Ct));
        Assert.Equal(Sample(), await storage.GetChaptersAsync(other, Ct));
        Assert.Equal(3L, Scalar(storage, "SELECT count(*) FROM chapters"));
    }

    [Fact]
    public async Task ANewTranscript_DeletesTheChaptersMadeFromTheOldOne_AndOnlyOfThatSession()
    {
        var storage = Open();
        var id = await NewSession(storage);
        var other = await NewSession(storage);
        await storage.SaveChaptersAsync(id, Sample(), Ct);
        await storage.SaveChaptersAsync(other, Sample(), Ct);

        await storage.SaveTranscriptAsync(new Transcript(id, [], "engine"), Ct);

        Assert.Empty(await storage.GetChaptersAsync(id, Ct));
        Assert.Equal(Sample(), await storage.GetChaptersAsync(other, Ct));
    }

    [Fact]
    public async Task ADatabaseFromBeforeChapters_IsMigratedAndKeepsItsSessions()
    {
        // A database at schema version 5 (before chapters) with one session in it.
        Directory.CreateDirectory(_root.Path);
        var databasePath = new StoragePaths(_root.Path).DatabasePath;
        var id = SessionId.New();
        using (var old = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString()))
        {
            old.Open();
            SchemaMigrator.Apply(old, [.. SchemaMigrator.Migrations.Take(5)]);
            using var insert = old.CreateCommand();
            insert.CommandText =
                "INSERT INTO sessions (id, title, started_at, duration_ms, state, has_mic, has_system, screen_segments, folder, error) " +
                "VALUES ($id, 'old', 0, 0, 6, 0, 0, '[]', 'x', NULL)";
            insert.Parameters.AddWithValue("$id", id.ToString());
            insert.ExecuteNonQuery();
        }

        var storage = Open();

        Assert.Equal("old", (await storage.GetSessionAsync(id, Ct))?.Title);
        Assert.Empty(await storage.GetChaptersAsync(id, Ct));
        await storage.SaveChaptersAsync(id, Sample(), Ct);
        Assert.Equal(Sample(), await storage.GetChaptersAsync(id, Ct));
    }

    private static long Scalar(SqliteStorage storage, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = storage.Paths.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
