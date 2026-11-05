using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Recording;

namespace Murmur.App.Services;

/// <summary>
/// Connects the floating recorder orb (#46) to the app: tells its view model whether the main window is showing the Recording
/// screen, and shows or hides the orb's window when the view model says so. The window is created the first time it is needed (a
/// recording runs while the main window is away), never at start-up.
/// </summary>
/// <remarks>
/// "Showing the Recording screen" is: the main window is visible, not minimised, and the page is the Recording screen (or the
/// onboarding, whose own trial recording the orb has nothing to add to). The buttons do what the Recording screen's do: pause and
/// stop go through the same view model and recorder, Open brings the main window to the front on the Recording screen.
/// </remarks>
internal sealed class FloatingOrbController : IDisposable
{
    private readonly MainWindow _main;
    private readonly ShellViewModel _shell;
    private readonly Func<FloatingOrbViewModel, FloatingOrbWindow> _createWindow;
    private FloatingOrbWindow? _window;
    private int _disposed;

    internal FloatingOrbController(MainWindow main, ShellViewModel shell, FloatingOrbViewModel viewModel, Func<FloatingOrbViewModel, FloatingOrbWindow>? createWindow = null)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(viewModel);
        _main = main;
        _shell = shell;
        ViewModel = viewModel;
        _createWindow = createWindow ?? (static vm => new FloatingOrbWindow { DataContext = vm });

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
        return new FloatingOrbController(main, shell, viewModel);
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

    /// <summary>The main window shows the Recording screen right now.</summary>
    internal bool IsRecorderInView =>
        _main.IsVisible
        && _main.WindowState != WindowState.Minimized
        && _shell.CurrentPage is RecordingViewModel or OnboardingViewModel;

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
        return window;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is FloatingOrbWindow closed && ReferenceEquals(closed, _window))
        {
            _window = null;
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
            window.Close();
        }
    }
}
