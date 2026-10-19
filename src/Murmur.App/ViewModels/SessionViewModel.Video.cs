using CommunityToolkit.Mvvm.ComponentModel;
using Murmur.App.Resources;
using Murmur.Core.Capture;
using Murmur.Core.Playback;

namespace Murmur.App.ViewModels;

/// <summary>
/// The screen recording of the session (decisions D-033 and D-035). The audio is the master clock: every step of
/// <see cref="ScreenPlaybackSync"/> reads the audio player and tells this view model what to do with the one
/// <see cref="IVideoPlayer"/> of the session.
/// </summary>
/// <remarks>
/// <para><b>When it runs.</b> The sync runs from <see cref="Tick"/> (the ~30 Hz poll while the audio plays) and, because
/// that poll is idle while paused, once more after every seek, play / pause and when the session opens, so a click on a
/// word or on the waveform shows the right picture of a paused session. The pictures themselves are pulled by
/// <c>VideoSurface</c> on its own animation frames.</para>
/// <para><b>Opening.</b> Opening a segment file is asynchronous: until it completes the sync is told
/// <see cref="VideoPlayerState.Opening"/>, and the placeholder reads "Loading". A missing file, a failed open or the
/// player's <see cref="IVideoPlayer.Failed"/> mark the segment unplayable (placeholder with a note); the audio is never
/// touched.</para>
/// <para>There is no video panel (<see cref="HasVideo"/> is false) when no <see cref="IVideoPlayback"/> is wired, it is
/// not supported, the session has no screen segments or the audio could not be opened.</para>
/// </remarks>
public sealed partial class SessionViewModel
{
    private readonly IVideoPlayback? _videoPlayback;

    private IVideoPlayer? _video;
    private ScreenPlaybackSync? _sync;
    private int _openSegment = VideoPlayerState.NoSegment;
    private int _openingSegment = VideoPlayerState.NoSegment;
    private CancellationTokenSource? _openCts;

    // Which open a Failed event belongs to: the event is raised on a Media Foundation thread and handled later on the UI
    // thread, by when the user may have moved on to another segment. The segment is written before the generation is
    // bumped, so a reader that sees the new generation also sees the new segment.
    private volatile int _videoGeneration;
    private volatile int _failureSegment = VideoPlayerState.NoSegment;

    /// <summary>The video panel is part of the view: there is a player and the session has screen segments.</summary>
    [ObservableProperty]
    public partial bool HasVideo { get; private set; }

    /// <summary>The player the video surface pulls its pictures from; <c>null</c> without video.</summary>
    [ObservableProperty]
    public partial IVideoPlayer? VideoPlayer { get; private set; }

    /// <summary>A segment is open: the picture and the size chip are shown instead of the placeholder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVideoPlaceholder))]
    public partial bool IsVideoOpen { get; private set; }

    /// <summary>"Screen off", "Loading…" or why the segment cannot be played; shown with the placeholder icon.</summary>
    [ObservableProperty]
    public partial string VideoPlaceholderText { get; private set; } = Strings.Session_VideoOff;

    /// <summary>Native size and codec of the open segment, e.g. <c>1920×1080 · H.264</c>.</summary>
    [ObservableProperty]
    public partial string VideoInfoText { get; private set; } = string.Empty;

    /// <summary>Counts the seeks, plays and pauses sent to the video; wakes the surface's frame loop.</summary>
    [ObservableProperty]
    public partial int VideoRevision { get; private set; }

    public bool ShowVideoPlaceholder => !IsVideoOpen;

    /// <summary>Creates the player once the audio is open, and shows the picture at the current position.</summary>
    private void StartVideo()
    {
        if (_video is not null
            || _disposed
            || !_playerReady
            || _videoPlayback is not { IsSupported: true } playback
            || _record.ScreenSegments.Count == 0)
        {
            return;
        }

        var player = playback.CreateVideoPlayer();
        _video = player;
        _sync = new ScreenPlaybackSync(_record.ScreenSegments, time: _time);
        player.Failed += OnVideoFailed;
        VideoPlayer = player;
        HasVideo = true;
        SyncVideo();
    }

    /// <summary>One step of <see cref="ScreenPlaybackSync"/>: reads the audio, applies the command to the video player.</summary>
    private void SyncVideo()
    {
        if (_video is not { } video || _sync is not { } sync || !_playerReady || _disposed)
        {
            return;
        }

        var segment = _openSegment != VideoPlayerState.NoSegment ? _openSegment : _openingSegment;
        try
        {
            var state = _openingSegment != VideoPlayerState.NoSegment ? VideoPlayerState.Opening(_openingSegment)
                : _openSegment != VideoPlayerState.NoSegment ? VideoPlayerState.Open(_openSegment, video.Position, video.IsPlaying, video.Duration)
                : VideoPlayerState.Closed;
            var command = sync.Update(_player.Position, _player.State == PlaybackState.Playing, state);
            segment = command.SegmentIndex != VideoPlayerState.NoSegment ? command.SegmentIndex : _openSegment;

            // Close, Open, Seek, Play / Pause (Close and Open never come with the others).
            if (command.Has(VideoSyncActions.Close))
            {
                CancelOpen();
                _openSegment = VideoPlayerState.NoSegment;
                _openingSegment = VideoPlayerState.NoSegment;
                ForgetFailureTarget();
                try
                {
                    video.Close();
                }
                catch (Exception)
                {
                    // Closing the old picture failing says nothing about the segment the audio is in now (none).
                }
            }
            else if (command.Has(VideoSyncActions.Open))
            {
                BeginOpen(video, command.SegmentIndex);
            }
            else if (_openSegment != VideoPlayerState.NoSegment)
            {
                if (command.Has(VideoSyncActions.Seek))
                {
                    video.Seek(command.Position);
                    VideoRevision++;
                }

                if (command.Has(VideoSyncActions.Play))
                {
                    video.Play();
                    VideoRevision++;
                }
                else if (command.Has(VideoSyncActions.Pause))
                {
                    video.Pause();
                    VideoRevision++;
                }
            }
        }
        catch (Exception)
        {
            // A player that throws cannot show this segment; the audio goes on.
            if (segment != VideoPlayerState.NoSegment)
            {
                FailSegment(segment);
            }

            return;
        }

        UpdateVideoState();
    }

