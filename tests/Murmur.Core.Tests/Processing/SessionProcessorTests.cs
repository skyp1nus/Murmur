using Murmur.Core.Audio;
using Murmur.Core.Jobs;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;
using Murmur.Core.Tests.Transcription;
using Murmur.Core.Transcription;

namespace Murmur.Core.Tests.Processing;

public sealed class SessionProcessorTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _dir = new();
    private readonly InMemorySessionStore _sessions;
    private readonly InMemoryTranscriptStore _transcripts = new();
    private readonly FakeAsr _asr = new();
    private readonly FakeRecorder _recorder = new();
    private readonly JobQueue _queue = new();
    private readonly List<SessionProcessingStatus> _statuses = [];

    public SessionProcessorTests()
    {
        _sessions = new InMemorySessionStore(_dir.File("sessions"));
        _asr.Script("mic.wav", (0.5, 1.5, "me first"), (3.0, 4.0, "me again"));
        _asr.Script("system.wav", (0.0, 1.0, "others first"), (2.0, 2.5, "others again"));
    }

    public async ValueTask DisposeAsync()
    {
        await _queue.DisposeAsync();
        _dir.Dispose();
    }

    [Fact]
    public async Task StoppedRecording_IsTranscribedMergedAndSaved()
    {
        using var processor = Create();
        var session = await RecordAsync(mic: true, system: true);

        var done = await WaitForStateAsync(session.Id, SessionState.Ready);

        var transcript = await _transcripts.GetTranscriptAsync(session.Id, CancellationToken.None);
        Assert.NotNull(transcript);
        Assert.Equal(SessionProcessor.Engine, transcript.Engine);
        Assert.Equal("parakeet-tdt-0.6b-v3-int8", transcript.Engine);
        Assert.Equal(["others first", "me first", "others again", "me again"], transcript.Segments.Select(s => s.Text));
        Assert.Equal([Speaker.Others, Speaker.Me, Speaker.Others, Speaker.Me], transcript.Segments.Select(s => s.Speaker));
        Assert.Equal(TimeSpan.FromSeconds(0.5), transcript.Segments[1].Start);
        Assert.Single(transcript.Segments[1].Words);

        Assert.Equal([(Speaker.Me, "mic.wav"), (Speaker.Others, "system.wav")], _asr.Requests.Select(r => (r.Speaker, Path.GetFileName(r.AudioPath))));
        Assert.Equal(
            [SessionState.Transcribing, SessionState.Transcribed, SessionState.Ready],
            StatesAfterRecording(session.Id));
        Assert.Null(done.Error);
        Assert.Equal("Meeting", done.Title);

        var peaks = PeaksFile.Read(_sessions.GetFiles(session.Id).Peaks);
        Assert.Equal(250, peaks.Length); // 5 s at 50 peaks/s
        Assert.Equal(PeaksBuilder.ToByte(0.5f), peaks.Max());

        var final = processor.GetStatus(session.Id);
        Assert.Equal(new SessionProcessingStatus(session.Id, SessionProcessingStage.Completed, 1), final);
        Assert.Equal(1, processor.GetProgress(session.Id));
    }

    [Fact]
    public async Task Progress_RisesMonotonicallyThroughTheStages()
    {
        using var processor = Create();
        var session = await RecordAsync(mic: true, system: true);
        await WaitForStateAsync(session.Id, SessionState.Ready);

        List<SessionProcessingStatus> statuses;
        lock (_statuses)
        {
            statuses = [.. _statuses.Where(s => s.Session == session.Id)];
        }

        Assert.Equal(SessionProcessingStage.Queued, statuses[0].Stage);
        Assert.Equal(SessionProcessingStage.Completed, statuses[^1].Stage);
        Assert.Contains(statuses, s => s.Stage == SessionProcessingStage.BuildingPeaks);
        Assert.Contains(statuses, s => s.Stage == SessionProcessingStage.Transcribing && s.Progress is > 0.3 and < 0.7);
        Assert.Contains(statuses, s => s.Stage == SessionProcessingStage.Saving);
        for (var i = 1; i < statuses.Count; i++)
        {
            Assert.True(statuses[i].Progress >= statuses[i - 1].Progress, $"progress fell at {i}: {statuses[i - 1]} -> {statuses[i]}");
            Assert.InRange(statuses[i].Progress, 0, 1);
        }
    }

    [Fact]
    public async Task OnlyExistingTracks_AreTranscribed()
    {
        using var processor = Create();
        var session = await RecordAsync(mic: false, system: true);

        await WaitForStateAsync(session.Id, SessionState.Ready);

        var request = Assert.Single(_asr.Requests);
        Assert.Equal(Speaker.Others, request.Speaker);
        var transcript = await _transcripts.GetTranscriptAsync(session.Id, CancellationToken.None);
        Assert.All(transcript!.Segments, s => Assert.Equal(Speaker.Others, s.Speaker));
    }

    [Fact]
    public async Task Indexer_RunsAsAnIndexingJob_ThenReady()
    {
        var indexed = new List<SessionId>();
        using var processor = Create((id, _) =>
        {
            lock (indexed)
            {
                indexed.Add(id);
            }

            Assert.Equal(SessionState.Indexing, _sessions.Get(id)!.State);
            return Task.CompletedTask;
        });
        var session = await RecordAsync(mic: true, system: false);

        await WaitForStateAsync(session.Id, SessionState.Ready);

        Assert.Equal([session.Id], indexed);
        Assert.Equal([SessionState.Transcribing, SessionState.Transcribed, SessionState.Indexing, SessionState.Ready], StatesAfterRecording(session.Id));
    }

    [Fact]
    public async Task IndexerFailure_KeepsTheTranscript_AndLeavesTheSessionReadyWithAnError()
    {
        using var processor = Create((_, _) => throw new InvalidOperationException("index broke"));
        var session = await RecordAsync(mic: true, system: false);

        var ready = await WaitForStateAsync(session.Id, SessionState.Ready);

        Assert.Equal("Індексування не вдалося: index broke", ready.Error);
        Assert.StartsWith(SessionProcessor.IndexingFailedPrefix, ready.Error, StringComparison.Ordinal);
        Assert.NotNull(await _transcripts.GetTranscriptAsync(session.Id, CancellationToken.None));
        Assert.Equal([session.Id], processor.NeedsIndex);
        Assert.Equal(SessionProcessingStage.Completed, processor.GetStatus(session.Id)!.Stage);
        Assert.Equal(ready.Error, processor.GetStatus(session.Id)!.Message);
    }

    [Fact]
    public async Task AiInstalled_IndexesAfterTranscription()
    {
        var calls = 0;
        using var processor = Create(
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            },
            isAiInstalled: () => true);
        Assert.True(processor.CanIndex);
        var session = await RecordAsync(mic: true, system: false);

        await WaitForStateAsync(session.Id, SessionState.Ready);

        Assert.Equal(1, calls);
        Assert.Empty(processor.NeedsIndex);
        Assert.Equal([SessionState.Transcribing, SessionState.Transcribed, SessionState.Indexing, SessionState.Ready], StatesAfterRecording(session.Id));
    }

    [Fact]
    public async Task AiNotInstalled_BecomesReadyWithoutIndexing_AndRemembersNeedsIndex()
    {
        var calls = 0;
        using var processor = Create(
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            },
            isAiInstalled: () => false);
        Assert.False(processor.CanIndex);
        var session = await RecordAsync(mic: true, system: false);

        var ready = await WaitForStateAsync(session.Id, SessionState.Ready);

        Assert.Equal(0, calls);
        Assert.Null(ready.Error);
        Assert.Equal([session.Id], processor.NeedsIndex);
        Assert.Equal([SessionState.Transcribing, SessionState.Transcribed, SessionState.Ready], StatesAfterRecording(session.Id));
        Assert.Equal(new SessionProcessingStatus(session.Id, SessionProcessingStage.Completed, 1), processor.GetStatus(session.Id));
        Assert.NotNull(await _transcripts.GetTranscriptAsync(session.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ReindexAll_IndexesTheSessionsThatNeededIndexing_WhenAiAppears()
    {
        var installed = false;
        var indexed = new List<SessionId>();
        using var processor = Create(
            (id, _) =>
            {
                lock (indexed)
                {
                    indexed.Add(id);
                }

                return Task.CompletedTask;
            },
            isAiInstalled: () => installed);
        var first = await RecordAsync(mic: true, system: false);
        await WaitForStateAsync(first.Id, SessionState.Ready);
        var second = await RecordAsync(mic: true, system: false);
        await WaitForStateAsync(second.Id, SessionState.Ready);
        var unrelated = await CreateSessionAsync(mic: true, system: false, SessionState.Ready);
        Assert.Equal(2, processor.NeedsIndex.Count);

        Assert.Equal(0, await processor.ReindexAllAsync(cancellationToken: Ct)); // still not installed
        installed = true;
        Assert.Equal(2, await processor.ReindexAllAsync(cancellationToken: Ct));

        await WaitUntilAsync(() => _queue.Status.Running == 0 && _queue.Status.Pending == 0);
        lock (indexed)
        {
            Assert.Equal(2, indexed.Count);
            Assert.Contains(first.Id, indexed);
            Assert.Contains(second.Id, indexed);
            Assert.DoesNotContain(unrelated.Id, indexed);
        }

        Assert.Empty(processor.NeedsIndex);
        Assert.Equal(SessionState.Ready, _sessions.Get(first.Id)!.State);
        Assert.Empty(_asr.Requests.Skip(2)); // no re-transcription

        // Nothing left to do, unless every Ready session is requested.
        Assert.Equal(0, await processor.ReindexAllAsync(cancellationToken: Ct));
        Assert.Equal(3, await processor.ReindexAllAsync(allReady: true, cancellationToken: Ct));
    }

    [Fact]
    public async Task ReindexAll_RetriesAFailedIndexing_AndClearsTheError()
    {
        var fail = true;
        using var processor = Create((_, _) => fail ? throw new InvalidOperationException("no server") : Task.CompletedTask);
        var session = await RecordAsync(mic: true, system: false);
        var ready = await WaitForStateAsync(session.Id, SessionState.Ready);
        Assert.NotNull(ready.Error);

        fail = false;
        Assert.Equal(1, await processor.ReindexAllAsync(cancellationToken: Ct));

        await WaitUntilAsync(() => _sessions.Get(session.Id)!.Error is null && _queue.Status.Running == 0 && _queue.Status.Pending == 0);
        Assert.Equal(SessionState.Ready, _sessions.Get(session.Id)!.State);
        Assert.Empty(processor.NeedsIndex);
    }

    [Fact]
    public async Task ReindexAll_FindsFailedIndexingAfterARestart_ByTheErrorText()
    {
        var failed = await CreateSessionAsync(mic: true, system: false, SessionState.Ready);
        _sessions.Seed(failed with { Error = SessionProcessor.IndexingFailedPrefix + "old" });
        var calls = 0;
        using var processor = Create((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });

        Assert.Equal(1, await processor.ReindexAllAsync(cancellationToken: Ct));

        await WaitUntilAsync(() => _sessions.Get(failed.Id)!.Error is null && _queue.Status.Running == 0);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ReindexAll_FindsSessionsWithoutVectorsAfterARestart_ByThePersistedMarker()
    {
        var aiInstalled = false;
        var indexed = new List<SessionId>();
        Task Index(SessionId id, CancellationToken _)
        {
            lock (indexed)
            {
                indexed.Add(id);
            }

            return Task.CompletedTask;
        }

        SessionId session;
        string marker;
        using (var first = Create(Index, () => aiInstalled))
        {
            var recorded = await RecordAsync(mic: true, system: false);
            session = recorded.Id;
            await WaitForStateAsync(session, SessionState.Ready);
            marker = Path.Combine(_sessions.GetFiles(session).Directory, "needs-index");
            Assert.True(File.Exists(marker));
        }

        // "Restart": a new processor over the same store knows nothing in memory; the AI models are installed meanwhile.
        aiInstalled = true;
        using var second = Create(Index, () => aiInstalled);
        Assert.Empty(second.NeedsIndex);

        Assert.Equal(1, await second.ReindexAllAsync(cancellationToken: Ct));

        await WaitUntilAsync(() => !File.Exists(marker) && _queue.Status.Running == 0 && _queue.Status.Pending == 0);
        lock (indexed)
        {
            Assert.Equal([session], indexed);
        }

        Assert.Equal(SessionState.Ready, _sessions.Get(session)!.State);
        Assert.Equal(0, await second.ReindexAllAsync(cancellationToken: Ct)); // marker gone: nothing left
    }

    [Fact]
    public async Task ReindexQueuedBeforeAManualRetranscription_DoesNotLeaveTheSessionTranscribed()
    {
        var indexed = new List<SessionId>();
        using var processor = Create(
            (id, _) =>
            {
                lock (indexed)
                {
                    indexed.Add(id);
                }

                return Task.CompletedTask;
            });
        var ready = await CreateSessionAsync(mic: true, system: false, SessionState.Ready);
        var other = await CreateSessionAsync(mic: true, system: false, SessionState.Recording);
        _recorder.BeginRecording(other); // pauses the queue: both jobs below wait

        Assert.Equal(1, await processor.ReindexAllAsync(allReady: true, cancellationToken: Ct));
        Assert.True(await processor.EnqueueAsync(ready.Id, Ct));

        await _sessions.UpdateSessionAsync(_sessions.Get(other.Id)! with { State = SessionState.Recorded }, Ct);
        _recorder.EndRecording();

        await WaitForStateAsync(other.Id, SessionState.Ready);
        var final = await WaitForStateAsync(ready.Id, SessionState.Ready);

        Assert.Null(final.Error);
        Assert.Equal(SessionProcessingStage.Completed, processor.GetStatus(ready.Id)!.Stage);
        Assert.Equal(SessionState.Ready, StatesAfterRecording(ready.Id)[^1]);
        lock (indexed)
        {
            Assert.Contains(ready.Id, indexed);
        }
    }

    [Fact]
    public async Task ReindexAll_WithoutAnIndexer_DoesNothing()
    {
        using var processor = Create();
        await CreateSessionAsync(mic: true, system: false, SessionState.Ready);

        Assert.False(processor.CanIndex);
        Assert.Equal(0, await processor.ReindexAllAsync(allReady: true, cancellationToken: Ct));
    }

    [Fact]
    public async Task Indexing_NeverRunsWhileRecording()
    {
        var calls = 0;
        using var processor = Create((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });
        var ready = await CreateSessionAsync(mic: true, system: false, SessionState.Ready);
        var next = await CreateSessionAsync(mic: true, system: false, SessionState.Recording);
        _recorder.BeginRecording(next);

        Assert.Equal(1, await processor.ReindexAllAsync(allReady: true, cancellationToken: Ct));
        await Task.Delay(100, Ct);
        Assert.Equal(0, calls);
        Assert.True(_queue.Paused);

        await _sessions.UpdateSessionAsync(_sessions.Get(next.Id)! with { State = SessionState.Recorded }, Ct);
        _recorder.EndRecording();

        await WaitForStateAsync(next.Id, SessionState.Ready);
        Assert.Equal(2, calls); // the queued reindex and the new recording's own indexing
        Assert.Equal(SessionState.Ready, _sessions.Get(ready.Id)!.State);
    }

    [Fact]
    public async Task Indexing_RunningWhenARecordingStarts_IsPreemptedAndResumedAfterwards()
    {
        var started = new SemaphoreSlim(0);
        var attempts = 0;
        using var processor = Create(async (_, ct) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                started.Release();
                await Task.Delay(Timeout, ct);
            }
        });
        var session = await RecordAsync(mic: true, system: false);
        Assert.True(await started.WaitAsync(Timeout, Ct));

        var next = await CreateSessionAsync(mic: true, system: false, SessionState.Recording);
        _recorder.BeginRecording(next);
        await WaitUntilAsync(() => _queue.Status.Running == 0);
        Assert.Equal(SessionState.Indexing, _sessions.Get(session.Id)!.State); // not failed, not Ready

        await _sessions.UpdateSessionAsync(_sessions.Get(next.Id)! with { State = SessionState.Recorded }, Ct);
        _recorder.EndRecording();

        await WaitForStateAsync(session.Id, SessionState.Ready);
        await WaitForStateAsync(next.Id, SessionState.Ready);
        Assert.Null(_sessions.Get(session.Id)!.Error);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task AiRemovedAfterQueuing_LeavesReadyWithoutCallingTheIndexer_AndRemembersIt()
    {
        var aiInstalled = true;
        var calls = 0;
        using var processor = Create((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        }, () => aiInstalled);
        var ready = await CreateSessionAsync(mic: true, system: false, SessionState.Ready);
        var next = await CreateSessionAsync(mic: true, system: false, SessionState.Recording);
        _recorder.BeginRecording(next);
        Assert.Equal(1, await processor.ReindexAllAsync(allReady: true, cancellationToken: Ct));

        aiInstalled = false;
        await _sessions.UpdateSessionAsync(_sessions.Get(next.Id)! with { State = SessionState.Failed }, Ct);
        _recorder.EndRecording();
        await WaitUntilAsync(() => processor.NeedsIndex.Contains(ready.Id) && _queue.Status.Running == 0 && _queue.Status.Pending == 0);

        Assert.Equal(0, calls);
        Assert.Equal(SessionState.Ready, _sessions.Get(ready.Id)!.State);
        Assert.True(File.Exists(Path.Combine(_sessions.GetFiles(ready.Id).Directory, "needs-index")));
    }

    [Fact]
    public async Task ReindexFailure_KeepsSessionReadyWithError_AndKeepsTheMarker()
    {
        var session = await CreateSessionAsync(mic: true, system: false, SessionState.Ready);
        var marker = Path.Combine(_sessions.GetFiles(session.Id).Directory, "needs-index");
        File.WriteAllBytes(marker, []);
        using var processor = Create((_, _) => throw new InvalidOperationException("boom"));

        Assert.Equal(1, await processor.ReindexAllAsync(cancellationToken: Ct));
        await WaitUntilAsync(() => _sessions.Get(session.Id)!.Error is not null && _queue.Status.Running == 0);

        var row = _sessions.Get(session.Id)!;
        Assert.Equal(SessionState.Ready, row.State);
        Assert.Equal(SessionProcessor.IndexingFailedPrefix + "boom", row.Error);
        Assert.True(File.Exists(marker));
        Assert.Contains(session.Id, processor.NeedsIndex);
    }

    [Fact]
    public async Task IndexerFailureWithEmptyMessage_UsesTheExceptionTypeName()
    {
        using var processor = Create((_, _) => throw new InvalidOperationException(""));
        var session = await RecordAsync(mic: true, system: false);

        var ready = await WaitForStateAsync(session.Id, SessionState.Ready);

        Assert.Equal(SessionProcessor.IndexingFailedPrefix + nameof(InvalidOperationException), ready.Error);
    }

    [Fact]
    public async Task WithoutAnIndexer_NoNeedsIndexMarkerIsWritten()
    {
        using var processor = Create();
        var session = await RecordAsync(mic: true, system: false);

        await WaitForStateAsync(session.Id, SessionState.Ready);

        Assert.Empty(processor.NeedsIndex);
        Assert.False(File.Exists(Path.Combine(_sessions.GetFiles(session.Id).Directory, "needs-index")));
        Assert.False(processor.CanIndex);
    }

    [Fact]
    public async Task ReindexAll_IgnoresSessionsThatAreNotReady()
    {
        var calls = 0;
        using var processor = Create((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });
        foreach (var state in new[] { SessionState.Failed, SessionState.Recorded, SessionState.Transcribing })
        {
            var s = await CreateSessionAsync(mic: true, system: false, state);
            File.WriteAllBytes(Path.Combine(_sessions.GetFiles(s.Id).Directory, "needs-index"), []);
        }

        Assert.Equal(0, await processor.ReindexAllAsync(allReady: true, cancellationToken: Ct));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ReindexAll_DoesNotQueueTheSameSessionTwice()
    {
        var release = new TaskCompletionSource();
        var calls = 0;
        using var processor = Create(async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
        });
        await CreateSessionAsync(mic: true, system: false, SessionState.Ready);

        Assert.Equal(1, await processor.ReindexAllAsync(allReady: true, cancellationToken: Ct));
        Assert.Equal(0, await processor.ReindexAllAsync(allReady: true, cancellationToken: Ct));
        release.SetResult();
        await WaitUntilAsync(() => _queue.Status.Running == 0 && _queue.Status.Pending == 0);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RecordingActivity_RaisesStoppedWhenTheRecorderGoesIdle()
    {
        using var activity = new RecordingActivity(_recorder);
        var stopped = 0;
        activity.RecordingStopped += () => stopped++;
        var s = await CreateSessionAsync(mic: true, system: false, SessionState.Recording);

        _recorder.BeginRecording(s);
        Assert.True(activity.IsRecording);
        _recorder.EndRecording();

        Assert.False(activity.IsRecording);
        Assert.Equal(1, stopped);
    }

    [Fact]
    public async Task ResumePending_WithAnIndexerButNoAi_PromotesTranscribedSessionsToReady_AndRemembersThem()
    {
        var transcribed = await CreateSessionAsync(mic: true, system: false, SessionState.Transcribed);
        var calls = 0;
        using var processor = Create(
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            },
            isAiInstalled: () => false);

        Assert.Equal(0, await processor.ResumePendingAsync(Ct));

        Assert.Equal(SessionState.Ready, _sessions.Get(transcribed.Id)!.State);
        Assert.Equal([transcribed.Id], processor.NeedsIndex);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RecordingActivity_ReflectsTheRecorder_AndRaisesStartedOncePerRecording()
    {
        using var activity = new RecordingActivity(_recorder);
        var started = 0;
        var stopped = 0;
        activity.RecordingStarted += () => started++;
        activity.RecordingStopped += () => stopped++;
        Assert.False(activity.IsRecording);

        var session = await CreateSessionAsync(mic: true, system: false, SessionState.Recording);
        _recorder.BeginRecording(session); // Starting -> Recording
        Assert.True(activity.IsRecording);
        Assert.Equal(1, started);
        Assert.Equal(0, stopped);

        _recorder.EndRecording(); // Stopping -> Idle
        Assert.False(activity.IsRecording);
        Assert.Equal(1, started);
        Assert.Equal(1, stopped);

        _recorder.BeginRecording(session);
        Assert.Equal(2, started);
    }

    [Fact]
    public async Task RecordingActivity_SwallowsHandlerExceptions_AndStopsAfterDispose()
    {
        var activity = new RecordingActivity(_recorder);
        var started = 0;
        activity.RecordingStarted += () => throw new InvalidOperationException("handler broke");
        activity.RecordingStarted += () => started++;
        var session = await CreateSessionAsync(mic: true, system: false, SessionState.Recording);

        _recorder.BeginRecording(session); // would throw into the recorder without the guard
        Assert.Equal(0, started); // multicast stops at the throwing handler, but the recorder is unaffected
        _recorder.EndRecording();

        activity.Dispose();
        activity.Dispose();
        var after = 0;
        activity.RecordingStarted += () => after++;
        _recorder.BeginRecording(session);
        Assert.Equal(0, after);
    }

    [Fact]
    public async Task RecordingActivity_StartsActive_WhenTheRecorderIsAlreadyBusy()
    {
        var session = await CreateSessionAsync(mic: true, system: false, SessionState.Recording);
        _recorder.BeginRecording(session);
        using var activity = new RecordingActivity(_recorder);
        var started = 0;
        activity.RecordingStarted += () => started++;

        Assert.True(activity.IsRecording);
        _recorder.EndRecording();
        Assert.False(activity.IsRecording);
        Assert.Equal(0, started);
    }

    [Fact]
    public async Task AsrFailure_FailsTheSessionWithTheError()
    {
        _asr.FailOn = "system.wav";
        using var processor = Create();
        var session = await RecordAsync(mic: true, system: true);

        var failed = await WaitForStateAsync(session.Id, SessionState.Failed);

        Assert.Equal("simulated ASR failure", failed.Error);
        Assert.Null(await _transcripts.GetTranscriptAsync(session.Id, CancellationToken.None));
        Assert.Equal(new SessionProcessingStatus(session.Id, SessionProcessingStage.Failed, 0, "simulated ASR failure"), processor.GetStatus(session.Id));
    }

    [Fact]
    public async Task SaveFailure_FailsTheSession()
    {
        _transcripts.FailWith = new IOException("database is locked");
        using var processor = Create();
        var session = await RecordAsync(mic: true, system: false);

        var failed = await WaitForStateAsync(session.Id, SessionState.Failed);

        Assert.Equal("database is locked", failed.Error);
    }

    [Fact]
    public async Task MissingAudio_FailsTheSession()
    {
        using var processor = Create();
        var session = await RecordAsync(mic: false, system: false);

        var failed = await WaitForStateAsync(session.Id, SessionState.Failed);

        Assert.Contains("no audio", failed.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_asr.Requests);
    }

    [Fact]
    public async Task AsrNotInstalled_LeavesTheSessionRecorded()
    {
        _asr.Installed = false;
        using var processor = Create();
        Assert.False(processor.IsAsrInstalled);
        Assert.Equal("Модель Parakeet не встановлена", processor.AsrStatusMessage);

        var session = await RecordAsync(mic: true, system: true);
        await WaitUntilAsync(() => processor.GetStatus(session.Id)?.Stage == SessionProcessingStage.ModelMissing);

        Assert.Equal(SessionState.Recorded, _sessions.Get(session.Id)!.State);
        Assert.Equal(SessionProcessor.AsrNotInstalledMessage, processor.GetStatus(session.Id)!.Message);
        Assert.Empty(_asr.Requests);

        // Installed later: a retry goes through.
        _asr.Installed = true;
        Assert.Null(processor.AsrStatusMessage);
        await WaitUntilAsync(() => _queue.Status is { Pending: 0, Running: 0 });
        Assert.True(await processor.EnqueueAsync(session.Id, Ct));
        await WaitForStateAsync(session.Id, SessionState.Ready);
    }

    [Fact]
    public async Task FailedRecording_IsNotProcessedAutomatically()
    {
        using var processor = Create();
        var session = await RecordAsync(mic: true, system: true, finalState: SessionState.Failed);

        await WaitUntilAsync(() => _queue.Status is { Pending: 0, Running: 0 } && processor.GetStatus(session.Id) is null);

        Assert.Empty(_asr.Requests);
        Assert.Equal(SessionState.Failed, _sessions.Get(session.Id)!.State);
    }

    [Fact]
    public async Task Queue_IsPausedWhileRecording()
    {
        using var processor = Create();
        var a = await CreateSessionAsync(mic: true, system: false, SessionState.Recorded);
        var b = await CreateSessionAsync(mic: true, system: false, SessionState.Recording);

        _recorder.BeginRecording(b);
        Assert.True(_queue.Paused);
        Assert.True(await processor.EnqueueAsync(a.Id, Ct));
        await Task.Delay(100, Ct);
        Assert.Empty(_asr.Requests);
        Assert.Equal(SessionState.Recorded, _sessions.Get(a.Id)!.State);

        await _sessions.UpdateSessionAsync(_sessions.Get(b.Id)! with { State = SessionState.Recorded }, CancellationToken.None);
        _recorder.EndRecording();
        Assert.False(_queue.Paused);

        await WaitForStateAsync(a.Id, SessionState.Ready);
        await WaitForStateAsync(b.Id, SessionState.Ready);
    }

    [Fact]
    public async Task RecordingStart_PreemptsARunningTranscription_WhichRestartsAfterwards()
    {
        _asr.Gate = new SemaphoreSlim(0);
        using var processor = Create();
        var a = await CreateSessionAsync(mic: true, system: false, SessionState.Recorded);
        Assert.True(await processor.EnqueueAsync(a.Id, Ct));
        await _asr.Started.Task.WaitAsync(Timeout, Ct);
        Assert.Equal(SessionState.Transcribing, _sessions.Get(a.Id)!.State);

        var b = await CreateSessionAsync(mic: true, system: false, SessionState.Recording);
        _recorder.BeginRecording(b);
        await WaitUntilAsync(() => _queue.Status is { Running: 0, Pending: 1 });
        Assert.Equal(SessionState.Transcribing, _sessions.Get(a.Id)!.State);
        Assert.Equal(SessionProcessingStage.Queued, processor.GetStatus(a.Id)!.Stage);

        _asr.Gate.Release(10);
        await _sessions.UpdateSessionAsync(_sessions.Get(b.Id)! with { State = SessionState.Recorded }, CancellationToken.None);
        _recorder.EndRecording();

        await WaitForStateAsync(a.Id, SessionState.Ready);
        await WaitForStateAsync(b.Id, SessionState.Ready);
        Assert.Equal(3, _asr.Requests.Count); // a (pre-empted), a again, b
    }

    [Fact]
    public async Task ResumePending_RequeuesRecordedAndTranscribingSessions()
    {
        var recorded = await CreateSessionAsync(mic: true, system: false, SessionState.Recorded);
        var transcribing = await CreateSessionAsync(mic: true, system: true, SessionState.Transcribing);
        var ready = await CreateSessionAsync(mic: true, system: false, SessionState.Ready);
        var failed = await CreateSessionAsync(mic: true, system: false, SessionState.Failed);
        var interrupted = await CreateSessionAsync(mic: true, system: false, SessionState.Interrupted);
        using var processor = Create();

        var queued = await processor.ResumePendingAsync(Ct);

        Assert.Equal(2, queued);
        await WaitForStateAsync(recorded.Id, SessionState.Ready);
        await WaitForStateAsync(transcribing.Id, SessionState.Ready);
        Assert.Equal(SessionState.Ready, _sessions.Get(ready.Id)!.State);
        Assert.Equal(SessionState.Failed, _sessions.Get(failed.Id)!.State);
        Assert.Equal(SessionState.Interrupted, _sessions.Get(interrupted.Id)!.State);
        Assert.Equal(3, _asr.Requests.Count);
    }

    [Fact]
    public async Task ResumePending_WithAnIndexer_AlsoRequeuesIndexingSessions()
    {
        var indexing = await CreateSessionAsync(mic: true, system: false, SessionState.Indexing);
        var calls = 0;
        using var processor = Create((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });

        Assert.Equal(1, await processor.ResumePendingAsync(Ct));

        await WaitForStateAsync(indexing.Id, SessionState.Ready);
        Assert.Equal(1, calls);
        Assert.Empty(_asr.Requests);
    }

    [Fact]
    public async Task ResumePending_WithAnIndexer_RequeuesTranscribedSessionsForIndexing()
    {
        var transcribed = await CreateSessionAsync(mic: true, system: false, SessionState.Transcribed);
        var calls = 0;
        using var processor = Create((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });

        Assert.Equal(1, await processor.ResumePendingAsync(Ct));

        await WaitForStateAsync(transcribed.Id, SessionState.Ready);
        Assert.Equal(1, calls);
        Assert.Empty(_asr.Requests);
    }

    [Fact]
    public async Task ResumePending_WithoutAnIndexer_PromotesTranscribedAndIndexingSessionsToReady()
    {
        var transcribed = await CreateSessionAsync(mic: true, system: false, SessionState.Transcribed);
        var indexing = await CreateSessionAsync(mic: true, system: false, SessionState.Indexing);
        using var processor = Create();

        Assert.Equal(0, await processor.ResumePendingAsync(Ct));

        Assert.Equal(SessionState.Ready, _sessions.Get(transcribed.Id)!.State);
        Assert.Equal(SessionState.Ready, _sessions.Get(indexing.Id)!.State);
        Assert.Equal(0, _queue.Status.Pending + _queue.Status.Running);
        Assert.Empty(_asr.Requests);
    }

    [Fact]
    public async Task Enqueue_RetriesAFailedSession()
    {
        var failed = await CreateSessionAsync(mic: true, system: false, SessionState.Failed);
        await _sessions.UpdateSessionAsync(_sessions.Get(failed.Id)! with { Error = "old error" }, CancellationToken.None);
        using var processor = Create();

        Assert.True(await processor.EnqueueAsync(failed.Id, Ct));

        var done = await WaitForStateAsync(failed.Id, SessionState.Ready);
        Assert.Null(done.Error);
    }

    [Fact]
    public async Task Enqueue_SkipsRecordingDuplicateAndUnknownSessions()
    {
        _asr.Gate = new SemaphoreSlim(0);
        using var processor = Create();
        var recording = await CreateSessionAsync(mic: true, system: false, SessionState.Recording);
        var recorded = await CreateSessionAsync(mic: true, system: false, SessionState.Recorded);

        Assert.False(await processor.EnqueueAsync(recording.Id, Ct));
        Assert.True(await processor.EnqueueAsync(recorded.Id, Ct));
        Assert.False(await processor.EnqueueAsync(recorded.Id, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => processor.EnqueueAsync(SessionId.New(), Ct).AsTask());

        _asr.Gate.Release(10);
        await WaitForStateAsync(recorded.Id, SessionState.Ready);
        Assert.True(await processor.EnqueueAsync(recorded.Id, Ct)); // finished: may run again
    }

    [Fact]
    public async Task Dispose_StopsFollowingTheRecorderAndLiftsItsPause()
    {
        var processor = Create();
        var b = await CreateSessionAsync(mic: true, system: false, SessionState.Recording);
        _recorder.BeginRecording(b);
        Assert.True(_queue.Paused);

        processor.Dispose();
        processor.Dispose();

        Assert.False(_queue.Paused);
        _recorder.EndRecording();
        await Task.Delay(50, Ct);
        Assert.Empty(_asr.Requests);
    }

    [Fact]
    public async Task WithTheWorkerClient_TranscribesEndToEnd_AndStopsTheWorkerWhenRecordingStarts()
    {
        var models = ModelLocatorTests.Install(_dir.File("models"));
        await using var client = new WorkerAsrClient(models, new WorkerAsrClientOptions(WorkerAsrClientTests.FakeWorkerDll, Threads: 1));
        using var processor = new SessionProcessor(_recorder, _sessions, _transcripts, client, _queue);
        var session = await RecordAsync(mic: true, system: true);

        await WaitForStateAsync(session.Id, SessionState.Ready);
        var transcript = await _transcripts.GetTranscriptAsync(session.Id, Ct);
        Assert.Equal(6, transcript!.Segments.Count); // 3 per track from the fake worker
        Assert.Equal(["mic 0", "system 0", "system 1", "mic 1", "system 2", "mic 2"], transcript.Segments.Select(s => s.Text));
        Assert.True(client.IsWorkerRunning);

        var next = await CreateSessionAsync(mic: true, system: false, SessionState.Recording);
        _recorder.BeginRecording(next);

        await WaitUntilAsync(() => !client.IsWorkerRunning);
        await _sessions.UpdateSessionAsync(_sessions.Get(next.Id)! with { State = SessionState.Recorded }, Ct);
        _recorder.EndRecording();
        await WaitForStateAsync(next.Id, SessionState.Ready);
        Assert.Equal(2, client.WorkerStarts);
    }

    [Fact]
    public async Task UnknownProgress_IsZero()
    {
        using var processor = Create();

        Assert.Equal(0, processor.GetProgress(SessionId.New()));
        Assert.Null(processor.GetStatus(SessionId.New()));
        await Task.CompletedTask;
    }

    private SessionProcessor Create(Func<SessionId, CancellationToken, Task>? indexer = null, Func<bool>? isAiInstalled = null)
    {
        var processor = new SessionProcessor(_recorder, _sessions, _transcripts, _asr, _queue, indexer, isAiInstalled);
        processor.ProgressChanged += s =>
        {
            lock (_statuses)
            {
                _statuses.Add(s);
            }
        };
        return processor;
    }

    /// <summary>Simulates a recording through the recorder events: Recording row, files, then Stop -&gt; <paramref name="finalState"/>.</summary>
    private async Task<SessionRecord> RecordAsync(bool mic, bool system, SessionState finalState = SessionState.Recorded)
    {
        var session = await CreateSessionAsync(mic, system, SessionState.Recording);
        _recorder.BeginRecording(session);
        await _sessions.UpdateSessionAsync(_sessions.Get(session.Id)! with { State = finalState }, CancellationToken.None);
        _recorder.EndRecording();
        return session;
    }

    private async Task<SessionRecord> CreateSessionAsync(bool mic, bool system, SessionState state)
    {
        var session = await _sessions.CreateSessionAsync("Meeting", DateTimeOffset.UtcNow, CancellationToken.None);
        var files = _sessions.GetFiles(session.Id);
        if (mic)
        {
            TestWav.Square(files.MicrophoneAudio, TimeSpan.FromSeconds(5), 0.5f);
        }

        if (system)
        {
            TestWav.Square(files.SystemAudio, TimeSpan.FromSeconds(3), 0.25f);
        }

        var updated = session with { State = state, HasMicrophone = mic, HasSystemAudio = system, Duration = TimeSpan.FromSeconds(5) };
        _sessions.Seed(updated);
        return updated;
    }

    /// <summary>States written after the recording finished (Recorded and earlier excluded).</summary>
    private List<SessionState> StatesAfterRecording(SessionId id) =>
        [.. _sessions.Updates.Where(u => u.Id == id).Select(u => u.State).Where(s => s is not (SessionState.Recording or SessionState.Recorded))];

    private async Task<SessionRecord> WaitForStateAsync(SessionId id, SessionState state)
    {
        await WaitUntilAsync(() => _sessions.Get(id)?.State == state && _queue.Status.Running == 0 && _queue.Status.Pending == 0);
        return _sessions.Get(id)!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(5, Ct);
        }
    }
}
