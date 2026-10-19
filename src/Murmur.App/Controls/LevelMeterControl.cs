using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Murmur.App.Controls;

/// <summary>
/// The live level meter of the audio step: 40 thin bars that show the last two seconds of an <see cref="ILevelSource"/>
/// (the newest level on the right), in Ink on a quiet bar of Stone. Quiet bars stay at the idle height.
/// </summary>
/// <remarks>
/// The meter polls <see cref="LevelSource"/> from its own 20 Hz timer, only while it is <see cref="IsActive"/> and attached to
/// a window; an idle meter (no source, or silence that has already been drawn) requests no repaint, so it costs nothing.
/// The bars are drawn from two cached brushes and a preallocated level buffer: no allocation per update.
/// </remarks>
public sealed class LevelMeterControl : Control
{
    public static readonly StyledProperty<ILevelSource?> LevelSourceProperty =
        AvaloniaProperty.Register<LevelMeterControl, ILevelSource?>(nameof(LevelSource));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<LevelMeterControl, bool>(nameof(IsActive));

    /// <summary>Number of bars (and so of remembered levels).</summary>
    public const int BarCount = 40;

    private const double BarGap = 3;
    private const double IdleBarHeight = 3;
    private const double MinLiveBarHeight = 4;
    private const double UpdateSeconds = 0.05;

    /// <summary>How much of the previous bar a quieter new level keeps (a short release, so single peaks are readable).</summary>
    private const float Release = 0.7f;
    private const float Silence = 0.01f;

    private readonly float[] _levels = new float[BarCount];
    private readonly DispatcherTimer _timer;
    private IBrush? _live;
    private IBrush? _idle;
    private bool _dirty;
    private bool _hasLevels;

    static LevelMeterControl()
    {
        AffectsRender<LevelMeterControl>(IsActiveProperty);
    }

    public LevelMeterControl()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromSeconds(UpdateSeconds) };
        _timer.Tick += (_, _) => Tick();
        IsHitTestVisible = false;
        Focusable = false;
        ActualThemeVariantChanged += (_, _) => BuildBrushes();
    }

    /// <summary>Where the meter reads the level from, once per update.</summary>
    public ILevelSource? LevelSource
    {
        get => GetValue(LevelSourceProperty);
        set => SetValue(LevelSourceProperty, value);
    }

    /// <summary>True while the source is live: turns the polling on. When it turns off the bars fall silent.</summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>True while the meter runs its update timer (for tests).</summary>
    internal bool IsPolling => _timer.IsEnabled;

    /// <summary>The remembered levels, oldest first (for tests).</summary>
    internal ReadOnlySpan<float> Levels => _levels;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, double.IsNaN(Height) ? 0 : Height);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        BuildBrushes();
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty)
        {
            if (!change.GetNewValue<bool>())
            {
                Array.Clear(_levels);
                _hasLevels = false;
                _dirty = true;
                InvalidateVisual();
            }

            UpdateTimer();
        }
    }

    private void UpdateTimer()
    {
        var run = IsActive && ((Visual)this).IsAttachedToVisualTree();
        if (run && !_timer.IsEnabled)
        {
            _timer.Start();
        }
        else if (!run && _timer.IsEnabled)
        {
            _timer.Stop();
        }
    }

    /// <summary>One update: shifts the history and appends the current level (internal: tests drive it directly).</summary>
    internal void Tick()
    {
        var level = IsActive ? LevelSource?.Level ?? 0f : 0f;
        level = float.IsNaN(level) ? 0f : Math.Clamp(level, 0f, 1f);
        var newest = Math.Max(level, _levels[^1] * Release);
        if (newest < Silence)
        {
            newest = 0f;
        }

        var hadLevels = _hasLevels;
        Array.Copy(_levels, 1, _levels, 0, BarCount - 1);
        _levels[^1] = newest;
        _hasLevels = false;
        foreach (var value in _levels)
        {
            if (value > 0f)
            {
                _hasLevels = true;
                break;
            }
        }

        // Silence on screen and silence arriving: nothing changes, so nothing is repainted.
        if (!hadLevels && !_hasLevels && !_dirty)
        {
            return;
        }

        _dirty = false;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        if (_live is null || _idle is null || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var barWidth = Math.Max(1, (bounds.Width - (BarGap * (BarCount - 1))) / BarCount);
        for (var i = 0; i < BarCount; i++)
        {
            var level = _levels[i];
            var live = level > 0f;
            var height = live ? Math.Max(MinLiveBarHeight, level * bounds.Height) : IdleBarHeight;
            var rect = new Rect(i * (barWidth + BarGap), (bounds.Height - height) / 2, barWidth, height);
            context.DrawRectangle(live ? _live : _idle, null, new RoundedRect(rect, Math.Min(barWidth / 2, height / 2)));
        }

    }

    private void BuildBrushes()
    {
        _live = this.TryFindResource("Brush.Ink", ActualThemeVariant, out var ink) ? ink as IBrush : null;
        _idle = this.TryFindResource("Brush.Stone", ActualThemeVariant, out var stone) ? stone as IBrush : null;
        InvalidateVisual();
    }
}
