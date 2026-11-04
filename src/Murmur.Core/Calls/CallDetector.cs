namespace Murmur.Core.Calls;

/// <summary>
/// Turns the audio-session activity of <see cref="IAudioSessionMonitor"/> into call sessions. It owns every timing rule, so the
/// platform monitors only report what the audio stack says:
/// <list type="bullet">
/// <item>a call starts when the microphone is in use and another process (never this one) has had an active render session,
/// without a break, for <see cref="DetectAfter"/> (3 s), all that time with the microphone in use;</item>
/// <item><see cref="ICallDetector.CallStarted"/> is raised once per call session; while the session lasts nothing else is
/// detected;</item>
/// <item>the session ends when both the microphone and that app's render session have been inactive for <see cref="EndAfter"/>
/// (30 s); then <see cref="ICallDetector.CallEnded"/> is raised and the next call can be detected.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>No idle polling.</b> The detector reacts to <see cref="IAudioSessionMonitor.Changed"/>. A re-evaluation timer runs only
/// while something counts down (a candidate app waits for its 3 s, or an ended-looking call for its 30 s), at most once a second
/// (<see cref="PollInterval"/>) and never while stopped; it also catches a change the monitor did not report.</para>
/// <para><b>Threads.</b> The monitor event and the timer may arrive on any thread. Evaluations never overlap: a request that
/// arrives during one is coalesced into one more pass, and the snapshot is taken outside the state lock, so a monitor that raises
/// <see cref="IAudioSessionMonitor.Changed"/> from its own thread cannot deadlock with it. The events are raised on the thread
/// that evaluates, outside the lock (off the UI thread).</para>
/// <para><b>Process ids.</b> This process (<see cref="Environment.ProcessId"/> by default) and ids below 1 (the system-sounds
/// session) never count. An app is followed by process id and process name, so a call app that moves its audio to another
/// process of the same name keeps its session.</para>
/// <para><see cref="Stop"/> forgets the current call silently (no <see cref="ICallDetector.CallEnded"/>); the monitor is not owned
/// and not disposed here.</para>
/// </remarks>
public sealed class CallDetector : ICallDetector
{
    /// <summary>How long mic and a render session must be active together before it is a call.</summary>
    public static readonly TimeSpan DetectAfter = TimeSpan.FromSeconds(3);

    /// <summary>How long mic and the call's render session must both be inactive before the call session ends.</summary>
    public static readonly TimeSpan EndAfter = TimeSpan.FromSeconds(30);

    /// <summary>The longest wait between two evaluations while something counts down (at most 1 Hz).</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IAudioSessionMonitor _monitor;
    private readonly TimeProvider _time;
    private readonly int _ownProcessId;
    private readonly Lock _gate = new();

    // Under _gate.
    private readonly Dictionary<int, Candidate> _candidates = [];
    private ITimer? _timer;
    private bool _running;
    private bool _disposed;
    private CallInfo? _call;
    private long _inactiveSince = -1;
    private bool _scheduled;

    // Coalesced, non-overlapping evaluations.
    private int _requested;
    private int _evaluating;

    /// <param name="monitor">Where the audio-session activity comes from; started and stopped with the detector, not disposed.</param>
    /// <param name="time">The clock of the 3 s and 30 s rules and of the re-evaluation timer.</param>
    /// <param name="ownProcessId">The process whose playback never counts; this process by default.</param>
    public CallDetector(IAudioSessionMonitor monitor, TimeProvider time, int? ownProcessId = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(time);
        _monitor = monitor;
        _time = time;
        _ownProcessId = ownProcessId ?? Environment.ProcessId;
    }

    public event Action<CallInfo>? CallStarted;

    public event Action<CallInfo>? CallEnded;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>The call session in progress, or <c>null</c> (for tests and diagnostics).</summary>
    public CallInfo? CurrentCall
    {
        get
        {
            lock (_gate)
            {
                return _call;
            }
        }
    }

    /// <summary>Whether the re-evaluation timer is waiting (for tests: it never is while nothing counts down).</summary>
    public bool IsTimerScheduled
    {
        get
        {
            lock (_gate)
            {
                return _scheduled;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                return;
            }

            _running = true;
            _timer = _time.CreateTimer(static state => ((CallDetector)state!).RequestEvaluation(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        // Subscribe first, start after: a session that starts in between is not lost.
        _monitor.Changed += RequestEvaluation;
        _monitor.Start();
        RequestEvaluation();
    }

    public void Stop()
    {
        ITimer? timer;
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            timer = _timer;
            _timer = null;
            _scheduled = false;
            _candidates.Clear();
            _call = null;
            _inactiveSince = -1;
        }

        _monitor.Changed -= RequestEvaluation;
        timer?.Dispose();
        _monitor.Stop();
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            _disposed = true;
        }
    }

    private void RequestEvaluation()
    {
        Volatile.Write(ref _requested, 1);
        while (Interlocked.CompareExchange(ref _evaluating, 1, 0) == 0)
        {
            try
            {
                while (Interlocked.Exchange(ref _requested, 0) == 1)
                {
                    EvaluateOnce();
                }
            }
            finally
            {
                Volatile.Write(ref _evaluating, 0);
            }

            // A request that arrived after the inner loop's last check and before the flag was cleared is taken here.
            if (Volatile.Read(ref _requested) == 0)
            {
                break;
            }
        }
    }

    private void EvaluateOnce()
    {
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }
        }

