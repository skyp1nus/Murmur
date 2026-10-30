using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.App.Services;
using Murmur.Core.Audio;
using Murmur.Core.Capture;
using Murmur.Core.Intelligence;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.App.ViewModels;

public enum SessionTab
{
    Transcript,
    Summary,
    Ask,
}

/// <summary>What the transcript tab shows.</summary>
public enum TranscriptStatus
{
    Loading,

    /// <summary>The transcript list.</summary>
    Available,

    /// <summary>A transcript exists but holds no speech.</summary>
    Empty,

    /// <summary>Queued or being transcribed: progress card.</summary>
    Processing,

    /// <summary>No transcript and the Parakeet model is not installed: how-to-install card.</summary>
    AsrMissing,

    /// <summary>The pipeline failed: error text and a retry button.</summary>
    Failed,

    /// <summary>No transcript and nothing queued (interrupted recording, never processed): offers to start.</summary>
    NotTranscribed,
}

/// <summary>
/// The recording view: audio player on the left, transcript (with the spoken word highlighted), summary and AI tabs on
/// the right.
/// </summary>
/// <remarks>
/// <para><b>Player.</b> The position is polled, not pushed: the view runs a ~30 Hz timer only while
/// <see cref="IsPlaying"/> and calls <see cref="Tick"/>. Idle = no timer, no work.</para>
/// <para><b>Current word.</b> <see cref="Tick"/> and every seek look the word up with a binary search over the word
/// start times (<see cref="TranscriptTimeline"/>) and move <see cref="SegmentViewModel.ActiveWord"/>; only the previous
/// and the new row get a property notification.</para>
/// <para><b>Auto-scroll.</b> When the current segment changes the view model raises
/// <see cref="ScrollToSegmentRequested"/> unless the user scrolled in the last <see cref="AutoScrollHold"/>
/// (<see cref="NotifyUserScrolled"/>). A seek by the user clears that hold.</para>
/// <para><b>Threads.</b> Everything runs on the UI thread; player and pipeline events are marshalled with the
/// <c>post</c> callback.</para>
/// </remarks>
public sealed partial class SessionViewModel : ObservableObject, IDisposable, IFillsFrame
{
    /// <summary>After a manual scroll the list is left alone for this long.</summary>
    public static readonly TimeSpan AutoScrollHold = TimeSpan.FromSeconds(3);

    private readonly SessionRecord _record;
    private readonly SessionFiles _files;
    private readonly ITranscriptStore _transcripts;
    private readonly IAudioPlayer _player;
    private readonly ISessionProcessing _processing;
    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private readonly SessionServices _services;

    private Transcript? _transcript;
    private TranscriptTimeline? _timeline;
    private bool _playerReady;
    private bool _scrubbing;
    private bool _disposed;
    private int _currentWord = -1;
    private int _currentSegment = -1;
    private long? _lastUserScroll;
    private bool _scrollPending;

    public SessionViewModel(
        SessionRecord record,
        SessionFiles files,
        ITranscriptStore transcripts,
        IAudioPlayer player,
        ISessionProcessing processing,
        TimeProvider time,
        Action<Action>? post = null,
        ChatViewModel? chat = null,
        IVideoPlayback? videoPlayback = null,
        SessionServices? services = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(transcripts);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(processing);
        ArgumentNullException.ThrowIfNull(time);
        _record = record;
        _files = files;
        _transcripts = transcripts;
        _player = player;
        _processing = processing;
        _time = time;
        _videoPlayback = videoPlayback;
        _services = services ?? new SessionServices();
        _post = post ?? (action => Avalonia.Threading.Dispatcher.UIThread.Post(action));

        Title = string.IsNullOrWhiteSpace(record.Title) ? Strings.Common_UntitledRecording : record.Title;
        DateText = UiFormat.Date(record.StartedAt, time);
        Duration = record.Duration;
        DurationText = UiFormat.Clock(record.Duration);
        PositionText = UiFormat.Clock(TimeSpan.Zero);
        Actions = new SessionActionsViewModel(this);

        // Without AI wiring the tab still exists and explains how to install the models.
        Chat = chat ?? new ChatViewModel(null, null, null, record.Id, post: _post);
        Chat.CitationActivated += OnCitationActivated;

        _controls = player as IAudioPlayerControls;
        _player.StateChanged += OnPlayerStateChanged;
        _processing.ProgressChanged += OnProcessingChanged;
    }

