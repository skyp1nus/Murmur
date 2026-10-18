using Murmur.Core.Audio;

namespace Murmur.Core.Capture.Synthetic;

/// <summary><see cref="IAudioPlayback"/> for Linux dev runs and tests: players are silent <see cref="ClockAudioPlayer"/>s.</summary>
public sealed class ClockAudioPlayback : IAudioPlayback
{
    private readonly TimeProvider _time;

    public ClockAudioPlayback(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public IAudioPlayer CreatePlayer() => new ClockAudioPlayer(_time);
}

/// <summary>
/// A silent <see cref="IAudioPlayer"/> driven by a <see cref="TimeProvider"/>. It defines the player contract that
/// every real player (WASAPI on Windows) follows.
/// </summary>
/// <remarks>
/// <para><b>States.</b> A new player is <see cref="PlaybackState.Closed"/>. <see cref="OpenAsync"/> opens 1..n 16 kHz
/// mono WAVs (missing file: <see cref="FileNotFoundException"/>; not a WAV: <see cref="InvalidDataException"/>; the
/// player is unchanged on failure) and leaves the player <see cref="PlaybackState.Paused"/> at 0 with
/// <see cref="Duration"/> = the longest track (from the WAV headers). Opening again replaces the tracks the same way.
/// <see cref="Dispose"/> returns to <see cref="PlaybackState.Closed"/>; every later call throws
/// <see cref="ObjectDisposedException"/>.</para>
/// <para><b>Play.</b> Paused -&gt; <see cref="PlaybackState.Playing"/>; while playing, <see cref="Position"/> advances in
/// real time. Play when <see cref="PlaybackState.Ended"/> restarts from 0. Play while playing is a no-op. Play while
/// paused at the very end (a zero-length session, or after seeking to <see cref="Duration"/>) goes straight to
/// Ended.</para>
/// <para><b>Pause.</b> Playing -&gt; Paused, holding the position. A no-op in Paused and Ended.</para>
/// <para><b>Seek.</b> Clamps to [0, <see cref="Duration"/>] and keeps playing or paused. Seeking to the very end while
/// playing ends playback (Ended); seeking before the end while Ended -&gt; Paused at the new position.</para>
/// <para><b>Ended.</b> When the position reaches <see cref="Duration"/> while playing, the player becomes Ended with
/// <see cref="Position"/> = <see cref="Duration"/>; StateChanged(Ended) is raised exactly once per end.
/// <see cref="Position"/> never exceeds <see cref="Duration"/>.</para>
/// <para><b>Closed.</b> Play, Pause and Seek before a successful open throw <see cref="InvalidOperationException"/>.</para>
/// <para><b>Threads.</b> Every member is thread-safe. <see cref="StateChanged"/> is raised only on a real change, after
/// the state was updated: on the calling thread for Open/Play/Pause/Seek, on a timer (thread-pool) thread for Ended -
/// UI subscribers must marshal. <see cref="Position"/> is cheap and lock-light enough for a 30 Hz UI timer.</para>
/// </remarks>
public sealed class ClockAudioPlayer : IAudioPlayer
{
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    private PlaybackState _state = PlaybackState.Closed;
    private TimeSpan _duration;
    private TimeSpan _basePosition;
    private long _playStartTimestamp;
    private ITimer? _endTimer;
    private long _generation;
    private bool _disposed;

    public ClockAudioPlayer(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public event Action<PlaybackState>? StateChanged;

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

    /// <summary>The tracks that are open (diagnostics and tests).</summary>
    public IReadOnlyList<string> Tracks { get; private set; } = [];

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

        var duration = TimeSpan.Zero;
        foreach (var path in trackPaths)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Audio track not found.", path);
            }

            using var reader = WavReader.Open(path);
            if (reader.Duration > duration)
            {
                duration = reader.Duration;
            }
        }

