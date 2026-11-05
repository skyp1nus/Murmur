using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Murmur.App.Controls;

/// <summary>
/// The small orb of the status bar and the Activity popover: an <see cref="OrbDisc"/> that turns once in nine seconds while a
/// recording runs. It turns only while <see cref="IsActive"/>, attached and with motion allowed; otherwise it holds still at angle 0.
/// </summary>
/// <remarks>
/// Like <see cref="OrbControl"/> it does not use the 60 Hz animation-frame loop (the bar is on screen for a whole recording, and the
/// frame loop alone costs CPU): a 15 Hz timer sets the <c>RotationAngle</c> of the composition visual, so there is no layout, no
/// drawing command and no allocation per tick. The timer runs only while active, attached and with motion allowed.
/// </remarks>
public sealed class SpinningOrb : Decorator
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<SpinningOrb, bool>(nameof(IsActive));

    /// <summary>A full turn takes nine seconds, like the mockup.</summary>
    internal static readonly TimeSpan Period = TimeSpan.FromSeconds(9);

    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000.0 / 15);

    private readonly DispatcherTimer _timer;
    private TimeProvider _clock = TimeProvider.System;
    private long _start;

    public SpinningOrb()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Child = new OrbDisc();
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = FrameInterval };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>The orb belongs to something that is running (a recording): it turns. Bind it to the state; it stays still otherwise.</summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>The 15 Hz timer runs (for tests).</summary>
    internal bool IsSpinning => _timer.IsEnabled;

    /// <summary>The angle of the composition visual in radians (for tests); <c>null</c> without one.</summary>
    internal float? Angle => ElementComposition.GetElementVisual(this)?.RotationAngle;

    /// <summary>The clock of the turn; the system clock unless a test replaces it. A new clock restarts the turn.</summary>
    internal TimeProvider Clock
    {
        get => _clock;
        set
        {
            _clock = value;
            _start = value.GetTimestamp();
        }
    }

    /// <summary>One step: the angle for the time since the start (nine seconds per turn).</summary>
    internal void Tick()
    {
        if (ElementComposition.GetElementVisual(this) is { } visual)
        {
            var turns = _clock.GetElapsedTime(_start).TotalSeconds / Period.TotalSeconds;
            visual.RotationAngle = (float)((turns - Math.Floor(turns)) * (2 * Math.PI));
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsNaN(Width) ? 0 : Width, double.IsNaN(Height) ? 0 : Height);

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        if (ElementComposition.GetElementVisual(this) is { } visual)
        {
            visual.CenterPoint = new Vector3D(finalSize.Width / 2, finalSize.Height / 2, 0);
        }

        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WidthProperty || change.Property == HeightProperty)
        {
            if (Child is { } child)
            {
                child.Width = Width;
                child.Height = Height;
            }
        }
        else if (change.Property == IsActiveProperty || change.Property == Motion.ReduceMotionProperty)
        {
            Update();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Update();
    }

    private void Update()
    {
        var run = IsActive && !Motion.GetReduceMotion(this) && ((Visual)this).IsAttachedToVisualTree();
        if (run == _timer.IsEnabled)
        {
            return;
        }

        if (run)
        {
            _start = _clock.GetTimestamp();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            if (ElementComposition.GetElementVisual(this) is { } visual)
            {
                visual.RotationAngle = 0;
            }
        }
    }
}
