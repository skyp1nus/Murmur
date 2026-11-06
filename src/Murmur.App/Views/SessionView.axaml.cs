using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Murmur.App.Controls;
using Murmur.App.Services;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

public partial class SessionView : UserControl
{
    /// <summary>Position poll while playing: about 30 Hz. The timer does not exist as a running object while idle.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(33);

    /// <summary>Where the current line is placed in the viewport: this fraction of the viewport's height from the top (a third, #56).</summary>
    internal const double ReadingLine = 1.0 / 3;

    private readonly DispatcherTimer _timer;
    private SessionViewModel? _viewModel;
    private TopLevel? _keyHost;
    private bool _spaceHeld;

    public SessionView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _timer.Tick += OnTick;

        Waveform.ScrubStarted += _ => _viewModel?.BeginScrub();
        Waveform.Scrubbed += fraction => _viewModel?.ScrubTo(fraction);
        Waveform.ScrubCompleted += fraction => _viewModel?.EndScrub(fraction);

        BookmarkFlags.FlagActivated += index => _viewModel?.SeekToBookmarkFlag(index);

        // Wheel and scroll-bar drags are the user's scrolls; programmatic ScrollIntoView raises neither.
        TranscriptScroller.AddHandler(PointerWheelChangedEvent, OnUserScroll, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        TranscriptScroller.AddHandler(PointerPressedEvent, OnTranscriptPointerPressed, RoutingStrategies.Tunnel);
        TranscriptScroller.AddHandler(KeyDownEvent, OnTranscriptKeyDown, RoutingStrategies.Tunnel);
        // A touch or pen drag scrolls the list through this gesture; it is raised only by the user, never by a programmatic scroll or a tap.
        TranscriptScroller.AddHandler(ScrollGestureEvent, OnUserScroll, RoutingStrategies.Bubble, handledEventsToo: true);

        // The "back" button of a mouse goes back, like Esc.
        AddHandler(PointerReleasedEvent, OnPointerReleasedForBack, RoutingStrategies.Tunnel);

        // The right column is as tall as the window leaves room for, so the transcript list scrolls inside it (and stays virtualised).
        PageScroller.PropertyChanged += OnPageScrollerPropertyChanged;
    }

    /// <summary>
    /// Space and the arrows run before the controls see them (tunnel): a focused button would click on Space, and the list would scroll on
    /// Up and Down. The rest of the keys bubble (<see cref="OnShortcutKeyDown"/>), so an open popup that handles a key keeps it.
    /// </summary>
    private void OnShortcutKeyTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Up or Key.Down) || e.Handled || _viewModel is not { } viewModel || !IsForThisPage(e) || SessionShortcuts.IsBlocked(e, this, textBoxesBlock: true))
        {
            return;
        }

        // The key repeats while Space is held: one press is one toggle.
        if (e.Key == Key.Space && _spaceHeld)
        {
            e.Handled = true;
            return;
        }

        // A transcript line with the keyboard focus (the uncertain words, #58) keeps Space: it opens the word's popover.
        if (e.Key == Key.Space && SessionShortcuts.FocusedElement(this) is TranscriptTextControl)
        {
            return;
        }

        SessionShortcuts.Run(viewModel, e.Key);
        e.Handled = true;
        _spaceHeld |= e.Key == Key.Space;
    }

    /// <summary>
    /// The key was pressed on this page or on the window itself (nothing focused): not in the top bar, the sidebar or the status bar, whose
    /// buttons and flyouts keep their keys (an Esc that closes the "..." menu must not also go back).
    /// </summary>
    private bool IsForThisPage(KeyEventArgs e) =>
        e.Source is Visual source && (source is TopLevel || this.IsVisualAncestorOf(source));

    /// <summary>A button clicks on the release of Space: the release of a Space this router took is taken too.</summary>
    private void OnShortcutKeyUpTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _spaceHeld)
        {
            _spaceHeld = false;
            e.Handled = true;
        }
    }

    /// <summary>J, L, M, F and Esc: see <see cref="SessionShortcuts"/>.</summary>
    private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.J or Key.L or Key.M or Key.F or Key.Escape) || e.Handled || _viewModel is not { } viewModel || !IsForThisPage(e))
        {
            return;
        }

        // Esc is also the way back from the search box; it is only the text keys that a text box keeps.
        if (SessionShortcuts.IsBlocked(e, this, textBoxesBlock: e.Key != Key.Escape))
        {
            return;
        }

        SessionShortcuts.Run(viewModel, e.Key);
        e.Handled = true;
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

        // The keyboard shortcuts (#61): this is the one place the keys are bound. They listen on the window, not on this view: with nothing
        // focused the key goes to the window and never passes through the view.
        _keyHost = TopLevel.GetTopLevel(this);
        _keyHost?.AddHandler(KeyDownEvent, OnShortcutKeyTunnel, RoutingStrategies.Tunnel);
        _keyHost?.AddHandler(KeyUpEvent, OnShortcutKeyUpTunnel, RoutingStrategies.Tunnel);
        _keyHost?.AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Bubble);
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        _timer.Stop();
        _keyHost?.RemoveHandler(KeyDownEvent, OnShortcutKeyTunnel);
        _keyHost?.RemoveHandler(KeyUpEvent, OnShortcutKeyUpTunnel);
        _keyHost?.RemoveHandler(KeyDownEvent, OnShortcutKeyDown);
        _keyHost = null;
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
    /// Brings the segment into view (which also realises its row), then, once laid out, moves it to about a third
    /// of the way down so the reader sees what was just said and what comes next. The list always jumps there: nothing in the
    /// transcript animates per frame, so reduced motion needs no other path.
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

        var offset = TranscriptScroller.Offset;
        TranscriptScroller.Offset = new Vector(offset.X, AlignedOffset(offset.Y, top.Y, TranscriptScroller.Viewport.Height, TranscriptScroller.Extent.Height));
    }

    /// <summary>
    /// The scroll offset that puts a row, now <paramref name="rowTop"/> below the top of the viewport at scroll offset
    /// <paramref name="offset"/>, <see cref="ReadingLine"/> of the way down; never before the start or past the end of the list.
    /// </summary>
    internal static double AlignedOffset(double offset, double rowTop, double viewport, double extent) =>
        Math.Clamp(offset + rowTop - (viewport * ReadingLine), 0, Math.Max(0, extent - viewport));

    private void OnUserScroll(object? sender, RoutedEventArgs e) => _viewModel?.NotifyUserScrolled();

    /// <summary>The keys that scroll a list: the user moves the list by hand. (Up and Down are shortcuts that seek, #61: the seek scrolls to the line itself.)</summary>
    private void OnTranscriptKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.PageUp or Key.PageDown or Key.Home or Key.End && e.Source is not TextBox)
        {
            _viewModel?.NotifyUserScrolled();
        }
    }

    private void OnTranscriptPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null)
        {
            _viewModel?.NotifyUserScrolled();
        }
    }
}
