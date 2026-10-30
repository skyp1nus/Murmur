using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Murmur.App.Services;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

public partial class SessionView : UserControl
{
    /// <summary>Position poll while playing: about 30 Hz. The timer does not exist as a running object while idle.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(33);

    /// <summary>Where the current segment is placed in the viewport: this fraction of its height from the top.</summary>
    private const double ReadingLine = 0.2;

    private readonly DispatcherTimer _timer;
    private SessionViewModel? _viewModel;

    public SessionView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _timer.Tick += OnTick;

        Waveform.ScrubStarted += _ => _viewModel?.BeginScrub();
        Waveform.Scrubbed += fraction => _viewModel?.ScrubTo(fraction);
        Waveform.ScrubCompleted += fraction => _viewModel?.EndScrub(fraction);

        // Wheel and scroll-bar drags are the user's scrolls; programmatic ScrollIntoView raises neither.
        TranscriptScroller.AddHandler(PointerWheelChangedEvent, OnUserScroll, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        TranscriptScroller.AddHandler(PointerPressedEvent, OnTranscriptPointerPressed, RoutingStrategies.Tunnel);

        // The "back" button of a mouse goes back, like Esc.
        AddHandler(PointerReleasedEvent, OnPointerReleasedForBack, RoutingStrategies.Tunnel);

        // The right column is as tall as the window leaves room for, so the transcript list scrolls inside it (and stays virtualised).
        PageScroller.PropertyChanged += OnPageScrollerPropertyChanged;
    }

    private void OnPointerReleasedForBack(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.XButton1 && _viewModel is { } viewModel)
        {
            viewModel.BackCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPageScrollerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.ViewportProperty)
        {
            var viewport = PageScroller.Viewport.Height;
            var padding = (Page.Parent as Border)?.Padding ?? default;
            Page.FillHeight = viewport > 0 ? viewport - padding.Top - padding.Bottom : double.NaN;
        }
    }

    /// <summary>True while the position timer runs (for tests).</summary>
    internal bool IsPollTimerRunning => _timer.IsEnabled;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.ScrollToSegmentRequested -= OnScrollToSegment;
        }

        _viewModel = DataContext as SessionViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.ScrollToSegmentRequested += OnScrollToSegment;

            // The export dialog needs the window this view is shown in.
            _viewModel.FileSaver ??= new TopLevelFileSaver(() => TopLevel.GetTopLevel(this));
            _viewModel.RefreshExport();
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
        if (e.PropertyName == nameof(SessionViewModel.IsPlaying))
        {
            UpdateTimer();
        }
    }

    private void UpdateTimer()
    {
        if (_viewModel is { IsPlaying: true } && IsLoaded)
        {
            _viewModel.Tick();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void OnTick(object? sender, EventArgs e) => _viewModel?.Tick();

    /// <summary>
    /// Brings the segment into view (which also realises its row), then, once laid out, moves it to about a fifth
    /// of the way down so the reader sees what was just said and what comes next.
    /// </summary>
    private void OnScrollToSegment(int index)
    {
        if (index < 0 || index >= Transcript.ItemCount)
        {
            return;
        }

        Transcript.ScrollIntoView(index);
        Dispatcher.UIThread.Post(() => AlignSegment(index), DispatcherPriority.Render);
    }

    private void AlignSegment(int index)
    {
        if (Transcript.ContainerFromIndex(index) is not Control row
            || row.TranslatePoint(default, TranscriptScroller) is not { } top)
        {
            return;
        }

        var wanted = TranscriptScroller.Viewport.Height * ReadingLine;
        var offset = TranscriptScroller.Offset;
        TranscriptScroller.Offset = new Vector(offset.X, Math.Max(0, offset.Y + top.Y - wanted));
    }

    private void OnUserScroll(object? sender, RoutedEventArgs e) => _viewModel?.NotifyUserScrolled();

    private void OnTranscriptPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null)
        {
            _viewModel?.NotifyUserScrolled();
        }
    }
}
