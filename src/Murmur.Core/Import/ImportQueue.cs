using System.Buffers;
using Murmur.Core.Jobs;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;

namespace Murmur.Core.Import;

/// <summary>
/// The import pipeline: a file becomes a session (one mixed track, stored as the system track, so its speaker is
/// "Others") that then goes through the same <see cref="SessionProcessor"/> path a stopped recording takes
/// (Recorded -&gt; Transcribing -&gt; Indexing -&gt; Ready).
/// </summary>
/// <remarks>
/// <para><b>One at a time.</b> Each import is one job on the shared <see cref="JobQueue"/> (priority
/// <see cref="JobPriority.Transcription"/>); the next import is queued only when the previous one finished, so files are
/// decoded one by one, off the UI thread. The <see cref="SessionProcessor"/> pauses that queue for as long as a recording
/// runs: an import waits, and one that was decoding is pre-empted (its half-made session is deleted) and starts again
/// after the recording. No model is involved in decoding; the transcription the hand-off queues follows the processor's
/// rules (never during a recording).</para>
/// <para><b>Steps of an import.</b> Probe the file (<see cref="IMediaDecoder.ProbeAsync"/>), create the session (title =
/// file name without extension, underscores to spaces; started at = the file's creation time, else its last write time)
/// and mark it right away as <see cref="SessionSource.Imported"/> with its final <see cref="SessionRecord.OriginalPath"/>,
/// copy the original into the session folder as <c>original&lt;ext&gt;</c> (<see cref="OriginalFileMode.CopyToLibrary"/>)
/// or only remember its path (<see cref="OriginalFileMode.LeaveInPlace"/>), decode the first audio stream into
/// <see cref="SessionFiles.SystemAudio"/>, and for an <c>.mp4</c> video put the file in as screen segment 0
/// (<c>screen-000.mp4</c>). Copied to the library: a hard link to the copy, else a symbolic link, else a copy. Left in
/// place: only a symbolic link (a hard link to a read-only original would make the session folder undeletable, and a copy
/// is what the user declined), else no video. The session becomes <see cref="SessionState.Recorded"/> and is handed to
/// <see cref="SessionProcessor.EnqueueAsync"/>. While it is decoded the session is <see cref="SessionState.Recording"/>
/// (what a new session is), so the processor never picks it up early; the start-up recovery leaves imported sessions to
/// this queue.</para>
/// <para><b>Persistence.</b> Every import is stored in <see cref="IImportStore"/>, whose entry goes with its session. So
/// before a half-made session is deleted to start again, the stored entry first forgets it (Queued, no session); when that
/// save fails the session is kept. <see cref="StartAsync"/> (once at start-up, after the crash recovery and before
/// <see cref="SessionProcessor.ResumePendingAsync"/>) restarts the imports that were queued or decoding (a half-made
/// session is deleted first; one whose decode had finished is handed off) and deletes the half-made imported sessions no
/// entry refers to (a crash between creating the session and saving the entry). Done entries stay stored with their
/// session so <see cref="LanguageOf"/> still knows the spoken language after a restart. Failed and cancelled entries are
/// removed from the store and stay in <see cref="Items"/> until dismissed.</para>
/// <para><b>Errors.</b> <see cref="ImportItem.Error"/> holds a code (the App words it), optionally followed by
/// <c>|detail</c> (<see cref="SessionNotes.Format"/>): <see cref="UnsupportedCode"/>, <see cref="DamagedCode"/>,
/// <see cref="MissingCode"/>, <see cref="FailedCode"/>. A failed file never stops the queue. A half-made session the user
/// deleted while it was decoded makes the import <see cref="ImportState.Cancelled"/>.</para>
/// </remarks>
public sealed class ImportQueue : IImportQueue, IDisposable
{
    /// <summary>The file type is not supported (rejected by <see cref="EnqueueAsync"/>, or the decoder cannot open the format).</summary>
    public const string UnsupportedCode = "proc.import.unsupported";

