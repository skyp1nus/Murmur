namespace Murmur.Core.Capture;

/// <summary>
/// What a session player can do beyond play, pause and seek, for the track filters (You / Others) and the speed chip of the
/// session view. A player offers it by implementing this interface next to <see cref="IAudioPlayer"/>; the UI asks for the
/// capability (<c>player as IAudioPlayerControls</c>) and disables the control a player cannot honour, so a player that has neither
/// is still a valid <see cref="IAudioPlayer"/>.
/// </summary>
public interface IAudioPlayerControls
{
    /// <summary>The player can silence one of the tracks it was opened with.</summary>
    bool CanMuteTracks { get; }

    /// <summary>The player can play faster or slower than real time.</summary>
    bool CanChangeSpeed { get; }

    /// <summary>
    /// Silences (or restores) the track at <paramref name="trackIndex"/> of the list given to <see cref="IAudioPlayer.OpenAsync"/>.
    /// The setting survives seeks and opening the tracks again; a call without <see cref="CanMuteTracks"/> does nothing.
    /// </summary>
    void SetTrackMuted(int trackIndex, bool muted);

    /// <summary>
    /// Playback speed, 1 = real time (the UI offers 0.75 to 2). <see cref="IAudioPlayer.Position"/> advances at this rate; a value
    /// outside [0.25, 4] is clamped. Setting it without <see cref="CanChangeSpeed"/> does nothing.
    /// </summary>
    double Speed { get; set; }
}
