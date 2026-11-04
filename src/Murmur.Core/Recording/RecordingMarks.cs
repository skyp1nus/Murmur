using Murmur.Core.Sessions;

namespace Murmur.Core.Recording;

/// <summary>
/// The marks of the running recording. A mark is the recorder's <see cref="IRecorder.Elapsed"/> at the moment of the call, i.e. a
/// position on the session timeline: the paused time is not counted, so a mark made while paused gets the position of the pause.
/// </summary>
/// <remarks>
/// <para>The marks are kept in memory (under a lock: this is the hotkey / UI path, never an audio callback) and are written to the
/// <see cref="IBookmarkStore"/> in one transaction when the recording ends (the recorder returns to
/// <see cref="RecorderState.Idle"/>), for the session that was recorded. They are cleared when a new recording starts and
/// after they have been handed to the store. A failed write never throws into the recorder: it is raised through
/// <see cref="SaveFailed"/> and the marks of that recording are lost (crash recovery of marks is out of scope).</para>
/// </remarks>
public sealed class RecordingMarks : IRecordingMarks, IDisposable
{
    private readonly IRecorder _recorder;
    private readonly IBookmarkStore _store;
    private readonly Lock _gate = new();
    private readonly List<TimeSpan> _marks = [];
    private SessionId? _session;
    private Task _pendingFlush = Task.CompletedTask;
    private bool _accepting;
    private int _disposed;

    /// <param name="recorder">The recorder whose timeline the marks are on.</param>
    /// <param name="store">Where the marks go when the recording stops.</param>
    public RecordingMarks(IRecorder recorder, IBookmarkStore store)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(store);
        _recorder = recorder;
        _store = store;
        _accepting = recorder.State is RecorderState.Recording or RecorderState.Paused;
        _recorder.StateChanged += OnStateChanged;
    }

    /// <inheritdoc />
    public event Action<TimeSpan>? Marked;

    /// <summary>
    /// The marks of a finished recording could not be saved (raised off the UI thread). Like the recorder's non-fatal warnings it
    /// never stops anything; the marks of that recording are gone.
    /// </summary>
    public event Action<SessionId?, Exception>? SaveFailed;

    /// <inheritdoc />
    public IReadOnlyList<TimeSpan> Current
    {
        get
        {
            lock (_gate)
            {
                return _marks.ToArray();
            }
        }
    }

    /// <summary>The save of the latest finished recording's marks (completed when nothing is being saved; never faults). For tests and shutdown.</summary>
    public Task PendingFlush
    {
        get
        {
            lock (_gate)
            {
                return _pendingFlush;
            }
        }
    }

    /// <inheritdoc />
    public bool TryMark(out TimeSpan at)
    {
        at = default;
        if (_recorder.State is not (RecorderState.Recording or RecorderState.Paused))
        {
            return false;
        }

        TimeSpan position;
        lock (_gate)
        {
            // Checked again under the lock: a stop that began after the state check above has closed the recording's marks.
            if (!_accepting)
            {
                return false;
            }

            position = _recorder.Elapsed;
            _session ??= _recorder.Current?.Id;
            _marks.Add(position);
        }

        at = position;
        Marked?.Invoke(position);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _recorder.StateChanged -= OnStateChanged;
        }
    }

    private void OnStateChanged(RecorderState state)
    {
        switch (state)
        {
            case RecorderState.Starting:
                lock (_gate)
                {
                    _accepting = false;
                    _marks.Clear();
                    _session = null;
                }

                break;
            case RecorderState.Recording:
            case RecorderState.Paused:
                lock (_gate)
                {
                    _accepting = true;
                    _session ??= _recorder.Current?.Id;
                }

                break;
            case RecorderState.Stopping:
                lock (_gate)
                {
                    _accepting = false;
                }

                break;
            case RecorderState.Idle:
                Flush();
                break;
        }
    }

    private void Flush()
    {
        NewBookmark[] pending;
        SessionId? session;
        Task previous;
        lock (_gate)
        {
            _accepting = false;
            if (_marks.Count == 0)
            {
                _session = null;
                return;
            }

            pending = new NewBookmark[_marks.Count];
            for (var i = 0; i < pending.Length; i++)
            {
                pending[i] = new NewBookmark(_marks[i]);
            }

            session = _session;
            previous = _pendingFlush;
            _marks.Clear();
            _session = null;
        }

        // Outside the lock: the save may complete (and raise SaveFailed) before it returns.
        var saving = SaveAsync(session, pending, previous);
        lock (_gate)
        {
            _pendingFlush = saving;
        }
    }

    private async Task SaveAsync(SessionId? session, NewBookmark[] marks, Task previous)
    {
        // Runs after an earlier save, so the order of recordings is kept; never faults.
        await previous.ConfigureAwait(false);
        try
        {
            if (session is not { } id)
            {
                throw new InvalidOperationException("The recording that was marked is unknown.");
            }

            await _store.AddBookmarksAsync(id, marks, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                SaveFailed?.Invoke(session, ex);
            }
            catch (Exception)
            {
                // A faulty subscriber must not break the next save.
            }
        }
    }
}
