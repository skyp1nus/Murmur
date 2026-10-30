namespace Murmur.Capture.Windows.Playback;

/// <summary>
/// The device side of a <see cref="WasapiAudioPlayer"/>: renders a <see cref="TrackMixer"/> it owns. Commands are
/// synchronous and return an HRESULT (negative = the stream is dead; the engine must then be disposed). Callers
/// serialize commands (the player issues them under its lock). <see cref="PlayedFrame"/> is a lock-free snapshot.
/// </summary>
internal interface IPlaybackEngine : IDisposable
{
    /// <summary>The frame being heard right now (submitted minus padding), refreshed every device period.</summary>
    long PlayedFrame { get; }

    /// <summary>Commands issued so far; an end notification that carries an older value is stale.</summary>
    long Generation { get; }

    /// <summary>Starts (or resumes) rendering from the engine's current position: prefill, then IAudioClient.Start.</summary>
    int Play();

    /// <summary>IAudioClient.Stop: the buffered audio and the position are kept.</summary>
    int Pause();

    /// <summary>
    /// Plays at <paramref name="speed"/> (already clamped, 1 = the recording's own speed) from the next hop on; lock-free and
    /// callable while rendering. <see cref="PlayedFrame"/> stays in media frames.
    /// </summary>
    void SetSpeed(double speed);

    /// <summary>Flushes (Stop + Reset), repositions the tracks to <paramref name="frame"/>, and plays if asked.</summary>
    int Seek(long frame, bool play);
}

/// <summary>Receives what the render thread detects on its own. Called on a thread-pool thread, never the render thread.</summary>
internal interface IPlaybackEngineOwner
{
    /// <summary>All the data was played out; <paramref name="generation"/> is <see cref="IPlaybackEngine.Generation"/> at that time.</summary>
    void OnEnded(IPlaybackEngine engine, long generation);

    /// <summary>The stream died while rendering (e.g. AUDCLNT_E_DEVICE_INVALIDATED); the engine has stopped.</summary>
    void OnFault(IPlaybackEngine engine, int hresult, string message);
}

/// <summary>
/// Creates an engine positioned at <paramref name="startFrame"/> that takes ownership of <paramref name="mixer"/> on
/// success (on failure the caller still owns it).
/// </summary>
internal delegate IPlaybackEngine PlaybackEngineFactory(TrackMixer mixer, long startFrame, IPlaybackEngineOwner owner);
