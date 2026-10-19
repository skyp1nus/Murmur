namespace Murmur.Core.Playback;

/// <summary>What <see cref="ScreenPlaybackSync.Update"/> asks the caller to do with its video player.</summary>
[Flags]
public enum VideoSyncActions
{
    None = 0,

    /// <summary>
    /// Close the player (cancelling an open in flight) and show the "screen off" placeholder: the audio is outside
    /// every playable segment.
    /// </summary>
    Close = 1,

    /// <summary>
    /// Open the file of <see cref="VideoSyncCommand.SegmentIndex"/> (replacing, or cancelling, whatever is open or
    /// opening) and report <see cref="VideoPlayerState.Opening"/> until it completes. Do not seek or play after the open:
    /// the next update does that with the then-current audio position.
    /// </summary>
    Open = 2,

    /// <summary>Seek the open player to <see cref="VideoSyncCommand.Position"/> (media time of the segment file).</summary>
    Seek = 4,

    /// <summary>Play (after the seek when both are set).</summary>
    Play = 8,

    /// <summary>Pause (after the seek when both are set).</summary>
    Pause = 16,
}

/// <summary>
/// One answer of <see cref="ScreenPlaybackSync.Update"/>. Apply the actions in this order: Close, Open, Seek, Play /
/// Pause (Close and Open never come together with the others).
/// </summary>
/// <param name="Actions">What to do; <see cref="VideoSyncActions.None"/> most of the time.</param>
/// <param name="SegmentIndex">
/// <see cref="Sessions.ScreenSegment.Index"/> of the segment to open (<see cref="VideoSyncActions.Open"/>), else the
/// segment the command is about; <see cref="VideoPlayerState.NoSegment"/> for <see cref="VideoSyncActions.Close"/> and
/// <see cref="VideoSyncActions.None"/> outside every segment.
/// </param>
/// <param name="Position">
/// Media time in the segment file: the seek target for <see cref="VideoSyncActions.Seek"/>; for
/// <see cref="VideoSyncActions.Open"/> the position the video should show once open (informational).
/// </param>
public readonly record struct VideoSyncCommand(VideoSyncActions Actions, int SegmentIndex, TimeSpan Position)
{
    public static VideoSyncCommand Nothing => new(VideoSyncActions.None, VideoPlayerState.NoSegment, TimeSpan.Zero);

    public bool Has(VideoSyncActions action) => (Actions & action) == action && action != VideoSyncActions.None;
}

/// <summary>
/// The caller's video player as <see cref="ScreenPlaybackSync.Update"/> needs to see it: which segment is open or
/// opening, and for an open one its position, whether it plays and its duration.
/// </summary>
/// <param name="SegmentIndex"><see cref="Sessions.ScreenSegment.Index"/> of the open / opening file, or <see cref="NoSegment"/>.</param>
/// <param name="IsOpening">The open of <paramref name="SegmentIndex"/> is still in flight.</param>
/// <param name="Position">Media position of the open file (<c>IVideoPlayer.Position</c>).</param>
/// <param name="IsPlaying"><c>IVideoPlayer.IsPlaying</c>.</param>
/// <param name="Duration">Media duration of the open file (<c>IVideoPlayer.Duration</c>); zero = unknown.</param>
public readonly record struct VideoPlayerState(int SegmentIndex, bool IsOpening, TimeSpan Position, bool IsPlaying, TimeSpan Duration)
{
    /// <summary>No segment is open or opening.</summary>
    public const int NoSegment = -1;

    /// <summary>Nothing open, nothing opening (the placeholder is shown).</summary>
    public static VideoPlayerState Closed => new(NoSegment, false, TimeSpan.Zero, false, TimeSpan.Zero);

    public bool IsClosed => SegmentIndex == NoSegment;

    /// <summary>The open of segment <paramref name="segmentIndex"/> is in flight.</summary>
    public static VideoPlayerState Opening(int segmentIndex) => new(segmentIndex, true, TimeSpan.Zero, false, TimeSpan.Zero);

    /// <summary>Segment <paramref name="segmentIndex"/> is open.</summary>
    public static VideoPlayerState Open(int segmentIndex, TimeSpan position, bool isPlaying, TimeSpan duration = default) =>
        new(segmentIndex, false, position, isPlaying, duration);
}

/// <summary>Tuning of <see cref="ScreenPlaybackSync"/>.</summary>
public sealed record ScreenPlaybackSyncOptions
{
    public static ScreenPlaybackSyncOptions Default { get; } = new();

    /// <summary>
    /// The largest |video − target| left alone; also how far the audio may jump between two updates before it counts as
    /// a user seek. Default 150 ms.
    /// </summary>
    public TimeSpan Tolerance { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// The shortest time between two drift-correcting seeks while playing (a slow decoder must not be buried in seeks).
    /// User seeks and the first seek after an open are not limited. Default 500 ms.
    /// </summary>
    public TimeSpan MinSeekInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Upper bound of the learned seek lead: while playing, a seek lands this far ahead of the audio to make up for the
    /// time the decoder needs to get there. Default 1 s.
    /// </summary>
    public TimeSpan MaxSeekLead { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a seek may stay pending while playing (the video still reports exactly the seek target: the decoder has
    /// not got there yet) before the sync gives up waiting and may measure and seek again. Default 2 s.
    /// </summary>
    public TimeSpan SeekPendingTimeout { get; init; } = TimeSpan.FromSeconds(2);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Tolerance, TimeSpan.Zero, nameof(Tolerance));
        ArgumentOutOfRangeException.ThrowIfLessThan(MinSeekInterval, TimeSpan.Zero, nameof(MinSeekInterval));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSeekLead, TimeSpan.Zero, nameof(MaxSeekLead));
        ArgumentOutOfRangeException.ThrowIfLessThan(SeekPendingTimeout, TimeSpan.Zero, nameof(SeekPendingTimeout));
    }
}
