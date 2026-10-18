using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Playback;

/// <summary><see cref="IAudioPlayback"/> over WASAPI: every player renders to the default output (render endpoint).</summary>
public sealed class WasapiAudioPlayback : IAudioPlayback
{
    /// <summary>A closed player; nothing native is touched until <see cref="IAudioPlayer.OpenAsync"/>.</summary>
    public IAudioPlayer CreatePlayer() => new WasapiAudioPlayer();
}

/// <summary>
/// Plays the 16 kHz mono PCM16 tracks of a session, mixed, on the default render endpoint (WASAPI shared mode; the
/// audio engine converts to the mix format). Follows the player contract defined on
/// <c>Murmur.Core.Capture.Synthetic.ClockAudioPlayer</c>:
/// <list type="bullet">
/// <item>New = <see cref="PlaybackState.Closed"/>. <see cref="OpenAsync"/> opens 1..n mono PCM16 WAVs of one sample
/// rate (missing: <see cref="FileNotFoundException"/>; not a WAV / not mono PCM16 / mixed rates:
/// <see cref="InvalidDataException"/>; no render endpoint: <see cref="InvalidOperationException"/>; the player is
/// unchanged on failure) and leaves it <see cref="PlaybackState.Paused"/> at 0, <see cref="Duration"/> = the longest
/// track. Opening again replaces the tracks. Play/Pause/Seek before a successful open throw
/// <see cref="InvalidOperationException"/>; every call after <see cref="Dispose"/> throws <see cref="ObjectDisposedException"/>.</item>
/// <item>Play: Paused -&gt; Playing; from Ended it restarts at 0; at the very end it goes straight to Ended; a no-op
/// while playing. Pause: Playing -&gt; Paused (IAudioClient.Stop, position kept); a no-op otherwise.</item>
/// <item>Seek clamps to [0, Duration] and flushes (Stop + Reset, readers repositioned), keeping Playing/Paused;
/// seeking to the end while playing -&gt; Ended, seeking before the end while Ended -&gt; Paused.</item>
/// <item><see cref="Position"/> = (frames submitted since the last flush - GetCurrentPadding) as time, refreshed by the
/// render thread every device period (~10 ms), so it is accurate to ~20 ms; it never exceeds <see cref="Duration"/>.</item>
/// <item>Ended: when the data has been played out, State = Ended, Position = Duration, StateChanged(Ended) once.</item>
/// <item>Faults: when the stream dies (AUDCLNT_E_DEVICE_INVALIDATED: the output was unplugged or disabled, the audio
/// service stopped, a track became unreadable) the player becomes Paused at the last heard position, raises
/// StateChanged(Paused) if it was playing, then <see cref="Faulted"/>; <see cref="LastFault"/> keeps the message.
/// The next Play opens the (new) default endpoint again; if that fails too the player stays Paused and reports
/// another fault instead of throwing. A stream that dies while Paused or Ended goes unnoticed until the next Play:
/// its failed command makes Play reopen the default endpoint once and retry (from Ended, a failed restart stays
/// Ended at Duration).</item>
/// <item>Threads: every member is thread-safe. StateChanged and Faulted are raised after the state was updated and
/// outside the lock: on the calling thread for Open/Play/Pause/Seek/Dispose, on a thread-pool thread for Ended and for
/// faults detected while rendering (never on the render thread) - UI subscribers must marshal.</item>
/// </list>
/// UNVERIFIED-ON-WINDOWS until the Windows-trait tests run in CI.
/// </summary>
public sealed class WasapiAudioPlayer : IAudioPlayer, IPlaybackEngineOwner
{
    private const string FaultSource = "wasapi-playback";

    private readonly PlaybackEngineFactory _factory;
    private readonly Lock _gate = new();

    private PlaybackState _state = PlaybackState.Closed;
    private string[] _paths = [];
    private int _sampleRate = 1;
    private long _totalFrames;
    private TimeSpan _duration;
    private TimeSpan _basePosition;
    private IPlaybackEngine? _engine;
    private CaptureFault? _lastFault;
    private bool _disposed;

    public WasapiAudioPlayer()
        : this(WasapiRenderEngine.Create)
    {
    }