    /// <summary>Raised when the user wants to go back to the library.</summary>
    public event Action? BackRequested;

    /// <summary>Raised when the list should bring this segment into view (never while the user is scrolling).</summary>
    public event Action<int>? ScrollToSegmentRequested;

    /// <summary>A chip of the chat cites another session: the shell opens it at that moment.</summary>
    public event Action<SessionId, TimeSpan>? OpenSessionRequested;

    /// <summary>The "Запитати AI" tab; its history lives as long as this view.</summary>
    public ChatViewModel Chat { get; }

    public SessionId Id => _record.Id;

    /// <summary>The title of the recording (the user's, or "Untitled recording"); <see cref="RenameAsync"/> changes it.</summary>
    [ObservableProperty]
    public partial string Title { get; private set; }

    public string DateText { get; }

    /// <summary>The line under the title: "Today, 10:30 · 47:12 · English · Parakeet TDT v3" as separate pieces (the canvas sets each in its own box).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<MetaPart> Meta { get; private set; }

    /// <summary>The buttons of the top bar (Export, the "…" menu) for this recording.</summary>
    public SessionActionsViewModel Actions { get; }

    // Player ------------------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsPlaying { get; private set; }

    [ObservableProperty]
    public partial TimeSpan Position { get; private set; }

    /// <summary>Length of the session; the player's own duration once it is open, the stored one before.</summary>
    [ObservableProperty]
    public partial TimeSpan Duration { get; private set; }

    /// <summary>Elapsed time as <c>HH:MM:SS</c>, shown in Geist Mono.</summary>
    [ObservableProperty]
    public partial string PositionText { get; private set; }

    [ObservableProperty]
    public partial string DurationText { get; private set; }

    /// <summary>Why the audio cannot be played (missing or broken files), or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlayerError))]
    public partial string? PlayerError { get; private set; }

    /// <summary>False until the audio is open (and forever when it cannot be).</summary>
    [ObservableProperty]
    public partial bool CanPlay { get; private set; }

    /// <summary>Waveform overview from <c>peaks.bin</c> (50 values per second); empty until the file exists.</summary>
    [ObservableProperty]
    public partial byte[] Peaks { get; private set; } = [];

    public string PlayPauseLabel => IsPlaying ? Strings.Session_Pause : Strings.Session_Play;

    public bool HasPlayerError => PlayerError is not null;

    /// <summary>Playhead position in [0, 1].</summary>
    public double PositionFraction => Duration > TimeSpan.Zero ? Math.Clamp(Position / Duration, 0, 1) : 0;

    // Transcript --------------------------------------------------------------------------------------------------

    /// <summary>The rows of the (virtualised) transcript list.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SegmentViewModel> Segments { get; private set; } = [];

    /// <summary>The segment being spoken (or the last one that started), or -1.</summary>
    [ObservableProperty]
    public partial int CurrentSegmentIndex { get; private set; } = -1;

    /// <summary>The word being spoken, numbered over the whole transcript, or -1.</summary>
    public int CurrentWordIndex => _currentWord;

    [ObservableProperty]
    public partial TranscriptStatus Status { get; private set; } = TranscriptStatus.Loading;

    [ObservableProperty]
    public partial string StatusTitle { get; private set; } = Strings.Session_Loading;

    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    /// <summary>Pipeline progress in whole percent (0..99) while <see cref="TranscriptStatus.Processing"/>.</summary>
    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RetryLabel { get; private set; } = Strings.Session_TryAgain;

    [ObservableProperty]
    public partial SessionTab SelectedTab { get; set; } = SessionTab.Transcript;

    public bool IsTranscriptTab
    {
        get => SelectedTab == SessionTab.Transcript;
        set => SelectTab(SessionTab.Transcript, value, nameof(IsTranscriptTab));
    }

    public bool IsSummaryTab
    {
        get => SelectedTab == SessionTab.Summary;
        set => SelectTab(SessionTab.Summary, value, nameof(IsSummaryTab));
    }

    public bool IsAskTab
    {
        get => SelectedTab == SessionTab.Ask;
        set => SelectTab(SessionTab.Ask, value, nameof(IsAskTab));
    }

