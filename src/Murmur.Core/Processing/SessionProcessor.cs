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
/// <param name="Message">Error text for <see cref="SessionProcessingStage.Failed"/>, the reason for <see cref="SessionProcessingStage.ModelMissing"/>.</param>
public sealed record SessionProcessingStatus(SessionId Session, SessionProcessingStage Stage, double Progress, string? Message = null);

/// <summary>
/// Post-recording pipeline: build peaks -&gt; transcribe every track -&gt; save the transcript -&gt; index (when AI is installed).
/// </summary>
/// <remarks>
/// <para><b>When.</b> After every recording that stops in <see cref="SessionState.Recorded"/> (via
/// <see cref="IRecorder.StateChanged"/>), for <see cref="EnqueueAsync"/> (manual retry, any state but Recording) and,
/// at start-up, <see cref="ResumePendingAsync"/>. The work runs as a <see cref="JobPriority.Transcription"/> job on the
/// shared <see cref="JobQueue"/>; the processor pauses that queue for as long as the recorder is not idle (a running
/// job is pre-empted and restarted after the recording) and stops the ASR worker so no model is in memory while
/// recording.</para>
/// <para><b>States.</b> Recorded -&gt; Transcribing -&gt; Transcribed -&gt; (Indexing -&gt;) Ready. Without an indexer, or
/// while the AI models are not installed, the session becomes Ready right after Transcribed (keyword search still
/// works) and is remembered in <see cref="NeedsIndex"/> and by a <c>needs-index</c> marker file in the session folder
/// (so it survives a restart); <see cref="ReindexAllAsync"/> indexes those later, when the AI models appear. A transcription failure -&gt; Failed with <see cref="SessionRecord.Error"/>. An indexing failure keeps
/// the transcript: the session is Ready with <see cref="SessionRecord.Error"/> =
/// <see cref="IndexingFailedPrefix"/> + reason, and stays in <see cref="NeedsIndex"/>. A pre-empted or cancelled job leaves the session Transcribing (or Indexing) so it is resumed later. With the ASR
/// not installed the session is left Recorded and the status is <see cref="SessionProcessingStage.ModelMissing"/>
/// (<see cref="AsrNotInstalledMessage"/>).</para>
/// <para><b>Tracks.</b> <c>mic.wav</c> is transcribed as <see cref="Speaker.Me"/>, <c>system.wav</c> as
/// <see cref="Speaker.Others"/> (only the tracks that exist); segments are merged by start time.</para>
/// <para><b>Threads.</b> <see cref="ProgressChanged"/> is raised on the job's thread (never the recorder's);
/// subscribers must marshal to the UI thread.</para>
/// </remarks>
public sealed class SessionProcessor : IDisposable
{
    public const string Engine = "parakeet-tdt-0.6b-v3-int8";

    public const string AsrNotInstalledMessage = "Модель Parakeet не встановлена";

    /// <summary>Start of <see cref="SessionRecord.Error"/> of a Ready session whose indexing failed.</summary>
    public const string IndexingFailedPrefix = "Індексування не вдалося: ";

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
    private readonly object _gate = new();
    private readonly HashSet<SessionId> _needsIndex = [];
    private readonly Dictionary<SessionId, SessionProcessingStatus> _statuses = [];
    private readonly Dictionary<SessionId, JobHandle?> _transcriptionJobs = [];
    private readonly Dictionary<SessionId, JobHandle?> _indexingJobs = [];

    private SessionId? _recordingSession;
    private bool _pausedForRecording;
    private bool _disposed;

    /// <param name="indexer">Phase 3 hook (<c>IAssistant.IndexSessionAsync</c>), run as an <see cref="JobPriority.Indexing"/> job.</param>
    /// <param name="isAiInstalled">Whether the AI models are installed (checked every time, so installing them later takes
    /// effect without a restart); <c>null</c> = always installed. Ignored without an <paramref name="indexer"/>.</param>
    public SessionProcessor(IRecorder recorder, ISessionStore sessions, ITranscriptStore transcripts, IAsr asr, JobQueue queue, Func<SessionId, CancellationToken, Task>? indexer = null, Func<bool>? isAiInstalled = null)
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