        AudioActivitySnapshot snapshot;
        try
        {
            snapshot = _monitor.Snapshot();
        }
        catch (Exception)
        {
            // A monitor that cannot read the sessions right now is asked again on its next change (or the next tick).
            return;
        }

        CallInfo? started = null;
        CallInfo? ended = null;
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            var now = _time.GetTimestamp();
            if (_call is { } call)
            {
                var active = snapshot.MicrophoneInUse || IsRendering(snapshot, call);
                if (active)
                {
                    _inactiveSince = -1;
                }
                else if (_inactiveSince < 0)
                {
                    _inactiveSince = now;
                }
                else if (_time.GetElapsedTime(_inactiveSince, now) >= EndAfter)
                {
                    ended = call;
                    _call = null;
                    _inactiveSince = -1;
                }
            }

            if (_call is null)
            {
                started = TrackCandidates(snapshot, now);
                if (started is not null)
                {
                    _call = started;
                    _inactiveSince = -1;
                    _candidates.Clear();
                }
            }

            Schedule(now);
        }

        if (ended is not null)
        {
            CallEnded?.Invoke(ended);
        }

        if (started is not null)
        {
            CallStarted?.Invoke(started);
        }
    }

    /// <summary>Follows the apps that render while the mic is in use; returns the first whose 3 s are up (the longest waiting).</summary>
    private CallInfo? TrackCandidates(AudioActivitySnapshot snapshot, long now)
    {
        if (!snapshot.MicrophoneInUse)
        {
            _candidates.Clear();
            return null;
        }

        // Apps that stopped rendering lose their time: the 3 s must be continuous.
        List<int>? gone = null;
        foreach (var pid in _candidates.Keys)
        {
            if (!Contains(snapshot.Rendering, pid))
            {
                (gone ??= []).Add(pid);
            }
        }

        if (gone is not null)
        {
            foreach (var pid in gone)
            {
                _candidates.Remove(pid);
            }
        }

        foreach (var process in snapshot.Rendering)
        {
            if (Counts(process) && !_candidates.ContainsKey(process.ProcessId))
            {
                _candidates[process.ProcessId] = new Candidate(process.ProcessName, now);
            }
        }

        KeyValuePair<int, Candidate>? due = null;
        foreach (var entry in _candidates)
        {
            if (_time.GetElapsedTime(entry.Value.Since, now) >= DetectAfter && (due is null || entry.Value.Since < due.Value.Value.Since))
            {
                due = entry;
            }
        }

        if (due is not { } found)
        {
            return null;
        }

        var processName = CallAppNames.Normalize(found.Value.ProcessName);
        return new CallInfo(CallAppNames.FriendlyName(processName), processName, found.Key);
    }

    /// <summary>Arms the timer for the nearest deadline (at most <see cref="PollInterval"/> away), or idles it when nothing counts down.</summary>
    private void Schedule(long now)
    {
        TimeSpan? next = null;
        if (_call is not null)
        {
            if (_inactiveSince >= 0)
            {
                next = EndAfter - _time.GetElapsedTime(_inactiveSince, now);
            }
        }
        else
        {
            foreach (var candidate in _candidates.Values)
            {
                var left = DetectAfter - _time.GetElapsedTime(candidate.Since, now);
                if (next is null || left < next)
                {
                    next = left;
                }
            }
        }

        if (next is not { } wait)
        {
            _scheduled = false;
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        if (wait > PollInterval)
        {
            wait = PollInterval;
        }

        if (wait < TimeSpan.Zero)
        {
            wait = TimeSpan.Zero;
        }

        _scheduled = true;
        _timer?.Change(wait, Timeout.InfiniteTimeSpan);
    }

    private bool Counts(RenderingProcess process) => process.ProcessId > 0 && process.ProcessId != _ownProcessId;

    private bool IsRendering(AudioActivitySnapshot snapshot, CallInfo call)
    {
        foreach (var process in snapshot.Rendering)
        {
            if (Counts(process) && (process.ProcessId == call.ProcessId || CallAppNames.SameProcess(process.ProcessName, call.ProcessName)))
            {
                return true;
            }
        }

        return false;
    }

    private bool Contains(IReadOnlyList<RenderingProcess> rendering, int processId)
    {
        foreach (var process in rendering)
        {
            if (process.ProcessId == processId && Counts(process))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct Candidate(string ProcessName, long Since);
}