    /// <summary>The transcript list is on screen.</summary>
    public bool ShowTranscriptList => SelectedTab == SessionTab.Transcript && Status == TranscriptStatus.Available;

    /// <summary>A state card (loading, progress, install hint, error, empty) is on screen.</summary>
    public bool ShowStatusCard => SelectedTab == SessionTab.Transcript && Status != TranscriptStatus.Available;

    public bool ShowProgress => Status == TranscriptStatus.Processing;

    public bool CanRetry => Status is TranscriptStatus.Failed or TranscriptStatus.AsrMissing or TranscriptStatus.NotTranscribed or TranscriptStatus.Empty;

    public bool ShowSummary => SelectedTab == SessionTab.Summary;

    public bool ShowAsk => SelectedTab == SessionTab.Ask;

    /// <summary>True while the user's last scroll is recent enough that the list must not move on its own.</summary>
    public bool AutoScrollSuspended => _lastUserScroll is { } stamp && _time.GetElapsedTime(stamp) < AutoScrollHold;

    /// <summary>Loads audio, waveform and transcript. Failures show up in the view (cards, <see cref="PlayerError"/>), never as exceptions.</summary>
    public async Task LoadAsync()
    {
        await OpenPlayerAsync().ConfigureAwait(true);
        StartVideo();
        await LoadPeaksAsync().ConfigureAwait(true);
        await LoadTranscriptAsync().ConfigureAwait(true);
        await LoadSummaryAsync().ConfigureAwait(true);
    }

