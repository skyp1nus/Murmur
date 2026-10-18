namespace Murmur.Core.Capture;

public enum PlaybackState
{
    Closed,
    Paused,
    Playing,
    Ended,
}

/// <summary>Plays one or more 16 kHz mono WAV tracks of a session mixed together.</summary>
public interface IAudioPlayer : IDisposable
{
    PlaybackState State { get; }

    TimeSpan Duration { get; }

    /// <summary>Current playback position; cheap enough to poll from a UI timer.</summary>
    TimeSpan Position { get; }

    ValueTask OpenAsync(IReadOnlyList<string> trackPaths, CancellationToken cancellationToken);

    void Play();

    void Pause();

    void Seek(TimeSpan position);

    event Action<PlaybackState>? StateChanged;
}

/// <summary>Platform playback factory, implemented next to <see cref="ICapture"/>.</summary>
public interface IAudioPlayback
{
    IAudioPlayer CreatePlayer();
}
