using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Controls;
using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.Core.Capture;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Settings;

namespace Murmur.App.ViewModels;

/// <summary>
/// The Recording screen: the editable title, the timer, the source pills, device pickers, the pause and stop buttons, the
/// Tracks card (levels, bars and device names) and the "Let people know" message.
/// </summary>
/// <remarks>
/// Before recording the pills choose the sources. While recording only the screen pill stays enabled (it maps to
/// <see cref="IRecorder.SetScreenEnabledAsync"/>). The view polls <see cref="Refresh"/> at about 4 Hz while
/// recording or paused; the orb polls <see cref="Level"/> from its own render tick. No per-buffer events are used.
/// While <see cref="RecorderState.Paused"/> the timer stands still, the orb rests and the tracks say "Paused".
/// </remarks>
public sealed partial class RecordingViewModel : ObservableObject, ILevelSource, IFillsFrame
{
    private readonly IRecorder _recorder;
    private readonly ICapture _capture;
    private readonly ISessionStore _store;
    private readonly Action<Action> _post;
    private readonly ISettingsStore? _settings;
    private readonly TimeProvider _time;
    private SessionId? _sessionId;
    private bool _stopRequested;
    private bool _pauseBusy;
    private string _committedTitle = string.Empty;
    private bool _titleEdited;
    private bool _microphoneRequested;
    private bool _systemAudioRequested;
    private long _shownSeconds = -1;
    private readonly TrackDetailMemo _microphoneDetail = new();
    private readonly TrackDetailMemo _systemDetail = new();
    private string _microphoneName = string.Empty;
    private string _systemAudioName = string.Empty;
    private CancellationTokenSource? _copiedReset;

    /// <summary>The rename in flight (never faults: it reports its own failure); the stop waits for it.</summary>
    private Task _renameTask = Task.CompletedTask;

    /// <summary>The stop in flight; meaningful only while it has not completed (a completed one is ignored).</summary>
    private Task? _stopTask;
    private bool _suppressScreenChange;
    private readonly List<string> _notices = [];

