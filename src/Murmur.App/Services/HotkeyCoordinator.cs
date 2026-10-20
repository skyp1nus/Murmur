using Murmur.App.ViewModels;
using Murmur.Core.Input;
using Murmur.Core.Settings;

namespace Murmur.App.Services;

/// <summary>
/// Connects the system-wide hotkeys to the app: registers the gestures of the settings (record, screen, show the window, pause),
/// keeps them in step with later settings changes and runs the action of a press on the UI thread.
/// </summary>
/// <remarks>
/// <para><b>Threads.</b> <see cref="IGlobalHotkeys.Register"/> is synchronous and marshals to the hotkey thread, so it never runs
/// on the UI thread: registrations are made on <c>runBackground</c> (the thread pool), one pass at a time under <c>_gate</c>.
/// <see cref="IGlobalHotkeys.Pressed"/> arrives on the hotkey thread; its handler only posts to the UI thread and returns, and
/// never takes the lock, so a <c>Register</c> that waits for the hotkey thread cannot deadlock with a press. The UI thread
/// never takes <c>_gate</c> either (only the short <c>_input</c> lock), so a slow <c>Register</c> cannot freeze the UI.</para>
/// <para><b>Re-registration.</b> Only the actions whose gesture text changed (and those that are <c>Taken</c>, which are tried
/// again on every settings change) are registered. Everything that changed is released first and registered afterwards, so
/// two actions may swap gestures, or one may take a gesture another gives up in the same change. Quick successive changes
/// are coalesced to the latest settings. After every pass the latest <see cref="HotkeyRegistration"/> of every action is
/// pushed to the Settings rows (a note under a row when the gesture is taken or unusable).</para>
/// <para><b>Capturing.</b> While a Settings (or onboarding) row waits for a key combination every registration is released, so
/// the combination reaches the field instead of triggering its action; when the capture ends they are registered again. Notes
/// stay as they were during the capture.</para>
/// <para><b>Presses are ignored</b> while a row captures and once the window is closing.</para>
/// </remarks>
internal sealed class HotkeyCoordinator : IDisposable
{
    private static readonly HotkeyAction[] Actions = [HotkeyAction.ToggleRecording, HotkeyAction.ToggleScreen, HotkeyAction.ShowApp, HotkeyAction.TogglePause];

    private readonly IGlobalHotkeys _hotkeys;
    private readonly ISettingsStore _settings;
    private readonly ShellViewModel _shell;
    private readonly Action _showApp;
    private readonly Func<bool> _isClosing;
    private readonly Action<Action> _post;
    private readonly Action<Action> _runBackground;

    // What the UI thread hands to the background pass (short lock, never held across a Register).
    private readonly Lock _input = new();
    private AppSettings? _current;
    private bool _dirty;
    private bool _suspendedRequested;

    // The background pass's own state, under _gate.
    private readonly Lock _gate = new();
    private readonly string?[] _applied = new string?[Actions.Length];
    private readonly bool[] _known = new bool[Actions.Length];
    private readonly HotkeyRegistration[] _registrations = [HotkeyRegistration.Cleared, HotkeyRegistration.Cleared, HotkeyRegistration.Cleared, HotkeyRegistration.Cleared];
    private bool _suspended;
    private bool _started;
    private int _disposed;

