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
/// <remarks>It redraws only when <see cref="History"/> changes (a few times per second), not per frame.</remarks>
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

    private const double Gap = 3;
    private const double MinBarHeight = 4;
    private const double MaxBarHeight = 32;

    private LevelHistory? _subscribed;

    static LevelHistoryControl()
    {
        AffectsRender<LevelHistoryControl>(PastBrushProperty, FutureBrushProperty, IsIdleProperty);
        HistoryProperty.Changed.AddClassHandler<LevelHistoryControl>((control, e) => control.OnHistoryChanged(e.NewValue as LevelHistory));
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

    /// <summary>Height of the bar for a normalised value (for tests): whole pixels from 4 to 32, like the canvas.</summary>
    public static double BarHeight(float value) => Math.Round(MinBarHeight + (Math.Clamp(value, 0f, 1f) * (MaxBarHeight - MinBarHeight)));

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, double.IsInfinity(availableSize.Height) ? MaxBarHeight : availableSize.Height);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(History);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Subscribe(null);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        const int count = LevelHistory.PastCount + LevelHistory.FutureCount;
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var barWidth = (width - (Gap * (count - 1))) / count;
        if (barWidth <= 0)
        {
            return;
        }

        var radius = barWidth / 2;
        var values = History is { } history ? history.Values : default;
        for (var i = 0; i < count; i++)
        {
            var past = i < LevelHistory.PastCount && !IsIdle;
            var barHeight = past && i < values.Length ? BarHeight(values[i]) : MinBarHeight;
            var brush = past ? PastBrush : FutureBrush;
            if (brush is null)
            {
                continue;
            }

            var x = i * (barWidth + Gap);
            context.DrawRectangle(brush, null, new Rect(x, (height - barHeight) / 2, barWidth, barHeight), radius, radius);
        }
    }

    private void OnHistoryChanged(LevelHistory? history)
    {
        if (((Visual)this).IsAttachedToVisualTree())
        {
            Subscribe(history);
        }

        InvalidateVisual();
    }

    private void Subscribe(LevelHistory? history)
    {
        if (ReferenceEquals(_subscribed, history))
        {
            return;
        }

        if (_subscribed is not null)
        {
            _subscribed.Changed -= InvalidateVisual;
        }

        _subscribed = history;
        if (history is not null)
        {
            history.Changed += InvalidateVisual;
        }
    }
}