    /// <param name="post">Marshals a callback to the UI thread; recorder events can arrive on any thread.</param>
    /// <param name="settings">
    /// The user's defaults (devices, display, screen on/off) the screen starts with every time it is shown;
    /// <c>null</c> = system default devices and no screen.
    /// </param>
    /// <param name="time">The clock for the default title ("Untitled — Oct 18, 01:12"); the system clock when <c>null</c>.</param>
    public RecordingViewModel(IRecorder recorder, ICapture capture, ISessionStore store, Action<Action>? post = null, ISettingsStore? settings = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(store);
        _recorder = recorder;
        _capture = capture;
        _store = store;
        _post = post ?? (action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        _settings = settings;
        _time = time ?? TimeProvider.System;

        ScreenAvailable = capture.Capabilities.Screen;
        RefreshDevices();
        ApplyDefaults();
        NewTitle();
        ApplyState(recorder.State);
        _recorder.StateChanged += OnRecorderStateChanged;
        _recorder.Warning += OnRecorderWarning;
    }

    [ObservableProperty]
    public partial bool Microphone { get; set; } = true;

    [ObservableProperty]
    public partial bool SystemAudio { get; set; } = true;

    [ObservableProperty]
    public partial bool Screen { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<AudioDevice> MicrophoneDevices { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<AudioDevice> SystemDevices { get; set; } = [];

    [ObservableProperty]
    public partial AudioDevice? SelectedMicrophone { get; set; }

    [ObservableProperty]
    public partial AudioDevice? SelectedSystemDevice { get; set; }

    /// <summary>The title field: the title of the running session, or of the next one before the recording starts.</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>The "Copy message" button was pressed a moment ago: it reads "Copied".</summary>
    [ObservableProperty]
    public partial bool MessageCopied { get; set; }

    /// <summary>"You · microphone": its level, bars and device (shown while the recording runs).</summary>
    public RecordingTrackViewModel MicrophoneTrack { get; } = new(isMicrophone: true);

    /// <summary>"Others · system audio".</summary>
    public RecordingTrackViewModel SystemTrack { get; } = new(isMicrophone: false);

    /// <summary>
    /// Writes text to the clipboard and tells whether it did (<c>false</c>: the window has no clipboard); set by the view (the
    /// clipboard belongs to the window). <c>null</c> = no clipboard at all, so <see cref="CopyMessageCommand"/> does nothing.
    /// </summary>
    public Func<string, Task<bool>>? ClipboardWriter { get; set; }

    /// <summary>How long "Copied" stays on the button.</summary>
    internal static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(2);

    /// <summary>Elapsed time as <c>HH:MM:SS</c>, shown in Geist Mono.</summary>
    [ObservableProperty]
    public partial string TimerText { get; set; } = UiFormat.Duration(TimeSpan.Zero);

    /// <summary>Inline error text, or <c>null</c>.</summary>
    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>
    /// Problems that did not stop the recording (the screen fell behind or stopped; the audio goes on), one per line, shown
    /// as a small notice under the controls; <c>null</c> when there are none. Cleared when a recording starts or ends.
    /// </summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    [ObservableProperty]
    public partial RecorderState State { get; set; }

    /// <summary>False hides the orb and stops its animation (perf runs use it to measure the orb's share).</summary>
    [ObservableProperty]
    public partial bool ShowOrb { get; set; } = true;

    /// <summary>The orb animates only while recording and visible.</summary>
    public bool OrbActive => IsRecording && ShowOrb;

    public bool ScreenAvailable { get; }

    public bool MicrophoneAvailable => _capture.Capabilities.Microphone && MicrophoneDevices.Count > 0;

    public bool SystemAudioAvailable => _capture.Capabilities.SystemAudio && SystemDevices.Count > 0;

    public bool IsRecording => State == RecorderState.Recording;

    /// <summary>Paused: the timer stands still, nothing is written.</summary>
    public bool IsPaused => State == RecorderState.Paused;

    /// <summary>Recording or paused: the session is open (the Tracks card, the pause and stop buttons).</summary>
    public bool IsRunning => State is RecorderState.Recording or RecorderState.Paused;

    /// <summary>Nothing is recording: the sources and devices can be chosen.</summary>
    public bool IsSetup => State == RecorderState.Idle;

    /// <summary>"Pause", or "Resume" while paused.</summary>
    public string PauseLabel => IsPaused ? Strings.Recording_Resume : Strings.Recording_Pause;

    /// <summary>The line under the timer: the auto-save note, or the pause note.</summary>
    public string TimerNote => IsPaused ? Strings.Recording_PausedNote : Strings.Recording_AutoSaved;

    /// <summary>The label of the screen pill: "Screen · 1080p" when on, "Screen off" when off.</summary>
    public string ScreenLabel => Screen ? ScreenOnLabel() : Strings.Recording_ScreenOff;

    /// <summary>"Copy message", or "Copied" for a moment after it was copied.</summary>
    public string CopyLabel => MessageCopied ? Strings.Recording_Copied : Strings.Recording_CopyMessage;

    /// <summary>The pause button works while recording or paused, and not while a pause or resume is still being applied.</summary>
    public bool CanPauseResume => !_pauseBusy && IsRunning;

    /// <summary>Starting or stopping: nothing may be changed.</summary>
    public bool IsBusy => State is RecorderState.Starting or RecorderState.Stopping;

    public bool CanToggleMicrophone => State == RecorderState.Idle && MicrophoneAvailable;

    public bool CanToggleSystemAudio => State == RecorderState.Idle && SystemAudioAvailable;

    /// <summary>The screen pill is the only one that works while recording.</summary>
    public bool CanToggleScreen => ScreenAvailable && !IsBusy;

    public bool CanChangeDevices => State == RecorderState.Idle;

    public bool CanGoBack => State == RecorderState.Idle;

    public string StartStopLabel => IsRunning ? Strings.Recording_StopAndTranscribe : Strings.Recording_Start;

    public bool HasError => Error is not null;

    public bool HasNotice => Notice is not null;

    /// <summary>Shown under the source pills when nothing can be recorded.</summary>
    public string? SourceHint => State != RecorderState.Idle ? null
        : !MicrophoneAvailable && !SystemAudioAvailable ? Strings.Recording_NoSources
        : !(Microphone && MicrophoneAvailable) && !(SystemAudio && SystemAudioAvailable) ? Strings.Recording_EnableSource
        : !MicrophoneAvailable ? Strings.Recording_NoMicrophone
        : null;

    public bool HasSourceHint => SourceHint is not null;

    /// <inheritdoc />
    float ILevelSource.Level => Level;

    /// <summary>Latest peak level in [0, 1]: the louder of the microphone and system audio.</summary>
    public float Level
    {
        get
        {
            var levels = _recorder.Levels;
            return Math.Max(levels.Microphone, levels.SystemAudio);
        }
    }

    /// <summary>Raised after a recording was stopped by the user, with the finished session.</summary>
    public event Action<SessionRecord>? Stopped;

    /// <summary>Raised when the user leaves the screen without recording.</summary>
    public event Action? BackRequested;

    /// <summary>The last screen toggle sent to the recorder (for tests).</summary>
    internal Task PendingScreenChange { get; private set; } = Task.CompletedTask;

    /// <summary>The last automatic-stop report (for tests).</summary>
    internal Task AutoStopReport { get; private set; } = Task.CompletedTask;

    /// <summary>Re-reads the device lists and clears stale errors. Called when the screen is shown.</summary>
    public void Prepare()
    {
        if (State != RecorderState.Idle)
        {
            return;
        }

        Error = null;
        ClearNotices();
        ResetTimer();
        RefreshDevices();
        ApplyDefaults();
        NewTitle();
    }

    /// <summary>Polls the elapsed time and the levels. Called by the view at about 4 Hz while recording or paused.</summary>
    public void Refresh()
    {
        if (State is RecorderState.Recording or RecorderState.Paused)
        {
            ShowElapsed(_recorder.Elapsed);
            UpdateTracks();
            if (State == RecorderState.Recording)
            {
                SyncScreenWithRecorder();
            }
        }
    }

    /// <summary>
    /// The timer shows whole seconds: the text is built when the second changes, not at every 4 Hz poll (three of four polls
    /// would build, compare and throw away the same string).
    /// </summary>
    private void ResetTimer()
    {
        _shownSeconds = -1;
        TimerText = UiFormat.Duration(TimeSpan.Zero);
    }

    private void ShowElapsed(TimeSpan elapsed)
    {
        var seconds = (long)elapsed.TotalSeconds;
        if (seconds == _shownSeconds && seconds >= 0)
        {
            return;
        }

        _shownSeconds = seconds;
        TimerText = UiFormat.Duration(elapsed);
    }

    /// <summary>Starts the recording, or stops it when one is running.</summary>
    [RelayCommand(CanExecute = nameof(CanStartStop))]
    public async Task StartStopAsync()
    {
        Error = null;
        if (State is RecorderState.Recording or RecorderState.Paused)
        {
            await StopAsync();
        }
        else if (State == RecorderState.Idle)
        {
            await StartAsync();
        }
    }

    /// <summary>
    /// Stops a running recording (used when the window closes). No-op when idle. A stop already in flight is awaited,
    /// not repeated, so the recorder is never asked to stop twice; a finished stop is never reused, so every later
    /// recording is really stopped. If the recorder has already ended the session by itself, that is reported instead.
    /// </summary>
    public Task StopAsync()
    {
        if (_stopTask is { IsCompleted: false } running)
        {
            return running;
        }

        _stopTask = null;
        if (State is not (RecorderState.Recording or RecorderState.Paused))
        {
            return Task.CompletedTask;
        }

        if (_recorder.State is not (RecorderState.Recording or RecorderState.Paused))
        {
            // The recorder ended the session by itself and its state change has not arrived yet: apply it now (that
            // reports the automatic stop) instead of asking the recorder to stop a session it no longer has.
            ApplyState(_recorder.State);
            return Task.CompletedTask;
        }

        // A stop can finish synchronously (its finally has then already run). Only a stop that is still running is
        // kept, so a finished one is never handed out as "in flight" and the next stop reaches the recorder again.
        var stop = StopCoreAsync();
        if (!stop.IsCompleted)
        {
            _stopTask = stop;
        }

        return stop;
    }

    private async Task StopCoreAsync()
    {
        _stopRequested = true;
        try
        {
            // A rename that is still being written finishes first: the stop must not overtake it.
            await _renameTask;
            var session = await _recorder.StopAsync(CancellationToken.None);
            if (_committedTitle.Length > 0)
            {
                // The recorder's record still has the title the session started with; the user may have changed it since.
                session = session with { Title = _committedTitle };
            }

            _sessionId = null;
            ApplyState(_recorder.State);
            ResetTimer();
            ResetTracks();

            // The session's own text (Error below) carries every warning of the recording.
            ClearNotices();
            if (SessionNoteText.Describe(session.Error) is { Length: > 0 } sessionError)
            {
                Error = sessionError;
            }

            Stopped?.Invoke(session);
        }
        catch (Exception ex)
        {
            Error = UiText.Format(Strings.Recording_StopFailed, ex.Message);
            ApplyState(_recorder.State);
        }
        finally
        {
            // _stopTask is not touched here: StopAsync owns it and ignores it once this task has completed.
            _stopRequested = false;
        }
    }

    /// <summary>The recorder itself is idle (this view model's <see cref="State"/> may still be about to catch up with an event).</summary>
    internal bool RecorderIsIdle => _recorder.State == RecorderState.Idle;

    /// <summary>
    /// The record hotkey: starts a recording with the saved defaults (the pills as <see cref="Prepare"/> set them) or stops
    /// the running one, exactly like the start/stop button. Ignored while the recorder is starting or stopping, and when the
    /// button itself would do nothing (no source to record). Completes when the start or stop has.
    /// </summary>
    internal Task ToggleRecordingFromHotkeyAsync()
    {
        var settled = _recorder.State switch
        {
            RecorderState.Idle => State == RecorderState.Idle,
            RecorderState.Recording => State == RecorderState.Recording,
            RecorderState.Paused => State == RecorderState.Paused,
            _ => false,
        };
        if (!settled || !StartStopCommand.CanExecute(null))
        {
            return Task.CompletedTask;
        }

        return StartStopCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// The screen hotkey: while recording, turns the screen to the opposite of what the recorder does now
    /// (<see cref="IRecorder.IsScreenRecording"/>, which a screen fault may have changed behind the pill's back). The pill is
    /// set to match first, then flipped, which sends the change to the recorder like a click does. Ignored when not
    /// recording, without a screen capture, and while an earlier change is still in flight. Completes when the change has
    /// been applied.
    /// </summary>
    internal Task ToggleScreenFromHotkeyAsync()
    {
        if (State is not (RecorderState.Recording or RecorderState.Paused) || _recorder.State != State || !ScreenAvailable || !PendingScreenChange.IsCompleted)
        {
            return Task.CompletedTask;
        }

        if (State == RecorderState.Paused)
        {
            // Paused, nothing records: the pill is what the next resume starts.
            Screen = !Screen;
            return PendingScreenChange;
        }

        SyncScreenWithRecorder();
        Screen = !_recorder.IsScreenRecording;
        return PendingScreenChange;
    }

    /// <summary>
    /// The pause hotkey: pauses a recording or resumes a paused one, exactly like the pause button. Ignored when nothing is
    /// recording, while the recorder is starting or stopping, and while an earlier pause or resume is still being applied.
    /// Completes when the change has.
    /// </summary>
    internal Task TogglePauseFromHotkeyAsync()
    {
        var settled = _recorder.State switch
        {
            RecorderState.Recording => State == RecorderState.Recording,
            RecorderState.Paused => State == RecorderState.Paused,
            _ => false,
        };
        return settled && PauseResumeCommand.CanExecute(null) ? PauseResumeCommand.ExecuteAsync(null) : Task.CompletedTask;
    }

    /// <summary>Pauses the recording, or resumes it when it is paused.</summary>
    [RelayCommand(CanExecute = nameof(CanPauseResume))]
    public async Task PauseResumeAsync()
    {
        if (_pauseBusy)
        {
            return;
        }

        var resuming = State == RecorderState.Paused;
        SetPauseBusy(true);
        try
        {
            if (resuming)
            {
                await _recorder.ResumeAsync(CancellationToken.None);
            }
            else
            {
                await _recorder.PauseAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            Error = UiText.Format(resuming ? Strings.Recording_ResumeFailed : Strings.Recording_PauseFailed, ex.Message);
        }
        finally
        {
            ApplyState(_recorder.State);
            SetPauseBusy(false);
        }
    }

    private void SetPauseBusy(bool busy)
    {
        _pauseBusy = busy;
        OnPropertyChanged(nameof(CanPauseResume));
        PauseResumeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Takes what the title field says: while recording it renames the session (Enter or leaving the field); before the
    /// recording starts it is the title the recording will get. An empty text goes back to the title it had. Completes when
    /// the rename has been stored.
    /// </summary>
    public Task CommitTitleAsync()
    {
        var text = Title.Trim();
        if (text.Length == 0 || text == _committedTitle)
        {
            Title = _committedTitle;
            return Task.CompletedTask;
        }

        if (State == RecorderState.Idle)
        {
            _committedTitle = text;
            _titleEdited = true;
            Title = text;
            return Task.CompletedTask;
        }

        // Only a running (or paused) session can be renamed: not one that is starting or stopping (the stop has the last word).
        if (State is not (RecorderState.Recording or RecorderState.Paused) || _stopRequested || _sessionId is not { } id)
        {
            Title = _committedTitle;
            return Task.CompletedTask;
        }

        var previous = _renameTask;
        _renameTask = RenameAfterAsync(previous, id, text);
        return _renameTask;
    }

    private async Task RenameAfterAsync(Task previous, SessionId id, string title)
    {
        await previous;
        await RenameAsync(id, title);
    }

    /// <summary>Esc in the title field: back to the title it had.</summary>
    public void CancelTitle() => Title = _committedTitle;

    private async Task RenameAsync(SessionId id, string title)
    {
        try
        {
            // Only the title column is written: the recorder and the transcription pipeline own the rest of the row.
            await _store.RenameSessionAsync(id, title, CancellationToken.None);
            if (_sessionId == id)
            {
                _committedTitle = title;
                Title = title;
            }
        }
        catch (Exception ex)
        {
            // A late answer for a session that is over (or another one) is not this screen's business any more.
            if (_sessionId == id)
            {
                Error = UiText.Format(Strings.Recording_RenameFailed, ex.Message);
                Title = _committedTitle;
            }
        }
    }

    /// <summary>Copies the notice for the other people on the call to the clipboard; the button reads "Copied" for two seconds.</summary>
    [RelayCommand]
    public async Task CopyMessageAsync()
    {
        if (ClipboardWriter is not { } write)
        {
            return;
        }

        try
        {
            if (!await write(Strings.Recording_MessageToCopy))
            {
                Error = UiText.Format(Strings.Recording_CopyFailed, Strings.Recording_ClipboardUnavailable);
                return;
            }
        }
        catch (Exception ex)
        {
            Error = UiText.Format(Strings.Recording_CopyFailed, ex.Message);
            return;
        }

        Error = null;

        MessageCopied = true;
        _copiedReset?.Cancel();
        _copiedReset = new CancellationTokenSource();
        _ = ResetCopiedAsync(_copiedReset.Token);
    }

    private async Task ResetCopiedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(CopiedFor, _time, token);
            _post(() => MessageCopied = false);
        }
        catch (OperationCanceledException)
        {
            // Pressed again: the newer timer owns the label.
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => BackRequested?.Invoke();

    private bool CanStartStop() => State switch
    {
        RecorderState.Recording or RecorderState.Paused => true,
        RecorderState.Idle => (Microphone && MicrophoneAvailable) || (SystemAudio && SystemAudioAvailable),
        _ => false,
    };

    private async Task StartAsync()
    {
        var options = new RecordingOptions(
            Microphone: Microphone && MicrophoneAvailable,
            SystemAudio: SystemAudio && SystemAudioAvailable,
            Screen: Screen && ScreenAvailable,
            MicrophoneDeviceId: SelectedMicrophone is { IsDefault: false } mic ? mic.Id : null,
            SystemDeviceId: SelectedSystemDevice is { IsDefault: false } output ? output.Id : null,
            DisplayId: SavedDisplayId(),
            Title: _titleEdited ? _committedTitle : UiFormat.DefaultTitle(_time.GetLocalNow(), _time));
        _microphoneRequested = options.Microphone;
        _systemAudioRequested = options.SystemAudio;
        _microphoneName = SelectedMicrophone?.Name ?? string.Empty;
        _systemAudioName = SelectedSystemDevice?.Name ?? string.Empty;

        try
        {
            _sessionId = null;
            ClearNotices();
            var session = await _recorder.StartAsync(options, CancellationToken.None);
            _sessionId = session.Id;
            _committedTitle = session.Title;
            _titleEdited = false;
            Title = session.Title;
            ResetTimer();
            ResetTracks();
            UpdateTracks();

            // The recorder skipped a track it could not start (e.g. Windows denies the microphone): say so inline.
            if (SessionNoteText.Describe(session.Error) is { Length: > 0 } warning)
            {
                Error = warning;
            }
        }
        catch (Exception ex)
        {
            Error = UiText.Format(Strings.Recording_StartFailed, ex.Message);
        }
        finally
        {
            ApplyState(_recorder.State);
        }
    }

    private void RefreshDevices()
    {
        MicrophoneDevices = _capture.Capabilities.Microphone ? _capture.GetAudioDevices(AudioDeviceKind.Microphone) : [];
        SystemDevices = _capture.Capabilities.SystemAudio ? _capture.GetAudioDevices(AudioDeviceKind.SystemOutput) : [];
        SelectedMicrophone = Pick(MicrophoneDevices, SelectedMicrophone);
        SelectedSystemDevice = Pick(SystemDevices, SelectedSystemDevice);

        if (!MicrophoneAvailable)
        {
            Microphone = false;
        }

        if (!SystemAudioAvailable)
        {
            SystemAudio = false;
        }

        NotifyStateDependents();
    }

    /// <summary>
    /// Starts from the user's defaults: the saved microphone and output (when they are still plugged in), the screen on or
    /// off. Called whenever the screen is shown, so a change in the Settings applies to the next recording.
    /// </summary>
    private void ApplyDefaults()
    {
        if (_settings is not { } store || State != RecorderState.Idle)
        {
            return;
        }

        var settings = store.Current;
        // A saved device that is gone falls back to the system default (as the Settings screen shows it), not to the last pick.
        SelectedMicrophone = Saved(MicrophoneDevices, settings.MicrophoneDeviceId) ?? Saved(MicrophoneDevices, null);
        SelectedSystemDevice = Saved(SystemDevices, settings.SystemAudioDeviceId) ?? Saved(SystemDevices, null);

        // The recorder is idle here, so the toggles only change the pills, not a running recording.
        Microphone = settings.RecordMicrophoneByDefault && MicrophoneAvailable;
        SystemAudio = settings.RecordSystemAudioByDefault && SystemAudioAvailable;
        Screen = settings.ScreenByDefault && ScreenAvailable;
    }

    /// <summary>The display chosen in the Settings while it is still connected, else <c>null</c> (the primary display).</summary>
    private string? SavedDisplayId()
    {
        if (!ScreenAvailable || _settings?.Current.ScreenDisplayId is not { } id)
        {
            return null;
        }

        foreach (var display in _capture.GetDisplays())
        {
            if (display.Id == id)
            {
                return id;
            }
        }

        return null;
    }

    private static AudioDevice? Saved(IReadOnlyList<AudioDevice> devices, string? id)
    {
        if (id is null)
        {
            // The system default: the device the platform marks as default, not whatever was picked last time.
            foreach (var device in devices)
            {
                if (device.IsDefault)
                {
                    return device;
                }
            }

            return devices.Count > 0 ? devices[0] : null;
        }

        foreach (var device in devices)
        {
            if (device.Id == id)
            {
                return device;
            }
        }

        return null;
    }

    private static AudioDevice? Pick(IReadOnlyList<AudioDevice> devices, AudioDevice? previous)
    {
        if (previous is not null)
        {
            foreach (var device in devices)
            {
                if (device.Id == previous.Id)
                {
                    return device;
                }
            }
        }

        foreach (var device in devices)
        {
            if (device.IsDefault)
            {
                return device;
            }
        }

        return devices.Count > 0 ? devices[0] : null;
    }

    private void OnRecorderStateChanged(RecorderState state) => _post(() => ApplyState(_recorder.State));

    private void OnRecorderWarning(RecorderWarning warning) => _post(() => ApplyWarning(warning));

    private void ApplyWarning(RecorderWarning warning)
    {
        // A warning that arrives after the recording has ended belongs to the session text, not to a notice.
        if (_recorder.State is not (RecorderState.Starting or RecorderState.Recording or RecorderState.Paused))
        {
            return;
        }

        // The text follows the UI language and the code of the problem (the recorder only reports the code).
        AddNotice(SessionNoteText.Describe(warning));
        if (warning.ScreenStopped)
        {
            SyncScreenWithRecorder();
        }
    }

    private void AddNotice(string text)
    {
        if (_notices.Contains(text))
        {
            return;
        }

        _notices.Add(text);
        Notice = string.Join('\n', _notices);
    }

    private void ClearNotices()
    {
        _notices.Clear();
        Notice = null;
    }

    /// <summary>
    /// The screen pill shows what the recorder really does: a fatal screen fault ends the segment by itself, so the pill
    /// turns off. Not while a toggle the user made is still being applied (the recorder has not caught up yet).
    /// </summary>
    private void SyncScreenWithRecorder()
    {
        if (State != RecorderState.Recording || !Screen || _recorder.IsScreenRecording || !PendingScreenChange.IsCompleted)
        {
            return;
        }

        _suppressScreenChange = true;
        try
        {
            Screen = false;
        }
        finally
        {
            _suppressScreenChange = false;
        }
    }

    private void ApplyState(RecorderState state)
    {
        var wasActive = State is RecorderState.Recording or RecorderState.Paused or RecorderState.Stopping;
        if (State != state)
        {
            State = state;
        }

        if (wasActive && state == RecorderState.Idle && !_stopRequested && _sessionId is not null)
        {
            // The recorder stopped on its own (every audio source failed). Tell the user why - but only for the session this
            // screen started: a recording another screen runs (the onboarding's trial) ends in its own way.
            AutoStopReport = ReportAutoStopAsync();
        }

        if (state == RecorderState.Idle)
        {
            ResetTracks();
        }
    }

    private async Task ReportAutoStopAsync()
    {
        var reason = Strings.Recording_StoppedItself;
        try
        {
            if (_sessionId is { } id && await _store.GetSessionAsync(id, CancellationToken.None) is { } session)
            {
                reason = SessionNoteText.Describe(session.Error) is { Length: > 0 } why ? UiText.Format(Strings.Recording_StoppedWithReason, why) : reason;
            }
        }
        catch (Exception ex)
        {
            reason = UiText.Format(Strings.Recording_StoppedItselfWithError, ex.Message);
        }

        _sessionId = null;
        Error = reason;
        ResetTimer();
        ResetTracks();
    }

    partial void OnStateChanged(RecorderState value)
    {
        NotifyStateDependents();
        StartStopCommand.NotifyCanExecuteChanged();
        PauseResumeCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        if (value is RecorderState.Recording or RecorderState.Paused)
        {
            UpdateTracks();
        }
    }

    partial void OnShowOrbChanged(bool value) => OnPropertyChanged(nameof(OrbActive));

    partial void OnMicrophoneChanged(bool value) => OnSourceChoiceChanged();

    partial void OnSystemAudioChanged(bool value) => OnSourceChoiceChanged();

    partial void OnMicrophoneDevicesChanged(IReadOnlyList<AudioDevice> value) => OnPropertyChanged(nameof(MicrophoneAvailable));

    partial void OnSystemDevicesChanged(IReadOnlyList<AudioDevice> value) => OnPropertyChanged(nameof(SystemAudioAvailable));

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnNoticeChanged(string? value) => OnPropertyChanged(nameof(HasNotice));

    partial void OnMessageCopiedChanged(bool value) => OnPropertyChanged(nameof(CopyLabel));

    partial void OnScreenChanged(bool value)
    {
        OnPropertyChanged(nameof(ScreenLabel));
        if (_suppressScreenChange || State is not (RecorderState.Recording or RecorderState.Paused))
        {
            return;
        }

        PendingScreenChange = ApplyScreenAsync(value);
    }

    private async Task ApplyScreenAsync(bool enabled)
    {
        try
        {
            await _recorder.SetScreenEnabledAsync(enabled, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Error = UiText.Format(enabled ? Strings.Recording_ScreenOnFailed : Strings.Recording_ScreenOffFailed, ex.Message);
            _suppressScreenChange = true;
            try
            {
                Screen = !enabled;
            }
            finally
            {
                _suppressScreenChange = false;
            }
        }
    }

    /// <summary>The title of the next recording: "Untitled — Oct 18, 01:12" until the user types one.</summary>
    private void NewTitle()
    {
        if (State != RecorderState.Idle)
        {
            return;
        }

        _committedTitle = UiFormat.DefaultTitle(_time.GetLocalNow(), _time);
        _titleEdited = false;
        Title = _committedTitle;
    }

    /// <summary>"Screen · 1080p": the height of what the video holds (the display, scaled down to the encoder's limit).</summary>
    private string ScreenOnLabel()
    {
        var options = new ScreenCaptureOptions();
        var id = SavedDisplayId();
        DisplayInfo? display = null;
        if (ScreenAvailable)
        {
            foreach (var candidate in _capture.GetDisplays())
            {
                if (id is not null ? candidate.Id == id : candidate.IsPrimary)
                {
                    display = candidate;
                    break;
                }
            }
        }

        return display is { Height: > 0 } shown
            ? UiText.Format(Strings.Recording_ScreenOn, Math.Min(shown.Height, options.MaxHeight).ToString(System.Globalization.CultureInfo.InvariantCulture))
            : Strings.Common_Screen;
    }

    /// <summary>Shows what each track does now: its level while recording, the state word otherwise. The strip moves only while recording.</summary>
    private void UpdateTracks()
    {
        if (State is not (RecorderState.Recording or RecorderState.Paused))
        {
            return;
        }

        var session = _recorder.Current;
        var levels = _recorder.Levels;
        var paused = State == RecorderState.Paused;
        var micRequested = session is null ? _microphoneRequested : _microphoneRequested || session.HasMicrophone;
        var systemRequested = session is null ? _systemAudioRequested : _systemAudioRequested || session.HasSystemAudio;

        MicrophoneTrack.Apply(
            TrackState(micRequested, session?.HasMicrophone ?? micRequested, paused, SessionNotes.MicrophoneDenied, session?.Error),
            levels.Microphone,
            TrackDetail(MicrophoneTrack, _microphoneName, loopback: false, SessionNotes.MicrophoneDenied, session?.Error),
            push: !paused);
        SystemTrack.Apply(
            TrackState(systemRequested, session?.HasSystemAudio ?? systemRequested, paused, SessionNotes.SystemAudioDenied, session?.Error),
            levels.SystemAudio,
            TrackDetail(SystemTrack, _systemAudioName, loopback: true, SessionNotes.SystemAudioDenied, session?.Error),
            push: !paused);
    }

    private static RecordingTrackState TrackState(bool requested, bool recorded, bool paused, string deniedCode, string? sessionError)
    {
        if (!requested)
        {
            return RecordingTrackState.Off;
        }

        if (!recorded)
        {
            return HasCode(sessionError, deniedCode) ? RecordingTrackState.Denied : RecordingTrackState.Unavailable;
        }

        return paused ? RecordingTrackState.Paused : RecordingTrackState.Active;
    }

    private string TrackDetail(RecordingTrackViewModel track, string device, bool loopback, string deniedCode, string? sessionError)
    {
        var requested = track.IsMicrophone ? _microphoneRequested : _systemAudioRequested;
        var recorded = track.IsMicrophone ? _recorder.Current?.HasMicrophone ?? requested : _recorder.Current?.HasSystemAudio ?? requested;

        // The text depends on these inputs only and is asked for at every poll: it is built again only when one of them changes.
        var memo = track.IsMicrophone ? _microphoneDetail : _systemDetail;
        if (memo.Matches(requested, recorded, device, sessionError, UiCulture.Language))
        {
            return memo.Text;
        }

        var text = BuildTrackDetail(track, device, loopback, deniedCode, sessionError, requested, recorded);
        memo.Store(requested, recorded, device, sessionError, UiCulture.Language, text);
        return text;
    }

    private static string BuildTrackDetail(
        RecordingTrackViewModel track,
        string device,
        bool loopback,
        string deniedCode,
        string? sessionError,
        bool requested,
        bool recorded)
    {
        if (!requested)
        {
            return Strings.Recording_TrackOffDetail;
        }

        if (!recorded)
        {
            return HasCode(sessionError, deniedCode)
                ? (track.IsMicrophone ? Strings.Note_MicDenied : Strings.Note_SystemDenied)
                : Strings.Recording_TrackLostDetail;
        }

        if (string.IsNullOrEmpty(device))
        {
            return string.Empty;
        }

        return loopback ? UiText.Format(Strings.Recording_Loopback, device) : device;
    }

    /// <summary>The last <see cref="TrackDetail"/> of one track with the inputs it was built from (compared by reference: they are stored strings).</summary>
    private sealed class TrackDetailMemo
    {
        private bool _requested;
        private bool _recorded;
        private string? _device;
        private string? _error;
        private string? _language;

        public string Text { get; private set; } = string.Empty;

        public bool Matches(bool requested, bool recorded, string device, string? error, string language) =>
            _language is not null && _requested == requested && _recorded == recorded && ReferenceEquals(_device, device)
                && ReferenceEquals(_error, error) && ReferenceEquals(_language, language);

        public void Store(bool requested, bool recorded, string device, string? error, string language, string text)
        {
            _requested = requested;
            _recorded = recorded;
            _device = device;
            _error = error;
            _language = language;
            Text = text;
        }
    }

    private static bool HasCode(string? sessionError, string code)
    {
        foreach (var note in SessionNotes.Parse(sessionError))
        {
            if (note.Code == code)
            {
                return true;
            }
        }

        return false;
    }

    private void ResetTracks()
    {
        MicrophoneTrack.Reset();
        SystemTrack.Reset();
    }

    private void OnSourceChoiceChanged()
    {
        OnPropertyChanged(nameof(SourceHint));
        OnPropertyChanged(nameof(HasSourceHint));
        StartStopCommand.NotifyCanExecuteChanged();
    }

    private void NotifyStateDependents()
    {
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsSetup));
        OnPropertyChanged(nameof(PauseLabel));
        OnPropertyChanged(nameof(TimerNote));
        OnPropertyChanged(nameof(CanPauseResume));
        OnPropertyChanged(nameof(OrbActive));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanToggleMicrophone));
        OnPropertyChanged(nameof(CanToggleSystemAudio));
        OnPropertyChanged(nameof(CanToggleScreen));
        OnPropertyChanged(nameof(CanChangeDevices));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(StartStopLabel));
        OnPropertyChanged(nameof(MicrophoneAvailable));
        OnPropertyChanged(nameof(SystemAudioAvailable));
        OnSourceChoiceChanged();
    }
}