    private void BeginOpen(IVideoPlayer video, int index)
    {
        CancelOpen();
        _openSegment = VideoPlayerState.NoSegment;
        _openingSegment = index;
        SetFailureTarget(index);
        var path = _files.ScreenSegment(index);
        if (!File.Exists(path))
        {
            FailSegment(index);
            return;
        }

        var cts = new CancellationTokenSource();
        _openCts = cts;
        _ = OpenVideoAsync(video, index, path, cts);
    }

    private async Task OpenVideoAsync(IVideoPlayer video, int index, string path, CancellationTokenSource cts)
    {
        try
        {
            await video.OpenAsync(path, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            if (!cts.IsCancellationRequested && !_disposed && _openingSegment == index)
            {
                FailSegment(index);
            }

            return;
        }

        if (cts.IsCancellationRequested || _disposed || _openingSegment != index)
        {
            return;
        }

        if (ReferenceEquals(_openCts, cts))
        {
            _openCts = null;
            cts.Dispose();
        }

        _openingSegment = VideoPlayerState.NoSegment;
        _openSegment = index;
        var size = video.Size;
        VideoInfoText = UiText.Format(Strings.Session_VideoInfo, size.Width, size.Height);
        VideoRevision++;
        UpdateVideoState();

        // The next step seeks and plays with the audio position of now (the open may have taken a while).
        SyncVideo();
    }

    /// <summary>Shows the placeholder for a segment that cannot be played and never opens it again.</summary>
    private void FailSegment(int index)
    {
        _sync?.MarkUnplayable(index);
        if (_openingSegment == index || _openSegment == index)
        {
            CancelOpen();
            ForgetFailureTarget();
            _openingSegment = VideoPlayerState.NoSegment;
            _openSegment = VideoPlayerState.NoSegment;
            try
            {
                _video?.Close();
            }
            catch (Exception)
            {
                // Already failed; the placeholder is shown either way.
            }
        }

        UpdateVideoState();
    }

    private void CancelOpen()
    {
        var cts = _openCts;
        _openCts = null;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    private void SetFailureTarget(int segment)
    {
        _failureSegment = segment;
        _videoGeneration++;
    }

    private void ForgetFailureTarget() => SetFailureTarget(VideoPlayerState.NoSegment);

    /// <summary>
    /// May be raised on any thread (a Media Foundation callback): remembers which open it belongs to and marshals to the
    /// UI thread, where it is dropped when another segment was opened or closed in the meantime.
    /// </summary>
    private void OnVideoFailed(string message)
    {
        var generation = _videoGeneration;
        var segment = _failureSegment;
        _post(() => OnVideoFailedOnUiThread(generation, segment));
    }

    private void OnVideoFailedOnUiThread(int generation, int segment)
    {
        if (_disposed || generation != _videoGeneration || segment == VideoPlayerState.NoSegment)
        {
            return;
        }

        FailSegment(segment);
    }

    /// <summary>What the panel shows now: the picture, "Loading…", the note, or "Screen off".</summary>
    private void UpdateVideoState()
    {
        if (_sync is not { } sync)
        {
            return;
        }

        IsVideoOpen = _openSegment != VideoPlayerState.NoSegment;
        if (IsVideoOpen)
        {
            return;
        }

        VideoPlaceholderText = _openingSegment != VideoPlayerState.NoSegment ? Strings.Session_Loading
            : sync.SegmentAt(_player.Position) is { } at && sync.IsUnplayable(at.Index) ? Strings.Session_VideoUnplayable
            : Strings.Session_VideoOff;
    }

    private void DisposeVideo()
    {
        CancelOpen();
        ForgetFailureTarget();
        _openSegment = VideoPlayerState.NoSegment;
        _openingSegment = VideoPlayerState.NoSegment;
        IsVideoOpen = false;
        VideoPlayer = null;
        HasVideo = false;
        _sync = null;
        if (_video is not { } video)
        {
            return;
        }

        _video = null;
        video.Failed -= OnVideoFailed;
        try
        {
            video.Dispose();
        }
        catch (Exception)
        {
            // Closing the session view must not fail because the decoder did.
        }
    }
}
