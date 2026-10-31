namespace Murmur.Core.Capture;

/// <summary>
/// What a screen video player can do beyond open, play, pause and seek: follow the playback speed of the session's audio, so the
/// video does not fall behind or run ahead of the audio clock and get re-sought all the time. A player offers it by implementing
/// this interface next to <see cref="IVideoPlayer"/>; the session asks for it (<c>player as IVideoPlayerControls</c>) and a player
/// without it is still a valid <see cref="IVideoPlayer"/> (the audio clock keeps it in step by seeking).
/// </summary>
public interface IVideoPlayerControls
{
    /// <summary>
    /// Plays at <paramref name="rate"/> × real time (1 = real time), the same speed as the audio (<see cref="IAudioPlayerControls.Speed"/>).
    /// The rate survives seeks and opening another segment, and may be set before a segment is opened; the player clamps it to what it
    /// supports.
    /// </summary>
    void SetPlaybackRate(double rate);
}