    /// <param name="hotkeys">The system-wide hotkeys; not disposed here (the composition root owns it).</param>
    /// <param name="settings">Where the gestures are read and watched.</param>
    /// <param name="shell">Runs the record and screen actions and holds the Settings rows that show the notes.</param>
    /// <param name="showApp">Brings the main window to the front; runs on the UI thread.</param>
    /// <param name="post">Marshals to the UI thread; the UI dispatcher by default.</param>
    /// <param name="runBackground">Runs registrations off the UI thread; the thread pool by default (tests run them inline).</param>
    /// <param name="isClosing">The window has started closing: no action runs any more. <c>null</c> = never.</param>
    public HotkeyCoordinator(
        IGlobalHotkeys hotkeys,
        ISettingsStore settings,
        ShellViewModel shell,
        Action showApp,
        Action<Action>? post = null,
        Action<Action>? runBackground = null,
        Func<bool>? isClosing = null)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(showApp);
        _hotkeys = hotkeys;
        _settings = settings;
        _shell = shell;
        _showApp = showApp;
        _isClosing = isClosing ?? (() => false);
        _post = post ?? (action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        _runBackground = runBackground ?? (action => Task.Run(action));
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>The result of the latest registration of <paramref name="action"/> (<see cref="HotkeyRegistration.Cleared"/> until one ran).</summary>
    public HotkeyRegistration RegistrationOf(HotkeyAction action)
    {
        lock (_gate)
        {
            return _registrations[(int)action];
        }
    }

    /// <summary>
    /// Registers the gestures of the settings and starts following them. Called on the UI thread after the first frame, not
    /// before (the start-up budget); the registrations themselves run in the background.
    /// </summary>
    public void Start()
    {
        if (_started || IsDisposed)
        {
            return;
        }

        _started = true;
        _hotkeys.Pressed += OnPressed;
        _settings.Changed += OnSettingsChanged;
        if (_shell.Settings?.Hotkeys is { } rows)
        {
            rows.CapturingChanged += OnCapturingChanged;
            _suspendedRequested = rows.Capturing is not null;
        }

        Queue(_settings.Current);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _hotkeys.Pressed -= OnPressed;
        _settings.Changed -= OnSettingsChanged;
        if (_shell.Settings?.Hotkeys is { } rows)
        {
            rows.CapturingChanged -= OnCapturingChanged;
        }

        // Waits for a pass that is running right now (it stops before its next Register), so the hotkeys are never used
        // after the owner disposes them.
        lock (_gate)
        {
        }
    }

    private void OnSettingsChanged(AppSettings settings) => Queue(settings);

    private void OnCapturingChanged(bool capturing)
    {
        lock (_input)
        {
            _suspendedRequested = capturing;
            _dirty = true;
        }

        _runBackground(Drain);
    }

    private void Queue(AppSettings settings)
    {
        lock (_input)
        {
            _current = settings;
            _dirty = true;
        }

        _runBackground(Drain);
    }

    private void Drain()
    {
        lock (_gate)
        {
            AppSettings settings;
            bool suspend;
            lock (_input)
            {
                if (!_dirty || _current is null)
                {
                    return;
                }

                _dirty = false;
                settings = _current;
                suspend = _suspendedRequested;
            }

            if (IsDisposed)
            {
                return;
            }

            _suspended = suspend;
            Apply(settings);

            // The UI shows what is true now; during a capture the notes stay as they were.
            if (!_suspended && !IsDisposed)
            {
                var snapshot = (HotkeyRegistration[])_registrations.Clone();
                _post(() => Publish(snapshot));
            }
        }
    }

    private void Apply(AppSettings settings)
    {
        string?[] wanted = _suspended
            ? [null, null, null, null]
            : [settings.RecordHotkey, settings.ToggleScreenHotkey, settings.ShowAppHotkey, settings.PauseHotkey];
        var changed = new List<int>(Actions.Length);
        for (var i = 0; i < Actions.Length; i++)
        {
            // A taken gesture is tried again on every change: the other program may have let it go.
            var retry = !_suspended && wanted[i] is not null && _registrations[i] == HotkeyRegistration.Taken;
            if (!_known[i] || retry || !string.Equals(_applied[i], wanted[i], StringComparison.Ordinal))
            {
                changed.Add(i);
            }
        }

        // Release first, whatever comes next: a gesture that moves from one action to another (or is given up by one
        // action in the very change that gives it to the other) is free by the time the other asks for it.
        foreach (var i in changed)
        {
            if (_known[i] && _applied[i] is not null)
            {
                if (IsDisposed)
                {
                    return;
                }

                TryRegister(Actions[i], null);
                _applied[i] = null;
            }
        }

        foreach (var i in changed)
        {
            if (_suspended)
            {
                _known[i] = true;
                continue;
            }

            HotkeyRegistration registration;
            if (wanted[i] is null)
            {
                registration = HotkeyRegistration.Cleared;
            }
            else
            {
                if (IsDisposed)
                {
                    return;
                }

                registration = TryRegister(Actions[i], wanted[i]);
            }

            _known[i] = true;
            _applied[i] = wanted[i];
            _registrations[i] = registration;
        }
    }

    private HotkeyRegistration TryRegister(HotkeyAction action, string? gesture)
    {
        try
        {
            return _hotkeys.Register(action, gesture);
        }
        catch (Exception ex)
        {
            // Disposed while shutting down, or an unexpected native failure: the action simply has no hotkey.
            Console.Error.WriteLine($"Registering the {action} hotkey failed: {ex.Message}");
            return HotkeyRegistration.Unsupported;
        }
    }

    private void Publish(HotkeyRegistration[] snapshot)
    {
        if (IsDisposed || _shell.Settings is not { } settings)
        {
            return;
        }

        for (var i = 0; i < Actions.Length; i++)
        {
            settings.Hotkeys.SetRegistration(Actions[i], snapshot[i]);
        }
    }

    /// <summary>On the hotkey thread: post and return, nothing else.</summary>
    private void OnPressed(HotkeyAction action) => _post(() => Run(action));

    private void Run(HotkeyAction action)
    {
        if (IsDisposed || _isClosing() || _shell.Settings?.Hotkeys.Capturing is not null)
        {
            return;
        }

        switch (action)
        {
            case HotkeyAction.ToggleRecording:
                _ = _shell.ToggleRecordingFromHotkeyAsync();
                break;
            case HotkeyAction.ToggleScreen:
                _ = _shell.ToggleScreenFromHotkeyAsync();
                break;
            case HotkeyAction.ShowApp:
                _showApp();
                break;
            case HotkeyAction.TogglePause:
                _ = _shell.TogglePauseFromHotkeyAsync();
                break;
        }
    }
}
