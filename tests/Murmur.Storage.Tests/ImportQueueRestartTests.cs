using Murmur.Core.Import;
using Murmur.Core.Jobs;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Storage.Tests;

/// <summary>
/// The import queue against the real SQLite storage, whose <c>imports.session_id ... ON DELETE CASCADE</c> deletes the
/// stored entry with its session: an import interrupted mid-decode (app closed, recording, crash) must survive the
/// deletion of its half-made session and finish after a restart.
/// </summary>
public sealed class ImportQueueRestartTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly ImportOptions Copy = new(null, OriginalFileMode.CopyToLibrary);

    private readonly TempDataRoot _root = new();
    private readonly string _media;

    public ImportQueueRestartTests()
    {
        _media = Path.Combine(_root.Path, "media");
        Directory.CreateDirectory(_media);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _root.Dispose();

    [Fact]
    public async Task ClosingTheAppDuringADecode_KeepsTheImport_ItCompletesAfterTheRestart()
    {
        var path = await NewFileAsync("podcast.mp3");
        using var gate = new SemaphoreSlim(0);
        SessionId half;
        long id;
        await using (var first = await Host.StartAsync(_root, new FakeMediaDecoder(".mp3") { Gate = gate }))
        {
            id = (await first.Imports.EnqueueAsync([new ImportRequest(path, Copy)], Ct))[0].Id;
            half = await WaitForHalfMadeSessionAsync(first.Imports, id);
        }

        await AssertStoredAsQueuedAsync(id, half);
        await using var second = await Host.StartAsync(_root, new FakeMediaDecoder(".mp3"));
        await AssertCompletesAsync(second, id);
    }

    [Fact]
    public async Task ARecordingThatPreEmptsADecode_ThenTheAppCloses_KeepsTheImport_ItCompletesAfterTheRestart()
    {
        var path = await NewFileAsync("lecture.mp3");
        using var gate = new SemaphoreSlim(0);
        SessionId half;
        long id;
        await using (var first = await Host.StartAsync(_root, new FakeMediaDecoder(".mp3") { Gate = gate }))
        {
            id = (await first.Imports.EnqueueAsync([new ImportRequest(path, Copy)], Ct))[0].Id;
            half = await WaitForHalfMadeSessionAsync(first.Imports, id);
            first.Recorder.BeginRecording(await first.Storage.CreateSessionAsync("Meeting", DateTimeOffset.Now, Ct));
            await WaitForItemAsync(first.Imports, id, ImportState.Queued);
            await WaitUntilAsync(async () => await first.Storage.GetSessionAsync(half, Ct) is null);
            Assert.Equal(ImportState.Queued, Assert.Single(await first.Storage.LoadImportsAsync(Ct)).Item.State);
        }

        var stored = Assert.Single(await Reopen().LoadImportsAsync(Ct));
        Assert.Equal(id, stored.Item.Id);
        await using var second = await Host.StartAsync(_root, new FakeMediaDecoder(".mp3"));
        await AssertCompletesAsync(second, id);
    }

    [Fact]
    public async Task AfterACrashDuringADecode_TheResumedImportKeepsItsEntry_WhileItsHalfMadeSessionIsDeleted()
    {
        var path = await NewFileAsync("memo.mp3");
        SessionId half;
        long id;
        using (var storage = Reopen())
        {
            // What a process that died mid-decode leaves: an imported session still Recording, an entry Decoding that points at it.
            var created = await storage.CreateSessionAsync("memo", DateTimeOffset.Now, Ct);
            await storage.UpdateSessionAsync(created with { Source = SessionSource.Imported, HasSystemAudio = true }, Ct);
            half = created.Id;
            var item = await storage.SaveImportAsync(new StoredImport(new ImportItem(0, path, ImportState.Queued, 0, null, null), new ImportRequest(path, Copy)), Ct);
            await storage.SaveImportAsync(new StoredImport(item with { State = ImportState.Decoding, Progress = 0.5, Session = half }, new ImportRequest(path, Copy)), Ct);
            id = item.Id;
        }

        await using var host = await Host.StartAsync(_root, new FakeMediaDecoder(".mp3"));
        var session = await AssertCompletesAsync(host, id);

        Assert.NotEqual(half, session);
        Assert.Null(await host.Storage.GetSessionAsync(half, Ct));
    }

    private SqliteStorage Reopen() => _root.Open();

    private async Task AssertStoredAsQueuedAsync(long id, SessionId half)
    {
        using var storage = Reopen();
        var stored = Assert.Single(await storage.LoadImportsAsync(Ct));
        Assert.Equal((id, ImportState.Queued, (SessionId?)null), (stored.Item.Id, stored.Item.State, stored.Item.Session));
        Assert.Null(await storage.GetSessionAsync(half, Ct));
    }

    private static async Task<SessionId> AssertCompletesAsync(Host host, long id)
    {
        var done = await WaitForItemAsync(host.Imports, id, ImportState.Done);
        var session = done.Session!.Value;
        await WaitUntilAsync(async () => (await host.Storage.GetSessionAsync(session, Ct))?.State == SessionState.Ready);
        var stored = Assert.Single(await host.Storage.LoadImportsAsync(Ct));
        Assert.Equal((id, ImportState.Done, (SessionId?)session), (stored.Item.Id, stored.Item.State, stored.Item.Session));
        var record = await host.Storage.GetSessionAsync(session, Ct);
        Assert.Equal(SessionSource.Imported, record!.Source);
        return session;
    }

    private async Task<string> NewFileAsync(string name)
    {
        var path = Path.Combine(_media, name);
        await File.WriteAllBytesAsync(path, [0, 1, 2, 3], Ct);
        return path;
    }

    /// <summary>The session of the import once it is being decoded (it is created right after the probe).</summary>
    private static async Task<SessionId> WaitForHalfMadeSessionAsync(ImportQueue imports, long id)
    {
        ImportItem? item = null;
        await WaitUntilAsync(() => Task.FromResult((item = imports.Items.FirstOrDefault(i => i.Id == id)) is { State: ImportState.Decoding, Session: not null }));
        return item!.Session!.Value;
    }

    private static async Task<ImportItem> WaitForItemAsync(ImportQueue imports, long id, ImportState state)
    {
        ImportItem? item = null;
        await WaitUntilAsync(() => Task.FromResult((item = imports.Items.FirstOrDefault(i => i.Id == id))?.State == state));
        return item!;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(10, Ct);
        }
    }

    /// <summary>One run of the app: storage, job queue, pipeline and import queue, closed in the composition root's order.</summary>
    private sealed class Host : IAsyncDisposable
    {
        private Host(SqliteStorage storage, IMediaDecoder decoder)
        {
            Storage = storage;
            Asr.Script("system.wav", (0.0, 1.0, "imported words"));
            ImportQueue? imports = null;
            Processor = new SessionProcessor(Recorder, storage, storage, Asr, Queue, languageOf: id => imports?.LanguageOf(id));
            Imports = imports = new ImportQueue(storage, storage, decoder, Queue, Processor);
        }

        public SqliteStorage Storage { get; }

        public FakeRecorder Recorder { get; } = new();

        public FakeAsr Asr { get; } = new();

        public JobQueue Queue { get; } = new();

        public SessionProcessor Processor { get; }

        public ImportQueue Imports { get; }

        public static async Task<Host> StartAsync(TempDataRoot root, IMediaDecoder decoder)
        {
            var host = new Host(root.Open(), decoder);
            await host.Imports.StartAsync(Ct);
            await host.Processor.ResumePendingAsync(Ct);
            return host;
        }

        public async ValueTask DisposeAsync()
        {
            Imports.Dispose();
            Processor.Dispose();
            await Queue.DisposeAsync();
            Storage.Dispose();
        }
    }
}