    /// <summary>The file is damaged or holds no audio; the detail is the reason.</summary>
    public const string DamagedCode = "proc.import.damaged";

    /// <summary>The file is gone (moved or deleted before it was imported).</summary>
    public const string MissingCode = "proc.import.missing";

    /// <summary>Anything else (disk full, access denied...); the detail is the reason.</summary>
    public const string FailedCode = "proc.import.failed";

    /// <summary>Share of the progress taken by copying the original when it goes to the library.</summary>
    internal const double CopyWeight = 0.2;

    private const int CopyBufferBytes = 1 << 20;

    private readonly ISessionStore _sessions;
    private readonly IImportStore _store;
    private readonly IMediaDecoder _decoder;
    private readonly JobQueue _queue;
    private readonly SessionProcessor _processor;
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<SessionId, string?> _languages = [];

    private Entry? _inFlight;
    private long _transientId;
    private int _changedRequested;
    private int _dispatching;
    private bool _started;
    private bool _disposed;

    /// <param name="queue">The job queue the <paramref name="processor"/> works on (it pauses it while recording).</param>
    public ImportQueue(ISessionStore sessions, IImportStore store, IMediaDecoder decoder, JobQueue queue, SessionProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(processor);
        _sessions = sessions;
        _store = store;
        _decoder = decoder;
        _queue = queue;
        _processor = processor;
    }

    /// <summary>Raised on the thread pool after changes of <see cref="Items"/>: coalesced, one handler call at a time, never concurrently.</summary>
    public event Action? Changed;

    public IReadOnlySet<string> SupportedExtensions => _decoder.Extensions;

    /// <summary>The imports of this run and the unfinished ones of earlier runs, in the order they were queued.</summary>
    public IReadOnlyList<ImportItem> Items
    {
        get
        {
            lock (_gate)
            {
                var items = new ImportItem[_entries.Count];
                for (var i = 0; i < items.Length; i++)
                {
                    items[i] = _entries[i].Snapshot();
                }

                return items;
            }
        }
    }

    /// <summary>Test seam: <c>File.CreateHardLink(link, target)</c>.</summary>
    internal Action<string, string> CreateHardLink { get; set; } = static (link, target) => File.CreateHardLink(link, target);

    /// <summary>Test seam: <c>File.CreateSymbolicLink(link, target)</c>.</summary>
    internal Action<string, string> CreateSymbolicLink { get; set; } = static (link, target) => File.CreateSymbolicLink(link, target);

    /// <summary>
    /// The spoken language chosen for the import that made <paramref name="session"/> (<c>null</c> = auto-detect, or not an
    /// import). The composition root passes it to <see cref="SessionProcessor"/> as its language hint.
    /// </summary>
    public string? LanguageOf(SessionId session)
    {
        lock (_gate)
        {
            return _languages.GetValueOrDefault(session);
        }
    }

