using Murmur.Core.Audio;
using Murmur.Core.Jobs;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.Core.Processing;

public enum SessionProcessingStage
{
    /// <summary>Waiting in the job queue (also after a recording pre-empted it).</summary>
    Queued,
    BuildingPeaks,
    Transcribing,
    Saving,

    /// <summary>The transcript is saved; the indexing hook runs (or waits in the queue).</summary>
    Indexing,

    /// <summary>Done: the session is <see cref="SessionState.Ready"/>.</summary>
    Completed,

    /// <summary>The session is <see cref="SessionState.Failed"/>; <see cref="SessionProcessingStatus.Message"/> says why.</summary>
    Failed,

    /// <summary>The ASR model is not installed; the session stays <see cref="SessionState.Recorded"/>.</summary>
    ModelMissing,
}

/// <param name="Progress">Transcription progress of the session in [0, 1] (peaks, every track, saving); 1 from <see cref="SessionProcessingStage.Indexing"/> on.</param>
/// <param name="Message">A technical (English) error text for <see cref="SessionProcessingStage.Failed"/>; a <see cref="SessionNotes"/> line (code and detail, not a sentence) for <see cref="SessionProcessingStage.ModelMissing"/> and for <see cref="SessionProcessingStage.Completed"/> with a failed indexing. The App words them.</param>
public sealed record SessionProcessingStatus(SessionId Session, SessionProcessingStage Stage, double Progress, string? Message = null);

/// <summary>
/// Post-recording pipeline: build peaks -&gt; transcribe every track -&gt; save the transcript -&gt; index (when AI is installed).
/// </summary>
/// <remarks>
/// <para><b>When.</b> After every recording that stops in <see cref="SessionState.Recorded"/> (via
/// <see cref="IRecorder.StateChanged"/>), for <see cref="EnqueueAsync"/> (manual retry or an imported file, any state but Recording) and,
/// at start-up, <see cref="ResumePendingAsync"/>. The work runs as a <see cref="JobPriority.Transcription"/> job on the
/// shared <see cref="JobQueue"/>; the processor pauses that queue for as long as the recorder is not idle (a running
/// job is pre-empted and restarted after the recording) and stops the ASR worker so no model is in memory while
/// recording.</para>
/// <para><b>States.</b> Recorded -&gt; Transcribing -&gt; Transcribed -&gt; (Indexing -&gt;) Ready. Without an indexer, or
/// while the AI models are not installed, the session becomes Ready right after Transcribed (keyword search still
/// works) and is remembered in <see cref="NeedsIndex"/> and by a <c>needs-index</c> marker file in the session folder
/// (so it survives a restart); <see cref="ReindexAllAsync"/> indexes those later, when the AI models appear. A transcription failure -&gt; Failed with <see cref="SessionRecord.Error"/>. An indexing failure keeps
/// the transcript: the session is Ready with <see cref="SessionRecord.Error"/> =
/// <see cref="IndexingFailedCode"/> + reason as the detail (<see cref="SessionNotes"/>; the App words it), and stays in <see cref="NeedsIndex"/>. A pre-empted or cancelled job leaves the session Transcribing (or Indexing) so it is resumed later. With the ASR
/// not installed the session is left Recorded and the status is <see cref="SessionProcessingStage.ModelMissing"/>
/// (<see cref="AsrNotInstalledCode"/>).</para>
/// <para><b>Tracks.</b> <c>mic.wav</c> is transcribed as <see cref="Speaker.Me"/>, <c>system.wav</c> as
/// <see cref="Speaker.Others"/> (only the tracks that exist); segments are merged by start time.</para>
/// <para><b>Threads.</b> <see cref="ProgressChanged"/> is raised on the job's thread (never the recorder's);
/// subscribers must marshal to the UI thread.</para>
/// </remarks>
public sealed class SessionProcessor : IDisposable
{
    public const string Engine = "parakeet-tdt-0.6b-v3-int8";

    /// <summary>Code (not a sentence: the App words it) of the status of a session left Recorded because the ASR model is missing.</summary>
    public const string AsrNotInstalledCode = SessionNotes.AsrMissing;

    /// <summary>Code at the start of <see cref="SessionRecord.Error"/> of a Ready session whose indexing failed; the reason follows as its detail.</summary>
    public const string IndexingFailedCode = SessionNotes.IndexFailed;