        _recorder.StateChanged += OnRecorderStateChanged;
        OnRecorderStateChanged(_recorder.State);
    }

    /// <summary>Raised whenever the processing status of a session changes.</summary>
    public event Action<SessionProcessingStatus>? ProgressChanged;

    public bool IsAsrInstalled => _asr.Status != AsrStatus.ModelMissing;

    /// <summary><see cref="AsrNotInstalledMessage"/> when the model is missing, else <c>null</c>.</summary>
    public string? AsrStatusMessage => IsAsrInstalled ? null : AsrNotInstalledMessage;

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
    /// whose Error starts with <see cref="IndexingFailedPrefix"/>, plus those with a persisted <c>needs-index</c> marker
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

            if ((wanted || session.Error?.StartsWith(IndexingFailedPrefix, StringComparison.Ordinal) == true) && EnqueueIndexing(session.Id, reindex: true))
            {
                queued++;
            }
        }

        return queued;
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
        TryEnqueue(_transcriptionJobs, session, JobPriority.Transcription, "transcribe ", SessionProcessingStage.Queued, 0, ct => TranscribeAsync(session, explicitRequest, ct));

    private bool EnqueueIndexing(SessionId session, bool reindex = false) =>
        _indexer is not null && TryEnqueue(_indexingJobs, session, JobPriority.Indexing, "index ", SessionProcessingStage.Indexing, 1, ct => IndexAsync(session, reindex, ct));

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
            return _indexingJobs.TryGetValue(session, out var job) && job?.State is null or JobState.Pending or JobState.Running;
        }
    }

    /// <summary>Queues a job unless one for the same session is already pending or running in <paramref name="jobs"/>.</summary>
    private bool TryEnqueue(Dictionary<SessionId, JobHandle?> jobs, SessionId session, JobPriority priority, string name, SessionProcessingStage stage, double progress, Func<CancellationToken, Task> work)
    {
        lock (_gate)
        {
            if (_disposed || (jobs.TryGetValue(session, out var existing) && existing?.State is null or JobState.Pending or JobState.Running))
            {
                return false;
            }

            jobs[session] = null; // reserved until the handle exists
        }

        SetStatus(session, stage, progress);
        JobHandle job;
        try
        {
            job = _queue.Enqueue(priority, name + session, work);
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
            jobs[session] = job;
        }

        return true;
    }

    private async Task TranscribeAsync(SessionId id, bool explicitRequest, CancellationToken cancellationToken)
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

            SetStatus(id, SessionProcessingStage.ModelMissing, 0, AsrNotInstalledMessage);
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
                await foreach (var segment in _asr.TranscribeAsync(new AsrRequest(tracks[i].Path, tracks[i].Speaker), reporter, cancellationToken).ConfigureAwait(false))
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
            SetStatus(id, SessionProcessingStage.Queued, 0);
            throw;
        }
        catch (Exception ex)
        {
            await FailAsync(id, ex).ConfigureAwait(false);
            return;
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
            var message = IndexingFailedPrefix + (string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message);
            await UpdateAsync(id, SessionState.Ready, message, CancellationToken.None).ConfigureAwait(false);
            SetStatus(id, SessionProcessingStage.Completed, 1, message);
        }
    }

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
        lock (_gate)
        {
            _statuses.Remove(id);
        }
    }

    /// <summary>Synchronous <see cref="IProgress{T}"/> (no <see cref="SynchronizationContext"/> capture).</summary>
    private sealed class ProgressReporter(Action<AsrProgress> report) : IProgress<AsrProgress>
    {
        public void Report(AsrProgress value) => report(value);
    }
}
