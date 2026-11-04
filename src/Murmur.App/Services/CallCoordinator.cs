using Murmur.App.Resources;
using Murmur.App.ViewModels;
using Murmur.Core.Calls;
using Murmur.Core.Recording;
using Murmur.Core.Settings;

namespace Murmur.App.Services;

/// <summary>Shows and closes the call toast (the real one is a <c>CallToastWindow</c>); called on the UI thread.</summary>
internal interface ICallToastHost
{
    /// <summary>Shows <paramref name="toast"/>, replacing the one on screen.</summary>
    void Show(CallToastViewModel toast);

    /// <summary>Closes the toast on screen, if any (does not raise <see cref="Closed"/>).</summary>
    void Close();

    /// <summary>The toast on screen was closed by something other than <see cref="Close"/> (Alt+F4, the system): the user dismissed it.</summary>
    event Action? Closed;
}

/// <summary>What the call toast does to the rest of the app; every delegate runs on the UI thread.</summary>
/// <param name="StartRecording">
/// Starts a recording through the Recording flow. The app uses the very start path of the record hotkey
/// (<c>ShellViewModel.ToggleRecordingFromHotkeyAsync</c>): the saved default sources, or the source choices the Recording screen
/// holds right now when it is open. An auto-recorded call starts the same way.
/// </param>
/// <param name="StopRecording">Stops the running recording.</param>
/// <param name="OpenApp">Brings the main window to the front on the recording screen.</param>
/// <param name="CanRecord">The app can take a recording now (not during the onboarding); <c>null</c> = always.</param>
internal sealed record CallActions(Func<Task> StartRecording, Func<Task> StopRecording, Action OpenApp, Func<bool>? CanRecord = null);

/// <summary>
/// The meeting autopilot of #44: runs the <see cref="ICallDetector"/> while <see cref="AppSettings.DetectCalls"/> is on (and only
/// then: off = the detector and its monitor are stopped, so nothing is watched or polled), and answers a detected call.
/// </summary>
/// <remarks>
/// <para><b>A call starts.</b> Nothing happens while Murmur records (or the app cannot take a recording now). An app on
/// <see cref="AppSettings.CallAllowlist"/> starts a recording with the default sources and shows the auto-record toast (Open /
/// Stop); any other app gets the "Call detected" toast (Record / Not now). The detector raises one start per call session, so
/// "Not now" (or the close button) dismisses the offer for the rest of that call.</para>
/// <para><b>Record</b> starts the recording; with "Always record calls from this app" checked it also adds the process name to
/// the allowlist through <see cref="SettingsWriter"/>. <b>Open</b> shows the recording, <b>Stop</b> stops it.</para>
/// <para><b>The toast closes</b> when the user chooses, when the offer's call ends, when a recording starts some other way (no
/// offer while recording), when the auto-recorded recording stops, and when Detect calls is turned off.</para>
/// <para><b>Threads.</b> The detector and the settings store raise their events on any thread; everything is marshalled with
/// <c>post</c> to the UI thread, where all the state lives.</para>
/// </remarks>
internal sealed class CallCoordinator : IDisposable
{
    private readonly ICallDetector _detector;
    private readonly ISettingsStore _settings;
    private readonly SettingsWriter _writer;
    private readonly IRecorder _recorder;
    private readonly ICallToastHost _toasts;
    private readonly CallActions _actions;
    private readonly Action<Action> _post;
    private CallToastViewModel? _toast;
    private bool _started;
    private bool _disposed;

    /// <param name="detector">The call detector; started and stopped here, not disposed (the composition root owns it).</param>
    /// <param name="settings">Where <c>DetectCalls</c> and the allowlist are read, watched and saved.</param>
    /// <param name="recorder">Tells whether Murmur records.</param>
    /// <param name="toasts">Shows the toast.</param>
    /// <param name="actions">Starts, stops and opens the recording.</param>
    /// <param name="post">Marshals to the UI thread; the UI dispatcher by default.</param>
    public CallCoordinator(
        ICallDetector detector,
        ISettingsStore settings,
        IRecorder recorder,
        ICallToastHost toasts,
        CallActions actions,
        Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(toasts);
        ArgumentNullException.ThrowIfNull(actions);
        _detector = detector;
        _settings = settings;
        _writer = SettingsWriter.For(settings);
        _recorder = recorder;
        _toasts = toasts;
        _actions = actions;
        _post = post ?? (action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
    }

    /// <summary>The toast on screen, or <c>null</c>.</summary>
    public CallToastViewModel? Toast => _toast;

    /// <summary>The last action started by a toast button or an auto-start (for tests).</summary>
    internal Task LastAction { get; private set; } = Task.CompletedTask;

    /// <summary>Subscribes and starts the detector when Detect calls is on. Called on the UI thread after the first frame.</summary>
    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _toasts.Closed += OnToastClosed;
        _detector.CallStarted += OnCallStarted;
        _detector.CallEnded += OnCallEnded;
        _recorder.StateChanged += OnRecorderStateChanged;
        _settings.Changed += OnSettingsChanged;
        Apply(_settings.Current);
    }

