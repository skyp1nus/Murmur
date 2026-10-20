using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

public partial class RecordingView : UserControl
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly DispatcherTimer _timer;
    private RecordingViewModel? _viewModel;

    public RecordingView()
    {
        InitializeComponent();
        Focusable = true; // the title field hands the keyboard back to the page
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _timer.Tick += OnTick;
        TitleBox.KeyDown += OnTitleKeyDown;
        TitleBox.LostFocus += OnTitleLostFocus;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.ClipboardWriter = null;
        }

        _viewModel = DataContext as RecordingViewModel;
        Orb.LevelSource = _viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.ClipboardWriter = WriteToClipboardAsync;
        }

        UpdateTimer();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        UpdateTimer();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        _timer.Stop();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecordingViewModel.IsRunning))
        {
            UpdateTimer();
        }
    }

    /// <summary>The ~4 Hz poll of elapsed time and levels runs only while recording (or paused) and while the screen is visible.</summary>
    private void UpdateTimer()
    {
        if (_viewModel is { IsRunning: true } && IsLoaded)
        {
            _viewModel.Refresh();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void OnTick(object? sender, EventArgs e) => _viewModel?.Refresh();

    /// <summary>Enter keeps the title, Esc gives it back; either way the field lets go of the keyboard.</summary>
    private void OnTitleKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || e.Key is not (Key.Enter or Key.Escape))
        {
            return;
        }

        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            _viewModel.CancelTitle();
        }

        // Leaving the field commits what it says now (nothing, after Esc).
        Focus();
    }

    private void OnTitleLostFocus(object? sender, RoutedEventArgs e) => _ = _viewModel?.CommitTitleAsync();

    private async Task<bool> WriteToClipboardAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return false;
        }

        await clipboard.SetTextAsync(text);
        return true;
    }
}
