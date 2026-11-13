using Murmur.Core.Audio;
using Murmur.Core.Import;
using Murmur.Core.Jobs;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;
using Murmur.Core.Transcription;

namespace Murmur.Core.Tests.Import;

public sealed class ImportQueueTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly ImportOptions Copy = new(null, OriginalFileMode.CopyToLibrary);

    private static readonly ImportOptions InPlace = new(null, OriginalFileMode.LeaveInPlace);

    private readonly TempDirectory _dir = new();
    private readonly InMemorySessionStore _sessions;
    private readonly InMemoryTranscriptStore _transcripts = new();
    private readonly InMemoryImportStore _store = new();
    private readonly FakeAsr _asr = new();
    private readonly FakeRecorder _recorder = new();
    private readonly JobQueue _queue = new();
    private SessionProcessor? _processor;
    private ImportQueue? _imports;

    public ImportQueueTests()
    {
        _sessions = new InMemorySessionStore(_dir.File("sessions"));
        _store.CascadeFrom(_sessions);
        _asr.Script("system.wav", (0.0, 1.0, "hello from the file"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        _imports?.Dispose();
        _processor?.Dispose();
        await _queue.DisposeAsync();
        _dir.Dispose();
    }

    // ---- session creation and the pipeline ----------------------------------------------------------------------------

    [Fact]
    public async Task AWavFile_BecomesAnImportedSession_WhoseTrackIsTheResampledFile_AndGoesThroughThePipelineToReady()
    {
        var interleaved = new float[48_000 * 2 * 2];
        for (var i = 0; i < interleaved.Length; i++)
        {
            interleaved[i] = 0.4f * MathF.Sin(i * 0.01f);
        }

        var path = AnyWav.Float32(_dir.File("Team_sync_call.wav"), 48_000, 2, interleaved);
        var expected = _dir.File("expected.wav");
        await new WavMediaDecoder().DecodeAudioAsync(path, expected, null, Ct);
        var imports = await StartAsync(new CompositeMediaDecoder(new WavMediaDecoder()));

        var queued = await imports.EnqueueAsync([new ImportRequest(path, Copy)], Ct);

        var item = Assert.Single(queued);
        Assert.Equal(ImportState.Queued, item.State);
        Assert.True(item.Id > 0);
        var done = await WaitForItemAsync(imports, item.Id, ImportState.Done);
        Assert.Equal(1, done.Progress);
        Assert.Null(done.Error);
        var id = done.Session!.Value;
        var session = await WaitForSessionStateAsync(id, SessionState.Ready);

        Assert.Equal("Team sync call", session.Title);
        Assert.Equal(SessionSource.Imported, session.Source);
        Assert.Equal(ImportQueue.StartedAtOf(path), session.StartedAt);
        Assert.Equal(TimeSpan.FromSeconds(2), session.Duration);
        Assert.True(session.HasSystemAudio);
        Assert.False(session.HasMicrophone);
        Assert.Empty(session.ScreenSegments);
        Assert.Null(session.Error);

        var files = _sessions.GetFiles(id);
        Assert.Equal(WavReader.ReadAll(expected).Samples, WavReader.ReadAll(files.SystemAudio).Samples); // the decoder's output, untouched
        Assert.False(File.Exists(files.MicrophoneAudio));

        // The same path a stopped recording takes: Recorded, then the processor's Transcribing -> Transcribed -> Ready.
        var states = _sessions.Updates.Where(u => u.Id == id).Select(u => u.State).ToList();
        Assert.Equal([SessionState.Recording, SessionState.Recorded, SessionState.Transcribing, SessionState.Transcribed, SessionState.Ready], states);
        var request = Assert.Single(_asr.Requests);
        Assert.Equal((files.SystemAudio, Speaker.Others), (request.AudioPath, request.Speaker));
        var transcript = await _transcripts.GetTranscriptAsync(id, Ct);
        Assert.Equal("hello from the file", Assert.Single(transcript!.Segments).Text);
        Assert.Equal(SessionProcessingStage.Completed, _processor!.GetStatus(id)!.Stage);
    }

    [Fact]
    public async Task TheSpokenLanguage_IsPassedToTheAsr_AutoDetectPassesNone()
    {
        var imports = await StartAsync(new FakeMediaDecoder(".mp3"));
        var ukrainian = await NewFileAsync("lecture.mp3");
        var auto = await NewFileAsync("memo.mp3");

        var items = await imports.EnqueueAsync([new ImportRequest(ukrainian, Copy with { Language = "uk" }), new ImportRequest(auto, Copy)], Ct);
        var first = await WaitForItemAsync(imports, items[0].Id, ImportState.Done);
        var second = await WaitForItemAsync(imports, items[1].Id, ImportState.Done);
        await WaitForSessionStateAsync(first.Session!.Value, SessionState.Ready);
        await WaitForSessionStateAsync(second.Session!.Value, SessionState.Ready);

        Assert.Equal(["uk", null], _asr.Requests.Select(r => r.Language));
        Assert.Equal("uk", imports.LanguageOf(first.Session!.Value));
        Assert.Null(imports.LanguageOf(second.Session!.Value));
    }

    [Fact]
    public async Task Changed_IsRaised_OffTheCallersThread()
    {
        var imports = await StartAsync(new FakeMediaDecoder(".mp3"));
        var path = await NewFileAsync("a.mp3");
        var caller = Environment.CurrentManagedThreadId;
        var inCall = true;
        var raised = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        imports.Changed += () => raised.TrySetResult(Volatile.Read(ref inCall) && Environment.CurrentManagedThreadId == caller);

        var call = imports.EnqueueAsync([new ImportRequest(path, Copy)], Ct);
        Volatile.Write(ref inCall, false);
        await call;

        Assert.False(await raised.Task.WaitAsync(Timeout, Ct)); // never inside the caller's call
    }

    // ---- rejected and failed files ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("archive.zip")]
    [InlineData("no-extension")]
    public async Task AnUnsupportedExtension_IsRejected_BeforeAnythingIsWritten(string name)
    {
        var decoder = new FakeMediaDecoder(".mp3", ".wav");
        var imports = await StartAsync(decoder);
        var path = await NewFileAsync(name);

        var items = await imports.EnqueueAsync([new ImportRequest(path, Copy)], Ct);

        var item = Assert.Single(items);
        Assert.Equal(ImportState.Failed, item.State);
        Assert.Equal(ImportQueue.UnsupportedCode, item.Error);
        Assert.True(item.Id < 0);
        Assert.Null(item.Session);
        Assert.Equal(item, Assert.Single(imports.Items));
        Assert.Equal(0, _store.Saves);
        Assert.Empty(_store.Rows);
        Assert.Equal(0, decoder.Probes);
        Assert.Empty(await _sessions.ListSessionsAsync(new SessionQuery(), Ct));
        Assert.False(Directory.Exists(_sessions.Root) && Directory.EnumerateFileSystemEntries(_sessions.Root).Any());
    }

    [Fact]
    public async Task SupportedExtensions_AreTheDecoders()
    {
        var imports = await StartAsync(new CompositeMediaDecoder(new FakeMediaDecoder(".mp3"), new WavMediaDecoder()));

        Assert.True(imports.SupportedExtensions.SetEquals([".mp3", ".wav"]));
    }

    [Fact]
    public async Task AFailedFile_DoesNotStopTheQueue_AndLeavesNoSession()
    {
        var decoder = new FakeMediaDecoder(".mp3");
        decoder.FailOn.Add("broken.mp3");
        var imports = await StartAsync(decoder);
        var broken = await NewFileAsync("broken.mp3");
        var gone = _dir.File("gone.mp3");
        var good = await NewFileAsync("good.mp3");

        var items = await imports.EnqueueAsync([new ImportRequest(broken, Copy), new ImportRequest(gone, Copy), new ImportRequest(good, Copy)], Ct);

        var failed = await WaitForItemAsync(imports, items[0].Id, ImportState.Failed);
        var missing = await WaitForItemAsync(imports, items[1].Id, ImportState.Failed);
        var done = await WaitForItemAsync(imports, items[2].Id, ImportState.Done);
        Assert.Equal(SessionNotes.Format(ImportQueue.DamagedCode, "damaged"), failed.Error);
        Assert.Equal(ImportQueue.MissingCode, missing.Error);
        Assert.Null(failed.Session);
        await WaitForSessionStateAsync(done.Session!.Value, SessionState.Ready);
        Assert.Equal([done.Session!.Value], (await _sessions.ListSessionsAsync(new SessionQuery(), Ct)).Select(s => s.Id));

        // Failed entries are not restarted after a restart.
        Assert.Equal([items[2].Id], _store.Rows.Select(r => r.Item.Id));
    }

    [Fact]
    public async Task AFormatTheDecoderCannotOpen_FailsAsUnsupported_AndDeletesTheSession()
    {
        var decoder = new FakeMediaDecoder(".mkv") { ProbeError = () => new NotSupportedException("no codec") };
        decoder.FailOn.Add("clip.mkv");
        var imports = await StartAsync(decoder);

        var items = await imports.EnqueueAsync([new ImportRequest(await NewFileAsync("clip.mkv"), Copy)], Ct);

        var failed = await WaitForItemAsync(imports, items[0].Id, ImportState.Failed);
        Assert.Equal(ImportQueue.UnsupportedCode, failed.Error);
        Assert.Empty(await _sessions.ListSessionsAsync(new SessionQuery(), Ct));
    }

    // ---- the original file --------------------------------------------------------------------------------------------

    [Fact]
    public async Task CopyToLibrary_CopiesTheOriginalIntoTheSessionFolder_DeletingTheSessionDeletesTheCopy()
    {
        var imports = await StartAsync(new FakeMediaDecoder(".m4a"));
        var path = await NewFileAsync("Voice memo.M4A", [1, 2, 3, 4, 5]);

        var items = await imports.EnqueueAsync([new ImportRequest(path, Copy)], Ct);
        var done = await WaitForItemAsync(imports, items[0].Id, ImportState.Done);
        var id = done.Session!.Value;
        var session = await WaitForSessionStateAsync(id, SessionState.Ready);

        var copy = Path.Combine(_sessions.GetFiles(id).Directory, "original.m4a");
        Assert.Equal(copy, session.OriginalPath);
        Assert.Equal([1, 2, 3, 4, 5], await File.ReadAllBytesAsync(copy, Ct));

        await _sessions.DeleteSessionAsync(id, Ct);

        Assert.False(File.Exists(copy));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task LeaveInPlace_StoresOnlyThePath_DeletingTheSessionNeverTouchesTheFile()
    {
        var decoder = new FakeMediaDecoder(".m4a");
        var imports = await StartAsync(decoder);
        var path = await NewFileAsync("Voice memo.m4a", [1, 2, 3]);

        var items = await imports.EnqueueAsync([new ImportRequest(path, InPlace)], Ct);
        var done = await WaitForItemAsync(imports, items[0].Id, ImportState.Done);
        var id = done.Session!.Value;
        var session = await WaitForSessionStateAsync(id, SessionState.Ready);

        Assert.Equal(path, session.OriginalPath);
        Assert.Equal([path], decoder.Decoded);
        Assert.Equal(["peaks.bin", "system.wav"], Directory.EnumerateFiles(_sessions.GetFiles(id).Directory).Select(Path.GetFileName).Order());

        await _sessions.DeleteSessionAsync(id, Ct);

        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(path, Ct));
    }

    // ---- video --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(OriginalFileMode.CopyToLibrary)]
    [InlineData(OriginalFileMode.LeaveInPlace)]
    public async Task AnMp4_IsAlsoTheSessionsScreenSegmentZero(OriginalFileMode mode)
    {
        if (mode == OriginalFileMode.LeaveInPlace)
        {
            SkipUnlessSymbolicLinksCanBeCreated(); // Left in place, the video is only ever a symbolic link.
        }

        var decoder = new FakeMediaDecoder(".mp4", ".mov") { Kind = MediaKind.Video, Duration = TimeSpan.FromSeconds(3) };
        var imports = await StartAsync(decoder);
        var path = await NewFileAsync("demo.mp4", [9, 8, 7]);

        var items = await imports.EnqueueAsync([new ImportRequest(path, new ImportOptions(null, mode))], Ct);
        var done = await WaitForItemAsync(imports, items[0].Id, ImportState.Done);
        var id = done.Session!.Value;
        var session = await WaitForSessionStateAsync(id, SessionState.Ready);

        Assert.Equal([new ScreenSegment(0, TimeSpan.Zero, TimeSpan.FromSeconds(3))], session.ScreenSegments);
        var segment = _sessions.GetFiles(id).ScreenSegment(0);
        Assert.Equal("screen-000.mp4", Path.GetFileName(segment));
        Assert.Equal([9, 8, 7], await File.ReadAllBytesAsync(segment, Ct));

        await _sessions.DeleteSessionAsync(id, Ct);

        Assert.Equal([9, 8, 7], await File.ReadAllBytesAsync(path, Ct));
    }

    [Fact]
    public async Task AnotherVideoContainer_IsAudioOnly()
    {
        var decoder = new FakeMediaDecoder(".mov") { Kind = MediaKind.Video };
        var imports = await StartAsync(decoder);

        var items = await imports.EnqueueAsync([new ImportRequest(await NewFileAsync("clip.mov"), Copy)], Ct);
        var done = await WaitForItemAsync(imports, items[0].Id, ImportState.Done);
        var session = await WaitForSessionStateAsync(done.Session!.Value, SessionState.Ready);

        Assert.Empty(session.ScreenSegments);
        Assert.False(File.Exists(_sessions.GetFiles(session.Id).ScreenSegment(0)));
    }

    // ---- cancel -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_StopsTheRunningDecode_DeletesTheHalfMadeSession_AndTheNextFileRuns()
    {
        using var gate = new SemaphoreSlim(0);
        var decoder = new FakeMediaDecoder(".mp3") { Gate = gate };
        var imports = await StartAsync(decoder);
        var items = await imports.EnqueueAsync([new ImportRequest(await NewFileAsync("long.mp3"), Copy), new ImportRequest(await NewFileAsync("next.mp3"), Copy)], Ct);
        await decoder.Started.Task.WaitAsync(Timeout, Ct);
        var running = await WaitForItemAsync(imports, items[0].Id, ImportState.Decoding);
        var half = running.Session!.Value;
        Assert.True(Directory.Exists(_sessions.GetFiles(half).Directory));
        decoder.ResetStarted();

        await imports.CancelAsync(items[0].Id, Ct);

        var cancelled = imports.Items.Single(i => i.Id == items[0].Id);
        Assert.Equal(ImportState.Cancelled, cancelled.State);
        Assert.Null(cancelled.Session);
        Assert.Null(_sessions.Get(half));
        Assert.False(Directory.Exists(_sessions.GetFiles(half).Directory));
        Assert.DoesNotContain(_store.Rows, r => r.Item.Id == items[0].Id);

        await decoder.Started.Task.WaitAsync(Timeout, Ct);
        gate.Release();
        var next = await WaitForItemAsync(imports, items[1].Id, ImportState.Done);
        await WaitForSessionStateAsync(next.Session!.Value, SessionState.Ready);
        Assert.Single(await _sessions.ListSessionsAsync(new SessionQuery(), Ct));

        // A finished item is dismissed by a second cancel.
        await imports.CancelAsync(items[0].Id, Ct);
        Assert.DoesNotContain(imports.Items, i => i.Id == items[0].Id);
    }

    [Fact]
    public async Task Cancel_AQueuedImport_RemovesIt()
    {
        using var gate = new SemaphoreSlim(0);
        var decoder = new FakeMediaDecoder(".mp3") { Gate = gate };
        var imports = await StartAsync(decoder);
        var items = await imports.EnqueueAsync([new ImportRequest(await NewFileAsync("first.mp3"), Copy), new ImportRequest(await NewFileAsync("waiting.mp3"), Copy)], Ct);
        await decoder.Started.Task.WaitAsync(Timeout, Ct);

        await imports.CancelAsync(items[1].Id, Ct);

        Assert.DoesNotContain(imports.Items, i => i.Id == items[1].Id);
        Assert.DoesNotContain(_store.Rows, r => r.Item.Id == items[1].Id);
        gate.Release();
        await WaitForItemAsync(imports, items[0].Id, ImportState.Done);
        await WaitUntilAsync(() => _queue.Status is { Pending: 0, Running: 0 });
        Assert.Single(decoder.Decoded);
    }

    // ---- restart ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AfterARestart_QueuedAndDecodingImportsStartAgain_TheHalfMadeSessionIsDeletedFirst()
    {
        var waiting = await NewFileAsync("waiting.mp3");
        var interrupted = await NewFileAsync("interrupted.mp3");
        var half = await _sessions.CreateSessionAsync("interrupted", DateTimeOffset.Now, Ct); // left Recording by the dead process
        _store.Seed(new StoredImport(new ImportItem(1, interrupted, ImportState.Decoding, 0.4, half.Id, null), new ImportRequest(interrupted, Copy with { Language = "de" })));
        _store.Seed(new StoredImport(new ImportItem(2, waiting, ImportState.Queued, 0, null, null), new ImportRequest(waiting, Copy)));
        var decoder = new FakeMediaDecoder(".mp3");

        var imports = await StartAsync(decoder);

        var first = await WaitForItemAsync(imports, 1, ImportState.Done);
        var second = await WaitForItemAsync(imports, 2, ImportState.Done);
        Assert.Null(_sessions.Get(half.Id));
        Assert.NotEqual(half.Id, first.Session);
        await WaitForSessionStateAsync(first.Session!.Value, SessionState.Ready);
        await WaitForSessionStateAsync(second.Session!.Value, SessionState.Ready);
        Assert.Equal(
            [Path.Combine(_sessions.GetFiles(first.Session!.Value).Directory, "original.mp3"), Path.Combine(_sessions.GetFiles(second.Session!.Value).Directory, "original.mp3")],
            decoder.Decoded);
        Assert.Equal(["de", null], _asr.Requests.Select(r => r.Language));
        Assert.All(_store.Rows, r => Assert.Equal(ImportState.Done, r.Item.State));
    }

    [Fact]
    public async Task AfterARestart_AnImportWhoseDecodeHadFinished_IsHandedOffWithoutDecodingAgain()
    {
        var path = await NewFileAsync("finished.mp3");
        var session = await _sessions.CreateSessionAsync("finished", DateTimeOffset.Now, Ct);
        TestWav.Square(_sessions.GetFiles(session.Id).SystemAudio, TimeSpan.FromSeconds(1), 0.5f);
        await _sessions.UpdateSessionAsync(session with { State = SessionState.Recorded, HasSystemAudio = true, Source = SessionSource.Imported }, Ct);
        _store.Seed(new StoredImport(new ImportItem(5, path, ImportState.Decoding, 1, session.Id, null), new ImportRequest(path, Copy)));
        var decoder = new FakeMediaDecoder(".mp3");

        var imports = await StartAsync(decoder);

        var done = await WaitForItemAsync(imports, 5, ImportState.Done);
        Assert.Equal(session.Id, done.Session);
        await WaitForSessionStateAsync(session.Id, SessionState.Ready);
        Assert.Empty(decoder.Decoded);
    }

    [Fact]
    public async Task AfterARestart_TheLanguageOfADoneImport_IsStillKnown_AndNothingIsImportedAgain()
    {
        var path = await NewFileAsync("done.mp3");
        var session = SessionId.New();
        _store.Seed(new StoredImport(new ImportItem(3, path, ImportState.Done, 1, session, null), new ImportRequest(path, Copy with { Language = "fr" })));
        var decoder = new FakeMediaDecoder(".mp3");

        var imports = await StartAsync(decoder);

        Assert.Equal("fr", imports.LanguageOf(session));
        Assert.Empty(imports.Items);
        Assert.Equal(0, decoder.Probes);
    }

    [Fact]
    public async Task NothingIsImported_BeforeStart()
    {
        var decoder = new FakeMediaDecoder(".mp3");
        var imports = Create(decoder);

        var items = await imports.EnqueueAsync([new ImportRequest(await NewFileAsync("early.mp3"), Copy)], Ct);
        await Task.Delay(100, Ct);
        Assert.Equal(0, decoder.Probes);

        await imports.StartAsync(Ct);
        await WaitForItemAsync(imports, items[0].Id, ImportState.Done);
    }

    // ---- recording ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Imports_WaitWhileARecordingRuns_AndStartWhenItStops()
    {
        var decoder = new FakeMediaDecoder(".mp3");
        var imports = await StartAsync(decoder);
        var meeting = await _sessions.CreateSessionAsync("Meeting", DateTimeOffset.Now, Ct);
        _recorder.BeginRecording(meeting);

        var items = await imports.EnqueueAsync([new ImportRequest(await NewFileAsync("podcast.mp3"), Copy)], Ct);
        await Task.Delay(200, Ct);

        Assert.Equal(0, decoder.Probes);
        Assert.Equal(ImportState.Queued, imports.Items[0].State);
        Assert.Empty(_asr.Requests);

        _recorder.EndRecording();
        var done = await WaitForItemAsync(imports, items[0].Id, ImportState.Done);
        await WaitForSessionStateAsync(done.Session!.Value, SessionState.Ready);
    }

    [Fact]
    public async Task ARecordingThatStartsDuringADecode_PreEmptsIt_TheImportStartsAgainAfterIt()
    {
        using var gate = new SemaphoreSlim(0);
        var decoder = new FakeMediaDecoder(".mp3") { Gate = gate };
        var imports = await StartAsync(decoder);
        var items = await imports.EnqueueAsync([new ImportRequest(await NewFileAsync("podcast.mp3"), Copy)], Ct);
        await decoder.Started.Task.WaitAsync(Timeout, Ct);
        var half = (await WaitForItemAsync(imports, items[0].Id, ImportState.Decoding)).Session!.Value;
        decoder.ResetStarted();
        decoder.Gate = null;

        var meeting = await _sessions.CreateSessionAsync("Meeting", DateTimeOffset.Now, Ct);
        _recorder.BeginRecording(meeting);

        var waiting = await WaitForItemAsync(imports, items[0].Id, ImportState.Queued);
        Assert.Null(waiting.Session);
        Assert.Null(_sessions.Get(half));
        Assert.Equal(ImportState.Queued, _store.Rows.Single().Item.State);

        _recorder.EndRecording();
        var done = await WaitForItemAsync(imports, items[0].Id, ImportState.Done);
        Assert.NotEqual(half, done.Session);
        await WaitForSessionStateAsync(done.Session!.Value, SessionState.Ready);
        Assert.Equal(2, decoder.Decoded.Count);
    }

    // ---- review fixes: persistence, links, metadata while decoding, clean-ups ----------------------------------------

    [Fact]
    public async Task WhileDecoding_TheSessionIsAlreadyMarkedAsAnImport_WithItsFinalOriginalPath()
    {
        using var gate = new SemaphoreSlim(0);
        var decoder = new FakeMediaDecoder(".mp3") { Gate = gate };
        var imports = await StartAsync(decoder);
        var items = await imports.EnqueueAsync([new ImportRequest(await NewFileAsync("talk.mp3"), Copy)], Ct);
        await decoder.Started.Task.WaitAsync(Timeout, Ct);
        var id = (await WaitForItemAsync(imports, items[0].Id, ImportState.Decoding)).Session!.Value;

        var session = _sessions.Get(id)!;

        Assert.Equal(SessionState.Recording, session.State);
        Assert.Equal(SessionSource.Imported, session.Source);
        Assert.Equal(Path.Combine(_sessions.GetFiles(id).Directory, "original.mp3"), session.OriginalPath);
        Assert.True(session.HasSystemAudio);
        Assert.False(session.HasMicrophone);
        Assert.Equal(id, _store.Rows.Single().Item.Session);
        gate.Release();
        await WaitForItemAsync(imports, items[0].Id, ImportState.Done);
    }

    [Fact]
    public async Task APreEmptedImport_KeepsItsStoredEntry_ThoughDeletingTheHalfMadeSessionCascades()
    {
        using var gate = new SemaphoreSlim(0);
        var decoder = new FakeMediaDecoder(".mp3") { Gate = gate };
        var imports = await StartAsync(decoder);
        var items = await imports.EnqueueAsync([new ImportRequest(await NewFileAsync("podcast.mp3"), Copy)], Ct);
        await decoder.Started.Task.WaitAsync(Timeout, Ct);
        var half = (await WaitForItemAsync(imports, items[0].Id, ImportState.Decoding)).Session!.Value;

        _recorder.BeginRecording(await _sessions.CreateSessionAsync("Meeting", DateTimeOffset.Now, Ct));
        await WaitForItemAsync(imports, items[0].Id, ImportState.Queued);
        await WaitUntilAsync(() => _sessions.Get(half) is null);

        var row = Assert.Single(_store.Rows);
        Assert.Equal((items[0].Id, ImportState.Queued, (SessionId?)null), (row.Item.Id, row.Item.State, row.Item.Session));
    }

    [Fact]
    public async Task WhenTheLeftoverCannotBeDeleted_TheImportStaysQueued_StillPointingAtIt()
    {
        var path = await NewFileAsync("held.mp3");
        var half = await _sessions.CreateSessionAsync("held", DateTimeOffset.Now, Ct);
        _store.Seed(new StoredImport(new ImportItem(4, path, ImportState.Decoding, 0.3, half.Id, null), new ImportRequest(path, Copy)));
        var attempts = 0;
        _sessions.FailDelete = id =>
        {
            if (id != half.Id)
            {
                return null;
            }

            Interlocked.Increment(ref attempts);
            return new IOException("a file is held open");
        };
        var decoder = new FakeMediaDecoder(".mp3");

        var imports = await StartAsync(decoder);
        await WaitUntilAsync(() => Volatile.Read(ref attempts) == 1 && _queue.Status is { Pending: 0, Running: 0 });
        await Task.Delay(200, Ct);

        Assert.Equal(1, Volatile.Read(ref attempts)); // not retried in a loop: it waits for the next start

        var item = Assert.Single(imports.Items);
        Assert.Equal(ImportState.Queued, item.State);
        Assert.Equal(half.Id, item.Session);
        Assert.Equal(half.Id, _store.Rows.Single().Item.Session);
        Assert.NotNull(_sessions.Get(half.Id));
        Assert.Equal(0, decoder.Probes);
    }

    [Fact]
    public async Task AtStart_HalfMadeImportedSessionsNoEntryRefersTo_AreDeleted_OthersAreKept()
    {
        async Task<SessionRecord> SeedAsync(string title, SessionState state, SessionSource source, bool track = false)
        {
            var created = await _sessions.CreateSessionAsync(title, DateTimeOffset.Now, Ct);
            if (track)
            {
                TestWav.Square(_sessions.GetFiles(created.Id).SystemAudio, TimeSpan.FromSeconds(1), 0.5f);
            }

            var record = created with { State = state, Source = source };
            await _sessions.UpdateSessionAsync(record, Ct);
            return record;
        }

        var orphan = await SeedAsync("orphan", SessionState.Recording, SessionSource.Imported);
        var failedHalf = await SeedAsync("failed half", SessionState.Failed, SessionSource.Imported);
        var failedTranscription = await SeedAsync("failed transcription", SessionState.Failed, SessionSource.Imported, track: true);
        var ready = await SeedAsync("ready", SessionState.Ready, SessionSource.Imported, track: true);
        var deadRecording = await SeedAsync("dead recording", SessionState.Recording, SessionSource.Recorded);
        var referenced = await SeedAsync("referenced", SessionState.Recorded, SessionSource.Imported, track: true);
        var path = await NewFileAsync("referenced.mp3");
        _store.Seed(new StoredImport(new ImportItem(9, path, ImportState.Done, 1, referenced.Id, null), new ImportRequest(path, Copy)));

        await StartAsync(new FakeMediaDecoder(".mp3"));

        Assert.Null(_sessions.Get(orphan.Id));
        Assert.Null(_sessions.Get(failedHalf.Id));
        Assert.NotNull(_sessions.Get(failedTranscription.Id));
        Assert.NotNull(_sessions.Get(ready.Id));
        Assert.NotNull(_sessions.Get(deadRecording.Id));
        Assert.NotNull(_sessions.Get(referenced.Id));
    }

    [Fact]
    public async Task Cancel_WithACancelledCallerToken_StillDeletesTheHalfMadeSession()
    {
        using var gate = new SemaphoreSlim(0);
        var decoder = new FakeMediaDecoder(".mp3") { Gate = gate };
        var imports = await StartAsync(decoder);
        var items = await imports.EnqueueAsync([new ImportRequest(await NewFileAsync("long.mp3"), Copy)], Ct);
        await decoder.Started.Task.WaitAsync(Timeout, Ct);
        var half = (await WaitForItemAsync(imports, items[0].Id, ImportState.Decoding)).Session!.Value;

        await imports.CancelAsync(items[0].Id, new CancellationToken(canceled: true));

        Assert.Equal(ImportState.Cancelled, imports.Items.Single().State);
        Assert.Null(_sessions.Get(half));
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task TheUserDeletingTheHalfMadeSession_CancelsThatImport_AndTheQueueGoesOn()
    {
        using var gate = new SemaphoreSlim(0);
        var decoder = new FakeMediaDecoder(".mp3") { Gate = gate };
        var imports = await StartAsync(decoder);
        var items = await imports.EnqueueAsync([new ImportRequest(await NewFileAsync("first.mp3"), Copy), new ImportRequest(await NewFileAsync("second.mp3"), Copy)], Ct);
        await decoder.Started.Task.WaitAsync(Timeout, Ct);
        var half = (await WaitForItemAsync(imports, items[0].Id, ImportState.Decoding)).Session!.Value;
        decoder.Gate = null;

        await _sessions.DeleteSessionAsync(half, Ct);
        gate.Release();

        var cancelled = await WaitForItemAsync(imports, items[0].Id, ImportState.Cancelled);
        Assert.Null(cancelled.Error);
        Assert.Null(cancelled.Session);
        var next = await WaitForItemAsync(imports, items[1].Id, ImportState.Done);
        Assert.Equal([items[1].Id], _store.Rows.Select(r => r.Item.Id));
        await WaitForSessionStateAsync(next.Session!.Value, SessionState.Ready);
    }

    [Fact]
    public async Task ARequestThatCannotBeStored_FailsAlone_TheOthersAreQueued()
    {
        var imports = await StartAsync(new FakeMediaDecoder(".mp3"));
        _store.FailSave = import => import.Item.Id == 0 && import.Request.Path.EndsWith("bad.mp3", StringComparison.Ordinal) ? new IOException("disk full") : null;

        var items = await imports.EnqueueAsync([new ImportRequest(await NewFileAsync("bad.mp3"), Copy), new ImportRequest(await NewFileAsync("good.mp3"), Copy)], Ct);

        Assert.Equal(ImportState.Failed, items[0].State);
        Assert.Equal(SessionNotes.Format(ImportQueue.FailedCode, "disk full"), items[0].Error);
        Assert.True(items[0].Id < 0);
        await WaitForItemAsync(imports, items[1].Id, ImportState.Done);
    }

    [Fact]
    public async Task Changed_IsNeverRaisedConcurrently()
    {
        var imports = await StartAsync(new FakeMediaDecoder(".mp3"));
        var inside = 0;
        var overlaps = 0;
        var calls = 0;
        imports.Changed += () =>
        {
            if (Interlocked.Increment(ref inside) > 1)
            {
                Interlocked.Increment(ref overlaps);
            }

            Interlocked.Increment(ref calls);
            Thread.Sleep(2);
            Interlocked.Decrement(ref inside);
        };

        var requests = new List<ImportRequest>();
        for (var i = 0; i < 6; i++)
        {
            requests.Add(new ImportRequest(await NewFileAsync($"f{i}.mp3"), Copy));
        }

        var items = await imports.EnqueueAsync(requests, Ct);
        foreach (var item in items)
        {
            await WaitForItemAsync(imports, item.Id, ImportState.Done);
        }

        Assert.True(Volatile.Read(ref calls) > 1);
        Assert.Equal(0, Volatile.Read(ref overlaps));
    }

    [Fact]
    public async Task CopyToLibrary_FallsBackFromAHardLink_ToASymbolicLink()
    {
        SkipUnlessSymbolicLinksCanBeCreated();
        var decoder = new FakeMediaDecoder(".mp4") { Kind = MediaKind.Video };

        // No hard links on this file system: a symbolic link to the copy.
        var imports = await StartAsync(decoder);
        imports.CreateHardLink = static (_, _) => throw new IOException("no hard links here");
        var linked = await ImportVideoAsync(imports, "a.mp4", Copy);
        var linkedSegment = _sessions.GetFiles(linked.Id).ScreenSegment(0);
        Assert.Equal(Path.Combine(_sessions.GetFiles(linked.Id).Directory, "original.mp4"), new FileInfo(linkedSegment).LinkTarget);
    }

    [Fact]
    public async Task CopyToLibrary_WithNeitherKindOfLink_FallsBackToACopy()
    {
        var decoder = new FakeMediaDecoder(".mp4") { Kind = MediaKind.Video };
        var imports = await StartAsync(decoder);
        imports.CreateHardLink = static (_, _) => throw new IOException("no hard links here");
        imports.CreateSymbolicLink = static (_, _) => throw new UnauthorizedAccessException("no symbolic links either");

        var copied = await ImportVideoAsync(imports, "b.mp4", Copy);

        var copiedSegment = _sessions.GetFiles(copied.Id).ScreenSegment(0);
        Assert.Null(new FileInfo(copiedSegment).LinkTarget);
        Assert.Equal([9, 8, 7], await File.ReadAllBytesAsync(copiedSegment, Ct));
        Assert.Single(copied.ScreenSegments);
    }

    [Fact]
    public async Task CopyToLibrary_WhenWindowsRefusesTheLinks_StillCopiesTheVideo()
    {
        // What File.CreateSymbolicLink throws on Windows without Developer Mode or admin rights is an IOException.
        var decoder = new FakeMediaDecoder(".mp4") { Kind = MediaKind.Video };
        var imports = await StartAsync(decoder);
        imports.CreateHardLink = static (_, _) => throw new IOException("A required privilege is not held by the client.");
        imports.CreateSymbolicLink = static (_, _) => throw new IOException("A required privilege is not held by the client.");

        var copied = await ImportVideoAsync(imports, "c.mp4", Copy);

        Assert.Equal([9, 8, 7], await File.ReadAllBytesAsync(_sessions.GetFiles(copied.Id).ScreenSegment(0), Ct));
        Assert.Single(copied.ScreenSegments);
    }

    [Fact]
    public async Task LeaveInPlace_NeverHardLinksTheOriginal_ASymbolicLink()
    {
        SkipUnlessSymbolicLinksCanBeCreated();
        var decoder = new FakeMediaDecoder(".mp4") { Kind = MediaKind.Video };
        var imports = await StartAsync(decoder);
        var hardLinks = 0;
        imports.CreateHardLink = (_, _) => Interlocked.Increment(ref hardLinks);

        var linked = await ImportVideoAsync(imports, "a.mp4", InPlace);

        Assert.Equal(_dir.File("a.mp4"), new FileInfo(_sessions.GetFiles(linked.Id).ScreenSegment(0)).LinkTarget);
        Assert.Single(linked.ScreenSegments);
        Assert.Equal(0, hardLinks);
    }

    [Fact]
    public async Task LeaveInPlace_NeverHardLinksTheOriginal_WithoutASymbolicLinkItIsAudioOnly()
    {
        var decoder = new FakeMediaDecoder(".mp4") { Kind = MediaKind.Video };
        var imports = await StartAsync(decoder);
        var hardLinks = 0;
        imports.CreateHardLink = (_, _) => Interlocked.Increment(ref hardLinks);
        imports.CreateSymbolicLink = static (_, _) => throw new UnauthorizedAccessException("no symbolic links");

        var audioOnly = await ImportVideoAsync(imports, "b.mp4", InPlace);

        Assert.Empty(audioOnly.ScreenSegments);
        Assert.False(File.Exists(_sessions.GetFiles(audioOnly.Id).ScreenSegment(0)));
        Assert.Equal(0, hardLinks);
    }

    // ---- metadata rules -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/x/Team_sync_2026.mp3", "Team sync 2026")]
    [InlineData("/x/Voice memo.m4a", "Voice memo")]
    [InlineData("/x/_.wav", "_.wav")]
    [InlineData("/x/archive.tar.mp4", "archive.tar")]
    public void TheTitle_IsTheFileNameWithoutExtension_UnderscoresToSpaces(string path, string title)
    {
        Assert.Equal(title, ImportQueue.TitleOf(path));
    }

    [Fact]
    public void StartedAt_IsTheCreationTime_ElseTheLastWriteTime()
    {
        var now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        var created = new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc);
        var written = new DateTime(2026, 2, 1, 8, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTimeOffset(created), ImportQueue.StartedAtOf(created, written, now));
        Assert.Equal(new DateTimeOffset(written), ImportQueue.StartedAtOf(DateTime.FromFileTimeUtc(0), written, now));
        Assert.Equal(new DateTimeOffset(written), ImportQueue.StartedAtOf(DateTime.UnixEpoch, written, now));
        Assert.Equal(new DateTimeOffset(written), ImportQueue.StartedAtOf(now.AddYears(3), written, now));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>Windows grants symbolic links only with Developer Mode or admin rights; a test that needs a real one skips without them.</summary>
    private void SkipUnlessSymbolicLinksCanBeCreated()
    {
        var link = _dir.File("probe.link");
        try
        {
            File.CreateSymbolicLink(link, _dir.File("probe.target"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("This system does not let the tests create symbolic links: " + ex.Message);
        }
        finally
        {
            File.Delete(link);
        }
    }

    private async Task<SessionRecord> ImportVideoAsync(ImportQueue imports, string name, ImportOptions options)
    {
        var items = await imports.EnqueueAsync([new ImportRequest(await NewFileAsync(name, [9, 8, 7]), options)], Ct);
        var done = await WaitForItemAsync(imports, items[0].Id, ImportState.Done);
        return await WaitForSessionStateAsync(done.Session!.Value, SessionState.Ready);
    }

    private ImportQueue Create(IMediaDecoder decoder)
    {
        ImportQueue? imports = null;
        _processor = new SessionProcessor(_recorder, _sessions, _transcripts, _asr, _queue, languageOf: id => imports?.LanguageOf(id));
        imports = _imports = new ImportQueue(_sessions, _store, decoder, _queue, _processor);
        return imports;
    }

    private async Task<ImportQueue> StartAsync(IMediaDecoder decoder)
    {
        var imports = Create(decoder);
        await imports.StartAsync(Ct);
        return imports;
    }

    private async Task<string> NewFileAsync(string name, byte[]? content = null)
    {
        var path = _dir.File(name);
        await File.WriteAllBytesAsync(path, content ?? [0, 1, 2, 3], Ct);
        return path;
    }

    private static async Task<ImportItem> WaitForItemAsync(ImportQueue imports, long id, ImportState state)
    {
        ImportItem? item = null;
        await WaitUntilAsync(() => (item = imports.Items.FirstOrDefault(i => i.Id == id))?.State == state, $"import {id} to be {state} (is {item?.State})");
        return item!;
    }

    private async Task<SessionRecord> WaitForSessionStateAsync(SessionId id, SessionState state)
    {
        await WaitUntilAsync(() => _sessions.Get(id)?.State == state, $"session to be {state} (is {_sessions.Get(id)?.State})");
        return _sessions.Get(id)!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string? what = null)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting for " + (what ?? "the condition"));
            }

            await Task.Delay(10, Ct);
        }
    }
}