    internal WasapiAudioPlayer(PlaybackEngineFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public event Action<PlaybackState>? StateChanged;

    /// <summary>The stream died (see the class remarks); raised after StateChanged.</summary>
    public event Action<CaptureFault>? Faulted;

    public PlaybackState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public TimeSpan Duration
    {
        get
        {
            lock (_gate)
            {
                return _duration;
            }
        }
    }

    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                return CurrentPositionLocked();
            }
        }
    }

    /// <summary>The last fault since the last successful open, or null.</summary>
    public CaptureFault? LastFault
    {
        get
        {
            lock (_gate)
            {
                return _lastFault;
            }
        }
    }

    /// <summary>
    /// Opens the tracks and the default render endpoint synchronously (header reads + IAudioClient initialization,
    /// typically a few tens of milliseconds) and completes.
    /// </summary>
    public ValueTask OpenAsync(IReadOnlyList<string> trackPaths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trackPaths);
        if (trackPaths.Count == 0)
        {
            throw new ArgumentException("At least one track is required.", nameof(trackPaths));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        var paths = new string[trackPaths.Count];
        for (var i = 0; i < paths.Length; i++)
        {
            paths[i] = trackPaths[i] ?? throw new ArgumentException("Track paths must not be null.", nameof(trackPaths));
        }

        var mixer = OpenMixer(paths);
        IPlaybackEngine engine;
        try
        {
            engine = _factory(mixer, 0, this);
        }
        catch
        {
            mixer.Dispose();
            throw;
        }

        IPlaybackEngine? previous;
        PlaybackState? changed;
        lock (_gate)
        {
            if (_disposed)
            {
                engine.Dispose();
                throw new ObjectDisposedException(nameof(WasapiAudioPlayer));
            }

            previous = _engine;
            _engine = engine;
            _paths = paths;
            _sampleRate = mixer.SampleRate;
            _totalFrames = mixer.TotalFrames;
            _duration = PlaybackTiming.ToTime(_totalFrames, _sampleRate);
            _basePosition = TimeSpan.Zero;
            _lastFault = null;
            changed = SetStateLocked(PlaybackState.Paused);
        }

        previous?.Dispose();
        Raise(changed, null);
        return ValueTask.CompletedTask;
    }

    public void Play()
    {
        PlaybackState? changed;
        CaptureFault? fault = null;
        lock (_gate)
        {
            ThrowIfNotOpenLocked();
            if (_state == PlaybackState.Playing)
            {
                return;
            }

            // From Ended, play restarts at 0; the held position only changes once the stream has really started, so a
            // failed restart stays Ended with Position == Duration.
            var restart = _state == PlaybackState.Ended;
            var start = restart ? TimeSpan.Zero : _basePosition;
            if (start >= _duration)
            {
                changed = EndLocked();
            }
            else
            {
                var frame = PlaybackTiming.ToFrame(start, _sampleRate, _totalFrames);
                changed = StartLocked(start, frame, restart, out fault);
            }
        }

        Raise(changed, fault);
    }

    public void Pause()
    {
        PlaybackState? changed;
        CaptureFault? fault = null;
        lock (_gate)
        {
            ThrowIfNotOpenLocked();
            if (_state != PlaybackState.Playing)
            {
                return;
            }

            var hr = _engine!.Pause();
            if (hr < 0)
            {
                changed = FaultLocked(CommandFailed("Pause", hr), out fault);
            }
            else
            {
                _basePosition = PlaybackTiming.ToTime(_engine.PlayedFrame, _sampleRate);
                changed = SetStateLocked(PlaybackState.Paused);
            }
        }

        Raise(changed, fault);
    }

    public void Seek(TimeSpan position)
    {
        PlaybackState? changed = null;
        CaptureFault? fault = null;
        lock (_gate)
        {
            ThrowIfNotOpenLocked();
            var target = PlaybackTiming.Clamp(position, _duration);
            var frame = PlaybackTiming.ToFrame(target, _sampleRate, _totalFrames);
            _basePosition = target;
            int hr;
            switch (_state)
            {
                case PlaybackState.Playing when target >= _duration:
                    hr = _engine!.Pause();
                    changed = hr < 0 ? FaultLocked(CommandFailed("Seek", hr), out fault) : EndLocked();
                    break;

                case PlaybackState.Playing:
                    hr = _engine!.Seek(frame, play: true);
                    changed = hr < 0 ? FaultLocked(CommandFailed("Seek", hr), out fault) : null;
                    break;

                case PlaybackState.Ended when target >= _duration:
                    break;

                default: // Paused, or Ended -> Paused at the new position
                    hr = _engine?.Seek(frame, play: false) ?? WasapiConstants.SOk;
                    if (hr < 0)
                    {
                        FaultLocked(CommandFailed("Seek", hr), out fault);
                    }

                    changed = SetStateLocked(PlaybackState.Paused);
                    break;
            }

            if (fault is not null)
            {
                _basePosition = target; // hold the requested position, not the one the dead stream reached
            }
        }

        Raise(changed, fault);
    }

    public void Dispose()
    {
        IPlaybackEngine? engine;
        PlaybackState? changed;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            engine = _engine;
            _engine = null;
            _paths = [];
            _totalFrames = 0;
            _duration = TimeSpan.Zero;
            _basePosition = TimeSpan.Zero;
            changed = SetStateLocked(PlaybackState.Closed);
        }

        engine?.Dispose();
        Raise(changed, null);
    }

    void IPlaybackEngineOwner.OnEnded(IPlaybackEngine engine, long generation)
    {
        PlaybackState? changed;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(engine, _engine) || generation != engine.Generation || _state != PlaybackState.Playing)
            {
                return; // A pause, seek, re-open or dispose superseded this end.
            }

            changed = EndLocked();
        }

        Raise(changed, null);
    }

    void IPlaybackEngineOwner.OnFault(IPlaybackEngine engine, int hresult, string message)
    {
        PlaybackState? changed;
        CaptureFault? fault;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(engine, _engine))
            {
                return;
            }

            changed = FaultLocked(message, out fault);
        }

        Raise(changed, fault);
    }

    private static TrackMixer OpenMixer(string[] paths)
    {
        var readers = new List<Pcm16TrackReader>(paths.Length);
        try
        {
            foreach (var path in paths)
            {
                readers.Add(Pcm16TrackReader.Open(path));
            }

            return new TrackMixer(readers);
        }
        catch
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }

            throw;
        }
    }

    private bool TryCreateEngineLocked(long frame, out CaptureFault? fault)
    {
        fault = null;
        TrackMixer? mixer = null;
        try
        {
            mixer = OpenMixer(_paths);
            _engine = _factory(mixer, frame, this);
            return true;
        }
        catch (Exception ex)
        {
            mixer?.Dispose();
            fault = new CaptureFault(FaultSource, ex.Message, IsFatal: true);
            _lastFault = fault;
            return false;
        }
    }

    /// <summary>
    /// Starts rendering at <paramref name="frame"/>. The render thread only watches the device while running, so an
    /// engine kept across Paused/Ended may have died unnoticed (output unplugged or invalidated): when its command
    /// fails, it is replaced by one on the (new) default endpoint and the start is retried once before it is a fault.
    /// </summary>
    private PlaybackState? StartLocked(TimeSpan start, long frame, bool restart, out CaptureFault? fault)
    {
        fault = null;
        var fresh = false;
        if (_engine is null)
        {
            if (!TryCreateEngineLocked(frame, out fault))
            {
                return null; // no endpoint can be opened: the state (and the held position) stay as they are
            }

            fresh = true;
        }

        // A fresh engine starts at the requested frame; a kept one must rewind first after Ended.
        var hr = fresh || !restart ? _engine!.Play() : _engine!.Seek(frame, play: true);
        if (hr < 0 && !fresh)
        {
            var dead = _engine!;
            _engine = null;
            dead.Dispose(); // the render thread is idle (not running): this only joins it
            if (!TryCreateEngineLocked(frame, out fault))
            {
                return null;
            }

            hr = _engine!.Play();
        }

        if (hr < 0)
        {
            return FaultLocked(CommandFailed("Play", hr), out fault);
        }

        _basePosition = start;
        return SetStateLocked(PlaybackState.Playing);
    }

    private TimeSpan CurrentPositionLocked()
    {
        if (_state != PlaybackState.Playing || _engine is null)
        {
            return _basePosition;
        }

        return PlaybackTiming.Clamp(PlaybackTiming.ToTime(_engine.PlayedFrame, _sampleRate), _duration);
    }

    private static string CommandFailed(string command, int hresult) => $"{command} failed: {ComHelpers.Describe(hresult)}";

    /// <summary>Drops the dead engine, holds the last heard position and records the fault; Playing -&gt; Paused.</summary>
    private PlaybackState? FaultLocked(string message, out CaptureFault fault)
    {
        var engine = _engine;
        if (engine is not null)
        {
            if (_state == PlaybackState.Playing)
            {
                _basePosition = PlaybackTiming.Clamp(PlaybackTiming.ToTime(engine.PlayedFrame, _sampleRate), _duration);
            }

            _engine = null;
            engine.Dispose(); // the render thread has already left its loop: this only joins it
        }

        fault = new CaptureFault(FaultSource, $"Playback stopped: {message}", IsFatal: true);
        _lastFault = fault;
        return _state == PlaybackState.Playing ? SetStateLocked(PlaybackState.Paused) : null;
    }

    private PlaybackState? EndLocked()
    {
        _basePosition = _duration;
        return SetStateLocked(PlaybackState.Ended);
    }

    private PlaybackState? SetStateLocked(PlaybackState state)
    {
        if (_state == state)
        {
            return null;
        }

        _state = state;
        return state;
    }

    private void ThrowIfNotOpenLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state == PlaybackState.Closed)
        {
            throw new InvalidOperationException("Open the player first.");
        }
    }

    private void Raise(PlaybackState? changed, CaptureFault? fault)
    {
        if (changed is { } state)
        {
            StateChanged?.Invoke(state);
        }

        if (fault is not null)
        {
            Faulted?.Invoke(fault);
        }
    }
}