    /// <summary>Stops following the player and the pipeline and releases the player.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelSummary();
        _player.StateChanged -= OnPlayerStateChanged;
        _processing.ProgressChanged -= OnProcessingChanged;
        Chat.CitationActivated -= OnCitationActivated;
        Chat.Dispose();
        DisposeVideo();
        _player.Dispose();
    }

    /// <summary>
    /// Lets go of the audio and video files (a folder cannot be deleted on Windows while a player has its files open) and stops
    /// the playback. The view stays usable for what needs no media (the transcript, the summary).
    /// </summary>
    private void ReleaseMedia()
    {
        if (_playerReady)
        {
            try
            {
                _player.Pause();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // Already closed.
            }
        }

        _playerReady = false;
        CanPlay = false;
        IsPlaying = false;
        DisposeVideo();
        _player.Dispose();
    }

    // Player commands ---------------------------------------------------------------------------------------------

    [RelayCommand]
    private void Back() => BackRequested?.Invoke();

    [RelayCommand]
    private void PlayPause()
    {
        if (!_playerReady || _disposed)
        {
            return;
        }

        if (_player.State == PlaybackState.Playing)
        {
            _player.Pause();
        }
        else
        {
            _player.Play();
        }

        SyncPlayerState();
    }

    /// <summary>The ~30 Hz poll while playing: reads the player position and moves the highlight.</summary>
    public void Tick()
    {
        if (!_playerReady || _scrubbing || _disposed)
        {
            return;
        }

        Position = _player.Position;
        UpdateCurrent(forceScroll: false);
        if (_player.State != PlaybackState.Playing || !IsPlaying)
        {
            SyncPlayerState();
        }

        SyncVideo();
    }

    /// <summary>Moves the playhead (clamped to the session) and keeps playing or paused.</summary>
    public void SeekTo(TimeSpan position)
    {
        if (!_playerReady || _disposed)
        {
            return;
        }

        position = Clamp(position);
        try
        {
            _player.Seek(position);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return;
        }

        // The user wants to see where they jumped to, whatever they scrolled before.
        _lastUserScroll = null;
        _scrollPending = false;
        Position = position;
        UpdateCurrent(forceScroll: true);
        SyncPlayerState();
        SyncVideo();
    }

    /// <summary>Seeks by a fraction of the duration (click on the waveform).</summary>
    public void SeekToFraction(double fraction) => SeekTo(FromFraction(fraction));

    /// <summary>Seeks and plays (click on a word or a time chip).</summary>
    public void PlayFrom(TimeSpan position)
    {
        SeekTo(position);
        if (_playerReady && !_disposed)
        {
            _player.Play();
            SyncPlayerState();
        }
    }

    /// <summary>A drag on the waveform starts: the playhead follows the pointer, the player is left alone until <see cref="EndScrub"/>.</summary>
    public void BeginScrub() => _scrubbing = true;

    public void ScrubTo(double fraction)
    {
        if (!_scrubbing)
        {
            return;
        }

        Position = FromFraction(fraction);
        UpdateCurrent(forceScroll: true);
    }

    /// <summary>The drag ended (or it was a plain click): one real seek.</summary>
    public void EndScrub(double fraction)
    {
        _scrubbing = false;
        SeekToFraction(fraction);
    }

    // Transcript commands -----------------------------------------------------------------------------------------

    /// <summary>The user scrolled the list (wheel, scroll bar): auto-scroll waits <see cref="AutoScrollHold"/>.</summary>
    public void NotifyUserScrolled() => _lastUserScroll = _time.GetTimestamp();

    [RelayCommand]
    private async Task RetryAsync()
    {
        bool queued;
        try
        {
            queued = await _processing.EnqueueAsync(Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowFailed(UiText.Format(Strings.Session_QueueFailed, ex.Message));
            return;
        }

        var live = _processing.GetStatus(Id);
        if (live is not null && ProcessingText.IsBusy(live.Stage))
        {
            ShowProcessing(live.Stage, live.Progress);
        }
        else if (queued)
        {
            ShowProcessing(SessionProcessingStage.Queued, 0);
        }
    }

    // Loading -----------------------------------------------------------------------------------------------------

    private async Task OpenPlayerAsync()
    {
        var tracks = new List<string>(2);
        if (_record.HasMicrophone && File.Exists(_files.MicrophoneAudio))
        {
            _microphoneTrack = tracks.Count;
            tracks.Add(_files.MicrophoneAudio);
        }

        if (_record.HasSystemAudio && File.Exists(_files.SystemAudio))
        {
            _systemTrack = tracks.Count;
            tracks.Add(_files.SystemAudio);
        }

        if (tracks.Count == 0)
        {
            PlayerError = Strings.Session_AudioMissing;
            return;
        }

        try
        {
            await _player.OpenAsync(tracks, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PlayerError = UiText.Format(Strings.Session_AudioOpenFailed, ex.Message);
            return;
        }

        if (_disposed)
        {
            return;
        }

        _playerReady = true;
        CanPlay = true;
        PlayerError = null;
        ApplyPlayerControls();
        if (_player.Duration > TimeSpan.Zero)
        {
            Duration = _player.Duration;
        }
    }

    private async Task LoadPeaksAsync()
    {
        var path = _files.Peaks;
        byte[] peaks;
        try
        {
            peaks = File.Exists(path) ? await Task.Run(() => PeaksFile.Read(path)).ConfigureAwait(true) : [];
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            peaks = [];
        }

        Peaks = peaks;
    }

    private async Task LoadTranscriptAsync()
    {
        Transcript? transcript;
        try
        {
            transcript = await _transcripts.GetTranscriptAsync(Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowFailed(UiText.Format(Strings.Session_TranscriptReadFailed, ex.Message));
            return;
        }

        if (_disposed)
        {
            return;
        }

        if (transcript is not null)
        {
            ApplyTranscript(transcript);
            return;
        }

        ShowWithoutTranscript(_processing.GetStatus(Id));
    }

    private void ApplyTranscript(Transcript transcript)
    {
        var segments = transcript.Segments;
        var timeline = new TranscriptTimeline(segments);
        var rows = new SegmentViewModel[segments.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = new SegmentViewModel(segments[i], i, timeline.FirstWordOfSegment(i), OnSeekRequested) { ShowWord = IsPlaying };
        }

        _transcript = transcript;
        _timeline = timeline;
        _currentWord = -1;
        _currentSegment = -1;
        CurrentSegmentIndex = -1;
        Segments = rows;
        RefreshExport();
        Meta = BuildMeta(transcript);
        ApplyTrackFilters();
        ApplySearch();
        if (rows.Length == 0)
        {
            SetStatus(TranscriptStatus.Empty, Strings.Session_NoSpeechTitle, Strings.Session_NoSpeechText, Strings.Session_TryAgain);
            return;
        }

        SetStatus(TranscriptStatus.Available, string.Empty, null, Strings.Session_TryAgain);
        UpdateCurrent(forceScroll: false);
    }

    private void ShowWithoutTranscript(SessionProcessingStatus? live)
    {
        if (live is { Stage: SessionProcessingStage.Failed } || (live is null && _record.State == SessionState.Failed))
        {
            ShowFailed(SessionNoteText.Describe(live?.Message ?? _record.Error));
        }
        else if (live is not null && ProcessingText.IsBusy(live.Stage))
        {
            ShowProcessing(live.Stage, live.Progress);
        }
        else if (!_processing.IsAsrInstalled)
        {
            // Asked afresh on every open: a "model missing" status left over from before the model was installed in the
            // Settings must not keep the install hint on screen (the status itself is never cleared by the pipeline).
            ShowAsrMissing();
        }
        else if (_record.State is SessionState.Transcribing or SessionState.Transcribed or SessionState.Indexing)
        {
            ShowProcessing(SessionProcessingStage.Queued, 0);
        }
        else if (_record.State == SessionState.Recording)
        {
            SetStatus(TranscriptStatus.NotTranscribed, Strings.Session_StillRecordingTitle, Strings.Session_StillRecordingText, Strings.Session_Transcribe);
        }
        else
        {
            SetStatus(TranscriptStatus.NotTranscribed, Strings.Session_NoTranscriptTitle, Strings.Session_NoTranscriptText, Strings.Session_Transcribe);
        }
    }

    private void ShowFailed(string? message) =>
        SetStatus(TranscriptStatus.Failed, Strings.Session_FailedTitle, string.IsNullOrWhiteSpace(message) ? Strings.Session_UnknownError : message, Strings.Session_TryAgain);

    private void ShowAsrMissing()
    {
        var folder = _processing.ModelsDirectory;
        SetStatus(
            TranscriptStatus.AsrMissing,
            Strings.Session_AsrMissingTitle,
            string.IsNullOrEmpty(folder) ? Strings.Session_AsrMissingText : UiText.Format(Strings.Session_AsrMissingTextWithFolder, folder),
            Strings.Session_TryAgain);
    }

    private void ShowProcessing(SessionProcessingStage stage, double progress)
    {
        ProgressValue = ProcessingText.Percent(progress);
        ProgressText = ProcessingText.PercentText(progress);
        SetStatus(TranscriptStatus.Processing, ProcessingText.Stage(stage), Strings.Session_ProcessingText, Strings.Session_TryAgain);
    }

    private void SetStatus(TranscriptStatus status, string title, string? text, string retryLabel)
    {
        OnPropertyChanged(nameof(CanRetranscribe));
        StatusTitle = title;
        StatusText = text;
        RetryLabel = retryLabel;
        Status = status;
        OnPropertyChanged(nameof(CanRetry));
    }

    // Pipeline events ---------------------------------------------------------------------------------------------

    private void OnProcessingChanged(SessionProcessingStatus status)
    {
        if (status.Session != Id)
        {
            return;
        }

        _post(() => ApplyProcessing(status));
    }

    private void ApplyProcessing(SessionProcessingStatus status)
    {
        if (_disposed)
        {
            return;
        }

        if (status.Stage == SessionProcessingStage.Completed)
        {
            _ = ReloadAfterCompletionAsync();
            return;
        }

        if (Status == TranscriptStatus.Available)
        {
            // Showing a finished transcript: the old one stays until the new one is saved.
            return;
        }

        switch (status.Stage)
        {
            case SessionProcessingStage.Failed:
                ShowFailed(SessionNoteText.Describe(status.Message));
                break;
            case SessionProcessingStage.ModelMissing:
                ShowAsrMissing();
                break;
            default:
                ShowProcessing(status.Stage, status.Progress);
                break;
        }
    }

    private async Task ReloadAfterCompletionAsync()
    {
        await LoadPeaksAsync().ConfigureAwait(true);
        await LoadTranscriptAsync().ConfigureAwait(true);
    }

    // Player events -----------------------------------------------------------------------------------------------

    private void OnPlayerStateChanged(PlaybackState state) => _post(SyncPlayerState);

    private void SyncPlayerState()
    {
        if (_disposed)
        {
            return;
        }

        var state = _player.State;
        var wasPlaying = IsPlaying;
        IsPlaying = state == PlaybackState.Playing;
        if (state == PlaybackState.Ended && !_scrubbing)
        {
            Position = Duration;
            UpdateCurrent(forceScroll: false);
        }

        if (wasPlaying != IsPlaying || state == PlaybackState.Ended)
        {
            SyncVideo();
        }
    }

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayPauseLabel));
        foreach (var row in Segments)
        {
            row.ShowWord = value;
        }
    }

    partial void OnPositionChanged(TimeSpan value)
    {
        PositionText = UiFormat.Clock(value);
        OnPropertyChanged(nameof(PositionFraction));
    }

    partial void OnDurationChanged(TimeSpan value)
    {
        DurationText = UiFormat.Clock(value);
        Meta = BuildMeta(_transcript);
        OnPropertyChanged(nameof(PositionFraction));
    }

    partial void OnStatusChanged(TranscriptStatus value)
    {
        RaiseLayoutChanged();
        RefreshSummaryAvailability();
    }

    partial void OnSelectedTabChanged(SessionTab value)
    {
        OnPropertyChanged(nameof(IsTranscriptTab));
        OnPropertyChanged(nameof(IsSummaryTab));
        OnPropertyChanged(nameof(IsAskTab));
        RaiseLayoutChanged();
        if (value is SessionTab.Ask or SessionTab.Summary)
        {
            // The models may have been installed (or a recording started) since the view opened.
            Chat.RefreshAvailability();
            RefreshSummaryAvailability();
        }
    }

    private void OnCitationActivated(Citation citation)
    {
        if (citation.Session == Id)
        {
            PlayFrom(citation.At);
        }
        else
        {
            OpenSessionRequested?.Invoke(citation.Session, citation.At);
        }
    }

    private void RaiseLayoutChanged()
    {
        OnPropertyChanged(nameof(ShowTranscriptList));
        OnPropertyChanged(nameof(ShowStatusCard));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(ShowSummary));
        OnPropertyChanged(nameof(ShowAsk));
    }

    private void SelectTab(SessionTab tab, bool selected, string propertyName)
    {
        if (selected)
        {
            SelectedTab = tab;
        }
        else
        {
            // A toggle button un-checks itself on click; push the real state back so the pill stays checked.
            OnPropertyChanged(propertyName);
        }
    }

    // Highlight ---------------------------------------------------------------------------------------------------

    private void OnSeekRequested(SegmentViewModel segment, int wordInSegment)
    {
        if (_timeline is null || wordInSegment < 0 || wordInSegment >= segment.WordCount)
        {
            PlayFrom(segment.Start);
            return;
        }

        PlayFrom(_timeline.WordStart(segment.FirstWord + wordInSegment));
    }

    private void UpdateCurrent(bool forceScroll)
    {
        if (_timeline is not { } timeline)
        {
            return;
        }

        var word = timeline.FindWord(Position);
        var segment = word >= 0 ? timeline.SegmentOfWord(word) : timeline.FindSegment(Position);
        if (word != _currentWord)
        {
            var rows = Segments;
            if (_currentWord >= 0)
            {
                rows[timeline.SegmentOfWord(_currentWord)].ActiveWord = -1;
            }

            if (word >= 0)
            {
                rows[segment].ActiveWord = word - timeline.FirstWordOfSegment(segment);
            }

            _currentWord = word;
        }

        var changed = segment != _currentSegment;
        if (changed)
        {
            var rows = Segments;
            if (_currentSegment >= 0 && _currentSegment < rows.Count)
            {
                rows[_currentSegment].IsCurrent = false;
            }

            _currentSegment = segment;
            CurrentSegmentIndex = segment;
            if (segment >= 0 && segment < rows.Count)
            {
                rows[segment].IsCurrent = true;
            }
        }

        if (segment < 0)
        {
            return;
        }

        if (AutoScrollSuspended)
        {
            // Keep the dropped request: it is delivered on the first tick after the hold, without waiting for the next
            // segment boundary (which can be tens of seconds away).
            _scrollPending |= changed || forceScroll;
            return;
        }

        if (changed || forceScroll || _scrollPending)
        {
            _scrollPending = false;
            ScrollToSegmentRequested?.Invoke(segment);
        }
    }

    private TimeSpan FromFraction(double fraction) =>
        Clamp(TimeSpan.FromTicks((long)(Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0, 1) * Duration.Ticks)));

    private TimeSpan Clamp(TimeSpan position) =>
        position < TimeSpan.Zero ? TimeSpan.Zero : position > Duration ? Duration : position;
}
