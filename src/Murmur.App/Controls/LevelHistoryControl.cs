using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Murmur.App.Controls;

/// <summary>
/// The level strip of a track in the Tracks card: <see cref="LevelHistory.PastCount"/> rounded bars for the recent levels
/// (<see cref="PastBrush"/>, the newest at the right end of them) followed by <see cref="LevelHistory.FutureCount"/> light
/// stubs (<see cref="FutureBrush"/>) for what has not been recorded yet, 3 px apart, filling the width, 4 to 32 px high and
/// centred in the control (the canvas draws 36 bars of a 36 px strip the same way).
/// </summary>
/// <remarks>
/// The bars are drawn on the render thread by a composition visual (<see cref="LevelBarsHost"/>): every new sample of the history
/// slides the strip one bar to the left over one sample interval, at the display rate, and the newest bar fades and grows in at the
/// boundary of the stubs, so the strip scrolls continuously instead of jumping. At rest the frame is exactly the static strip. The
/// UI thread only hands each sample over (no drawing, no allocation); under reduced motion, while hidden, or for a burst of samples
/// the strip shows the new values at rest at once and the render loop is not woken.
/// </remarks>
public sealed class LevelHistoryControl : Control
{
    public static readonly StyledProperty<LevelHistory?> HistoryProperty =
        AvaloniaProperty.Register<LevelHistoryControl, LevelHistory?>(nameof(History));

    public static readonly StyledProperty<IBrush?> PastBrushProperty =
        AvaloniaProperty.Register<LevelHistoryControl, IBrush?>(nameof(PastBrush));

    public static readonly StyledProperty<IBrush?> FutureBrushProperty =
        AvaloniaProperty.Register<LevelHistoryControl, IBrush?>(nameof(FutureBrush));

    /// <summary>The track is not recording: every bar is a light stub.</summary>
    public static readonly StyledProperty<bool> IsIdleProperty =
        AvaloniaProperty.Register<LevelHistoryControl, bool>(nameof(IsIdle));

    /// <summary>The strip: 26 past bars and 10 stubs, 3 px apart, 4 to 32 px high, one bar per Recording poll (250 ms).</summary>
    internal static readonly LevelBarsStyle Style = new(
        LevelBarsShape.History,
        LevelHistory.PastCount,
        LevelHistory.FutureCount,
        Gap: 3,
        MinHeight: 4,
        MaxHeight: 32,
        IdleHeight: 4,
        SampleSeconds: 0.25);

    private readonly LevelBarsHost _bars;
    private readonly LevelBar[] _staticBars = new LevelBar[Style.MaxRects];
    private LevelHistory? _subscribed;

    static LevelHistoryControl()
    {
        AffectsRender<LevelHistoryControl>(PastBrushProperty, FutureBrushProperty, IsIdleProperty);
        HistoryProperty.Changed.AddClassHandler<LevelHistoryControl>((control, e) => control.OnHistoryChanged(e.NewValue as LevelHistory));
    }

    public LevelHistoryControl()
    {
        _bars = new LevelBarsHost(this, Style);
    }

    public LevelHistory? History
    {
        get => GetValue(HistoryProperty);
        set => SetValue(HistoryProperty, value);
    }

    public IBrush? PastBrush
    {
        get => GetValue(PastBrushProperty);
        set => SetValue(PastBrushProperty, value);
    }

    public IBrush? FutureBrush
    {
        get => GetValue(FutureBrushProperty);
        set => SetValue(FutureBrushProperty, value);
    }

    public bool IsIdle
    {
        get => GetValue(IsIdleProperty);
        set => SetValue(IsIdleProperty, value);
    }

    /// <summary>The bars' composition visual (for tests).</summary>
    internal LevelBarsHost Bars => _bars;

    /// <summary>Height of the bar for a normalised value (for tests): whole pixels from 4 to 32, like the canvas.</summary>
    public static double BarHeight(float value) => LevelBarsMotion.HistoryHeight(Style, value);

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, double.IsInfinity(availableSize.Height) ? Style.MaxHeight : availableSize.Height);

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        _bars.Resize(size);
        return size;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(History);
        _bars.Reset(History is { } history ? history.Values : default);
        _bars.SetLook(PastBrush, FutureBrush, IsIdle);
        _bars.Attach();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Subscribe(null);
        _bars.Detach();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PastBrushProperty || change.Property == FutureBrushProperty || change.Property == IsIdleProperty)
        {
            _bars?.SetLook(PastBrush, FutureBrush, IsIdle);
        }
    }

    /// <summary>Draws the static strip only when no composition visual does (a renderer without a compositor).</summary>
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_bars.HasVisual)
        {
            return;
        }

        var count = _bars.Rest.Layout(_staticBars, Bounds.Width, Bounds.Height, position: 1, IsIdle);
        for (var i = 0; i < count; i++)
        {
            var bar = _staticBars[i];
            if ((bar.Second ? FutureBrush : PastBrush) is { } brush)
            {
                context.DrawRectangle(brush, null, new Rect(bar.X, bar.Y, bar.Width, bar.Height), bar.Radius, bar.Radius);
            }
        }
    }

    private void OnHistoryChanged(LevelHistory? history)
    {
        if (((Visual)this).IsAttachedToVisualTree())
        {
            Subscribe(history);
            _bars.Reset(history is { } h ? h.Values : default);
        }

        InvalidateVisual();
    }

    /// <summary>A sample was pushed (it slides in) or the history was cleared (the strip shows it at once).</summary>
    private void OnHistoryUpdated()
    {
        if (_subscribed is not { } history)
        {
            return;
        }

        if (history.LastChangeWasPush)
        {
            _bars.Push(history.Values[^1], animate: !Motion.GetReduceMotion(this) && MotionGate.IsShown(this));
        }
        else
        {
            _bars.Reset(history.Values);
        }

        if (!_bars.HasVisual)
        {
            InvalidateVisual();
        }
    }

    private void Subscribe(LevelHistory? history)
    {
        if (ReferenceEquals(_subscribed, history))
        {
            return;
        }

        if (_subscribed is not null)
        {
            _subscribed.Changed -= OnHistoryUpdated;
        }

        _subscribed = history;
        if (history is not null)
        {
            history.Changed += OnHistoryUpdated;
        }
    }
}
