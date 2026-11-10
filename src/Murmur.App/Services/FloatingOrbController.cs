using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Platform;
using Murmur.Core.Recording;

namespace Murmur.App.Services;

/// <summary>
/// Connects the floating recorder orb (#46) to the app: tells its view model whether the main window is showing the Recording
/// screen, and shows or hides the orb's window when the view model says so. The window is created the first time it is needed (a
/// recording runs while the main window is away), never at start-up.
/// </summary>
/// <remarks>
/// "Showing the Recording screen" is: the main window is visible, not minimised, and the page is the Recording screen. While the
/// onboarding is the page the orb is never shown, whatever the window's state. The buttons do what the Recording screen's do: pause and
/// stop go through the same view model and recorder, Open brings the main window to the front on the Recording screen.
/// </remarks>
internal sealed class FloatingOrbController : IDisposable
{
    private readonly MainWindow _main;
    private readonly ShellViewModel _shell;
    private readonly Func<FloatingOrbViewModel, FloatingOrbWindow> _createWindow;
    private readonly IFloatingWindowStyle? _style;
    private readonly Func<FloatingOrbWindow, nint> _nativeHandle;
    private FloatingOrbWindow? _window;
    private int _disposed;

    internal FloatingOrbController(MainWindow main, ShellViewModel shell, FloatingOrbViewModel viewModel, Func<FloatingOrbViewModel, FloatingOrbWindow>? createWindow = null, IFloatingWindowStyle? style = null, Func<FloatingOrbWindow, nint>? nativeHandle = null)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(viewModel);
        _main = main;
        _shell = shell;
        ViewModel = viewModel;
        _createWindow = createWindow ?? (static vm => new FloatingOrbWindow { DataContext = vm });
        _style = style;
        _nativeHandle = nativeHandle ?? (static window => window.TryGetPlatformHandle()?.Handle ?? 0);

        main.PropertyChanged += OnMainWindowChanged;
        shell.PropertyChanged += OnShellChanged;
        viewModel.PropertyChanged += OnViewModelChanged;
        main.Closed += OnMainClosed;
        Refresh();
    }

    public FloatingOrbViewModel ViewModel { get; }

    /// <summary>The orb's window, or <c>null</c> until it was needed for the first time (for tests).</summary>
    internal FloatingOrbWindow? Window => _window;

    /// <summary>Wires the floating orb of the app to the shell's Recording screen and recorder; the window is created lazily.</summary>
    public static FloatingOrbController Attach(MainWindow main, ShellViewModel shell, AppServices services)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(services);
        var actions = new FloatingOrbActions(
            PauseResume: shell.TogglePauseFromHotkeyAsync,
            Stop: shell.Recording.StopAsync,
            Open: () => ShowRecording(main, shell));
        var viewModel = new FloatingOrbViewModel(services.Recorder, actions, services.Marks, services.Settings, services.Motion, services.Time);
        return new FloatingOrbController(main, shell, viewModel, style: services.FloatingWindowStyle);
    }

    /// <summary>The Open button: the main window comes to the front on the Recording screen.</summary>
    private static void ShowRecording(MainWindow main, ShellViewModel shell)
    {
        if (!ReferenceEquals(shell.CurrentPage, shell.Recording))
        {
            shell.ShowRecording();
        }

        main.BringToFront();
    }

    /// <summary>
    /// The orb is not needed: the main window shows the Recording screen right now, or the onboarding is the page (whatever the state of
    /// the window: its trial recording is the onboarding's own business, never the orb's).
    /// </summary>
    internal bool IsRecorderInView =>
        _shell.CurrentPage is OnboardingViewModel
        || (_main.IsVisible && _main.WindowState != WindowState.Minimized && _shell.CurrentPage is RecordingViewModel);

    private void OnMainWindowChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty || e.Property == Avalonia.Controls.Window.WindowStateProperty)
        {
            Refresh();
        }
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentPage))
        {
            Refresh();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FloatingOrbViewModel.IsOrbVisible))
        {
            Apply();
        }
    }

    private void Refresh()
    {
        ViewModel.IsRecorderInView = IsRecorderInView;
        Apply();
    }

    /// <summary>Shows the orb's window while the view model wants it, hides it otherwise.</summary>
    private void Apply()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (ViewModel.IsOrbVisible)
        {
            _window ??= CreateWindow();
            _window.ShowOrb();
        }
        else
        {
            _window?.HideOrb();
        }
    }

    private FloatingOrbWindow CreateWindow()
    {
        var window = _createWindow(ViewModel);
        window.Closed += OnWindowClosed;
        window.Opened += OnWindowOpened;
        window.PropertyChanged += OnWindowPropertyChanged;
        window.InputRegionChanged += OnInputRegionChanged;

        // As soon as the window exists: clicks on the orb must never take focus from the call, and it has no Alt+Tab entry. Avalonia
        // rewrites the extended style on Show and on later property changes; the platform side keeps the bits through those rewrites,
        // and it is applied again after the show and on every change that rewrites the style (below), in case one slipped past.
        ApplyStyle(window);
        return window;
    }

    /// <summary>Applies the non-activating style to the window's native handle (idempotent; nothing without a platform style or handle).</summary>
    private void ApplyStyle(FloatingOrbWindow window)
    {
        if (_style is not null && _nativeHandle(window) is var handle && handle != 0)
        {
            _style.MakeNonActivating(handle);
        }
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        if (sender is FloatingOrbWindow window)
        {
            ApplyStyle(window);
            ApplyInputRegion(window);
        }
    }

    private void OnInputRegionChanged(object? sender, EventArgs e)
    {
        if (sender is FloatingOrbWindow window)
        {
            ApplyInputRegion(window);
        }
    }

    /// <summary>
    /// Only the shapes that are shown take clicks; the transparent rest of the fixed-size window lets them through to the window below.
    /// Nothing before the first layout (no region yet: the whole window would take input, as without a platform style).
    /// </summary>
    private void ApplyInputRegion(FloatingOrbWindow window)
    {
        if (_style is not null && window.InputRegion.Length > 0 && _nativeHandle(window) is var handle && handle != 0)
        {
            _style.SetInputRegion(handle, window.InputRegion);
        }
    }

    /// <summary>Avalonia rebuilds the window's style when it is shown again, when Topmost changes and when the window is resized.</summary>
    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is FloatingOrbWindow window
            && (e.Property == Avalonia.Controls.Window.TopmostProperty
                || e.Property == TopLevel.ClientSizeProperty
                || (e.Property == Visual.IsVisibleProperty && window.IsVisible)))
        {
            ApplyStyle(window);
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is FloatingOrbWindow closed)
        {
            closed.Opened -= OnWindowOpened;
            closed.PropertyChanged -= OnWindowPropertyChanged;
            closed.InputRegionChanged -= OnInputRegionChanged;
            if (ReferenceEquals(closed, _window))
            {
                _window = null;
            }
        }
    }

    private void OnMainClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _main.PropertyChanged -= OnMainWindowChanged;
        _main.Closed -= OnMainClosed;
        _shell.PropertyChanged -= OnShellChanged;
        ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel.Dispose();
        if (_window is { } window)
        {
            _window = null;
            window.Closed -= OnWindowClosed;
            window.Opened -= OnWindowOpened;
            window.PropertyChanged -= OnWindowPropertyChanged;
            window.InputRegionChanged -= OnInputRegionChanged;
            window.Close();
        }
    }
}