    /// <summary>
    /// Settings → "Try it": the "Call detected" toast for a sample app. Ignored while the toast of a real call is on screen (the
    /// sample never replaces a live offer or an auto-record toast).
    /// </summary>
    public void ShowSample()
    {
        if (_disposed || _toast is { IsSample: false })
        {
            return;
        }

        Show(new CallToastViewModel(new CallInfo(Strings.Calls_SampleApp, "sample", 0), CallToastKind.Offer, isSample: true));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_started)
        {
            _toasts.Closed -= OnToastClosed;
            _detector.CallStarted -= OnCallStarted;
            _detector.CallEnded -= OnCallEnded;
            _recorder.StateChanged -= OnRecorderStateChanged;
            _settings.Changed -= OnSettingsChanged;
            _detector.Stop();
        }

        CloseToast();
    }

    private void OnSettingsChanged(AppSettings settings) => _post(() => Apply(_settings.Current));

    private void Apply(AppSettings settings)
    {
        if (_disposed)
        {
            return;
        }

        if (settings.DetectCalls)
        {
            if (!_detector.IsRunning)
            {
                _detector.Start();
            }
        }
        else
        {
            if (_detector.IsRunning)
            {
                _detector.Stop();
            }

            if (_toast is { IsSample: false })
            {
                CloseToast();
            }
        }
    }

    private void OnCallStarted(CallInfo call) => _post(() => HandleCallStarted(call));

    private void OnCallEnded(CallInfo call) => _post(() =>
    {
        // The offer is about a call that is over; an auto-recorded call keeps its toast (Open / Stop) until the recording ends.
        if (!_disposed && _toast is { IsSample: false, IsOffer: true } toast && toast.Call == call)
        {
            CloseToast();
        }
    });

    private void OnRecorderStateChanged(RecorderState state) => _post(() =>
    {
        if (_disposed || _toast is not { } toast)
        {
            return;
        }

        if (state == RecorderState.Idle && toast.IsAutoRecording)
        {
            CloseToast();
        }
        else if (state != RecorderState.Idle && toast.IsOffer && !toast.IsSample)
        {
            // A recording started some other way: no offer while recording.
            CloseToast();
        }
    });

    private void HandleCallStarted(CallInfo call)
    {
        var settings = _settings.Current;
        if (_disposed || !settings.DetectCalls || _recorder.State != RecorderState.Idle || _actions.CanRecord?.Invoke() == false)
        {
            return;
        }

        if (ProcessNameList.From(settings.CallAllowlist).Contains(call.ProcessName))
        {
            LastAction = AutoRecordAsync(call);
            return;
        }

        Show(new CallToastViewModel(call, CallToastKind.Offer));
    }

    private async Task AutoRecordAsync(CallInfo call)
    {
        if (!await TryStartRecordingAsync())
        {
            return;
        }

        if (!_disposed && _recorder.State != RecorderState.Idle)
        {
            Show(new CallToastViewModel(call, CallToastKind.AutoRecording));
        }
    }

    private void Show(CallToastViewModel toast)
    {
        CloseToast();
        _toast = toast;
        toast.ActionChosen += OnActionChosen;
        _toasts.Show(toast);
    }

    private void CloseToast()
    {
        if (_toast is not { } toast)
        {
            return;
        }

        toast.ActionChosen -= OnActionChosen;
        _toast = null;
        _toasts.Close();
    }

    /// <summary>The window went away without a button (Alt+F4, the system): the same as dismissing it.</summary>
    private void OnToastClosed() => _post(() =>
    {
        if (_toast is { } toast)
        {
            toast.ActionChosen -= OnActionChosen;
            _toast = null;
        }
    });

    private void OnActionChosen(CallToastViewModel toast, CallToastAction action)
    {
        if (!ReferenceEquals(toast, _toast))
        {
            return;
        }

        CloseToast();
        LastAction = action switch
        {
            CallToastAction.Record => RecordAsync(toast),
            CallToastAction.Stop => StopAsync(),
            CallToastAction.Open => OpenAsync(),
            _ => Task.CompletedTask,
        };
    }

    /// <summary>
    /// Record: the recording starts whatever happens to the allowlist; with "Always record" the process name is saved too, and a
    /// save that fails is logged (the recording of this call is unaffected, the next call of the app simply asks again).
    /// </summary>
    private async Task RecordAsync(CallToastViewModel toast)
    {
        Task<string?>? save = null;
        if (toast is { AlwaysRecord: true, IsSample: false })
        {
            var processName = toast.Call.ProcessName;
            save = _writer.UpdateAsync(s => s with { CallAllowlist = ProcessNameList.From(s.CallAllowlist).Add(processName) });
        }

        if (_recorder.State == RecorderState.Idle && _actions.CanRecord?.Invoke() != false)
        {
            await TryStartRecordingAsync();
        }

        if (save is not null && await save is { } error)
        {
            Console.Error.WriteLine($"Adding {toast.Call.ProcessName} to the call allowlist failed: {error}");
        }
    }

    /// <summary>Starts the recording; a failure is the Recording screen's to show (its inline error), the toast just does not follow.</summary>
    private async Task<bool> TryStartRecordingAsync()
    {
        try
        {
            await _actions.StartRecording();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private Task StopAsync() => _recorder.State is RecorderState.Recording or RecorderState.Paused ? _actions.StopRecording() : Task.CompletedTask;

    private Task OpenAsync()
    {
        _actions.OpenApp();
        return Task.CompletedTask;
    }
}