        PlaybackState? changed;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancelEndTimerLocked();
            Tracks = [.. trackPaths];
            _duration = duration;
            _basePosition = TimeSpan.Zero;
            changed = SetStateLocked(PlaybackState.Paused);
        }

        Raise(changed);
        return ValueTask.CompletedTask;
    }

    public void Play()
    {
        PlaybackState? changed;
        lock (_gate)
        {
            ThrowIfNotOpenLocked();
            if (_state == PlaybackState.Playing)
            {
                return;
            }

            if (_state == PlaybackState.Ended)
            {
                _basePosition = TimeSpan.Zero;
            }

            changed = _basePosition >= _duration ? EndLocked() : StartPlayingLocked();
        }

        Raise(changed);
    }

    public void Pause()
    {
        PlaybackState? changed;
        lock (_gate)
        {
            ThrowIfNotOpenLocked();
            if (_state != PlaybackState.Playing)
            {
                return;
            }

            _basePosition = CurrentPositionLocked();
            CancelEndTimerLocked();
            changed = SetStateLocked(PlaybackState.Paused);
        }

        Raise(changed);
    }

    public void Seek(TimeSpan position)
    {
        PlaybackState? changed;
        lock (_gate)
        {
            ThrowIfNotOpenLocked();
            var target = position < TimeSpan.Zero ? TimeSpan.Zero : position > _duration ? _duration : position;
            CancelEndTimerLocked();
            _basePosition = target;
            changed = _state switch
            {
                PlaybackState.Playing => target >= _duration ? EndLocked() : StartPlayingLocked(),
                PlaybackState.Ended when target < _duration => SetStateLocked(PlaybackState.Paused),
                PlaybackState.Ended => null,
                _ => null,
            };
        }

        Raise(changed);
    }

    public void Dispose()
    {
        PlaybackState? changed;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelEndTimerLocked();
            _basePosition = TimeSpan.Zero;
            _duration = TimeSpan.Zero;
            Tracks = [];
            changed = SetStateLocked(PlaybackState.Closed);
        }

        Raise(changed);
    }

    private TimeSpan CurrentPositionLocked()
    {
        if (_state != PlaybackState.Playing)
        {
            return _basePosition;
        }

        var position = _basePosition + _time.GetElapsedTime(_playStartTimestamp);
        return position > _duration ? _duration : position;
    }

    private PlaybackState? StartPlayingLocked()
    {
        CancelEndTimerLocked();
        _playStartTimestamp = _time.GetTimestamp();
        var generation = ++_generation;
        var remaining = _duration - _basePosition;
        _endTimer = _time.CreateTimer(static s => ((EndCallback)s!).Fire(), new EndCallback(this, generation), remaining, Timeout.InfiniteTimeSpan);
        return SetStateLocked(PlaybackState.Playing);
    }

    private PlaybackState? EndLocked()
    {
        CancelEndTimerLocked();
        _basePosition = _duration;
        return SetStateLocked(PlaybackState.Ended);
    }

    private void OnEndTimer(long generation)
    {
        PlaybackState? changed;
        lock (_gate)
        {
            if (_disposed || generation != _generation || _state != PlaybackState.Playing)
            {
                return; // A pause, seek or re-open superseded this timer.
            }

            if (_time.GetElapsedTime(_playStartTimestamp) < _duration - _basePosition)
            {
                // Fired early (coarse timer): re-arm for the rest.
                var rest = _duration - _basePosition - _time.GetElapsedTime(_playStartTimestamp);
                _endTimer?.Change(rest, Timeout.InfiniteTimeSpan);
                return;
            }

            changed = EndLocked();
        }

        Raise(changed);
    }

    private void CancelEndTimerLocked()
    {
        _generation++;
        _endTimer?.Dispose();
        _endTimer = null;
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

    private void Raise(PlaybackState? changed)
    {
        if (changed is { } state)
        {
            StateChanged?.Invoke(state);
        }
    }

    private sealed class EndCallback(ClockAudioPlayer player, long generation)
    {
        public void Fire() => player.OnEndTimer(generation);
    }
}
