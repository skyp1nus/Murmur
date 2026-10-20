using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Murmur.Core.Audio;
using Murmur.Core.Capture;
using Murmur.Core.Sessions;

namespace Murmur.Core.Recording;

/// <summary>
/// Records one session: one <see cref="CaptureTrack"/> per audio source, one dedicated writer thread that drains every
/// ring through a <see cref="StreamingResampler"/> into a crash-safe 16 kHz mono <see cref="WavPcm16Writer"/>
/// (<c>mic.wav</c> = "Me", <c>system.wav</c> = "Others"), and optional screen segments.
/// </summary>
/// <remarks>
/// <para>
/// <b>Durability.</b> The writer hands every drained block to the OS at once (a killed process loses only what was
/// still in the rings, i.e. at most about one <see cref="RecorderSettings.WriterPeriod"/>), rewrites the WAV headers
/// every <see cref="RecorderSettings.FlushInterval"/> and then wakes a small flusher thread that forces the files to
/// disk, so a slow disk flush can never stall draining (a power cut loses at most about one flush interval).
/// </para>
/// <para>
/// <b>Source contract.</b> Every <see cref="IAudioSource"/> emits a wall-clock-continuous stream from
/// <see cref="IAudioSource.Start"/>: gaps (silent packets, idle loopback, glitches) are filled by the source with
/// <see cref="IAudioSink.WriteSilence"/>. The recorder therefore never pads a healthy track; sample <c>n</c> of every
/// file is at <c>n / 16000</c> s on the session timeline.
/// </para>
/// <para>
/// <b>Faults.</b> A fatal <see cref="IAudioSource.Faulted"/> marks the track faulted; the writer then keeps that track
/// aligned with silence up to the elapsed wall time (or the longest healthy track, whichever is longer). When every
/// audio track has failed the recorder stops by itself and the session ends in <see cref="SessionState.Failed"/>
/// with <see cref="SessionRecord.Error"/>. Non-fatal faults are ignored.
/// </para>
/// <para>
/// <b>Missing sources.</b> A requested source that cannot be created or started (no device, Windows privacy settings
/// denying microphone access) is released and its track skipped: the session records the other track, its
/// <see cref="SessionRecord.HasMicrophone"/>/<see cref="SessionRecord.HasSystemAudio"/> say what was recorded and a
/// warning (a stable code, see <see cref="SessionNotes"/>) is kept in <see cref="SessionRecord.Error"/> while the state stays healthy. Only when no audio
/// track starts does <see cref="StartAsync"/> fail (with the first failure) and the session end in
/// <see cref="SessionState.Failed"/>.
/// </para>
/// <para>
/// <b>Screen faults.</b> The recorder listens to <see cref="IScreenRecorder.Faulted"/> for the lifetime of each segment.
/// Every fault is counted in <see cref="RecorderStats.ScreenFaults"/>. A non-fatal one is told apart by its
/// <see cref="CaptureFault.Kind"/> (never by its message), raises <see cref="Warning"/> once per distinct code and adds that code to the session's warnings (<see cref="SessionRecord.Error"/>). Core never words a problem for the user: the App maps the codes to text. A fatal one
/// marks the segment failed (<see cref="IsScreenRecording"/> turns false at once), raises <see cref="Warning"/> with
/// <see cref="RecorderWarning.ScreenStopped"/> and ends the segment like "screen off" does (its file and real end stay
/// on the timeline); the audio is never touched and the screen may be switched on again. Faults arrive on capture
/// threads: the handler never blocks and holds no lock across a callback.
/// </para>
/// <para>
/// <b>Stop.</b> Every step of a stop runs even if an earlier one threw: each source is stopped and released, every
/// file closed, the open screen segment finalized (and kept on the timeline even if finalizing fails) and the screen
/// recorder released; the recorder always returns to <see cref="RecorderState.Idle"/>. A failure is reported in
/// <see cref="SessionRecord.Error"/> (audio before screen); an audio failure also makes the session
/// <see cref="SessionState.Failed"/>, a screen failure alone does not. The final stop asks the screen recorder to stop
/// the open segment and stops the audio side by side, so both end where the user stopped; that relies on
/// <see cref="IScreenRecorder.StopSegmentAsync"/> taking the stop time and returning from its synchronous part
/// promptly (the audio stop is queued right after it).
/// </para>
/// <para>
/// <b>Pause.</b> The session timeline (<see cref="Elapsed"/>, sample <c>n</c> of a file at <c>n / 16000</c> s, screen
/// offsets) leaves the paused time out. <see cref="PauseAsync"/> asks the writer thread to drain what the sources have
/// delivered so far, flush the resampler (its pending tail belongs to the audio before the pause) and switch to discarding;
/// the call returns when the writer has done it and the timeline is frozen at that moment. The sources keep running (no
/// device is re-opened) and the writer throws their audio away, so nothing is written and the levels are zero.
/// <see cref="ResumeAsync"/> does the opposite handshake: the writer discards the rest of the pause, restarts the
/// resampler and the timeline continues from the frozen value, so the files continue with no gap and no silence. The
/// screen segment in progress ends at the pause call (like "screen off"); when the screen was on, a new segment starts
/// on resume. The writer's steady state allocates nothing in either mode. A crash while paused leaves the files of a
/// normal interrupted session. A pause or resume in the wrong state is a no-op. <see cref="StopAsync"/> works while
/// paused and ends the files exactly where the pause left them.
/// </para>
/// <para>
/// <b>Threads.</b> <see cref="StartAsync"/>, <see cref="PauseAsync"/>, <see cref="ResumeAsync"/>, <see cref="StopAsync"/> and <see cref="SetScreenEnabledAsync"/> are
/// serialized. <see cref="StateChanged"/> is raised on the calling thread (or a thread-pool thread for an automatic
/// stop) while that serialization is held, so handlers must not call back into the recorder synchronously.
/// <see cref="Levels"/>, <see cref="Stats"/> and <see cref="Elapsed"/> are lock-free and cheap. The steady-state writer
/// loop allocates nothing.
/// </para>
/// </remarks>
public sealed class Recorder : IRecorder
{
    private const int ReadFrames = 4096;
    private const int WarmupIterations = 8;
    private const double LevelHalfLifeSeconds = 0.12;

    // What the writer thread does with the audio of the sources (ActiveRecording.Mode). The two "requested" values are
    // the handshake of PauseAsync / ResumeAsync: the writer moves on to the next mode and signals ModeChanged.
    private const int ModeRecording = 0;
    private const int ModePauseRequested = 1;
    private const int ModePaused = 2;
    private const int ModeResumeRequested = 3;

    private readonly ICapture _capture;
    private readonly ISessionStore _store;
    private readonly TimeProvider _time;
    private readonly RecorderSettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly float _levelDecay;

