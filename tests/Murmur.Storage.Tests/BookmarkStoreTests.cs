using Microsoft.Data.Sqlite;
using Murmur.Core.Sessions;

namespace Murmur.Storage.Tests;

public sealed class BookmarkStoreTests : IDisposable
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

    private static NewBookmark At(int seconds, string note = "") => new(TimeSpan.FromSeconds(seconds), note);

    [Fact]
    public async Task ASessionWithoutBookmarks_HasNone()
    {
        var storage = Open();
        var id = await NewSession(storage);

        Assert.Empty(await storage.GetBookmarksAsync(id, Ct));
    }

    [Fact]
    public async Task AddBookmarks_ReturnsEveryBookmarkOfTheSession_OrderedByPosition()
    {
        var storage = Open();
        var id = await NewSession(storage);

        var first = await storage.AddBookmarksAsync(id, [At(90), At(10, "intro")], Ct);
        var second = await storage.AddBookmarksAsync(id, [At(50)], Ct);

        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(90)], first.Select(b => b.At));
        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(90)], second.Select(b => b.At));
        Assert.All(second, b => Assert.Equal(id, b.Session));
        Assert.Equal("intro", second[0].Note);
        Assert.Equal(second, await storage.GetBookmarksAsync(id, Ct));
        Assert.Equal(3, second.Select(b => b.Id).Distinct().Count());
    }

    [Fact]
    public async Task BookmarksAtTheSamePosition_AreOrderedByTheOrderTheyWereAdded()
    {
        var storage = Open();
        var id = await NewSession(storage);

        var all = await storage.AddBookmarksAsync(id, [At(5, "a"), At(5, "b"), At(5, "c")], Ct);

        Assert.Equal(["a", "b", "c"], all.Select(b => b.Note));
        Assert.True(all[0].Id < all[1].Id && all[1].Id < all[2].Id);
    }

    [Fact]
    public async Task APositionKeepsItsMilliseconds()
    {
        var storage = Open();
        var id = await NewSession(storage);

        var all = await storage.AddBookmarksAsync(id, [new NewBookmark(TimeSpan.FromMilliseconds(61_234))], Ct);

        Assert.Equal(TimeSpan.FromMilliseconds(61_234), all.Single().At);
    }

    [Fact]
    public async Task BookmarksOfOtherSessions_AreNotMixedIn()
    {
        var storage = Open();
        var a = await NewSession(storage);
        var b = await NewSession(storage);

        await storage.AddBookmarksAsync(a, [At(1)], Ct);
        var forB = await storage.AddBookmarksAsync(b, [At(2), At(3)], Ct);

        Assert.Equal(2, forB.Count);
        Assert.Single(await storage.GetBookmarksAsync(a, Ct));
    }

    [Fact]
    public async Task AddingNothing_ReturnsTheExistingBookmarks()
    {
        var storage = Open();
        var id = await NewSession(storage);
        await storage.AddBookmarksAsync(id, [At(1)], Ct);

        var all = await storage.AddBookmarksAsync(id, [], Ct);

        Assert.Single(all);
    }

    [Fact]
    public async Task AddingToAnUnknownSession_ThrowsAndStoresNothing()
    {
        var storage = Open();
        var known = await NewSession(storage);

        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await storage.AddBookmarksAsync(SessionId.New(), [At(1)], Ct));

        Assert.Equal(0L, Scalar(storage, "SELECT count(*) FROM bookmarks"));
        Assert.Empty(await storage.GetBookmarksAsync(known, Ct));
    }

    [Fact]
    public async Task UpdateBookmarkNote_ChangesOnlyTheNote()
    {
        var storage = Open();
        var id = await NewSession(storage);
        var all = await storage.AddBookmarksAsync(id, [At(7, "old"), At(8, "other")], Ct);

        await storage.UpdateBookmarkNoteAsync(all[0].Id, "decision: ship it", Ct);

        var after = await storage.GetBookmarksAsync(id, Ct);
        Assert.Equal("decision: ship it", after[0].Note);
        Assert.Equal(all[0].At, after[0].At);
        Assert.Equal("other", after[1].Note);
    }

    [Fact]
    public async Task UpdateBookmarkNote_OfAnUnknownId_Throws()
    {
        var storage = Open();

        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await storage.UpdateBookmarkNoteAsync(12345, "x", Ct));
    }

    [Fact]
    public async Task DeleteBookmark_RemovesIt_AndAnUnknownIdIsIgnored()
    {
        var storage = Open();
        var id = await NewSession(storage);
        var all = await storage.AddBookmarksAsync(id, [At(1), At(2)], Ct);

        await storage.DeleteBookmarkAsync(all[0].Id, Ct);
        await storage.DeleteBookmarkAsync(99999, Ct);
        await storage.DeleteBookmarkAsync(all[0].Id, Ct);

        var after = await storage.GetBookmarksAsync(id, Ct);
        Assert.Equal(all[1].Id, after.Single().Id);
    }

    [Fact]
    public async Task DeletingTheSession_DeletesItsBookmarks_AndOnlyThose()
    {
        var storage = Open();
        var gone = await NewSession(storage);
        var kept = await NewSession(storage);
        await storage.AddBookmarksAsync(gone, [At(1), At(2)], Ct);
        await storage.AddBookmarksAsync(kept, [At(3)], Ct);

        await storage.DeleteSessionAsync(gone, Ct);

        Assert.Empty(await storage.GetBookmarksAsync(gone, Ct));
        Assert.Single(await storage.GetBookmarksAsync(kept, Ct));
        Assert.Equal(1L, Scalar(storage, "SELECT count(*) FROM bookmarks"));
    }

    [Fact]
    public async Task BookmarksSurviveReopeningTheDatabase()
    {
        SessionId id;
        using (var first = _root.Open())
        {
            id = (await first.CreateSessionAsync("t", T0, Ct)).Id;
            await first.AddBookmarksAsync(id, [At(42, "kept")], Ct);
        }

        var second = Open();

        var all = await second.GetBookmarksAsync(id, Ct);
        Assert.Equal("kept", all.Single().Note);
        Assert.Equal(TimeSpan.FromSeconds(42), all.Single().At);
    }

    [Fact]
    public async Task ADatabaseFromBeforeTheBookmarks_GainsThemAndKeepsItsSessions()
    {
        Directory.CreateDirectory(_root.Path);
        var databasePath = new StoragePaths(_root.Path).DatabasePath;
        var id = SessionId.New();
        using (var old = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString()))
        {
            old.Open();
            SchemaMigrator.Apply(old, [.. SchemaMigrator.Migrations.Take(6)]);
            using var insert = old.CreateCommand();
            insert.CommandText =
                "INSERT INTO sessions (id, title, started_at, duration_ms, state, has_mic, has_system, screen_segments, folder, error) " +
                "VALUES ($id, 'old', 0, 0, 6, 0, 0, '[]', 'x', NULL)";
            insert.Parameters.AddWithValue("$id", id.ToString());
            insert.ExecuteNonQuery();
        }

        var storage = Open();

        Assert.Equal("old", (await storage.GetSessionAsync(id, Ct))?.Title);
        var all = await storage.AddBookmarksAsync(id, [At(3)], Ct);
        Assert.Single(all);
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
