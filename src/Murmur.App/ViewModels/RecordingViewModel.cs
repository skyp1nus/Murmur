using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Controls;
using Murmur.App.Resources;
using Murmur.Core.Capture;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Settings;

namespace Murmur.App.ViewModels;

/// <summary>
/// The Recording screen: source toggles, device pickers, the timer and the start/stop button.
/// </summary>
/// <remarks>
/// Before recording the pills choose the sources. While recording only the screen pill stays enabled (it maps to
/// <see cref="IRecorder.SetScreenEnabledAsync"/>). The view polls <see cref="Refresh"/> at about 4 Hz while
/// recording; the orb polls <see cref="Level"/> from its own render tick. No per-buffer events are used.
/// </remarks>
public sealed partial class RecordingViewModel : ObservableObject, ILevelSource
{
    private readonly IRecorder _recorder;
    private readonly ICapture _capture;
    private readonly ISessionStore _store;
    private readonly Action<Action> _post;
    private readonly ISettingsStore? _settings;
    private readonly TimeProvider _time;
    private SessionId? _sessionId;
    private bool _stopRequested;

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

    /// <summary>Starting or stopping: nothing may be changed.</summary>
    public bool IsBusy => State is RecorderState.Starting or RecorderState.Stopping;

    public bool CanToggleMicrophone => State == RecorderState.Idle && MicrophoneAvailable;

    public bool CanToggleSystemAudio => State == RecorderState.Idle && SystemAudioAvailable;

    /// <summary>The screen pill is the only one that works while recording.</summary>
    public bool CanToggleScreen => ScreenAvailable && !IsBusy;

    public bool CanChangeDevices => State == RecorderState.Idle;

    public bool CanGoBack => State == RecorderState.Idle;

    public string StartStopLabel => IsRecording ? Strings.Common_Stop : Strings.Recording_Start;

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
        TimerText = UiFormat.Duration(TimeSpan.Zero);
        RefreshDevices();
        ApplyDefaults();
    }

    /// <summary>Polls the elapsed time. Called by the view at about 4 Hz while recording.</summary>
    public void Refresh()
    {
        if (State == RecorderState.Recording)
        {
            TimerText = UiFormat.Duration(_recorder.Elapsed);
            SyncScreenWithRecorder();
        }
    }

    /// <summary>Starts the recording, or stops it when one is running.</summary>
    [RelayCommand(CanExecute = nameof(CanStartStop))]
    public async Task StartStopAsync()
    {
        Error = null;
        if (State == RecorderState.Recording)
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
        if (State != RecorderState.Recording)
        {
            return Task.CompletedTask;
        }

        if (_recorder.State != RecorderState.Recording)
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
            var session = await _recorder.StopAsync(CancellationToken.None);
            _sessionId = null;
            ApplyState(_recorder.State);
            TimerText = UiFormat.Duration(TimeSpan.Zero);

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
        if (State != RecorderState.Recording || _recorder.State != RecorderState.Recording || !ScreenAvailable || !PendingScreenChange.IsCompleted)
        {
            return Task.CompletedTask;
        }

        SyncScreenWithRecorder();
        Screen = !_recorder.IsScreenRecording;
        return PendingScreenChange;
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => BackRequested?.Invoke();

    private bool CanStartStop() => State switch
    {
        RecorderState.Recording => true,
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
            Title: UiFormat.DefaultTitle(_time.GetLocalNow(), _time));

        try
        {
            _sessionId = null;
            ClearNotices();
            var session = await _recorder.StartAsync(options, CancellationToken.None);
            _sessionId = session.Id;
            TimerText = UiFormat.Duration(TimeSpan.Zero);

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

        // The recorder is idle here, so the toggle only changes the pill, not a running recording.
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
        if (_recorder.State is not (RecorderState.Starting or RecorderState.Recording))
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
        var wasActive = State is RecorderState.Recording or RecorderState.Stopping;
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
        TimerText = UiFormat.Duration(TimeSpan.Zero);
    }

    partial void OnStateChanged(RecorderState value)
    {
        NotifyStateDependents();
        StartStopCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
    }

    partial void OnShowOrbChanged(bool value) => OnPropertyChanged(nameof(OrbActive));

    partial void OnMicrophoneChanged(bool value) => OnSourceChoiceChanged();

    partial void OnSystemAudioChanged(bool value) => OnSourceChoiceChanged();

    partial void OnMicrophoneDevicesChanged(IReadOnlyList<AudioDevice> value) => OnPropertyChanged(nameof(MicrophoneAvailable));

    partial void OnSystemDevicesChanged(IReadOnlyList<AudioDevice> value) => OnPropertyChanged(nameof(SystemAudioAvailable));

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnNoticeChanged(string? value) => OnPropertyChanged(nameof(HasNotice));

    partial void OnScreenChanged(bool value)
    {
        if (_suppressScreenChange || State != RecorderState.Recording)
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

    private void OnSourceChoiceChanged()
    {
        OnPropertyChanged(nameof(SourceHint));
        OnPropertyChanged(nameof(HasSourceHint));
        StartStopCommand.NotifyCanExecuteChanged();
    }

    private void NotifyStateDependents()
    {
        OnPropertyChanged(nameof(IsRecording));
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
