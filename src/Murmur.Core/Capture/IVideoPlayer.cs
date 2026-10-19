namespace Murmur.Core.Capture;

/// <summary>Size of a video frame in pixels.</summary>
public readonly record struct VideoSize(int Width, int Height);

/// <summary>
/// Plays one screen segment file (MP4, video only) for the session view. It draws nothing by itself: on its render tick
/// the UI asks for the current picture, scaled to its own bitmap (<see cref="TryCopyFrame"/>), so the video can sit in
/// a normal styled control (rounded corners, overlays) on every platform. The session audio stays the master clock:
/// <c>Murmur.Core.Playback.ScreenPlaybackSync</c> decides which segment is open, when to seek, play and pause.
/// Call it from one thread (the UI thread); <see cref="Failed"/> may be raised on any thread.
/// </summary>
public interface IVideoPlayer : IDisposable
{
    bool IsOpen { get; }

    /// <summary>Media duration of the open file; <see cref="TimeSpan.Zero"/> when nothing is open.</summary>
    TimeSpan Duration { get; }

    /// <summary>Native frame size of the open file.</summary>
    VideoSize Size { get; }

    /// <summary>Current media position; cheap enough to poll every UI tick.</summary>
    TimeSpan Position { get; }

    bool IsPlaying { get; }

    /// <summary>Opens a file, replacing the open one (paused at 0); completes when duration and size are known.</summary>
    ValueTask OpenAsync(string filePath, CancellationToken cancellationToken);

    void Close();

    void Play();

    void Pause();

    /// <summary>Seeks the open file; clamped to [0, <see cref="Duration"/>]. Shows the frame at the new position.</summary>
    void Seek(TimeSpan position);

    /// <summary>
    /// Copies the picture at the current position, scaled to <paramref name="width"/> × <paramref name="height"/>
    /// (letterboxing is the caller's job: pass the fitted size), into a BGRA32 buffer with <paramref name="stride"/>
    /// bytes per row. False when nothing is open or the picture has not changed since the last successful call (the
    /// buffer is left untouched). Allocation-free after the first call for a given size.
    /// </summary>
    bool TryCopyFrame(Span<byte> destination, int width, int height, int stride);

    /// <summary>Playback of the open file failed (decoder, unreadable file). English, for logs.</summary>
    event Action<string>? Failed;
}

/// <summary>Platform video playback factory, implemented next to <see cref="ICapture"/>.</summary>
public interface IVideoPlayback
{
    /// <summary>False when this machine cannot decode the recordings; the session view then plays audio only.</summary>
    bool IsSupported { get; }

    IVideoPlayer CreateVideoPlayer();
}
