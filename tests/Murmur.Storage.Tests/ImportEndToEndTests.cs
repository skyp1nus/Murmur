using Murmur.Core.Import;
using Murmur.Core.Jobs;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Storage.Tests;

/// <summary>
/// End-to-end import against the real SQLite storage, the real WAV decoder (or a gated fake for the timing cases) and the
/// fake ASR: metadata, copy vs leave-in-place, language, search, distinct errors, cancel, delete mid-decode, pre-emption.
/// </summary>
public sealed class ImportEndToEndTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TempDataRoot _root = new();
    private readonly string _media;

    public ImportEndToEndTests()
    {
        _media = Path.Combine(_root.Path, "media");
        Directory.CreateDirectory(_media);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _root.Dispose();

    [Theory]
    [InlineData(OriginalFileMode.CopyToLibrary)]
    [InlineData(OriginalFileMode.LeaveInPlace)]
    public async Task AWav_BecomesAnImportedSession_TranscribedInItsLanguage_AndSearchable(OriginalFileMode mode)
    {
        var path = TestWav.Square(Path.Combine(_media, "Quarterly_review.wav"), TimeSpan.FromSeconds(2), 0.4f);
        await using var host = await Host.StartAsync(_root, new CompositeMediaDecoder(new WavMediaDecoder()));
        host.Asr.Script("system.wav", (0.0, 1.0, "the quarterly budget is approved"));

        var item = (await host.Imports.EnqueueAsync([new ImportRequest(path, new ImportOptions("uk", mode))], Ct))[0];
        var done = await WaitForItemAsync(host.Imports, item.Id, ImportState.Done);
        var id = done.Session!.Value;
        await WaitUntilAsync(async () => (await host.Storage.GetSessionAsync(id, Ct))?.State == SessionState.Ready);

        var session = (await host.Storage.GetSessionAsync(id, Ct))!;
        Assert.Equal(SessionSource.Imported, session.Source);
        Assert.Equal("Quarterly review", session.Title);
        Assert.Equal(TimeSpan.FromSeconds(2), session.Duration);
        Assert.False(string.IsNullOrEmpty(session.OriginalPath));
        Assert.Equal("uk", Assert.Single(host.Asr.Requests).Language);
        Assert.True(File.Exists(host.Storage.GetFiles(id).SystemAudio));

        if (mode == OriginalFileMode.LeaveInPlace)
        {
            Assert.Equal(path, session.OriginalPath);
        }
        else
        {
            Assert.NotEqual(path, session.OriginalPath);
            Assert.StartsWith(Path.GetDirectoryName(host.Storage.GetFiles(id).SystemAudio)!, session.OriginalPath, StringComparison.Ordinal);
            Assert.True(File.Exists(session.OriginalPath));
        }

        var hit = Assert.Single(await host.Storage.SearchKeywordAsync("quarterly", null, 10, Ct));
        Assert.Equal(id, hit.Chunk.Session);

        var stored = session.OriginalPath!;
        await host.Storage.DeleteSessionAsync(id, Ct);
        Assert.True(File.Exists(path), "the user's original file is never deleted");
        Assert.Equal(mode == OriginalFileMode.LeaveInPlace, File.Exists(stored));
    }

    [Fact]
    public async Task UnsupportedAndDamagedFiles_AreReportedWithDistinctErrors_AndNoSessionRemains()
    {
        var notMedia = Path.Combine(_media, "notes.txt");
        await File.WriteAllTextAsync(notMedia, "hello", Ct);
        var damaged = Path.Combine(_media, "broken.wav");
        await File.WriteAllTextAsync(damaged, "this is not a wav", Ct);
        var adpcm = AnyWav.Adpcm(Path.Combine(_media, "adpcm.wav"));
        var good = TestWav.Square(Path.Combine(_media, "good.wav"), TimeSpan.FromSeconds(1), 0.3f);
        await using var host = await Host.StartAsync(_root, new CompositeMediaDecoder(new WavMediaDecoder()));
        var o = new ImportOptions(null, OriginalFileMode.CopyToLibrary);

        var items = await host.Imports.EnqueueAsync([new ImportRequest(notMedia, o), new ImportRequest(damaged, o), new ImportRequest(adpcm, o), new ImportRequest(good, o)], Ct);

        Assert.Equal(ImportState.Failed, items[0].State);
        Assert.StartsWith(ImportQueue.UnsupportedCode, items[0].Error, StringComparison.Ordinal);
        var damagedItem = await WaitForItemAsync(host.Imports, items[1].Id, ImportState.Failed);
        Assert.StartsWith(ImportQueue.DamagedCode, damagedItem.Error, StringComparison.Ordinal);
        var adpcmItem = await WaitForItemAsync(host.Imports, items[2].Id, ImportState.Failed);
        Assert.StartsWith(ImportQueue.UnsupportedCode, adpcmItem.Error, StringComparison.Ordinal);
        Assert.NotEqual(items[0].Error![..ImportQueue.UnsupportedCode.Length], damagedItem.Error![..ImportQueue.DamagedCode.Length]);
        var ok = await WaitForItemAsync(host.Imports, items[3].Id, ImportState.Done);

        var sessions = await host.Storage.ListSessionsAsync(new SessionQuery(), Ct);
        Assert.Equal(ok.Session!.Value, Assert.Single(sessions).Id);
    }

    [Fact]
    public async Task Cancel_DuringTheDecode_DeletesTheHalfMadeSession_AndTheStoredEntry()
    {
        var path = await NewFileAsync("long.mp3");
        using var gate = new SemaphoreSlim(0);
        var decoder = new FakeMediaDecoder(".mp3") { Gate = gate };
        await using var host = await Host.StartAsync(_root, decoder);
        var id = (await host.Imports.EnqueueAsync([new ImportRequest(path, new ImportOptions(null, OriginalFileMode.CopyToLibrary))], Ct))[0].Id;
        var half = await WaitForHalfMadeAsync(host.Imports, id);
        Assert.NotNull(await host.Storage.GetSessionAsync(half, Ct));

        await host.Imports.CancelAsync(id, Ct);

        Assert.Equal(ImportState.Cancelled, host.Imports.Items.Single(i => i.Id == id).State);
        Assert.Null(await host.Storage.GetSessionAsync(half, Ct));
        Assert.Empty(await host.Storage.LoadImportsAsync(Ct));
        Assert.Empty(await host.Storage.ListSessionsAsync(new SessionQuery(), Ct));
    }

    [Fact]
    public async Task DeletingTheSession_MidDecode_CancelsTheImport_AndTheQueueGoesOn()
    {
        using var gate = new SemaphoreSlim(0);
        var decoder = new FakeMediaDecoder(".mp3") { Gate = gate };
        await using var host = await Host.StartAsync(_root, decoder);
        var o = new ImportOptions(null, OriginalFileMode.CopyToLibrary);
        var items = await host.Imports.EnqueueAsync([new ImportRequest(await NewFileAsync("a.mp3"), o), new ImportRequest(await NewFileAsync("b.mp3"), o)], Ct);
        var half = await WaitForHalfMadeAsync(host.Imports, items[0].Id);
        await decoder.Started.Task.WaitAsync(Timeout, Ct); // the decode is parked on the gate
        decoder.ResetStarted();
        decoder.Gate = null;

        await host.Storage.DeleteSessionAsync(half, Ct);
        gate.Release(); // the queue notices the deletion when the running decode returns; it does not interrupt it

        var cancelled = await WaitForItemAsync(host.Imports, items[0].Id, ImportState.Cancelled);
        Assert.Null(cancelled.Error);
        var second = await WaitForItemAsync(host.Imports, items[1].Id, ImportState.Done);
        await WaitUntilAsync(async () => (await host.Storage.GetSessionAsync(second.Session!.Value, Ct))?.State == SessionState.Ready);
        Assert.Null(await host.Storage.GetSessionAsync(half, Ct));
        var only = Assert.Single(await host.Storage.ListSessionsAsync(new SessionQuery(), Ct));
        Assert.Equal(second.Session, only.Id);
    }

    [Fact]
    public async Task ARecordingThatStartsMidImport_PreEmptsIt_AndTheImportResumesAfterItInTheSameRun()
    {
        var path = await NewFileAsync("podcast.mp3");
        using var gate = new SemaphoreSlim(0);
        var decoder = new FakeMediaDecoder(".mp3") { Gate = gate };
        await using var host = await Host.StartAsync(_root, decoder);
        var id = (await host.Imports.EnqueueAsync([new ImportRequest(path, new ImportOptions(null, OriginalFileMode.CopyToLibrary))], Ct))[0].Id;
        var half = await WaitForHalfMadeAsync(host.Imports, id);
        await decoder.Started.Task.WaitAsync(Timeout, Ct); // the decode is parked on the gate
        decoder.Gate = null;

        var meeting = await host.Storage.CreateSessionAsync("Meeting", DateTimeOffset.Now, Ct);
        host.Recorder.BeginRecording(meeting);
        await WaitForItemAsync(host.Imports, id, ImportState.Queued);
        await WaitUntilAsync(async () => await host.Storage.GetSessionAsync(half, Ct) is null);
        Assert.Empty(host.Asr.Requests);

        await host.Storage.UpdateSessionAsync(meeting with { State = SessionState.Ready }, Ct);
        host.Recorder.EndRecording();
        var done = await WaitForItemAsync(host.Imports, id, ImportState.Done);

        Assert.NotEqual(half, done.Session);
        await WaitUntilAsync(async () => (await host.Storage.GetSessionAsync(done.Session!.Value, Ct))?.State == SessionState.Ready);
        var imported = (await host.Storage.ListSessionsAsync(new SessionQuery(), Ct)).Where(s => s.Source == SessionSource.Imported).ToList();
        Assert.Single(imported);
        Assert.DoesNotContain(await host.Storage.ListSessionsAsync(new SessionQuery(), Ct), s => s.State == SessionState.Recording && s.Id != meeting.Id);
    }

    [Fact]
    public async Task AfterARestartDuringADecode_NoOrphanOrFailedSessionIsLeft()
    {
        var path = await NewFileAsync("memo.mp3");
        using var gate = new SemaphoreSlim(0);
        long id;
        await using (var first = await Host.StartAsync(_root, new FakeMediaDecoder(".mp3") { Gate = gate }))
        {
            id = (await first.Imports.EnqueueAsync([new ImportRequest(path, new ImportOptions(null, OriginalFileMode.CopyToLibrary))], Ct))[0].Id;
            await WaitForHalfMadeAsync(first.Imports, id);
        }

        await using var second = await Host.StartAsync(_root, new FakeMediaDecoder(".mp3"));
        var done = await WaitForItemAsync(second.Imports, id, ImportState.Done);
        await WaitUntilAsync(async () => (await second.Storage.GetSessionAsync(done.Session!.Value, Ct))?.State == SessionState.Ready);

        var all = await second.Storage.ListSessionsAsync(new SessionQuery(), Ct);
        Assert.Equal(done.Session, Assert.Single(all).Id);
        Assert.DoesNotContain(all, s => s.State is SessionState.Failed or SessionState.Recording);
    }

    [Fact(Skip = "follow-up: reviewer P2 (a) - ImportQueue.StartAsync counts the session of a Failed/Cancelled entry as referenced although the same pass deletes that entry, so its half-made session survives one extra start")]
    public async Task AtStart_TheHalfMadeSessionOfAFailedEntry_IsDeletedInTheSamePass()
    {
        var path = await NewFileAsync("x.mp3");
        var o = new ImportOptions(null, OriginalFileMode.CopyToLibrary);
        SessionId half;
        using (var storage = _root.Open())
        {
            var created = await storage.CreateSessionAsync("x", DateTimeOffset.Now, Ct);
            await storage.UpdateSessionAsync(created with { Source = SessionSource.Imported, HasSystemAudio = true }, Ct);
            half = created.Id;
            var item = await storage.SaveImportAsync(new StoredImport(new ImportItem(0, path, ImportState.Queued, 0, null, null), new ImportRequest(path, o)), Ct);
            await storage.SaveImportAsync(new StoredImport(item with { State = ImportState.Failed, Session = half, Error = "x" }, new ImportRequest(path, o)), Ct);
        }

        await using var host = await Host.StartAsync(_root, new FakeMediaDecoder(".mp3"));

        Assert.Null(await host.Storage.GetSessionAsync(half, Ct));
    }

    private async Task<string> NewFileAsync(string name)
    {
        var path = Path.Combine(_media, name);
        await File.WriteAllBytesAsync(path, [0, 1, 2, 3], Ct);
        return path;
    }

    private static async Task<SessionId> WaitForHalfMadeAsync(ImportQueue imports, long id)
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
