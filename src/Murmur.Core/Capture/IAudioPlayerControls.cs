namespace Murmur.Core.Capture;

/// <summary>
/// What a session player can do beyond play, pause and seek, for the track filters (You / Others) and the speed chip of the
/// session view. A player offers it by implementing this interface next to <see cref="IAudioPlayer"/>; the UI asks for the
/// capability (<c>player as IAudioPlayerControls</c>) and disables the control a player cannot honour, so a player that has neither
/// is still a valid <see cref="IAudioPlayer"/>.
/// </summary>
public interface IAudioPlayerControls
{
    /// <summary>
    /// The number of tracks a session has and the player can address: the track at index 0 of the list given to
    /// <see cref="IAudioPlayer.OpenAsync"/> is the microphone, the one at index 1 the system audio (the session view opens them in that
    /// order; with one of them missing the other one is index 0).
    /// </summary>
    public const int MaxTracks = 2;

    /// <summary>The player can silence one of the tracks it was opened with.</summary>
    bool CanMuteTracks { get; }

    /// <summary>
    /// Silences (or restores) the track at <paramref name="track"/>. The setting survives seeks and opening the tracks again, and may be
    /// made before the tracks are opened. Does nothing without <see cref="CanMuteTracks"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="track"/> is not in [0, <see cref="MaxTracks"/>).</exception>
    void SetTrackMuted(int track, bool muted);

    /// <summary>Whether the track at <paramref name="track"/> is silenced (always <c>false</c> without <see cref="CanMuteTracks"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="track"/> is not in [0, <see cref="MaxTracks"/>).</exception>
    bool IsTrackMuted(int track);

    /// <summary>The player can play faster or slower than real time.</summary>
    bool CanChangeSpeed { get; }

    /// <summary>
    /// The playback speed in effect, 1 = real time; <see cref="IAudioPlayer.Position"/> advances at this rate. Always 1 without
    /// <see cref="CanChangeSpeed"/>.
    /// </summary>
    double Speed { get; }

    /// <summary>
    /// Asks for a playback speed (the UI offers 0.75 to 2). The player clamps it to what it supports; <see cref="Speed"/> reports the value
    /// that is in effect afterwards. Does nothing without <see cref="CanChangeSpeed"/>.
    /// </summary>
    void SetSpeed(double speed);
}
