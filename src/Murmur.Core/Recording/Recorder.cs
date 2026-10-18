using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
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
/// <b>Stop.</b> Every step of a stop runs even if an earlier one threw: each source is stopped and released, every
/// file closed, the open screen segment finalized (and kept on the timeline even if finalizing fails) and the screen
/// recorder released; the recorder always returns to <see cref="RecorderState.Idle"/>. A failure is reported in
/// <see cref="SessionRecord.Error"/> (audio before screen); an audio failure also makes the session
/// <see cref="SessionState.Failed"/>, a screen failure alone does not.
/// </para>
/// <para>
/// <b>Threads.</b> <see cref="StartAsync"/>, <see cref="StopAsync"/> and <see cref="SetScreenEnabledAsync"/> are
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

    public TimeSpan Elapsed => _active is { } active ? _time.GetElapsedTime(active.StartTimestamp) : TimeSpan.Zero;

    public AudioLevels Levels => _active is { } active
        ? new AudioLevels(BitConverter.Int32BitsToSingle(Volatile.Read(ref active.MicrophoneLevelBits)), BitConverter.Int32BitsToSingle(Volatile.Read(ref active.SystemLevelBits)))
        : default;

    /// <summary>
    /// Live counters while recording; once a stop has drained the audio, the final counters of that session (taken
    /// while the screen recorder is still alive); the final counters of the last session when idle.
    /// </summary>
    public RecorderStats Stats => _active is { } active ? active.FinalStats?.Value ?? ComputeStats(active) : _lastStats;

    /// <summary>Bytes the writer thread allocated after its warm-up iterations (diagnostics; must stay 0).</summary>
    internal long WriterSteadyStateAllocatedBytes { get; private set; }

    /// <summary>Writer-loop iterations of the last session (diagnostics).</summary>
    internal long WriterIterations { get; private set; }

    /// <summary>Longest time between two writer-loop iterations of the last session (diagnostics).</summary>
    internal TimeSpan WriterMaxCycle { get; private set; }

    /// <summary>Longest flush to disk of every track (flusher thread) of the last session (diagnostics).</summary>
    internal TimeSpan WriterMaxFlush { get; private set; }

    public event Action<RecorderState>? StateChanged;

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
            if (_state != RecorderState.Recording || _active is not { } active)
            {
                throw new InvalidOperationException("Not recording.");
            }

            if (enabled)
            {
                if (active.OpenSegment is null)
                {
                    if (!_capture.Capabilities.Screen)
                    {
                        throw new NotSupportedException("Screen capture is not available.");
                    }

                    await StartScreenSegmentAsync(active, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (active.OpenSegment is not null)
            {
                // The caller's token is honoured by the gate wait above; a half-cancelled finalize has no clean meaning.
                await StopScreenSegmentAsync(active, end: null, CancellationToken.None).ConfigureAwait(false);
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
            if (_state != RecorderState.Recording || _active is not { } active)
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

    internal static string DefaultTitle(DateTimeOffset startedAt) =>
        string.Create(CultureInfo.InvariantCulture, $"Запис {startedAt:yyyy-MM-dd HH:mm}");

    private async ValueTask<ActiveRecording> StartCoreAsync(RecordingOptions options, CancellationToken cancellationToken)
    {
        var startedAt = _time.GetLocalNow();
        var title = string.IsNullOrWhiteSpace(options.Title) ? DefaultTitle(startedAt) : options.Title;
        var record = await _store.CreateSessionAsync(title, startedAt, cancellationToken).ConfigureAwait(false);
        var files = _store.GetFiles(record.Id);
        Directory.CreateDirectory(files.Directory);

        var active = new ActiveRecording(record, files, options.DisplayId);
        try
        {
            if (options.Microphone)
            {
                active.Tracks.Add(CreateTrack(AudioDeviceKind.Microphone, options.MicrophoneDeviceId, files.MicrophoneAudio));
            }

            if (options.SystemAudio)
            {
                active.Tracks.Add(CreateTrack(AudioDeviceKind.SystemOutput, options.SystemDeviceId, files.SystemAudio));
            }

            foreach (var track in active.Tracks)
            {
                track.FaultHandler = fault => OnSourceFaulted(active, track, fault);
                track.Source.Faulted += track.FaultHandler;
            }

            active.StartTimestamp = _time.GetTimestamp();
            foreach (var track in active.Tracks)
            {
                track.Source.Start(track.Capture);
                track.Started = true;
            }

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
                HasMicrophone = options.Microphone,
                HasSystemAudio = options.SystemAudio,
            };
            await _store.UpdateSessionAsync(active.Record, cancellationToken).ConfigureAwait(false);
            return active;
        }
        catch (Exception ex)
        {
            await AbortAsync(active, ex).ConfigureAwait(false);
            throw;
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
        var index = active.NextSegmentIndex;
        var offset = _time.GetElapsedTime(active.StartTimestamp);
        await active.Screen.StartSegmentAsync(active.Files.ScreenSegment(index), cancellationToken).ConfigureAwait(false);
        active.NextSegmentIndex = index + 1;
        active.OpenSegment = (index, offset);
    }

    /// <param name="end">Segment end on the session timeline; <c>null</c> = now.</param>
    /// <remarks>
    /// When finalizing throws after the screen recorder has left the segment, the segment is still recorded and closed:
    /// its file is already on disk (a fragmented MP4 stays playable without a clean finalize), so it belongs to the
    /// session, and the next "on" starts a new segment. When it throws while the recorder is still recording (the
    /// failure came before the sink stopped), the segment stays open: the file is still growing past any end we could
    /// write, so a later "off" or the final stop retries the finalize. The error reaches the caller either way.
    /// </remarks>
    private async ValueTask StopScreenSegmentAsync(ActiveRecording active, TimeSpan? end, CancellationToken cancellationToken)
    {
        if (active.OpenSegment is not { } open || active.Screen is null)
        {
            return;
        }

        end ??= _time.GetElapsedTime(active.StartTimestamp);
        try
        {
            await active.Screen.StopSegmentAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!active.Screen.IsRecording)
            {
                CloseScreenSegment(active, open, end.Value);
            }
        }
    }

    private static void CloseScreenSegment(ActiveRecording active, (int Index, TimeSpan Offset) open, TimeSpan end)
    {
        active.Segments.Add(new ScreenSegment(open.Index, open.Offset, end - open.Offset));
        active.OpenSegment = null;
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
                if (ReferenceEquals(_active, active) && _state == RecorderState.Recording)
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
        var stopElapsed = _time.GetElapsedTime(active.StartTimestamp);
        try
        {
            string? audioError = null;
            try
            {
                await Task.Run(() => StopAudio(active, stopElapsed)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                audioError = ex.Message;
            }

            // The audio counters are final now. Take them with the screen counters while the screen recorder is still
            // alive: once disposed, its Stats may throw or be reset.
            FreezeStats(active);

            // Finalizing and releasing the screen are independent steps: a failed finalize (an HRESULT from Media
            // Foundation) must not leak the capture session, device and encoder, which nothing references after this.
            string? screenError = null;
            try
            {
                // The last segment ends where the user stopped, like the audio. If finalizing throws before the
                // recorder left the segment, it stays open here and is closed below once the recorder is released.
                await StopScreenSegmentAsync(active, stopElapsed, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                screenError = ex.Message;
            }

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
                    if (active.OpenSegment is { } open)
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
                Error = error ?? screenError,
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
                        Drain(active, track, stopElapsed);
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
        }

        try
        {
            await _store.UpdateSessionAsync(active.Record with { State = SessionState.Failed, Error = error.Message }, CancellationToken.None).ConfigureAwait(false);
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
                var elapsed = _time.GetElapsedTime(active.StartTimestamp);
                foreach (var track in active.Tracks)
                {
                    Drain(active, track, elapsed);
                }

                UpdateLevels(active);

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
                else if (iterations > WarmupIterations)
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

    /// <summary>Moves what is in the ring right now to the file; pads a faulted track with silence up to the target time.</summary>
    private static void Drain(ActiveRecording active, Track track, TimeSpan elapsed)
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

            Feed(track, track.Input.AsSpan(0, frames * channels));
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

        return new RecorderStats(mic, system, dropped, ScreenFramesDropped(active.Screen));
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

    private void SetState(RecorderState state)
    {
        _state = state;
        StateChanged?.Invoke(state);
    }

    private sealed class ActiveRecording(SessionRecord record, SessionFiles files, string? displayId)
    {
        public readonly List<Track> Tracks = [];
        public readonly List<ScreenSegment> Segments = [];
        public readonly ManualResetEventSlim StopSignal = new(false);
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

        public Thread? Writer { get; set; }

        public Thread? Flusher { get; set; }

        public volatile bool FlusherStop;

        public volatile string? FlusherError;

        public IScreenRecorder? Screen { get; set; }

        public int NextSegmentIndex { get; set; }

        public (int Index, TimeSpan Offset)? OpenSegment { get; set; }

        public volatile string? WriterError;

        /// <summary>Final counters, set once by <see cref="FreezeStats"/> during the stop; <c>null</c> before.</summary>
        public volatile StrongBox<RecorderStats>? FinalStats;
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
