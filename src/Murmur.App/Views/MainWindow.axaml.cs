using Avalonia;
using Avalonia.Controls;
using Murmur.App.ViewModels;
using Murmur.Core.Recording;

namespace Murmur.App.Views;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;
    private Task? _stopping;
    private WindowState _restoreState = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Completes when the stop-then-close sequence has finished (for tests); <c>null</c> if it never started.</summary>
    internal Task? StoppingTask => _stopping;

    /// <summary>
    /// Closing the window never abandons a recording: while the recorder is not idle (starting, recording or stopping)
    /// every close request is cancelled; the first one starts "settle, stop, flush, close". The window closes itself
    /// only after the recorder is back to idle, so the process cannot exit while files and the session row are written.
    /// </summary>
    /// <summary>
    /// Brings the window back: shows it when hidden, restores it when minimized (to Normal or Maximized, as it was before) and
    /// activates it. Windows may refuse to move another program's window to the foreground while the user works elsewhere
    /// (the foreground lock); then the taskbar button flashes, which is as far as <see cref="Window.Activate"/> goes.
    /// </summary>
    public void BringToFront()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = _restoreState;
        }

        Activate();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && change.GetNewValue<WindowState>() != WindowState.Minimized)
        {
            _restoreState = change.GetNewValue<WindowState>();
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || DataContext is not ShellViewModel shell)
        {
            return;
        }

        if (_stopping is null && shell.Recording.State == RecorderState.Idle)
        {
            return;
        }

        e.Cancel = true;
        _stopping ??= StopThenCloseAsync(shell);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // The audio meters and the trial recording of the onboarding never outlive the window.
        (DataContext as ShellViewModel)?.OnWindowClosed();
    }

    private async Task StopThenCloseAsync(ShellViewModel shell)
    {
        try
        {
            // Starting settles into Recording (or back to Idle if the start fails).
            while (shell.Recording.State == RecorderState.Starting)
            {
                await Task.Delay(25);
            }

            await shell.Recording.StopAsync();

            // A stop already in flight (user pressed Stop just before) is awaited, not repeated.
            while (shell.Recording.State == RecorderState.Stopping)
            {
                await Task.Delay(25);
            }
        }
        finally
        {
            _closeConfirmed = true;
            Close();
        }
    }
}
