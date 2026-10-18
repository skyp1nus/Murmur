using Avalonia.Controls;
using Murmur.App.ViewModels;
using Murmur.Core.Recording;

namespace Murmur.App.Views;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;
    private Task? _stopping;

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
