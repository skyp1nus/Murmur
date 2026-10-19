using Murmur.App.ViewModels;
using Murmur.Core.Input;
using Murmur.Core.Settings;
using HotkeyAction = Murmur.Core.Input.HotkeyAction;

namespace Murmur.App.Services;

/// <summary>
/// Connects the system-wide hotkeys to the app: registers the gestures of the settings (record, screen, show the window),
/// keeps them in step with later settings changes and runs the action of a press on the UI thread.
/// </summary>
/// <remarks>
/// <para><b>Threads.</b> <see cref="IGlobalHotkeys.Register"/> is synchronous and marshals to the hotkey thread, so it never runs
/// on the UI thread: registrations are made on <c>runBackground</c> (the thread pool), one at a time under a private lock.
/// <see cref="IGlobalHotkeys.Pressed"/> arrives on the hotkey thread; its handler only posts to the UI thread and returns, and
/// never takes that lock, so a <c>Register</c> that waits for the hotkey thread cannot deadlock with a press.</para>
/// <para><b>Re-registration.</b> Only the actions whose gesture text changed are registered again. Changed gestures are
/// released first and registered afterwards, so two actions may swap gestures. Quick successive changes are coalesced to the
/// latest settings. The latest <see cref="HotkeyRegistration"/> of every action is pushed to the Settings rows (a note under a
/// row when the gesture is taken or unusable).</para>
/// <para><b>Presses are ignored</b> while the Settings wait for a key combination (the user is typing a gesture, not recording).</para>
/// </remarks>
internal sealed class HotkeyCoordinator : IDisposable
{
    private static readonly HotkeyAction[] Actions = [HotkeyAction.ToggleRecording, HotkeyAction.ToggleScreen, HotkeyAction.ShowApp];

    private readonly IGlobalHotkeys _hotkeys;
    private readonly ISettingsStore _settings;
    private readonly ShellViewModel _shell;
    private readonly Action _showApp;
    private readonly Action<Action> _post;
    private readonly Action<Action> _runBackground;

    private readonly Lock _gate = new();
    private readonly string?[] _applied = new string?[3];
    private readonly bool[] _known = new bool[3];
    private readonly HotkeyRegistration[] _registrations = [HotkeyRegistration.Cleared, HotkeyRegistration.Cleared, HotkeyRegistration.Cleared];
    private AppSettings? _pending;
    private bool _started;
    private bool _disposed;

    /// <param name="hotkeys">The system-wide hotkeys; not disposed here (the composition root owns it).</param>
    /// <param name="settings">Where the gestures are read and watched.</param>
    /// <param name="shell">Runs the record and screen actions and holds the Settings rows that show the notes.</param>
    /// <param name="showApp">Brings the main window to the front; runs on the UI thread.</param>
    /// <param name="post">Marshals to the UI thread; the UI dispatcher by default.</param>
    /// <param name="runBackground">Runs registrations off the UI thread; the thread pool by default (tests run them inline).</param>
    public HotkeyCoordinator(
        IGlobalHotkeys hotkeys,
        ISettingsStore settings,
        ShellViewModel shell,
        Action showApp,
        Action<Action>? post = null,
        Action<Action>? runBackground = null)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(showApp);
        _hotkeys = hotkeys;
        _settings = settings;
        _shell = shell;
        _showApp = showApp;
        _post = post ?? (action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        _runBackground = runBackground ?? (action => Task.Run(action));
    }

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
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _hotkeys.Pressed += OnPressed;
        _settings.Changed += OnSettingsChanged;
        Queue(_settings.Current);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hotkeys.Pressed -= OnPressed;
        _settings.Changed -= OnSettingsChanged;

        // Waits for a registration that is running right now, so the hotkeys are never used after the owner disposes them.
        lock (_gate)
        {
            _pending = null;
        }
    }

    private void OnSettingsChanged(AppSettings settings) => Queue(settings);

    private void Queue(AppSettings settings)
    {
        lock (_gate)
        {
            _pending = settings;
        }

        _runBackground(Drain);
    }

    private void Drain()
    {
        (HotkeyAction Action, HotkeyRegistration Result)[] changes;
        lock (_gate)
        {
            if (_disposed || _pending is not { } settings)
            {
                return;
            }

            _pending = null;
            changes = Apply(settings);
        }

        if (changes.Length > 0)
        {
            _post(() => Publish(changes));
        }
    }

    private (HotkeyAction Action, HotkeyRegistration Result)[] Apply(AppSettings settings)
    {
        string?[] wanted = [settings.RecordHotkey, settings.ToggleScreenHotkey, settings.ShowAppHotkey];
        var changed = new List<int>(3);
        for (var i = 0; i < Actions.Length; i++)
        {
            if (!_known[i] || !string.Equals(_applied[i], wanted[i], StringComparison.Ordinal))
            {
                changed.Add(i);
            }
        }

        // Release first: a gesture that moves from one action to another is free by the time the other one asks for it.
        foreach (var i in changed)
        {
            if (_known[i] && _applied[i] is not null && wanted[i] is not null)
            {
                TryRegister(Actions[i], null);
            }
        }

        var result = new (HotkeyAction, HotkeyRegistration)[changed.Count];
        for (var n = 0; n < changed.Count; n++)
        {
            var i = changed[n];
            var registration = wanted[i] is null && (!_known[i] || _applied[i] is null)
                ? HotkeyRegistration.Cleared
                : TryRegister(Actions[i], wanted[i]);
            _known[i] = true;
            _applied[i] = wanted[i];
            _registrations[i] = registration;
            result[n] = (Actions[i], registration);
        }

        return result;
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

    private void Publish((HotkeyAction Action, HotkeyRegistration Result)[] changes)
    {
        if (_disposed || _shell.Settings is not { } settings)
        {
            return;
        }

        foreach (var (action, result) in changes)
        {
            settings.Hotkeys.SetRegistration(action, result);
        }
    }

    /// <summary>On the hotkey thread: post and return, nothing else.</summary>
    private void OnPressed(HotkeyAction action) => _post(() => Run(action));

    private void Run(HotkeyAction action)
    {
        if (_disposed || _shell.Settings?.Hotkeys.Capturing is not null)
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
        }
    }
}
