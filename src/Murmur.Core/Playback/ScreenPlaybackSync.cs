using Murmur.Core.Sessions;

namespace Murmur.Core.Playback;

/// <summary>
/// Keeps the screen video of a session in step with its audio, which is the master clock. The session view calls
/// <see cref="Update"/> from its UI timer with the audio position and the state of its <c>IVideoPlayer</c>, and applies
/// the returned <see cref="VideoSyncCommand"/>. Pure decision logic: it touches no player, and <see cref="Update"/> is
/// allocation-free.
/// </summary>
/// <remarks>
/// <para><b>Timeline (D-033).</b> Media time 0 of <c>screen-NNN.mp4</c> is session time
/// <see cref="ScreenSegment.Offset"/>; the audio starts at session time 0. Between segments the screen was off.</para>
/// <para><b>Rules.</b></para>
/// <list type="bullet">
/// <item>The segment that contains the audio position (<c>Offset ≤ position ≤ Offset + Duration</c>; the later one where
/// two touch) is the one to show. Outside every segment, or in one marked with <see cref="MarkUnplayable"/>:
/// <see cref="VideoSyncActions.Close"/> (show the placeholder) - once, while anything is open or opening.</item>
/// <item>Another segment is open, or none: <see cref="VideoSyncActions.Open"/>. While the caller reports it
/// <see cref="VideoPlayerState.Opening"/>, nothing is asked (no second open).</item>
/// <item>Target video time = audio position − <see cref="ScreenSegment.Offset"/>, clamped to
/// [0, <see cref="ScreenSegment.Duration"/>] (and to the file's duration when the player knows it).</item>
/// <item>Drift: when |video − target| &gt; <see cref="ScreenPlaybackSyncOptions.Tolerance"/>,
/// <see cref="VideoSyncActions.Seek"/>. Right after an open, after a user seek (the audio position jumped by more than
/// the tolerance between two updates) and while paused to a new target: at once. Otherwise while playing: at most once per
/// <see cref="ScreenPlaybackSyncOptions.MinSeekInterval"/>, so a slow decoder is not buried in seeks; a paused re-seek to
/// the same target is limited the same way.</item>
/// <item>Seek lead: a seek while playing lands <see cref="SeekLead"/> ahead of the audio. The lead is learned from how far
/// behind the video is one <see cref="ScreenPlaybackSyncOptions.MinSeekInterval"/> after such a seek (clamped to
/// [0, <see cref="ScreenPlaybackSyncOptions.MaxSeekLead"/>]), so a decoder that needs time to seek does not drift back
/// out of tolerance after every seek.</item>
/// <item>Play / pause follow the audio. Within <see cref="ScreenPlaybackSyncOptions.Tolerance"/> of the end of the file
/// the video is not started (a media player restarts an ended file from 0); it holds the last frame until the audio
/// leaves the segment, which then closes it.</item>
/// </list>
/// <para><b>Example</b> (the session view model, on its existing UI timer):</para>
/// <code>
/// var sync = new ScreenPlaybackSync(session.ScreenSegments);
///
/// void OnTick()
/// {
///     var video = _openingSegment is { } opening ? VideoPlayerState.Opening(opening)
///         : _openSegment is { } open ? VideoPlayerState.Open(open, _video.Position, _video.IsPlaying, _video.Duration)
///         : VideoPlayerState.Closed;
///     var command = sync.Update(_audio.Position, _audio.State == PlaybackState.Playing, video);
///     if (command.Has(VideoSyncActions.Close))
///     {
///         CancelOpen();
///         _video.Close();
///         _openSegment = null;
///         ShowScreenOffPlaceholder();
///     }
///     else if (command.Has(VideoSyncActions.Open))
///     {
///         _ = OpenAsync(command.SegmentIndex); // sets _openingSegment, then _openSegment; on failure sync.MarkUnplayable(index)
///     }
///
///     if (command.Has(VideoSyncActions.Seek)) _video.Seek(command.Position);
///     if (command.Has(VideoSyncActions.Play)) _video.Play();
///     if (command.Has(VideoSyncActions.Pause)) _video.Pause();
///     if (_openSegment is not null) CopyFrameIntoBitmap(); // IVideoPlayer.TryCopyFrame
/// }
/// </code>
/// <para><b>Threads.</b> Not thread-safe: call it from one thread (the UI thread).</para>
/// </remarks>
public sealed class ScreenPlaybackSync
{
    private readonly ScreenSegment[] _segments;
    private readonly bool[] _unplayable;
    private readonly ScreenPlaybackSyncOptions _options;
    private readonly TimeProvider _time;