    /// <summary>
    /// Loads the stored queue and starts importing. Unfinished imports of an earlier run go first, in their order; half-made
    /// imported sessions no stored entry refers to are deleted. Nothing is imported before this is called; later calls do
    /// nothing.
    /// </summary>
    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }
        }

        var stored = await _store.LoadImportsAsync(cancellationToken).ConfigureAwait(false);
        var resumed = new List<Entry>();
        var referenced = new HashSet<SessionId>();
        foreach (var import in stored)
        {
            var item = import.Item;
            if (item.Session is { } session)
            {
                referenced.Add(session);
            }

            switch (item.State)
            {
                case ImportState.Done:
                    if (item.Session is { } done)
                    {
                        lock (_gate)
                        {
                            _languages[done] = import.Request.Options.Language;
                        }
                    }

                    break;

                case ImportState.Queued or ImportState.Decoding:
                    // A decode that was running when the app stopped starts again; its half-made session goes first.
                    resumed.Add(new Entry(item.Id, import.Request) { Session = item.Session });
                    break;

                default:
                    await _store.DeleteImportAsync(item.Id, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        await DeleteOrphansAsync(referenced, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            _entries.InsertRange(0, resumed);
        }

        RaiseChanged();
        Pump();
    }

    /// <summary>
    /// Queues the files in order. A file whose extension no decoder takes is rejected here (Failed with
    /// <see cref="UnsupportedCode"/>, nothing written); a request that cannot be stored (an invalid path, a storage error)
    /// becomes a Failed item too; the others are queued. Failed items have negative ids and are not stored.
    /// </summary>
    public async ValueTask<IReadOnlyList<ImportItem>> EnqueueAsync(IReadOnlyList<ImportRequest> requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        foreach (var request in requests)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);
            ArgumentNullException.ThrowIfNull(request.Options);
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        var result = new ImportItem[requests.Count];
        try
        {
            for (var i = 0; i < requests.Count; i++)
            {
                var request = requests[i];
                Entry entry;
                if (!SupportedExtensions.Contains(CompositeMediaDecoder.ExtensionOf(request.Path)))
                {
                    // Rejected before anything is written: no row, no session. Negative ids never collide with stored ones.
                    entry = Rejected(request, UnsupportedCode);
                }
                else
                {
                    try
                    {
                        var full = request with { Path = Path.GetFullPath(request.Path) };
                        var saved = await _store.SaveImportAsync(
                            new StoredImport(new ImportItem(0, full.Path, ImportState.Queued, 0, null, null), full),
                            cancellationToken).ConfigureAwait(false);
                        entry = new Entry(saved.Id, full);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        entry = Rejected(request, ErrorOf(ex));
                    }
                }

                lock (_gate)
                {
                    _entries.Add(entry);
                    result[i] = entry.Snapshot();
                }
            }
        }
        finally
        {
            RaiseChanged();
            Pump();
        }

        return result;
    }

    /// <summary>
    /// A queued import is removed; a decoding one is stopped, its half-made session deleted, and it stays in
    /// <see cref="Items"/> as <see cref="ImportState.Cancelled"/>. A finished item (Done, Failed, Cancelled) is dismissed
    /// from <see cref="Items"/> (a Done session is kept). Returns when the decode has stopped and the session is gone; the
    /// clean-up runs to the end even when <paramref name="cancellationToken"/> is cancelled. An unknown id is a no-op.
    /// </summary>
    public async ValueTask CancelAsync(long id, CancellationToken cancellationToken)
    {
        Entry? entry;
        Task? attempt = null;
        lock (_gate)
        {
            entry = _entries.Find(e => e.Id == id);
            if (entry is null)
            {
                return;
            }

            if (entry.State is ImportState.Done or ImportState.Failed or ImportState.Cancelled)
            {
                _entries.Remove(entry);
                entry = null;
            }
            else
            {
                entry.CancelRequested = true;
                if (entry.Running)
                {
                    attempt = entry.Attempt?.Task;
                }
            }
        }

        if (entry is null)
        {
            RaiseChanged();
            return;
        }

        await entry.Cancel.CancelAsync().ConfigureAwait(false);
        if (attempt is not null)
        {
            // Its token is cancelled, so the attempt ends promptly; not waiting for it would leave the half-made session behind.
            await attempt.ConfigureAwait(false);
        }

        await AbandonAsync(entry).ConfigureAwait(false);
    }

    /// <summary>Stops queuing further imports. A running decode is stopped by the job queue's disposal (it restarts next time).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }

    /// <summary>Session title of an imported file: the file name without extension, underscores to spaces.</summary>
    internal static string TitleOf(string path)
    {
        var title = Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Trim();
        return title.Length > 0 ? title : Path.GetFileName(path);
    }

    /// <summary>When the imported media started: the file's creation time, else (unknown on this file system) its last write time.</summary>
    internal static DateTimeOffset StartedAtOf(string path) =>
        StartedAtOf(File.GetCreationTimeUtc(path), File.GetLastWriteTimeUtc(path), DateTime.UtcNow);

    internal static DateTimeOffset StartedAtOf(DateTime createdUtc, DateTime writtenUtc, DateTime nowUtc)
    {
        // An unset time reads as 1601-01-01 (Windows) or 1970-01-01 (Unix); one in the future is a broken clock.
        var floor = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var valid = createdUtc > floor && createdUtc <= nowUtc.AddDays(1);
        var at = valid ? createdUtc : writtenUtc;
        return new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc));
    }

    /// <summary>The <see cref="ImportItem.Error"/> for a failure while importing.</summary>
    internal static string ErrorOf(Exception error) => error switch
    {
        NotSupportedException => UnsupportedCode,
        FileNotFoundException or DirectoryNotFoundException => MissingCode,
        InvalidDataException or EndOfStreamException => SessionNotes.Format(DamagedCode, error.Message),
        _ => SessionNotes.Format(FailedCode, string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message),
    };

    private Entry Rejected(ImportRequest request, string error) =>
        new(Interlocked.Decrement(ref _transientId), request) { State = ImportState.Failed, Error = error };

    /// <summary>
    /// Deletes imported sessions that were left half-made and that no stored entry refers to: still Recording, or Failed
    /// without a finished track. Best effort.
    /// </summary>
    private async Task DeleteOrphansAsync(HashSet<SessionId> referenced, CancellationToken cancellationToken)
    {
        var sessions = await _sessions.ListSessionsAsync(new SessionQuery(Limit: int.MaxValue), cancellationToken).ConfigureAwait(false);
        foreach (var session in sessions)
        {
            if (session.Source != SessionSource.Imported || referenced.Contains(session.Id))
            {
                continue;
            }

            var halfMade = session.State == SessionState.Recording
                || (session.State == SessionState.Failed && !File.Exists(_sessions.GetFiles(session.Id).SystemAudio));
            if (!halfMade)
            {
                continue;
            }

            try
            {
                await _sessions.DeleteSessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Tried again at the next start.
            }
        }
    }

    private void Pump()
    {
        Entry? next = null;
        lock (_gate)
        {
            if (_disposed || !_started || _inFlight is not null)
            {
                return;
            }

            foreach (var entry in _entries)
            {
                if (entry.State == ImportState.Queued && !entry.CancelRequested && !entry.Deferred)
                {
                    next = entry;
                    break;
                }
            }

            if (next is null)
            {
                return;
            }

            _inFlight = next;
        }

        JobHandle job;
        try
        {
            job = _queue.Enqueue(JobPriority.Transcription, "import " + next.Id, ct => RunAsync(next, ct));
        }
        catch (ObjectDisposedException)
        {
            // Shutting down: the import stays stored and restarts next time.
            lock (_gate)
            {
                _inFlight = null;
            }

            return;
        }

        _ = job.Completion.ContinueWith(
            static (_, state) => ((ImportQueue)state!).OnJobFinished(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void OnJobFinished()
    {
        lock (_gate)
        {
            _inFlight = null;
        }

        Pump();
    }

    private async Task RunAsync(Entry entry, CancellationToken jobToken)
    {
        TaskCompletionSource attempt;
        lock (_gate)
        {
            if (entry.CancelRequested || entry.State != ImportState.Queued)
            {
                return; // cancelled while it waited
            }

            entry.Running = true;
            attempt = entry.Attempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        try
        {
            await ImportAsync(entry, jobToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                entry.Running = false;
            }

            attempt.TrySetResult();
        }
    }

    private async Task ImportAsync(Entry entry, CancellationToken jobToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(jobToken, entry.Cancel.Token);
        var cancellationToken = linked.Token;
        var request = entry.Request;
        try
        {
            if (entry.Session is { } previous)
            {
                // Left by an earlier attempt (pre-empted, or the app stopped while decoding).
                var old = await _sessions.GetSessionAsync(previous, cancellationToken).ConfigureAwait(false);
                if (old is not null && old.State is not (SessionState.Recording or SessionState.Interrupted or SessionState.Failed))
                {
                    // Its decode had finished: the app stopped before the import was marked done.
                    await CompleteAsync(entry, previous).ConfigureAwait(false);
                    return;
                }

                if (!await ReleaseSessionAsync(entry).ConfigureAwait(false))
                {
                    // The leftover cannot be deleted now (a file held open): stays queued, tried again at the next start.
                    Update(entry, e => e.Deferred = true);
                    return;
                }
            }

            Update(entry, e =>
            {
                e.State = ImportState.Decoding;
                e.Progress = 0;
            });
            await PersistAsync(entry, cancellationToken).ConfigureAwait(false);

            var path = request.Path;
            var probe = await _decoder.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
            var created = await _sessions.CreateSessionAsync(TitleOf(path), StartedAtOf(path), cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                entry.Session = created.Id;
                _languages[created.Id] = request.Options.Language;
            }

            // Marked as an import at once, so the library, the recovery and the session screen never take it for a dead recording.
            var files = _sessions.GetFiles(created.Id);
            var extension = CompositeMediaDecoder.ExtensionOf(path);
            var copy = request.Options.OriginalFile == OriginalFileMode.CopyToLibrary;
            var source = copy ? Path.Combine(files.Directory, "original" + extension) : Path.GetFullPath(path);
            var record = created with
            {
                Source = SessionSource.Imported,
                OriginalPath = source,
                Duration = probe.Duration,
                HasMicrophone = false,
                HasSystemAudio = true,
                State = SessionState.Recording,
            };
            await _sessions.UpdateSessionAsync(record, cancellationToken).ConfigureAwait(false);
            await PersistAsync(entry, cancellationToken).ConfigureAwait(false);

            var decodeFrom = 0.0;
            if (copy)
            {
                await CopyAsync(path, source, new Reporter(p => SetProgress(entry, p * CopyWeight)), cancellationToken).ConfigureAwait(false);
                decodeFrom = CopyWeight;
            }

            await _decoder.DecodeAudioAsync(source, files.SystemAudio, new Reporter(p => SetProgress(entry, decodeFrom + ((1 - decodeFrom) * Math.Clamp(p, 0, 1)))), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<ScreenSegment> segments = [];
            if (probe.Kind == MediaKind.Video && extension == ".mp4" && await TryAddVideoAsync(source, files.ScreenSegment(0), copy, cancellationToken).ConfigureAwait(false))
            {
                segments = [new ScreenSegment(0, TimeSpan.Zero, probe.Duration)];
            }

            await _sessions.UpdateSessionAsync(record with { ScreenSegments = segments, State = SessionState.Recorded }, cancellationToken).ConfigureAwait(false);
            await CompleteAsync(entry, created.Id).ConfigureAwait(false);
        }
        catch (Exception) when (entry.CancelRequested)
        {
            // CancelAsync cleans up once this attempt returned.
        }
        catch (Exception) when (jobToken.IsCancellationRequested)
        {
            // Pre-empted by a recording (or the app is closing): back to the queue, from scratch, after it. When the half-made
            // session cannot be released now, the entry keeps it and the next attempt (or start) deletes it first.
            if (entry.Session is null)
            {
                Update(entry, e =>
                {
                    e.State = ImportState.Queued;
                    e.Progress = 0;
                });
                await TryPersistAsync(entry).ConfigureAwait(false);
            }
            else
            {
                await ReleaseSessionAsync(entry).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (await SessionDeletedByUserAsync(entry).ConfigureAwait(false))
            {
                // The user deleted the half-made session from the library: that is a cancellation, not a broken file.
                Update(entry, e =>
                {
                    e.State = ImportState.Cancelled;
                    e.Session = null;
                    e.Progress = 0;
                });
                await TryDeleteImportAsync(entry.Id).ConfigureAwait(false);
                return;
            }

            // The entry first (so a session that cannot be deleted is never resumed), then the session.
            await TryDeleteImportAsync(entry.Id).ConfigureAwait(false);
            await TryDeleteSessionAsync(entry).ConfigureAwait(false);
            Update(entry, e =>
            {
                e.State = ImportState.Failed;
                e.Session = null;
                e.Progress = 0;
                e.Error = ErrorOf(ex);
            });
        }
    }

    /// <summary>
    /// Back to Queued without the half-made session. The stored entry forgets the session first (deleting the session
    /// deletes the entries that refer to it), then the session is deleted. A failed save keeps the session (and the entry
    /// still pointing at it); a failed delete puts the session back into the entry, so it is deleted before the next try.
    /// </summary>
    /// <returns>Whether the session is gone.</returns>
    private async Task<bool> ReleaseSessionAsync(Entry entry)
    {
        SessionId previous;
        lock (_gate)
        {
            if (entry.Session is not { } session)
            {
                return true;
            }

            previous = session;
            entry.Session = null;
            entry.State = ImportState.Queued;
            entry.Progress = 0;
        }

        RaiseChanged();
        try
        {
            await PersistAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Update(entry, e => e.Session = previous);
            return false;
        }

        try
        {
            await _sessions.DeleteSessionAsync(previous, CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                _languages.Remove(previous);
            }

            return true;
        }
        catch (Exception)
        {
            Update(entry, e => e.Session = previous);
            await TryPersistAsync(entry).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>Whether the import's session no longer exists although this import created it (deleted from the library).</summary>
    private async Task<bool> SessionDeletedByUserAsync(Entry entry)
    {
        SessionId? session;
        lock (_gate)
        {
            session = entry.Session;
        }

        if (session is not { } id)
        {
            return false;
        }

        try
        {
            return await _sessions.GetSessionAsync(id, CancellationToken.None).ConfigureAwait(false) is null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The session is Recorded: mark the import done and hand the session to the pipeline.</summary>
    private async Task CompleteAsync(Entry entry, SessionId session)
    {
        Update(entry, e =>
        {
            e.State = ImportState.Done;
            e.Progress = 1;
            e.Session = session;
            e.Error = null;
        });
        await TryPersistAsync(entry).ConfigureAwait(false);
        try
        {
            await _processor.EnqueueAsync(session, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Deleted by the user in the meantime (nothing to transcribe), or the processor is shutting down: the session is
            // Recorded, so the processor's start-up resume picks it up.
        }
    }

    /// <summary>Removes a cancelled import: its stored entry, then its half-made session. Idempotent.</summary>
    private async Task AbandonAsync(Entry entry)
    {
        bool remove;
        lock (_gate)
        {
            // Already cleaned up, or the decode finished (or failed) before the cancellation reached it: nothing to undo.
            if (entry.State is ImportState.Cancelled or ImportState.Done or ImportState.Failed || !_entries.Contains(entry))
            {
                return;
            }

            // A decode that ran is shown as cancelled; one that only waited simply disappears.
            remove = entry.State == ImportState.Queued;
            entry.State = ImportState.Cancelled;
            entry.Progress = 0;
            if (remove)
            {
                _entries.Remove(entry);
            }
        }

        await TryDeleteImportAsync(entry.Id).ConfigureAwait(false);
        await TryDeleteSessionAsync(entry).ConfigureAwait(false);
        lock (_gate)
        {
            entry.Session = null;
        }

        RaiseChanged();
    }

    private async Task TryDeleteSessionAsync(Entry entry)
    {
        SessionId? session;
        lock (_gate)
        {
            session = entry.Session;
        }

        if (session is not { } id)
        {
            return;
        }

        try
        {
            await _sessions.DeleteSessionAsync(id, CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                _languages.Remove(id);
            }
        }
        catch (Exception)
        {
            // No entry refers to it any more: the orphan sweep of the next start deletes it.
        }
    }

    private Task PersistAsync(Entry entry, CancellationToken cancellationToken)
    {
        StoredImport import;
        lock (_gate)
        {
            import = new StoredImport(entry.Snapshot(), entry.Request);
        }

        return _store.SaveImportAsync(import, cancellationToken).AsTask();
    }

    private async Task TryPersistAsync(Entry entry)
    {
        try
        {
            await PersistAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: the worst case is one redundant restart of the import next time.
        }
    }

    private async Task TryDeleteImportAsync(long id)
    {
        try
        {
            await _store.DeleteImportAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A leftover failed entry is dropped at the next start.
        }
    }

    private void Update(Entry entry, Action<Entry> change)
    {
        lock (_gate)
        {
            change(entry);
        }

        RaiseChanged();
    }

    private void SetProgress(Entry entry, double progress) => Update(entry, e => e.Progress = Math.Clamp(progress, 0, 1));

    private void RaiseChanged()
    {
        Volatile.Write(ref _changedRequested, 1);
        if (Interlocked.CompareExchange(ref _dispatching, 1, 0) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static queue => queue.DispatchChanged(), this, preferLocal: false);
        }
    }

    /// <summary>One dispatcher at a time; a change requested while it runs is delivered by the same dispatcher afterwards.</summary>
    private void DispatchChanged()
    {
        while (true)
        {
            while (Interlocked.Exchange(ref _changedRequested, 0) == 1)
            {
                try
                {
                    Changed?.Invoke();
                }
                catch (Exception)
                {
                    // A faulty subscriber must not stop the imports.
                }
            }

            Volatile.Write(ref _dispatching, 0);
            if (Volatile.Read(ref _changedRequested) == 0 || Interlocked.CompareExchange(ref _dispatching, 1, 0) != 0)
            {
                return;
            }
        }
    }

    /// <summary>Puts the video at <paramref name="destination"/>; see the remarks of the class for the order of the attempts.</summary>
    private async Task<bool> TryAddVideoAsync(string source, string destination, bool copiedToLibrary, CancellationToken cancellationToken)
    {
        if (copiedToLibrary && TryLink(CreateHardLink, destination, source))
        {
            return true;
        }

        if (TryLink(CreateSymbolicLink, destination, source))
        {
            return true;
        }

        if (!copiedToLibrary)
        {
            return false; // Leave in place never copies the file: audio only.
        }

        await CopyAsync(source, destination, null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static bool TryLink(Action<string, string> create, string link, string target)
    {
        try
        {
            create(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Another volume, a file system without links (FAT32, exFAT), or Windows without the right to make symbolic links.
            return false;
        }
    }

    private static async Task CopyAsync(string from, string to, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            await using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var target = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
            var total = Math.Max(source.Length, 1);
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, CopyBufferBytes), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                done += read;
                progress?.Report((double)done / total);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed class Entry(long id, ImportRequest request)
    {
        public long Id { get; } = id;

        public ImportRequest Request { get; } = request;

        public CancellationTokenSource Cancel { get; } = new();

        public ImportState State { get; set; } = ImportState.Queued;

        public double Progress { get; set; }

        public SessionId? Session { get; set; }

        public string? Error { get; set; }

        public bool CancelRequested { get; set; }

        public bool Running { get; set; }

        /// <summary>Waits for the next start: the half-made session of an earlier attempt could not be deleted.</summary>
        public bool Deferred { get; set; }

        public TaskCompletionSource? Attempt { get; set; }

        public ImportItem Snapshot() => new(Id, Request.Path, State, Progress, Session, Error);
    }

    /// <summary>Synchronous <see cref="IProgress{T}"/> (no <see cref="SynchronizationContext"/> capture).</summary>
    private sealed class Reporter(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