    private volatile ActiveRecording? _active;
    private volatile RecorderState _state = RecorderState.Idle;
    private RecorderStats _lastStats;

    public Recorder(ICapture capture, ISessionStore store, TimeProvider time, RecorderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.FlushInterval <= TimeSpan.Zero || settings.WriterPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "FlushInterval and WriterPeriod must be positive.");
        }

        if (settings.RingSeconds < CaptureTrack.MinimumRingSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), $"RingSeconds must be at least {CaptureTrack.MinimumRingSeconds}.");
        }

        _capture = capture;
        _store = store;
        _time = time;
        _settings = settings;
        _levelDecay = (float)Math.Pow(0.5, settings.WriterPeriod.TotalSeconds / LevelHalfLifeSeconds);
    }

    public RecorderState State => _state;

    public SessionRecord? Current => _active?.Record;

    public TimeSpan Elapsed => _active is { } active ? MonotonicElapsed(active) : TimeSpan.Zero;

    public AudioLevels Levels => _active is { } active
        ? new AudioLevels(BitConverter.Int32BitsToSingle(Volatile.Read(ref active.MicrophoneLevelBits)), BitConverter.Int32BitsToSingle(Volatile.Read(ref active.SystemLevelBits)))
        : default;

    /// <summary>
    /// Live counters while recording; once a stop has drained the audio, the final counters of that session (taken
    /// while the screen recorder is still alive); the final counters of the last session when idle.
    /// </summary>
    public RecorderStats Stats => _active is { } active ? active.FinalStats?.Value ?? ComputeStats(active) : _lastStats;

    /// <summary>Bytes the writer thread allocated after its warm-up iterations, up to the stop request (diagnostics; must stay 0).</summary>
    internal long WriterSteadyStateAllocatedBytes { get; private set; }

    /// <summary>Writer-loop iterations of the last session (diagnostics).</summary>
    internal long WriterIterations { get; private set; }

    /// <summary>Longest time between two writer-loop iterations of the last session (diagnostics).</summary>
    internal TimeSpan WriterMaxCycle { get; private set; }

    /// <summary>Longest flush to disk of every track (flusher thread) of the last session (diagnostics).</summary>
    internal TimeSpan WriterMaxFlush { get; private set; }

    public bool IsScreenRecording => _active?.Open is { Failed: false };

    public event Action<RecorderState>? StateChanged;

    public event Action<RecorderWarning>? Warning;

    public async ValueTask<SessionRecord> StartAsync(RecordingOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state != RecorderState.Idle)
            {
                throw new InvalidOperationException($"Cannot start while {_state}.");
            }

            if (!options.Microphone && !options.SystemAudio)
            {
                throw new ArgumentException("At least one audio source must be recorded.", nameof(options));
            }

            SetState(RecorderState.Starting);
            try
            {
                var active = await StartCoreAsync(options, cancellationToken).ConfigureAwait(false);
                _active = active;
                SetState(RecorderState.Recording);
                return active.Record;
            }
            catch
            {
                SetState(RecorderState.Idle);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SetScreenEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state is not (RecorderState.Recording or RecorderState.Paused) || _active is not { } active)
            {
                throw new InvalidOperationException("Not recording.");
            }

            if (_state == RecorderState.Paused)
            {
                // No segment can run on a timeline that stands still: only decide whether one starts on resume.
                if (enabled && !_capture.Capabilities.Screen)
                {
                    throw new NotSupportedException("Screen capture is not available.");
                }

                active.ScreenOnResume = enabled;
                return;
            }

            if (enabled)
            {
                if (active.Open is { Failed: true })
                {
                    // The screen recorder reported a fatal fault and the cleanup queued behind this gate has not run yet:
                    // finish the failed segment first, so "on" really starts a new one. A failed segment that cannot be
                    // finalized (the fault was the reason) must not make "on" fail.
                    try
                    {
                        await StopScreenSegmentAsync(active, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogError("finalizing the failed screen segment failed", ex);
                    }

                    if (active.Open is not null)
                    {
                        throw new InvalidOperationException("The failed screen segment could not be finished; the screen cannot be switched on again.");
                    }
                }

                if (active.Open is null)
                {
                    if (!_capture.Capabilities.Screen)
                    {
                        throw new NotSupportedException("Screen capture is not available.");
                    }

                    await StartScreenSegmentAsync(active, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (active.Open is not null)
            {
                // The caller's token is honoured by the gate wait above; a half-cancelled finalize has no clean meaning.
                await StopScreenSegmentAsync(active, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask PauseAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state != RecorderState.Recording || _active is not { } active)
            {
                return;
            }

            // The screen segment ends where the pause is called (the end is read on this thread, as for "screen off"); the
            // audio handshake runs next to its finalize, so neither waits for the other.
            active.ScreenOnResume = active.Open is { Failed: false };
            var pauseMark = SessionElapsed(active);
            var screenStop = StopScreenSegmentAsync(active, CancellationToken.None).AsTask();

            long at;
            try
            {
                at = await SwitchWriterModeAsync(active, ModePauseRequested).ConfigureAwait(false);
            }
            catch
            {
                await SettleAsync(screenStop).ConfigureAwait(false);
                throw;
            }

            SetState(RecorderState.Paused);

            try
            {
                await screenStop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The audio is paused. A segment the screen recorder has not left is marked failed at the pause mark: it is no
                // longer recorded (IsScreenRecording turns false), its end on the timeline is the pause, and the resume finishes it
                // (or the final stop does) before it starts the next one.
                LogError("finalizing the screen segment at the pause failed", ex);
                if (active.Open is { } open)
                {
                    Interlocked.CompareExchange(ref open.FaultElapsedTicks, pauseMark.Ticks, -1);
                    open.Failed = true;
                }

                WarnScreenStopped(active);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ResumeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state != RecorderState.Paused || _active is not { } active)
            {
                return;
            }

            var at = await SwitchWriterModeAsync(active, ModeResumeRequested).ConfigureAwait(false);
            active.Clock = active.Clock.ResumedAt(at);
            SetState(RecorderState.Recording);

            if (active.Open is { Failed: true })
            {
                // A segment that could not be finalized at the pause: try once more, so the new one does not start under it.
                try
                {
                    await StopScreenSegmentAsync(active, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogError("finalizing the failed screen segment at the resume failed", ex);
                }
            }

            if (active.ScreenOnResume && active.Open is null && _capture.Capabilities.Screen)
            {
                // The audio is recording again; a screen that cannot start must not fail the resume.
                try
                {
                    await StartScreenSegmentAsync(active, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogError("starting the screen segment at the resume failed", ex);
                    WarnScreenStopped(active);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<SessionRecord> StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state is not (RecorderState.Recording or RecorderState.Paused) || _active is not { } active)
            {
                throw new InvalidOperationException("Not recording.");
            }

            return await StopCoreAsync(active).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The title of a session started without one: a neutral English "Untitled — Oct 18, 01:12". The App always passes a
    /// localized title, so this one shows up in logs and tests only.
    /// </summary>
    internal static string DefaultTitle(DateTimeOffset startedAt) =>
        string.Create(CultureInfo.InvariantCulture, $"Untitled — {startedAt:MMM d, HH:mm}");

    private async ValueTask<ActiveRecording> StartCoreAsync(RecordingOptions options, CancellationToken cancellationToken)
    {
        var startedAt = _time.GetLocalNow();
        var title = string.IsNullOrWhiteSpace(options.Title) ? DefaultTitle(startedAt) : options.Title;
        var record = await _store.CreateSessionAsync(title, startedAt, cancellationToken).ConfigureAwait(false);
        var files = _store.GetFiles(record.Id);
        Directory.CreateDirectory(files.Directory);

        var active = new ActiveRecording(record, files, options.DisplayId);
        var failures = new List<(AudioDeviceKind Kind, Exception Error)>();
        try
        {
            // A source that cannot be opened or started (e.g. Windows denies microphone access) only costs its own
            // track: the session goes on with the others and carries a warning. Only when no track is left does the
            // start fail as a whole.
            if (options.Microphone)
            {
                TryCreateTrack(active, AudioDeviceKind.Microphone, options.MicrophoneDeviceId, files.MicrophoneAudio, failures);
            }

            if (options.SystemAudio)
            {
                TryCreateTrack(active, AudioDeviceKind.SystemOutput, options.SystemDeviceId, files.SystemAudio, failures);
            }

            foreach (var track in active.Tracks)
            {
                track.FaultHandler = fault => OnSourceFaulted(active, track, fault);
                track.Source.Faulted += track.FaultHandler;
            }

            active.StartTimestamp = _time.GetTimestamp();
            active.Clock = SessionClock.StartedAt(active.StartTimestamp);
            List<Track>? notStarted = null;
            foreach (var track in active.Tracks)
            {
                try
                {
                    track.Source.Start(track.Capture);
                    track.Started = true;
                }
                catch (Exception ex)
                {
                    failures.Add((track.Kind, ex));
                    (notStarted ??= []).Add(track);
                }
            }

            if (notStarted is not null)
            {
                // Copy-on-write: fault handlers of the tracks that did start may be enumerating the current list.
                active.Tracks = active.Tracks.FindAll(t => !notStarted.Contains(t));
                foreach (var track in notStarted)
                {
                    DiscardTrack(track);
                }
            }

            if (active.Tracks.Count == 0)
            {
                ExceptionDispatchInfo.Capture(failures[0].Error).Throw();
            }

            active.Warning = DescribeSkippedTracks(
                failures,
                active.Tracks.Exists(t => t.Kind == AudioDeviceKind.Microphone),
                active.Tracks.Exists(t => t.Kind == AudioDeviceKind.SystemOutput));

            active.Writer = new Thread(() => WriterLoop(active))
            {
                IsBackground = true,
                Name = "Murmur audio writer",
                Priority = ThreadPriority.AboveNormal,
            };
            active.Writer.Start();

            active.Flusher = new Thread(() => FlusherLoop(active))
            {
                IsBackground = true,
                Name = "Murmur audio flush",
            };
            active.Flusher.Start();

            if (options.Screen && _capture.Capabilities.Screen)
            {
                await StartScreenSegmentAsync(active, cancellationToken).ConfigureAwait(false);
            }

            active.Record = record with
            {
                State = SessionState.Recording,
                HasMicrophone = active.Tracks.Exists(t => t.Kind == AudioDeviceKind.Microphone),
                HasSystemAudio = active.Tracks.Exists(t => t.Kind == AudioDeviceKind.SystemOutput),
                Error = active.WarningText,
            };
            await _store.UpdateSessionAsync(active.Record, cancellationToken).ConfigureAwait(false);

            // A started track may have faulted fatally while a sibling was failing to start: its auto-stop check saw
            // the failed sibling as healthy. Re-check now that the track list is final (the stop runs after the gate).
            if (active.Tracks.TrueForAll(t => t.Capture.IsFaulted))
            {
                ScheduleAutoStop(active);
            }

            return active;
        }
        catch (Exception ex)
        {
            await AbortAsync(active, ex).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The stored note (see <see cref="SessionNotes"/>: one code per line, the OS message as detail) about the requested
    /// tracks that could not start, or <c>null</c> when none was lost. Kept in <see cref="SessionRecord.Error"/> of a
    /// session that still records.
    /// </summary>
    internal static string? DescribeSkippedTracks(IReadOnlyList<(AudioDeviceKind Kind, Exception Error)> failures, bool hasMicrophone, bool hasSystemAudio)
    {
        if (failures.Count == 0)
        {
            return null;
        }

        var parts = new List<string>(failures.Count + 1);
        foreach (var (kind, error) in failures)
        {
            var mic = kind == AudioDeviceKind.Microphone;
            if (IsAccessDenied(error))
            {
                parts.Add(SessionNotes.Format(mic ? SessionNotes.MicrophoneDenied : SessionNotes.SystemAudioDenied));
            }
            else
            {
                var message = string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message.Trim().TrimEnd('.');
                parts.Add(SessionNotes.Format(mic ? SessionNotes.MicrophoneUnavailable : SessionNotes.SystemAudioUnavailable, message));
            }
        }

        if (hasMicrophone != hasSystemAudio)
        {
            parts.Add(SessionNotes.Format(hasSystemAudio ? SessionNotes.OnlySystemAudio : SessionNotes.OnlyMicrophone));
        }

        return string.Join('\n', parts);
    }

    private static string? JoinMessages(string? first, string? second) => SessionNotes.Join(first, second);

    /// <summary>The warning code of a screen fault.</summary>
    internal static RecorderWarningCode ScreenWarningCode(CaptureFault fault) =>
        fault.IsFatal ? RecorderWarningCode.ScreenStopped : fault.Kind switch
        {
            CaptureFaultKind.FramesDropped => RecorderWarningCode.ScreenFramesDropped,
            CaptureFaultKind.CpuFallback => RecorderWarningCode.ScreenCpuFallback,
            _ => RecorderWarningCode.ScreenOther,
        };

    private static bool IsAccessDenied(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }

    private void TryCreateTrack(ActiveRecording active, AudioDeviceKind kind, string? deviceId, string path, List<(AudioDeviceKind Kind, Exception Error)> failures)
    {
        try
        {
            active.Tracks.Add(CreateTrack(kind, deviceId, path));
        }
        catch (Exception ex)
        {
            failures.Add((kind, ex));
            TryDelete(path);
        }
    }

    /// <summary>Releases a track whose source never started and removes its (empty) file, so nothing treats it as audio.</summary>
    private static void DiscardTrack(Track track)
    {
        ReleaseSource(track);
        try
        {
            track.Writer.Dispose();
        }
        catch (Exception)
        {
            // Deleting below is what matters.
        }

        if (track.Writer.Path is { } path)
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // An empty leftover file is harmless enough not to fail the start.
        }
    }

    private Track CreateTrack(AudioDeviceKind kind, string? deviceId, string path)
    {
        var source = _capture.CreateAudioSource(kind, deviceId);
        try
        {
            var capture = new CaptureTrack(source.Format, _settings.RingSeconds);
            var resampler = new StreamingResampler(source.Format, AudioFormat.Asr.SampleRate);
            var writer = new WavPcm16Writer(path, AudioFormat.Asr.SampleRate, AudioFormat.Asr.Channels);
            return new Track(kind, source, capture, resampler, writer, ReadFrames);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private async ValueTask StartScreenSegmentAsync(ActiveRecording active, CancellationToken cancellationToken)
    {
        active.Screen ??= _capture.CreateScreenRecorder(new ScreenCaptureOptions(active.DisplayId));
        var screen = active.Screen;
        var index = active.NextSegmentIndex;
        var path = active.Files.ScreenSegment(index);

        // Media time 0 of the segment file is the moment StartSegmentAsync is called (IScreenRecorder), so the offset is
        // read on the session clock as the last step before the call: nothing awaitable sits in between, and however
        // long the screen recorder takes to start, its first frame covers that time.
        var run = new ScreenRun(index, SessionElapsed(active));

        // Subscribed before the start: a fault raised while the segment starts belongs to this segment too.
        run.Handler = fault => OnScreenFaulted(active, run, fault);
        screen.Faulted += run.Handler;
        try
        {
            await screen.StartSegmentAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Unsubscribe(screen, run);
            throw;
        }

        active.NextSegmentIndex = index + 1;
        active.Open = run;
    }

    /// <remarks>
    /// The segment ends where the screen recorder is asked to stop: the session time is read as the last step before
    /// <see cref="IScreenRecorder.StopSegmentAsync"/>, which ends the file at the moment it is called, so
    /// <see cref="ScreenSegment.Duration"/> is the file's media duration however long finalizing takes.
    /// When finalizing throws after the screen recorder has left the segment, the segment is still recorded and closed:
    /// its file is already on disk (a fragmented MP4 stays playable without a clean finalize), so it belongs to the
    /// session, and the next "on" starts a new segment. When it throws while the recorder is still recording (the
    /// failure came before the sink stopped), the segment stays open: the file is still growing past any end we could
    /// write, so a later "off" or the final stop retries the finalize. The error reaches the caller either way.
    /// </remarks>
    private async ValueTask StopScreenSegmentAsync(ActiveRecording active, CancellationToken cancellationToken)
    {
        if (active.Open is not { } open || active.Screen is null)
        {
            return;
        }

        // The end is read, and StopSegmentAsync called, on the calling thread as the last steps: the screen recorder
        // takes the stop time and returns from its synchronous part promptly (IScreenRecorder.StopSegmentAsync), so the
        // audio stop queued right after by StopCoreAsync runs next to the finalize instead of after it. Hopping to the
        // thread pool here would not help: its work queue can run the audio stop first.
        var end = SessionElapsed(active);
        try
        {
            await active.Screen.StopSegmentAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!active.Screen.IsRecording)
            {
                CloseScreenSegment(active, open, end);
            }
        }
    }

    private static void CloseScreenSegment(ActiveRecording active, ScreenRun open, TimeSpan end)
    {
        // A failed segment ends where it failed: the screen recorder stops writing frames at its fatal fault, so the file
        // is no longer than that, however late the cleanup runs.
        var faultTicks = Volatile.Read(ref open.FaultElapsedTicks);
        if (faultTicks >= 0)
        {
            end = TimeSpan.FromTicks(Math.Max(Math.Min(end.Ticks, faultTicks), open.Offset.Ticks));
        }

        active.Segments.Add(new ScreenSegment(open.Index, open.Offset, end - open.Offset));
        active.Open = null;
        Unsubscribe(active.Screen, open);
    }

    /// <summary>Stops listening to a segment; a fault it raises from now on (a late queued one) is ignored.</summary>
    private static void Unsubscribe(IScreenRecorder? screen, ScreenRun run)
    {
        run.Closed = true;
        try
        {
            if (screen is not null && run.Handler is not null)
            {
                screen.Faulted -= run.Handler;
            }
        }
        catch (Exception)
        {
            // Closed already makes the handler a no-op.
        }
    }

    /// <summary>
    /// A fault of the screen recorder, on whatever thread it raises it. Never blocks and holds no lock across a
    /// callback: a fatal fault only marks the segment failed (<see cref="IsScreenRecording"/> turns false at once) and
    /// queues the cleanup behind the recorder's gate.
    /// </summary>
    private void OnScreenFaulted(ActiveRecording active, ScreenRun run, CaptureFault fault)
    {
        if (run.Closed)
        {
            return;
        }

        Interlocked.Increment(ref active.ScreenFaults);
        try
        {
            // The technical detail is for the console; the user gets the App's text for the code.
            Console.Error.WriteLine($"Murmur: {fault.Source} {(fault.IsFatal ? "fatal fault" : "warning")}: {fault.Message}");
        }
        catch (Exception)
        {
            // A broken console must not break the capture thread.
        }

        if (fault.IsFatal)
        {
            // The first fatal fault fixes where the segment ends on the session timeline.
            Interlocked.CompareExchange(ref run.FaultElapsedTicks, SessionElapsed(active).Ticks, -1);
            run.Failed = true;
        }

        // The session keeps each code once, and so does the UI: a fault whose code is already known adds nothing.
        // A fatal fault always reaches the UI (the pill must turn off).
        var code = ScreenWarningCode(fault);
        var isNew = active.AddWarning(SessionNotes.Format(SessionNotes.CodeOf(code)));
        if (fault.IsFatal || isNew)
        {
            RaiseWarning(new RecorderWarning(fault.Source, code, ScreenStopped: fault.IsFatal, fault.Kind));
        }

        if (fault.IsFatal && Interlocked.Exchange(ref run.StopScheduled, 1) == 0)
        {
            ScheduleScreenStop(active, run);
        }
    }

    private void RaiseWarning(RecorderWarning warning)
    {
        try
        {
            Warning?.Invoke(warning);
        }
        catch (Exception)
        {
            // A subscriber's failure must not reach the capture thread.
        }
    }

    /// <summary>
    /// Ends the failed segment like "screen off" would, once the gate is free, and stores the session's warnings (a crash
    /// later must not lose why the screen stopped). The audio is not touched.
    /// </summary>
    private void ScheduleScreenStop(ActiveRecording active, ScreenRun run)
    {
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_active, active) && _state is RecorderState.Recording or RecorderState.Paused)
                {
                    if (ReferenceEquals(active.Open, run))
                    {
                        try
                        {
                            await StopScreenSegmentAsync(active, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            // The segment stays open when the finalize failed before the recorder left it: the next "off"
                            // or the final stop retries; the fault itself was reported already.
                            LogError("finalizing the failed screen segment failed", ex);
                        }
                    }

                    active.Record = active.Record with { Error = active.WarningText };
                    await _store.UpdateSessionAsync(active.Record, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                LogError("storing the screen warning failed", ex);
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    private static void LogError(string what, Exception ex)
    {
        try
        {
            Console.Error.WriteLine($"Murmur: {what}: {ex.Message}");
        }
        catch (Exception)
        {
            // A broken console must not break the recorder.
        }
    }

    private void OnSourceFaulted(ActiveRecording active, Track track, CaptureFault fault)
    {
        if (!fault.IsFatal)
        {
            return;
        }

        track.FaultMessage ??= $"{fault.Source}: {fault.Message}";
        track.Capture.MarkFaulted();

        foreach (var t in active.Tracks)
        {
            if (!t.Capture.IsFaulted)
            {
                return;
            }
        }

        ScheduleAutoStop(active);
    }

    private void ScheduleAutoStop(ActiveRecording active)
    {
        if (Interlocked.Exchange(ref active.AutoStopScheduled, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_active, active) && _state is RecorderState.Recording or RecorderState.Paused)
                {
                    await StopCoreAsync(active).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // StopCoreAsync already recorded the failure in the session as far as it could.
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    private async ValueTask<SessionRecord> StopCoreAsync(ActiveRecording active)
    {
        SetState(RecorderState.Stopping);
        var stopElapsed = SessionElapsed(active);
        try
        {
            // The screen segment and the audio stop together: the segment's end is read as its stop is requested (see
            // StopScreenSegmentAsync), so starting it after the audio had stopped would leave the video longer than the
            // audio by the time the audio needs to stop. A failed finalize (an HRESULT from Media Foundation) is
            // collected below and must not leak the capture session, device and encoder, which nothing references after
            // this. If finalizing throws before the recorder left the segment, it stays open here and is closed once the
            // recorder is released.
            var screenStop = StopScreenSegmentAsync(active, CancellationToken.None).AsTask();

            string? audioError = null;
            try
            {
                await Task.Run(() => StopAudio(active, stopElapsed)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                audioError = ex.Message;
            }

            string? screenError = null;
            try
            {
                await screenStop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                screenError = ex.Message;
            }

            // Both counters are final now. Take them while the screen recorder is still alive: once disposed, its Stats
            // may throw or be reset.
            FreezeStats(active);

            if (active.Screen is not null)
            {
                try
                {
                    await active.Screen.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    screenError ??= ex.Message;
                }
                finally
                {
                    // Releasing the recorder ends the segment whatever finalize did; its file belongs to the session.
                    if (active.Open is { } open)
                    {
                        CloseScreenSegment(active, open, stopElapsed);
                    }
                }
            }

            var error = active.WriterError ?? active.FlusherError ?? audioError;
            if (error is null && active.Tracks.TrueForAll(t => t.Capture.IsFaulted))
            {
                error = "All audio sources failed: " + string.Join("; ", active.Tracks.ConvertAll(t => t.FaultMessage ?? t.Kind.ToString()));
            }

            var duration = TimeSpan.Zero;
            foreach (var track in active.Tracks)
            {
                if (track.Writer.Duration > duration)
                {
                    duration = track.Writer.Duration;
                }
            }

            var record = active.Record with
            {
                Duration = duration,
                State = error is null ? SessionState.Recorded : SessionState.Failed,
                HasMicrophone = active.Tracks.Exists(t => t.Kind == AudioDeviceKind.Microphone),
                HasSystemAudio = active.Tracks.Exists(t => t.Kind == AudioDeviceKind.SystemOutput),
                ScreenSegments = [.. active.Segments],
                Error = JoinMessages((error ?? screenError) is { } failure ? SessionNotes.FreeText(failure) : null, active.WarningText),
            };

            active.Record = record;
            await _store.UpdateSessionAsync(record, CancellationToken.None).ConfigureAwait(false);
            return record;
        }
        finally
        {
            // Everything here sits inside the inner try, so the state reset below runs whatever throws.
            try
            {
                FreezeStats(active); // No-op unless the stop failed before the snapshot.
                WriterSteadyStateAllocatedBytes = Volatile.Read(ref active.WriterAllocatedBytes);
                WriterIterations = Volatile.Read(ref active.WriterIterations);
                WriterMaxCycle = Stopwatch.GetElapsedTime(0, active.MaxCycleTicks);
                WriterMaxFlush = Stopwatch.GetElapsedTime(0, active.MaxFlushTicks);

                // Each source is released on its own: one throwing on release (COM) must not leak the others
                // or leave the recorder stuck in Stopping.
                foreach (var track in active.Tracks)
                {
                    ReleaseSource(track);
                }
            }
            finally
            {
                _active = null;
                active.StopSignal.Dispose();
                active.FlushRequest.Dispose();
                active.ModeChanged.Dispose();
                SetState(RecorderState.Idle);
            }
        }
    }

    /// <summary>Unsubscribes from and disposes a track's source; swallows failures so the caller can go on.</summary>
    private static void ReleaseSource(Track track)
    {
        try
        {
            if (track.FaultHandler is not null)
            {
                track.Source.Faulted -= track.FaultHandler;
            }
        }
        catch (Exception)
        {
            // Still try to dispose it.
        }

        try
        {
            track.Source.Dispose();
        }
        catch (Exception)
        {
            // Nothing left to do with a source that cannot be released.
        }
    }

    /// <summary>Stops sources, then the writer, then drains, pads, flushes and closes every file. Blocking.</summary>
    private void StopAudio(ActiveRecording active, TimeSpan stopElapsed)
    {
        Exception? first = null;
        foreach (var track in active.Tracks)
        {
            try
            {
                if (track.Started)
                {
                    track.Source.Stop();
                }
            }
            catch (Exception ex)
            {
                first ??= ex;
            }
        }

        StopThreads(active);

        // Healthy tracks first, so faulted tracks are padded up to the final length of the healthy ones.
        var faultedAtStop = active.Tracks.ConvertAll(t => t.Capture.IsFaulted);
        foreach (var faulted in (ReadOnlySpan<bool>)[false, true])
        {
            for (var i = 0; i < active.Tracks.Count; i++)
            {
                var track = active.Tracks[i];
                if (faultedAtStop[i] != faulted)
                {
                    continue;
                }

                try
                {
                    if (active.WriterError is null)
                    {
                        // Stopped while paused: what the sources delivered since the pause is not part of the recording.
                        Drain(active, track, stopElapsed, discard: Volatile.Read(ref active.Mode) == ModePaused);
                        var tail = track.Resampler.Flush(track.Output);
                        track.Writer.Append(track.Output.AsSpan(0, tail));
                    }

                    track.Writer.Dispose();
                }
                catch (Exception ex)
                {
                    first ??= ex;
                }
            }
        }

        if (first is not null)
        {
            throw first;
        }
    }

    /// <summary>
    /// Rolls back a failed start: stops whatever started, closes files and marks the session failed. Every step runs
    /// even if an earlier one threw, so a misbehaving source can never leave threads running or files open.
    /// </summary>
    private async ValueTask AbortAsync(ActiveRecording active, Exception error)
    {
        // Best effort throughout: the original start error is what the caller needs to see.
        foreach (var track in active.Tracks)
        {
            try
            {
                if (track.Started)
                {
                    track.Source.Stop();
                }
            }
            catch (Exception)
            {
                // Keep stopping the other sources.
            }
        }

        var threadsStopped = false;
        try
        {
            StopThreads(active);
            threadsStopped = true;
        }
        catch (Exception)
        {
            // Join cannot realistically fail; if it did, the events below stay alive for the threads.
        }

        foreach (var track in active.Tracks)
        {
            try
            {
                track.Writer.Dispose();
            }
            catch (Exception)
            {
                // Keep closing the other files.
            }

            ReleaseSource(track);
        }

        if (active.Screen is not null)
        {
            try
            {
                await active.Screen.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The session is marked failed below regardless.
            }
        }

        if (threadsStopped)
        {
            active.StopSignal.Dispose();
            active.FlushRequest.Dispose();
            active.ModeChanged.Dispose();
        }

        try
        {
            await _store.UpdateSessionAsync(active.Record with { State = SessionState.Failed, Error = SessionNotes.FreeText(error.Message) }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing more to roll back; the start error is rethrown by the caller.
        }
    }

    private void WriterLoop(ActiveRecording active)
    {
        var periodMs = Math.Max(1, (int)Math.Round(_settings.WriterPeriod.TotalMilliseconds));
        var lastFlush = _time.GetTimestamp();
        long baseline = 0;
        long iterations = 0;
        var previousStart = Stopwatch.GetTimestamp();

        try
        {
            while (true)
            {
                var iterationStart = Stopwatch.GetTimestamp();
                active.MaxCycleTicks = Math.Max(active.MaxCycleTicks, iterationStart - previousStart);
                previousStart = iterationStart;
                var stopping = active.StopSignal.IsSet;
                var elapsed = SessionElapsed(active);
                var mode = Volatile.Read(ref active.Mode);
                if (mode == ModePauseRequested)
                {
                    // The timeline stops now, at the time this iteration pads the faulted tracks to: at once and in place, so the
                    // padding and the frozen timeline are one value.
                    elapsed = SessionElapsed(active);
                    active.Clock.Freeze(elapsed);
                }

                if (mode is ModePaused or ModeResumeRequested)
                {
                    // Paused: the sources run on, their audio is thrown away and the meters stay at zero.
                    foreach (var track in active.Tracks)
                    {
                        Drain(active, track, elapsed, discard: true);
                    }

                    ZeroLevels(active);
                    if (mode == ModeResumeRequested)
                    {
                        // Everything the sources delivered up to here belongs to the pause; what follows is kept.
                        Volatile.Write(ref active.ModeChangedAt, _time.GetTimestamp());
                        Volatile.Write(ref active.Mode, ModeRecording);
                        active.ModeChanged.Set();
                    }
                }
                else
                {
                    foreach (var track in active.Tracks)
                    {
                        Drain(active, track, elapsed, discard: false);
                    }

                    UpdateLevels(active);
                    if (mode == ModePauseRequested)
                    {
                        // Everything delivered so far is in the files; the resampler's pending tail goes with it and the
                        // resampler starts afresh at the resume, so no sample of the pause leaks into the audio after it.
                        foreach (var track in active.Tracks)
                        {
                            var tail = track.Resampler.Flush(track.Output);
                            track.Writer.Append(track.Output.AsSpan(0, tail));
                            track.Resampler.Reset();
                        }

                        ZeroLevels(active);

                        Volatile.Write(ref active.ModeChangedAt, _time.GetTimestamp());
                        Volatile.Write(ref active.Mode, ModePaused);
                        active.ModeChanged.Set();
                    }
                }

                if (_time.GetElapsedTime(lastFlush) >= _settings.FlushInterval)
                {
                    // Cheap header rewrite here; the slow flush to disk runs on the flusher thread so a busy disk
                    // can never stall draining the rings.
                    foreach (var track in active.Tracks)
                    {
                        track.Writer.UpdateHeader();
                    }

                    active.FlushRequest.Set();
                    lastFlush = _time.GetTimestamp();
                }

                iterations++;
                Volatile.Write(ref active.WriterIterations, iterations);
                if (iterations == WarmupIterations)
                {
                    baseline = GC.GetAllocatedBytesForCurrentThread();
                }
                else if (iterations > WarmupIterations && !stopping)
                {
                    Volatile.Write(ref active.WriterAllocatedBytes, GC.GetAllocatedBytesForCurrentThread() - baseline);
                }

                if (stopping)
                {
                    return;
                }

                active.StopSignal.Wait(periodMs);
            }
        }
        catch (Exception ex)
        {
            active.WriterError = "Writing audio failed: " + ex.Message;
            ScheduleAutoStop(active);
        }
    }

    /// <summary>Flushes every track to disk whenever the writer asks; a slow flush only delays the next one.</summary>
    private static void FlusherLoop(ActiveRecording active)
    {
        try
        {
            while (true)
            {
                active.FlushRequest.WaitOne();
                if (active.FlusherStop)
                {
                    return;
                }

                var start = Stopwatch.GetTimestamp();
                foreach (var track in active.Tracks)
                {
                    track.Writer.FlushToDisk();
                }

                active.MaxFlushTicks = Math.Max(active.MaxFlushTicks, Stopwatch.GetTimestamp() - start);
            }
        }
        catch (Exception ex)
        {
            active.FlusherError = "Flushing audio to disk failed: " + ex.Message;
        }
    }

    private static void StopThreads(ActiveRecording active)
    {
        active.StopSignal.Set();
        active.Writer?.Join();
        active.FlusherStop = true;
        active.FlushRequest.Set();
        active.Flusher?.Join();
    }

    /// <summary>
    /// Moves what is in the ring right now to the file (or drops it while <paramref name="discard"/>); pads a faulted track
    /// with silence up to the target time on the session timeline.
    /// </summary>
    private static void Drain(ActiveRecording active, Track track, TimeSpan elapsed, bool discard)
    {
        // Read the flag before draining: a source writes its last frames before it reports the fault, so once the flag
        // is seen every frame it ever wrote is in the ring and the silence goes strictly after them.
        var faulted = track.Capture.IsFaulted;
        var channels = track.Channels;
        var pending = track.Capture.AvailableFrames;
        while (pending > 0)
        {
            var frames = track.Capture.Read(track.Input.AsSpan(0, Math.Min(pending, ReadFrames) * channels));
            if (frames == 0)
            {
                break;
            }

            if (discard)
            {
                // Paused: the audio is dropped, but the source's peak is taken so the first level after the resume is fresh.
                track.Capture.TakePeak();
            }
            else
            {
                Feed(track, track.Input.AsSpan(0, frames * channels));
            }

            pending -= frames;
        }

        if (!faulted)
        {
            return;
        }

        var targetSeconds = elapsed.TotalSeconds;
        foreach (var other in active.Tracks)
        {
            if (!other.Capture.IsFaulted)
            {
                targetSeconds = Math.Max(targetSeconds, (double)other.FramesConsumed / other.Capture.Format.SampleRate);
            }
        }

        var target = (long)(targetSeconds * track.Capture.Format.SampleRate);
        while (track.FramesConsumed < target)
        {
            var frames = (int)Math.Min(target - track.FramesConsumed, ReadFrames);
            var silence = track.Input.AsSpan(0, frames * channels);
            silence.Clear();
            Feed(track, silence);
        }
    }

    private static void Feed(Track track, ReadOnlySpan<float> interleaved)
    {
        var produced = track.Resampler.Process(interleaved, track.Output);
        track.Writer.Append(track.Output.AsSpan(0, produced));
        Volatile.Write(ref track.FramesConsumed, track.FramesConsumed + (interleaved.Length / track.Channels));
    }

    private static void ZeroLevels(ActiveRecording active)
    {
        foreach (var track in active.Tracks)
        {
            track.Level = 0f;
        }

        Volatile.Write(ref active.MicrophoneLevelBits, 0);
        Volatile.Write(ref active.SystemLevelBits, 0);
    }

    private void UpdateLevels(ActiveRecording active)
    {
        foreach (var track in active.Tracks)
        {
            // A dead source drops to 0 at once instead of decaying.
            track.Level = track.Capture.IsFaulted ? 0f : Math.Max(Math.Min(1f, track.Capture.TakePeak()), track.Level * _levelDecay);
            var bits = BitConverter.SingleToInt32Bits(track.Level);
            if (track.Kind == AudioDeviceKind.Microphone)
            {
                Volatile.Write(ref active.MicrophoneLevelBits, bits);
            }
            else
            {
                Volatile.Write(ref active.SystemLevelBits, bits);
            }
        }
    }

    private static RecorderStats ComputeStats(ActiveRecording active)
    {
        long mic = 0, system = 0, dropped = 0;
        foreach (var track in active.Tracks)
        {
            if (track.Kind == AudioDeviceKind.Microphone)
            {
                mic += track.Capture.FramesWritten;
            }
            else
            {
                system += track.Capture.FramesWritten;
            }

            dropped += track.Capture.FramesDropped;
        }

        return new RecorderStats(mic, system, dropped, ScreenFramesDropped(active.Screen), Volatile.Read(ref active.ScreenFaults));
    }

    /// <summary>
    /// Reads the screen counter without letting a screen recorder that cannot report (faulted, already released) fail
    /// the caller: <see cref="Stats"/> is polled by the UI and read on the stop path.
    /// </summary>
    private static long ScreenFramesDropped(IScreenRecorder? screen)
    {
        if (screen is null)
        {
            return 0;
        }

        try
        {
            return screen.Stats.FramesDropped;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Takes the final counters of a stopping session once; from then on <see cref="Stats"/> returns them without
    /// touching the sources or the screen recorder. Must run before the screen recorder is disposed.
    /// </summary>
    private void FreezeStats(ActiveRecording active)
    {
        if (active.FinalStats is not null)
        {
            return;
        }

        var stats = ComputeStats(active);
        _lastStats = stats;
        active.FinalStats = new StrongBox<RecorderStats>(stats);
    }

    /// <summary>
    /// What <see cref="Elapsed"/> reports: the timeline, never stepping back for any reader. The timeline itself stops at the
    /// writer's pause mark, which can be a few microseconds before a value a poller read a moment earlier; the largest value any
    /// reader has seen is therefore kept, and a reader gets at least that.
    /// </summary>
    private TimeSpan MonotonicElapsed(ActiveRecording active)
    {
        var now = SessionElapsed(active).Ticks;
        var seen = Volatile.Read(ref active.ElapsedHighWater);
        while (now > seen)
        {
            var previous = Interlocked.CompareExchange(ref active.ElapsedHighWater, now, seen);
            if (previous == seen)
            {
                return TimeSpan.FromTicks(now);
            }

            seen = previous;
        }

        return TimeSpan.FromTicks(seen);
    }

    /// <summary>The recorded time of <paramref name="active"/> on the session timeline (the paused time is left out).</summary>
    private TimeSpan SessionElapsed(ActiveRecording active) => active.Clock.Now(_time);

    /// <summary>
    /// Asks the writer thread to move to the next mode (pause or resume) and waits until it has, so the audio on both sides
    /// of the change is exactly the audio the writer had drained when it switched.
    /// </summary>
    /// <returns>The clock timestamp at which the writer switched.</returns>
    private static async ValueTask<long> SwitchWriterModeAsync(ActiveRecording active, int requested)
    {
        active.ModeChanged.Reset();
        Volatile.Write(ref active.Mode, requested);
        await Task.Run(() =>
        {
            // The writer answers within one period; it only stays silent when it has died (WriterError, auto-stop on its way).
            while (!active.ModeChanged.Wait(25))
            {
                if (active.Writer is not { IsAlive: true } || active.WriterError is not null)
                {
                    throw new InvalidOperationException("The audio writer is not running.");
                }
            }
        }).ConfigureAwait(false);
        return Volatile.Read(ref active.ModeChangedAt);
    }

    private static async ValueTask SettleAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The caller is already reporting another failure.
        }
    }

    /// <summary>The screen could not be (re)started or finalized around a pause: tell the UI and keep the code in the session.</summary>
    private void WarnScreenStopped(ActiveRecording active)
    {
        const RecorderWarningCode code = RecorderWarningCode.ScreenStopped;
        active.AddWarning(SessionNotes.Format(SessionNotes.CodeOf(code)));
        RaiseWarning(new RecorderWarning("screen", code, ScreenStopped: true));
    }

    private void SetState(RecorderState state)
    {
        _state = state;
        StateChanged?.Invoke(state);
    }

    private sealed class ActiveRecording(SessionRecord record, SessionFiles files, string? displayId)
    {
        /// <summary>Final once the start has succeeded; replaced (never mutated) when a source fails to start.</summary>
        public volatile List<Track> Tracks = [];
        public readonly List<ScreenSegment> Segments = [];
        // No spinning: the writer only sleeps out its period here, and spinning first can use up the whole period on a busy
        // machine, which pushes the first blocking wait (and its one-time lazy allocation) past the allocation baseline.
        public readonly ManualResetEventSlim StopSignal = new(false, spinCount: 0);
        public readonly AutoResetEvent FlushRequest = new(false);
        public int MicrophoneLevelBits;
        public int SystemLevelBits;
        public int AutoStopScheduled;
        public long WriterAllocatedBytes;
        public long WriterIterations;
        public long MaxCycleTicks;
        public long MaxFlushTicks;

        public SessionRecord Record { get; set; } = record;

        public SessionFiles Files { get; } = files;

        public string? DisplayId { get; } = displayId;

        public long StartTimestamp { get; set; }

        /// <summary>Maps the time source to the session timeline; replaced (never mutated) by pause and resume.</summary>
        public volatile SessionClock Clock = SessionClock.StartedAt(0);

        /// <summary>What the writer does with the audio (<c>ModeRecording</c>...); changed by the gate holder and by the writer.</summary>
        public int Mode;

        /// <summary>Set by the writer when it has switched mode; the gate holder waits for it.</summary>
        public readonly ManualResetEventSlim ModeChanged = new(false, spinCount: 0);

        /// <summary>The time source's timestamp at which the writer switched mode.</summary>
        public long ModeChangedAt;

        /// <summary>The largest <see cref="Elapsed"/> handed out so far, in ticks (Interlocked).</summary>
        public long ElapsedHighWater;

        /// <summary>The screen was on when the recording was paused (or was switched on while paused): a segment starts on resume.</summary>
        public bool ScreenOnResume;

        public Thread? Writer { get; set; }

        public Thread? Flusher { get; set; }

        public volatile bool FlusherStop;

        public volatile string? FlusherError;

        public IScreenRecorder? Screen { get; set; }

        public int NextSegmentIndex { get; set; }

        /// <summary>The screen segment being recorded (or failed and not cleaned up yet); <c>null</c> when none.</summary>
        public volatile ScreenRun? Open;

        public int ScreenFaults;

        private readonly Lock _warningLock = new();
        private readonly HashSet<string> _raisedWarnings = [];

        public volatile string? WriterError;

        /// <summary>Requested tracks that could not start (see <see cref="DescribeSkippedTracks"/>); <c>null</c> if none.</summary>
        public string? Warning { get; set; }

        /// <summary>The session's warnings: <see cref="Warning"/> followed by the screen problems reported since.</summary>
        public string? WarningText
        {
            get
            {
                lock (_warningLock)
                {
                    return Warning;
                }
            }
        }

        /// <summary>Adds a warning text to the session; <c>false</c> when this exact text was already added.</summary>
        public bool AddWarning(string text)
        {
            lock (_warningLock)
            {
                if (!_raisedWarnings.Add(text))
                {
                    return false;
                }

                Warning = JoinMessages(Warning, text);
                return true;
            }
        }

        /// <summary>Final counters, set once by <see cref="FreezeStats"/> during the stop; <c>null</c> before.</summary>
        public volatile StrongBox<RecorderStats>? FinalStats;
    }

    /// <summary>
    /// Maps the time source to the session timeline: <c>offset</c> is the timeline value at <c>baseTimestamp</c>, and the
    /// timeline then advances with the time source while <c>running</c>. Immutable, so a reader needs no lock.
    /// </summary>
    private sealed class SessionClock(long baseTimestamp, TimeSpan offset)
    {
        private long _frozenTicks = -1;

        public static SessionClock StartedAt(long timestamp) => new(timestamp, TimeSpan.Zero);

        /// <summary>The timeline value; once <see cref="Freeze"/> has run, that value and no other.</summary>
        public TimeSpan Now(TimeProvider time)
        {
            var frozen = Volatile.Read(ref _frozenTicks);
            return frozen >= 0 ? TimeSpan.FromTicks(frozen) : offset + time.GetElapsedTime(baseTimestamp);
        }

        /// <summary>Stops this clock at <paramref name="at"/> (the writer thread, once per pause; allocation-free).</summary>
        public void Freeze(TimeSpan at) => Volatile.Write(ref _frozenTicks, Math.Max(0, at.Ticks));

        /// <summary>A running clock that continues from the frozen value at <paramref name="timestamp"/>.</summary>
        public SessionClock ResumedAt(long timestamp) => new(timestamp, TimeSpan.FromTicks(Math.Max(0, Volatile.Read(ref _frozenTicks))));
    }

    private sealed class ScreenRun(int index, TimeSpan offset)
    {
        public int Index { get; } = index;

        public TimeSpan Offset { get; } = offset;

        public Action<CaptureFault>? Handler { get; set; }

        /// <summary>A fatal fault ended the recording of this segment (the file may still need finalizing).</summary>
        public volatile bool Failed;

        /// <summary>The segment is over and unsubscribed; faults it still delivers are ignored.</summary>
        public volatile bool Closed;

        public int StopScheduled;

        /// <summary>Session time (ticks) of the first fatal fault, or -1 (Interlocked).</summary>
        public long FaultElapsedTicks = -1;
    }

    private sealed class Track(AudioDeviceKind kind, IAudioSource source, CaptureTrack capture, StreamingResampler resampler, WavPcm16Writer writer, int readFrames)
    {
        public long FramesConsumed;

        public AudioDeviceKind Kind { get; } = kind;

        public IAudioSource Source { get; } = source;

        public CaptureTrack Capture { get; } = capture;

        public StreamingResampler Resampler { get; } = resampler;

        public WavPcm16Writer Writer { get; } = writer;

        public int Channels { get; } = capture.Format.Channels;

        public float[] Input { get; } = new float[readFrames * capture.Format.Channels];

        public float[] Output { get; } = new float[Math.Max(resampler.MaxOutputSamples(readFrames), resampler.MaxFlushSamples)];

        public bool Started { get; set; }

        public float Level { get; set; }

        public Action<CaptureFault>? FaultHandler { get; set; }

        public string? FaultMessage { get; set; }
    }
}