    /// <summary>
    /// The start of the text a session got before codes existed (Ukrainian, written by an earlier version). Matched only, to
    /// find such sessions for re-indexing; never written.
    /// </summary>
    private const string LegacyIndexingFailedPrefix = "Індексування не вдалося: ";

    private const double PeaksWeight = 0.02;
    private const double SaveWeight = 0.02;

    /// <summary>Empty file in the session folder: the session is Ready but has no vectors yet (survives a restart).</summary>
    private const string NeedsIndexMarker = "needs-index";

    private readonly IRecorder _recorder;
    private readonly ISessionStore _sessions;
    private readonly ITranscriptStore _transcripts;
    private readonly IAsr _asr;
    private readonly JobQueue _queue;
    private readonly Func<SessionId, CancellationToken, Task>? _indexer;
    private readonly Func<bool>? _isAiInstalled;
    private readonly Func<SessionId, string?>? _languageOf;
    private readonly object _gate = new();
    private readonly HashSet<SessionId> _needsIndex = [];
    private readonly Dictionary<SessionId, SessionProcessingStatus> _statuses = [];
    private readonly Dictionary<SessionId, JobEntry> _transcriptionJobs = [];
    private readonly Dictionary<SessionId, JobEntry> _indexingJobs = [];

    private SessionId? _recordingSession;
    private bool _pausedForRecording;
    private bool _disposed;

    /// <param name="indexer">Phase 3 hook (<c>IAssistant.IndexSessionAsync</c>), run as an <see cref="JobPriority.Indexing"/> job.</param>
    /// <param name="isAiInstalled">Whether the AI models are installed (checked every time, so installing them later takes
    /// effect without a restart); <c>null</c> = always installed. Ignored without an <paramref name="indexer"/>.</param>
    /// <param name="languageOf">The spoken language of a session (the language chosen for an imported file), read when its
    /// transcription starts and passed to the ASR as <see cref="AsrRequest.Language"/>; <c>null</c> (or a <c>null</c>
    /// result) = auto-detect.</param>
    public SessionProcessor(IRecorder recorder, ISessionStore sessions, ITranscriptStore transcripts, IAsr asr, JobQueue queue, Func<SessionId, CancellationToken, Task>? indexer = null, Func<bool>? isAiInstalled = null, Func<SessionId, string?>? languageOf = null)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(transcripts);
        ArgumentNullException.ThrowIfNull(asr);
        ArgumentNullException.ThrowIfNull(queue);
        _recorder = recorder;
        _sessions = sessions;
        _transcripts = transcripts;
        _asr = asr;
        _queue = queue;
        _indexer = indexer;
        _isAiInstalled = isAiInstalled;
        _languageOf = languageOf;

