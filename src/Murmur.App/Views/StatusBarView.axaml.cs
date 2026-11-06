using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

public partial class StatusBarView : UserControl
{
    private static readonly TimeSpan ElapsedInterval = TimeSpan.FromMilliseconds(250);

    private readonly DispatcherTimer _timer;
    private StatusBarViewModel? _viewModel;

    public StatusBarView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ElapsedInterval };
        _timer.Tick += (_, _) => _viewModel?.Refresh();
    }

    /// <summary>The elapsed-time poll runs (for tests).</summary>
    internal bool IsPolling => _timer.IsEnabled;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as StatusBarViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
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

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StatusBarViewModel.IsRecording))
        {
            UpdateTimer();
        }
    }

    /// <summary>The once-a-second read of the elapsed time runs only while the bar shows a recording and is on screen.</summary>
    private void UpdateTimer()
    {
        if (_viewModel is { IsRecording: true } && IsLoaded)
        {
            _viewModel.Refresh();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }
}
