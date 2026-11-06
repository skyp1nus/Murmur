using Microsoft.Data.Sqlite;
using Murmur.Core.Import;
using Murmur.Core.Sessions;

namespace Murmur.Storage.Tests;

public sealed class ImportStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);
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

    [Fact]
    public async Task ANewImport_GetsAnId_AndIsLoadedBackWithItsRequest_OldestFirst()
    {
        var storage = Open();

        var first = await storage.SaveImportAsync(Queued("/media/a.mp3", new ImportOptions("uk", OriginalFileMode.CopyToLibrary)), Ct);
        var second = await storage.SaveImportAsync(Queued("/media/b.mp4", new ImportOptions(null, OriginalFileMode.LeaveInPlace)), Ct);

        Assert.True(first.Id > 0);
        Assert.True(second.Id > first.Id);
        var loaded = await Reopen().LoadImportsAsync(Ct);
        Assert.Equal(
            [
                new StoredImport(new ImportItem(first.Id, "/media/a.mp3", ImportState.Queued, 0, null, null), new ImportRequest("/media/a.mp3", new ImportOptions("uk", OriginalFileMode.CopyToLibrary))),
                new StoredImport(new ImportItem(second.Id, "/media/b.mp4", ImportState.Queued, 0, null, null), new ImportRequest("/media/b.mp4", new ImportOptions(null, OriginalFileMode.LeaveInPlace))),
            ],
            loaded);
    }

    [Fact]
    public async Task Saving_AnExistingImport_UpdatesItsState_NotItsRequest()
    {
        var storage = Open();
        var session = await storage.CreateSessionAsync("a", T0, Ct);
        var item = await storage.SaveImportAsync(Queued("/media/a.mp3", new ImportOptions("uk", OriginalFileMode.CopyToLibrary)), Ct);

        var decoding = item with { State = ImportState.Decoding, Progress = 0.25, Session = session.Id, Error = "x" };
        var returned = await storage.SaveImportAsync(new StoredImport(decoding, new ImportRequest("/other.wav", new ImportOptions(null, OriginalFileMode.LeaveInPlace))), Ct);

        Assert.Equal(decoding, returned);
        var loaded = Assert.Single(await storage.LoadImportsAsync(Ct));
        Assert.Equal(decoding, loaded.Item);
        Assert.Equal(new ImportRequest("/media/a.mp3", new ImportOptions("uk", OriginalFileMode.CopyToLibrary)), loaded.Request);
    }

    [Fact]
    public async Task Saving_AnImportThatIsGone_DoesNotRecreateIt()
    {
        var storage = Open();
        var item = await storage.SaveImportAsync(Queued("/media/a.mp3"), Ct);
        await storage.DeleteImportAsync(item.Id, Ct);

        await storage.SaveImportAsync(new StoredImport(item with { State = ImportState.Done, Progress = 1 }, new ImportRequest("/media/a.mp3", Options)), Ct);

        Assert.Empty(await storage.LoadImportsAsync(Ct));
    }

    [Fact]
    public async Task Delete_RemovesTheImport_AnUnknownIdIsIgnored()
    {
        var storage = Open();
        var keep = await storage.SaveImportAsync(Queued("/media/keep.mp3"), Ct);
        var drop = await storage.SaveImportAsync(Queued("/media/drop.mp3"), Ct);

        await storage.DeleteImportAsync(drop.Id, Ct);
        await storage.DeleteImportAsync(12345, Ct);

        Assert.Equal([keep.Id], (await storage.LoadImportsAsync(Ct)).Select(i => i.Item.Id));
    }

    [Fact]
    public async Task DeletingTheSession_DeletesItsImport()
    {
        var storage = Open();
        var session = await storage.CreateSessionAsync("a", T0, Ct);
        var other = await storage.SaveImportAsync(Queued("/media/other.mp3"), Ct);
        var item = await storage.SaveImportAsync(Queued("/media/a.mp3"), Ct);
        await storage.SaveImportAsync(new StoredImport(item with { State = ImportState.Done, Progress = 1, Session = session.Id }, new ImportRequest("/media/a.mp3", Options)), Ct);

        await storage.DeleteSessionAsync(session.Id, Ct);

        Assert.Equal([other.Id], (await storage.LoadImportsAsync(Ct)).Select(i => i.Item.Id));
    }

    [Fact]
    public async Task AnImportOfAnUnknownSession_IsRefused()
    {
        var storage = Open();
        var item = await storage.SaveImportAsync(Queued("/media/a.mp3"), Ct);

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await storage.SaveImportAsync(new StoredImport(item with { Session = SessionId.New() }, new ImportRequest("/media/a.mp3", Options)), Ct));
    }

    [Fact]
    public async Task TheSourceAndTheOriginalPath_AreStored_ARecordingIsRecordedWithout()
    {
        var storage = Open();
        var recorded = await storage.CreateSessionAsync("recorded", T0, Ct);
        var imported = await storage.CreateSessionAsync("imported", T0, Ct);
        await storage.UpdateSessionAsync(imported with { Source = SessionSource.Imported, OriginalPath = "/media/talk.mp3", State = SessionState.Recorded }, Ct);

        var reopened = Reopen();
        var a = await reopened.GetSessionAsync(recorded.Id, Ct);
        var b = await reopened.GetSessionAsync(imported.Id, Ct);

        Assert.Equal((SessionSource.Recorded, (string?)null), (a!.Source, a.OriginalPath));
        Assert.Equal((SessionSource.Imported, "/media/talk.mp3"), (b!.Source, b.OriginalPath));
        Assert.Contains(await reopened.ListSessionsAsync(new SessionQuery(), Ct), s => s.Id == imported.Id && s.Source == SessionSource.Imported);
    }

    [Fact]
    public async Task DeletingAnImportedSession_DeletesTheCopy_NeverAFileLeftInPlace()
    {
        var storage = Open();
        var outside = Path.Combine(_root.Path, "elsewhere", "memo.m4a");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        await File.WriteAllBytesAsync(outside, [1, 2, 3], Ct);
        var copied = await storage.CreateSessionAsync("copied", T0, Ct);
        var copy = Path.Combine(storage.GetFiles(copied.Id).Directory, "original.m4a");
        File.Copy(outside, copy);
        await storage.UpdateSessionAsync(copied with { Source = SessionSource.Imported, OriginalPath = copy }, Ct);
        var inPlace = await storage.CreateSessionAsync("in place", T0, Ct);
        await storage.UpdateSessionAsync(inPlace with { Source = SessionSource.Imported, OriginalPath = outside }, Ct);

        await storage.DeleteSessionAsync(copied.Id, Ct);
        await storage.DeleteSessionAsync(inPlace.Id, Ct);

        Assert.False(File.Exists(copy));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(outside, Ct));
    }

    [Fact]
    public async Task ASessionFromBeforeImports_IsRecorded_AfterTheMigration()
    {
        var paths = new StoragePaths(_root.Path);
        Directory.CreateDirectory(paths.Root);
        using (var connection = new SqliteConnection("Data Source=" + paths.DatabasePath + ";Pooling=False"))
        {
            connection.Open();
            SchemaMigrator.Apply(connection, [.. SchemaMigrator.Migrations.Take(8)]);
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO sessions (id, title, started_at, duration_ms, state, has_mic, has_system, screen_segments, folder) " +
                "VALUES ($id, 'old', 0, 0, 5, 1, 1, '[]', 'Recordings/x')";
            insert.Parameters.AddWithValue("$id", SessionId.New().ToString());
            insert.ExecuteNonQuery();
        }

        var storage = Open();
        var session = Assert.Single(await storage.ListSessionsAsync(new SessionQuery(), Ct));

        Assert.Equal(SessionSource.Recorded, session.Source);
        Assert.Null(session.OriginalPath);
        Assert.Empty(await storage.LoadImportsAsync(Ct));
    }

    private static readonly ImportOptions Options = new(null, OriginalFileMode.CopyToLibrary);

    private static StoredImport Queued(string path, ImportOptions? options = null) =>
        new(new ImportItem(0, path, ImportState.Queued, 0, null, null), new ImportRequest(path, options ?? Options));

    private SqliteStorage Open()
    {
        var storage = _root.Open();
        _opened.Add(storage);
        return storage;
    }

    private SqliteStorage Reopen() => Open();
}