        _recorder.StateChanged += OnRecorderStateChanged;
        OnRecorderStateChanged(_recorder.State);
    }

    /// <summary>Raised whenever the processing status of a session changes.</summary>
    public event Action<SessionProcessingStatus>? ProgressChanged;

    /// <summary>
    /// Raised when the status of a session is dropped without a final status (the session is gone, e.g. deleted while it waited):
    /// <see cref="GetStatus"/> returns <c>null</c> from then on. Raised on the job's thread, like <see cref="ProgressChanged"/>.
    /// </summary>
    public event Action<SessionId>? StatusCleared;

    public bool IsAsrInstalled => _asr.Status != AsrStatus.ModelMissing;

    /// <summary><see cref="AsrNotInstalledCode"/> when the model is missing, else <c>null</c>.</summary>
    public string? AsrStatusMessage => IsAsrInstalled ? null : AsrNotInstalledCode;

    /// <summary>Whether sessions are indexed right now: an indexer is configured and the AI models are installed.</summary>
    public bool CanIndex => _indexer is not null && (_isAiInstalled?.Invoke() ?? true);

    /// <summary>
    /// Sessions that became Ready without vectors (AI not installed) or whose indexing failed, since start.
    /// This list is in memory only; the same fact is persisted as a <c>needs-index</c> marker file in the session
    /// folder, which <see cref="ReindexAllAsync"/> also honours, so a restart loses nothing.
    /// </summary>
    public IReadOnlyCollection<SessionId> NeedsIndex
    {
        get
        {
            lock (_gate)
            {
                return [.. _needsIndex];
            }
        }
    }

    /// <summary>Progress in [0, 1] of a session this processor handled; 0 when unknown.</summary>
    public double GetProgress(SessionId session) => GetStatus(session)?.Progress ?? 0;

    /// <summary>The last status of a session this processor handled since start, or <c>null</c>.</summary>
    public SessionProcessingStatus? GetStatus(SessionId session)
    {
        lock (_gate)
        {
            return _statuses.GetValueOrDefault(session);
        }
    }

    /// <summary>Queues a session for (re-)transcription. Sessions that are still recording or already queued are skipped.</summary>
    /// <returns>Whether a job was queued.</returns>
    /// <exception cref="KeyNotFoundException">No such session.</exception>
    public async ValueTask<bool> EnqueueAsync(SessionId session, CancellationToken cancellationToken = default)
    {
        var record = await _sessions.GetSessionAsync(session, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session {session} not found.");
        return record.State != SessionState.Recording && EnqueueTranscription(session, explicitRequest: true);
    }

    /// <summary>
    /// Start-up recovery: re-queues every session left Recorded or Transcribing for transcription. Sessions left
    /// Transcribed or Indexing are re-queued for indexing when an indexer is configured; without one they are
    /// promoted to Ready directly (their transcript is already saved).
    /// </summary>
    /// <returns>The number of jobs queued (direct promotions to Ready are not counted).</returns>
    public async ValueTask<int> ResumePendingAsync(CancellationToken cancellationToken = default)
    {
        var sessions = await _sessions.ListSessionsAsync(new SessionQuery(Limit: int.MaxValue), cancellationToken).ConfigureAwait(false);
        var queued = 0;
        foreach (var session in sessions)
        {
            if (session.State is SessionState.Transcribed or SessionState.Indexing && !CanIndex)
            {
                await UpdateAsync(session.Id, SessionState.Ready, null, cancellationToken).ConfigureAwait(false);
                if (_indexer is not null)
                {
                    MarkNeedsIndex(session.Id);
                }

                continue;
            }

            var added = session.State switch
            {
                SessionState.Recorded or SessionState.Transcribing => EnqueueTranscription(session.Id, explicitRequest: false),
                SessionState.Transcribed or SessionState.Indexing => EnqueueIndexing(session.Id),
                _ => false,
            };
            if (added)
            {
                queued++;
            }
        }

        return queued;
    }

    /// <summary>
    /// Queues indexing of the Ready sessions that were left without vectors: those in <see cref="NeedsIndex"/> and those
    /// whose Error carries <see cref="IndexingFailedCode"/> (or the text an earlier version wrote), plus those with a persisted <c>needs-index</c> marker
    /// (so both survive a restart). Call it when the AI models appear, and at start-up. Jobs wait in the queue while a
    /// recording is running.
    /// </summary>
    /// <param name="allReady">Also index every other Ready session (a full re-embed; indexing is idempotent).</param>
    /// <returns>The number of jobs queued; 0 when no indexer is configured or the AI is not installed.</returns>
    public async ValueTask<int> ReindexAllAsync(bool allReady = false, CancellationToken cancellationToken = default)
    {
        if (!CanIndex)
        {
            return 0;
        }

        var sessions = await _sessions.ListSessionsAsync(new SessionQuery(Limit: int.MaxValue), cancellationToken).ConfigureAwait(false);
        var queued = 0;
        foreach (var session in sessions)
        {
            if (session.State != SessionState.Ready)
            {
                continue;
            }

            bool wanted;
            lock (_gate)
            {
                wanted = allReady || _needsIndex.Contains(session.Id);
            }

            wanted = wanted || File.Exists(NeedsIndexMarkerPath(session.Id));

            if ((wanted || IndexingFailed(session.Error)) && EnqueueIndexing(session.Id, reindex: true))
            {
                queued++;
            }
        }

        return queued;
    }

    /// <summary>Cancels the session's queued or running transcription and indexing; <c>true</c> when something was cancelled. Waits until the running job has stopped, so the caller may delete the session afterwards.</summary>
    /// <remarks>
    /// A job that is still queued is dropped without running (it does not wait for the queue: a pause for a recording or a
    /// long job in front of it does not delay this call); a running one is stopped through its token (the ASR client then
    /// cancels its worker). The status of the session is cleared (<see cref="StatusCleared"/>, <see cref="GetStatus"/> is
    /// <c>null</c>); the session row is left as it was (e.g. Transcribing, picked up again by <see cref="ResumePendingAsync"/>
    /// at the next start), so a caller that does not delete the session may <see cref="EnqueueAsync"/> it again.
    /// </remarks>
    public async ValueTask<bool> CancelAsync(SessionId session, CancellationToken cancellationToken)
    {
        // Transcription first and awaited: it may queue the indexing job itself, which is then found below.
        var cancelled = await CancelJobAsync(_transcriptionJobs, session, cancellationToken).ConfigureAwait(false);
        cancelled |= await CancelJobAsync(_indexingJobs, session, cancellationToken).ConfigureAwait(false);
        if (cancelled)
        {
            ClearStatus(session);
        }

        return cancelled;
    }

    private async ValueTask<bool> CancelJobAsync(Dictionary<SessionId, JobEntry> jobs, SessionId session, CancellationToken cancellationToken)
    {
        JobEntry? entry;
        lock (_gate)
        {
            if (!jobs.TryGetValue(session, out entry) || !entry.IsLive)
            {
                return false;
            }
        }

        // A job not started yet never starts (the queue just finds it cancelled); a running one is waited for.
        var running = await entry.CancelAsync().ConfigureAwait(false);
        if (running is not null)
        {
            await running.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Stops following the recorder and lifts its pause of the queue. Queued jobs stay in the queue.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _recorder.StateChanged -= OnRecorderStateChanged;
            if (_pausedForRecording)
            {
                _pausedForRecording = false;
                _queue.Resume();
            }
        }
    }

    private static List<TranscriptSegment> Merge(List<TranscriptSegment> segments)
    {
        // Stable: on equal starts the microphone ("Me") segments, transcribed first, stay first.
        return [.. segments.OrderBy(s => s.Start)];
    }

    private void OnRecorderStateChanged(RecorderState state)
    {
        SessionId? stopped = null;
        var release = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (state != RecorderState.Idle)
            {
                if (_recorder.Current is { } current)
                {
                    _recordingSession = current.Id;
                }

                if (!_pausedForRecording)
                {
                    try
                    {
                        _queue.Pause();
                        _pausedForRecording = true;
                        release = true;
                    }
                    catch (ObjectDisposedException)
                    {
                        // The app is shutting down.
                    }
                }
            }
            else
            {
                stopped = _recordingSession;
                _recordingSession = null;
            }
        }

        if (release && _asr is WorkerAsrClient client)
        {
            _ = ReleaseWorkerAsync(client);
        }

        if (state != RecorderState.Idle)
        {
            return;
        }

        if (stopped is { } session)
        {
            EnqueueTranscription(session, explicitRequest: false);
        }

        lock (_gate)
        {
            if (_pausedForRecording)
            {
                _pausedForRecording = false;
                _queue.Resume();
            }
        }
    }

    private static async Task ReleaseWorkerAsync(WorkerAsrClient client)
    {
        try
        {
            await client.ShutdownWorkerAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: the worker also exits on its own when idle.
        }
    }

    private bool EnqueueTranscription(SessionId session, bool explicitRequest) =>
        TryEnqueue(_transcriptionJobs, session, JobPriority.Transcription, "transcribe ", SessionProcessingStage.Queued, 0, (entry, ct) => TranscribeAsync(session, explicitRequest, entry, ct));

    private bool EnqueueIndexing(SessionId session, bool reindex = false) =>
        _indexer is not null && TryEnqueue(_indexingJobs, session, JobPriority.Indexing, "index ", SessionProcessingStage.Indexing, 1, (_, ct) => IndexAsync(session, reindex, ct));

    private string NeedsIndexMarkerPath(SessionId session) => Path.Combine(_sessions.GetFiles(session).Directory, NeedsIndexMarker);

    private void MarkNeedsIndex(SessionId session)
    {
        lock (_gate)
        {
            _needsIndex.Add(session);
        }

        try
        {
            var path = NeedsIndexMarkerPath(session);
            if (Directory.Exists(Path.GetDirectoryName(path))) // never resurrect the folder of a deleted session
            {
                using var _ = File.Create(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the in-memory set still covers this run, and a failed indexing is also found by its Error text.
        }
    }

    private void ClearNeedsIndex(SessionId session)
    {
        lock (_gate)
        {
            _needsIndex.Remove(session);
        }

        try
        {
            File.Delete(NeedsIndexMarkerPath(session));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale marker only causes one redundant (idempotent) reindex.
        }
    }

    /// <summary>Whether an indexing job for the session is queued or running (or being queued).</summary>
    private bool HasActiveIndexJob(SessionId session)
    {
        lock (_gate)
        {
            return _indexingJobs.TryGetValue(session, out var job) && job.IsActive;
        }
    }

    /// <summary>Queues a job unless one for the same session is already pending or running in <paramref name="jobs"/>.</summary>
    private bool TryEnqueue(Dictionary<SessionId, JobEntry> jobs, SessionId session, JobPriority priority, string name, SessionProcessingStage stage, double progress, Func<JobEntry, CancellationToken, Task> work)
    {
        var entry = new JobEntry();
        lock (_gate)
        {
            if (_disposed || (jobs.TryGetValue(session, out var existing) && existing.IsActive))
            {
                return false;
            }

            jobs[session] = entry; // reserved until the handle exists
        }

        SetStatus(session, stage, progress);
        JobHandle job;
        try
        {
            job = _queue.Enqueue(priority, name + session, ct => RunJobAsync(entry, work, ct));
        }
        catch (ObjectDisposedException)
        {
            lock (_gate)
            {
                jobs.Remove(session);
            }

            ClearStatus(session);
            return false;
        }

        lock (_gate)
        {
            entry.Handle = job;
        }

        return true;
    }

    /// <summary>
    /// Runs the work under a token that also fires when <see cref="CancelAsync"/> cancels the entry. A cancelled entry never
    /// starts, and one cancelled while running ends quietly (so the queue neither re-queues nor reports it as failed).
    /// </summary>
    private static async Task RunJobAsync(JobEntry entry, Func<JobEntry, CancellationToken, Task> work, CancellationToken queueToken)
    {
        if (!entry.TryStart())
        {
            return;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(queueToken, entry.Token);
            await work(entry, linked.Token).ConfigureAwait(false);
        }
        catch (Exception) when (entry.IsCancelled)
        {
            // Cancelled by the user (possibly together with a pre-emption): the job is over, it does not run again.
        }
        finally
        {
            entry.MarkFinished();
        }
    }

    private async Task TranscribeAsync(SessionId id, bool explicitRequest, JobEntry entry, CancellationToken cancellationToken)
    {
        var session = await _sessions.GetSessionAsync(id, cancellationToken).ConfigureAwait(false);
        if (session is null || session.State == SessionState.Recording
            || (!explicitRequest && session.State is not (SessionState.Recorded or SessionState.Transcribing)))
        {
            // Deleted, still recording, or (automatic run) not ours to process, e.g. a recording that failed.
            ClearStatus(id);
            return;
        }

        if (_asr.Status == AsrStatus.ModelMissing)
        {
            if (session.State == SessionState.Transcribing)
            {
                await UpdateAsync(id, SessionState.Recorded, null, cancellationToken).ConfigureAwait(false);
            }

            SetStatus(id, SessionProcessingStage.ModelMissing, 0, AsrNotInstalledCode);
            return;
        }

        try
        {
            var files = _sessions.GetFiles(id);
            var tracks = new List<(string Path, Speaker Speaker)>(2);
            if (File.Exists(files.MicrophoneAudio))
            {
                tracks.Add((files.MicrophoneAudio, Speaker.Me));
            }

            if (File.Exists(files.SystemAudio))
            {
                tracks.Add((files.SystemAudio, Speaker.Others));
            }

            await UpdateAsync(id, SessionState.Transcribing, null, cancellationToken).ConfigureAwait(false);
            if (tracks.Count == 0)
            {
                throw new FileNotFoundException("The session has no audio tracks to transcribe.", files.MicrophoneAudio);
            }

            SetStatus(id, SessionProcessingStage.BuildingPeaks, 0);
            var paths = tracks.ConvertAll(t => t.Path);
            await Task.Run(() => PeaksBuilder.Build(paths, files.Peaks, cancellationToken), cancellationToken).ConfigureAwait(false);

            var language = _languageOf?.Invoke(id);
            var seconds = paths.ConvertAll(TrackSeconds);
            var totalSeconds = Math.Max(seconds.Sum(), 1e-9);
            var doneSeconds = 0.0;
            var segments = new List<TranscriptSegment>();
            SetStatus(id, SessionProcessingStage.Transcribing, PeaksWeight);
            for (var i = 0; i < tracks.Count; i++)
            {
                var offset = doneSeconds;
                var trackSeconds = seconds[i];
                var reporter = new ProgressReporter(p =>
                {
                    var processed = Math.Clamp(p.Processed.TotalSeconds, 0, trackSeconds);
                    SetStatus(id, SessionProcessingStage.Transcribing, PeaksWeight + ((1 - PeaksWeight - SaveWeight) * Math.Clamp((offset + processed) / totalSeconds, 0, 1)));
                });
                await foreach (var segment in _asr.TranscribeAsync(new AsrRequest(tracks[i].Path, tracks[i].Speaker, language), reporter, cancellationToken).ConfigureAwait(false))
                {
                    segments.Add(segment);
                }

                doneSeconds += trackSeconds;
            }

            SetStatus(id, SessionProcessingStage.Saving, 1 - SaveWeight);
            await _transcripts.SaveTranscriptAsync(new Transcript(id, Merge(segments), Engine), cancellationToken).ConfigureAwait(false);
            await UpdateAsync(id, SessionState.Transcribed, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Pre-empted by a recording or shutting down: stays Transcribing and runs again later.
            // (Cancelled by the user: no status, the caller of CancelAsync clears it.)
            if (!entry.IsCancelled)
            {
                SetStatus(id, SessionProcessingStage.Queued, 0);
            }

            throw;
        }
        catch (Exception ex)
        {
            await FailAsync(id, ex).ConfigureAwait(false);
            return;
        }

        if (entry.IsCancelled)
        {
            return; // Cancelled while saving: the transcript is kept, nothing follows (no indexing job is queued).
        }

        // An indexing job that is already pending or running (e.g. a ReindexAll queued before a manual re-transcription)
        // picks the Transcribed session up itself: IndexAsync chooses its path from the state it reads.
        if (CanIndex && (EnqueueIndexing(id) || HasActiveIndexJob(id)))
        {
            return;
        }

        // No indexer, or the AI is not installed (or no index job could be queued): Ready without vectors, indexed later.
        if (_indexer is not null)
        {
            MarkNeedsIndex(id);
        }

        await UpdateAsync(id, SessionState.Ready, null, CancellationToken.None).ConfigureAwait(false);
        SetStatus(id, SessionProcessingStage.Completed, 1);
    }

    /// <param name="reindex">The job was queued for a Ready session; whether it runs in place is decided by the state read at run time.</param>
    private async Task IndexAsync(SessionId id, bool reindex, CancellationToken cancellationToken)
    {
        // The path follows the state at run time, not the flag: a re-transcription may have moved a Ready session back
        // to Transcribed after this job was queued. Transcribed/Indexing walk Indexing -> Ready; Ready with reindex
        // indexes in place; anything else (deleted, recording, being transcribed...) is not ours.
        var session = await _sessions.GetSessionAsync(id, cancellationToken).ConfigureAwait(false);
        var fresh = session?.State is SessionState.Transcribed or SessionState.Indexing;
        if (!fresh && !(reindex && session?.State == SessionState.Ready))
        {
            ClearStatus(id);
            return;
        }

        reindex = !fresh;

        if (!CanIndex)
        {
            // The AI models went away after queuing: Ready without vectors, to be indexed later.
            MarkNeedsIndex(id);
            if (!reindex)
            {
                await UpdateAsync(id, SessionState.Ready, null, cancellationToken).ConfigureAwait(false);
            }

            SetStatus(id, SessionProcessingStage.Completed, 1);
            return;
        }

        try
        {
            if (!reindex)
            {
                await UpdateAsync(id, SessionState.Indexing, null, cancellationToken).ConfigureAwait(false);
            }

            SetStatus(id, SessionProcessingStage.Indexing, 1);
            await _indexer!(id, cancellationToken).ConfigureAwait(false);
            ClearNeedsIndex(id);
            await UpdateAsync(id, SessionState.Ready, null, cancellationToken).ConfigureAwait(false);
            SetStatus(id, SessionProcessingStage.Completed, 1);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Pre-empted by a recording: a fresh run stays Indexing (resumed later); a reindex stays in NeedsIndex.
            throw;
        }
        catch (Exception ex)
        {
            // The transcript is intact: Ready (keyword search works) with the reason in Error.
            MarkNeedsIndex(id);
            var message = SessionNotes.Format(IndexingFailedCode, string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message);
            await UpdateAsync(id, SessionState.Ready, message, CancellationToken.None).ConfigureAwait(false);
            SetStatus(id, SessionProcessingStage.Completed, 1, message);
        }
    }

    private static bool IndexingFailed(string? error) =>
        SessionNotes.Has(error, IndexingFailedCode) || error?.StartsWith(LegacyIndexingFailedPrefix, StringComparison.Ordinal) == true;

    private async Task FailAsync(SessionId id, Exception error)
    {
        var message = string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message;
        await UpdateAsync(id, SessionState.Failed, message, CancellationToken.None).ConfigureAwait(false);
        SetStatus(id, SessionProcessingStage.Failed, 0, message);
    }

    /// <summary>Re-reads the row so concurrent edits (title) are kept, then stores the new state.</summary>
    private async Task UpdateAsync(SessionId id, SessionState state, string? error, CancellationToken cancellationToken)
    {
        var current = await _sessions.GetSessionAsync(id, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return;
        }

        await _sessions.UpdateSessionAsync(current with { State = state, Error = error }, cancellationToken).ConfigureAwait(false);
    }

    private static double TrackSeconds(string path)
    {
        using var reader = WavReader.Open(path);
        return reader.Duration.TotalSeconds;
    }

    private void SetStatus(SessionId id, SessionProcessingStage stage, double progress, string? message = null)
    {
        var status = new SessionProcessingStatus(id, stage, progress, message);
        lock (_gate)
        {
            _statuses[id] = status;
        }

        ProgressChanged?.Invoke(status);
    }

    private void ClearStatus(SessionId id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _statuses.Remove(id);
        }

        // Outside the lock, like ProgressChanged: the session is no longer handled here (deleted, or gone while it waited).
        if (removed)
        {
            StatusCleared?.Invoke(id);
        }
    }

    /// <summary>One queued job of a session: its handle (set once queued) and the token <see cref="CancelAsync"/> fires.</summary>
    private sealed class JobEntry
    {
        private readonly object _lock = new();
        private readonly CancellationTokenSource _cts = new();
        private bool _cancelled;

        // Completed when the current run ends; null between runs (a pre-empted job runs more than once).
        private TaskCompletionSource? _running;

        /// <summary>Set under the processor's lock once the job is queued; <c>null</c> until then.</summary>
        public JobHandle? Handle { get; set; }

        public bool IsCancelled
        {
            get
            {
                lock (_lock)
                {
                    return _cancelled;
                }
            }
        }

        public CancellationToken Token => _cts.Token;

        /// <summary>The queue has not finished the job yet (it is being queued, pending or running); read under the processor's lock.</summary>
        public bool IsLive => Handle?.State is null or JobState.Pending or JobState.Running;

        /// <summary>Live and not cancelled: a new job for the session is refused meanwhile.</summary>
        public bool IsActive => IsLive && !IsCancelled;

        /// <summary>The queue starts the job: <c>false</c> when it was cancelled before, so it never runs.</summary>
        public bool TryStart()
        {
            lock (_lock)
            {
                if (_cancelled)
                {
                    return false;
                }

                _running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return true;
            }
        }

        public void MarkFinished()
        {
            TaskCompletionSource? running;
            lock (_lock)
            {
                running = _running;
                _running = null;
            }

            running?.TrySetResult();
        }

        /// <summary>Cancels the job; returns what to wait for when it already runs, else <c>null</c>.</summary>
        public async ValueTask<Task?> CancelAsync()
        {
            Task? running;
            lock (_lock)
            {
                _cancelled = true;
                running = _running?.Task;
            }

            await _cts.CancelAsync().ConfigureAwait(false);
            return running;
        }
    }

    /// <summary>Synchronous <see cref="IProgress{T}"/> (no <see cref="SynchronizationContext"/> capture).</summary>
    private sealed class ProgressReporter(Action<AsrProgress> report) : IProgress<AsrProgress>
    {
        public void Report(AsrProgress value) => report(value);
    }
}