    private bool _hasLast;
    private TimeSpan _lastAudio;
    private bool _lastAudioPlaying;
    private long _lastTimestamp;

    // The open segment the decisions below are about: reset whenever another one is opened.
    private int _readySegment = VideoPlayerState.NoSegment;
    private bool _seekedSinceReady;
    private long _lastSeekTimestamp;
    private TimeSpan _lastSeekTarget;
    private bool _leadMeasurable;

    /// <param name="segments">The session's <see cref="SessionRecord.ScreenSegments"/>, in any order.</param>
    /// <param name="options">Tolerances; <see cref="ScreenPlaybackSyncOptions.Default"/> when null.</param>
    /// <param name="time">The clock that rate-limits seeks and detects audio jumps; the system clock when null.</param>
    public ScreenPlaybackSync(IReadOnlyList<ScreenSegment> segments, ScreenPlaybackSyncOptions? options = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        _options = options ?? ScreenPlaybackSyncOptions.Default;
        _options.Validate();
        _time = time ?? TimeProvider.System;
        _segments = [.. segments];
        foreach (var segment in _segments)
        {
            ArgumentNullException.ThrowIfNull(segment, nameof(segments));
        }

        Array.Sort(_segments, static (a, b) => a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset) : a.Index.CompareTo(b.Index));
        _unplayable = new bool[_segments.Length];
    }

    /// <summary>The segments, ordered by <see cref="ScreenSegment.Offset"/>.</summary>
    public IReadOnlyList<ScreenSegment> Segments => _segments;

    public ScreenPlaybackSyncOptions Options => _options;

    /// <summary>How far ahead of the audio a seek while playing lands (learned; diagnostics).</summary>
    public TimeSpan SeekLead { get; private set; }

    /// <summary>
    /// The segment to show at session time <paramref name="sessionPosition"/> (see the rules), or null when the screen
    /// was off then. Segments marked with <see cref="MarkUnplayable"/> are still found here.
    /// </summary>
    public ScreenSegment? SegmentAt(TimeSpan sessionPosition)
    {
        var slot = Find(sessionPosition);
        return slot < 0 ? null : _segments[slot];
    }

    /// <summary>
    /// Marks a segment whose file cannot be played (the open failed, or the player raised <c>Failed</c>): it is shown as
    /// "screen off" from now on, and never opened again by this instance.
    /// </summary>
    public void MarkUnplayable(int segmentIndex)
    {
        for (var i = 0; i < _segments.Length; i++)
        {
            if (_segments[i].Index == segmentIndex)
            {
                _unplayable[i] = true;
            }
        }
    }

    /// <summary>True when <paramref name="segmentIndex"/> was marked with <see cref="MarkUnplayable"/>.</summary>
    public bool IsUnplayable(int segmentIndex)
    {
        for (var i = 0; i < _segments.Length; i++)
        {
            if (_segments[i].Index == segmentIndex && _unplayable[i])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Decides what to do with the video player for this tick. <paramref name="audioPosition"/> is the session audio
    /// position (<c>IAudioPlayer.Position</c>), <paramref name="audioPlaying"/> whether it plays; <paramref name="video"/>
    /// is the player's state. Allocation-free.
    /// </summary>
    public VideoSyncCommand Update(TimeSpan audioPosition, bool audioPlaying, in VideoPlayerState video)
    {
        var now = _time.GetTimestamp();
        var jumped = !_hasLast || Jumped(audioPosition, audioPlaying, now);
        _hasLast = true;
        _lastAudio = audioPosition;
        _lastAudioPlaying = audioPlaying;
        _lastTimestamp = now;

        var slot = Find(audioPosition);
        if (slot >= 0 && _unplayable[slot])
        {
            slot = -1;
        }

        if (slot < 0)
        {
            ForgetReady();
            return video.IsClosed ? VideoSyncCommand.Nothing : new VideoSyncCommand(VideoSyncActions.Close, VideoPlayerState.NoSegment, TimeSpan.Zero);
        }

        var segment = _segments[slot];
        var end = segment.Duration;
        if (video.SegmentIndex == segment.Index && !video.IsOpening && video.Duration > TimeSpan.Zero && video.Duration < end)
        {
            end = video.Duration;
        }

        var target = Clamp(audioPosition - segment.Offset, end);
        if (video.SegmentIndex != segment.Index)
        {
            ForgetReady();
            return new VideoSyncCommand(VideoSyncActions.Open, segment.Index, target);
        }

        if (video.IsOpening)
        {
            ForgetReady();
            return new VideoSyncCommand(VideoSyncActions.None, segment.Index, target);
        }

        var first = _readySegment != segment.Index;
        if (first)
        {
            _readySegment = segment.Index;
            _seekedSinceReady = false;
            _leadMeasurable = false;
        }

        var actions = VideoSyncActions.None;
        var seekTo = TimeSpan.Zero;
        var error = target - video.Position; // positive: the video is behind
        var tolerance = _options.Tolerance;
        var sinceSeek = _seekedSinceReady ? _time.GetElapsedTime(_lastSeekTimestamp, now) : TimeSpan.MaxValue;

        if (audioPlaying)
        {
            if (jumped || first)
            {
                _leadMeasurable = false; // the last seek's outcome says nothing about the decoder any more
            }
            else if (_leadMeasurable && sinceSeek >= _options.MinSeekInterval)
            {
                _leadMeasurable = false;
                if (error.Duration() > tolerance)
                {
                    SeekLead = Clamp(SeekLead + error, _options.MaxSeekLead);
                }
            }

            if (error.Duration() > tolerance && (first || jumped || sinceSeek >= _options.MinSeekInterval))
            {
                actions |= VideoSyncActions.Seek;
                seekTo = Clamp(target + SeekLead, end);
                _leadMeasurable = true;
            }
        }
        else
        {
            _leadMeasurable = false;
            var sameTarget = _seekedSinceReady && (target - _lastSeekTarget).Duration() <= tolerance;
            if (error.Duration() > tolerance && (!sameTarget || sinceSeek >= _options.MinSeekInterval))
            {
                actions |= VideoSyncActions.Seek;
                seekTo = target;
            }
        }

        if (actions != VideoSyncActions.None)
        {
            _seekedSinceReady = true;
            _lastSeekTimestamp = now;
            _lastSeekTarget = seekTo;
        }

        var position = (actions & VideoSyncActions.Seek) != 0 ? seekTo : target;
        if (audioPlaying && !video.IsPlaying && position < end - tolerance)
        {
            actions |= VideoSyncActions.Play;
        }
        else if (!audioPlaying && video.IsPlaying)
        {
            actions |= VideoSyncActions.Pause;
        }

        return new VideoSyncCommand(actions, segment.Index, (actions & VideoSyncActions.Seek) != 0 ? seekTo : target);
    }

    /// <summary>Forgets the audio history and the per-open state (e.g. after the audio was reopened). The seek lead stays.</summary>
    public void Reset()
    {
        _hasLast = false;
        ForgetReady();
    }

    private void ForgetReady()
    {
        _readySegment = VideoPlayerState.NoSegment;
        _seekedSinceReady = false;
        _leadMeasurable = false;
    }

    /// <summary>The audio moved by more than the tolerance away from where the clock says it should be.</summary>
    private bool Jumped(TimeSpan audioPosition, bool audioPlaying, long now)
    {
        var expected = audioPlaying || _lastAudioPlaying ? _time.GetElapsedTime(_lastTimestamp, now) : TimeSpan.Zero;
        var moved = audioPosition - _lastAudio;
        if (audioPlaying != _lastAudioPlaying)
        {
            // Started or stopped in between: anything from 0 to the elapsed time is a normal move.
            return moved < -_options.Tolerance || moved > expected + _options.Tolerance;
        }

        return (moved - expected).Duration() > _options.Tolerance;
    }

    /// <summary>Slot of the segment containing <paramref name="position"/> (the later one where two touch), or -1.</summary>
    private int Find(TimeSpan position)
    {
        // Last segment whose offset is at or before the position (binary search over the sorted offsets).
        int lo = 0, hi = _segments.Length - 1, found = -1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (_segments[mid].Offset <= position)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found >= 0 && position <= _segments[found].Offset + _segments[found].Duration ? found : -1;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan max) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value > max ? max : value;
}
