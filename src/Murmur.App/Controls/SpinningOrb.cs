using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Murmur.App.Controls;

/// <summary>
/// The small orb of the status bar and the Activity popover: an <see cref="OrbDisc"/> that turns once in nine seconds while a
/// recording runs. It is a recording orb, so it may animate; it does so at 15 Hz (a 12 px disc needs no more), while it is
/// attached (every second only while hidden), and holds still (angle 0, no timer) under <see cref="Motion.ReduceMotionProperty"/>.
/// </summary>
public sealed class SpinningOrb : Decorator
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000.0 / 15);

    /// <summary>While the orb is hidden (no recording) the timer only looks now and then whether it can be seen again.</summary>
    private static readonly TimeSpan HiddenInterval = TimeSpan.FromSeconds(1);

    /// <summary>A full turn takes nine seconds, like the mockup.</summary>
    private const double DegreesPerSecond = 360.0 / 9;

    private readonly DispatcherTimer _timer;
    private readonly RotateTransform _rotation = new();
    private TimeProvider _clock = TimeProvider.System;
    private long _last;

    public SpinningOrb()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Child = new OrbDisc();
        RenderTransform = _rotation;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = FrameInterval };
        _timer.Tick += (_, _) => Step();
    }

    /// <summary>The frame timer runs (for tests); it is slow while the orb is hidden.</summary>
    internal bool IsSpinning => _timer.IsEnabled;

    /// <summary>The current angle in degrees (for tests).</summary>
    internal double Angle => _rotation.Angle;

    internal TimeProvider Clock
    {
        get => _clock;
        set
        {
            _clock = value;
            _last = value.GetTimestamp();
        }
    }

    /// <summary>Advances the angle by the time since the last step (the timer calls it; tests call it with their own clock).</summary>
    internal void Step()
    {
        if (!IsEffectivelyVisible)
        {
            _timer.Interval = HiddenInterval;
            _last = _clock.GetTimestamp();
            return;
        }

        if (_timer.Interval != FrameInterval)
        {
            _timer.Interval = FrameInterval;
            _last = _clock.GetTimestamp();
            return;
        }

        var now = _clock.GetTimestamp();
        var seconds = _clock.GetElapsedTime(_last, now).TotalSeconds;
        _last = now;
        _rotation.Angle = (_rotation.Angle + (seconds * DegreesPerSecond)) % 360;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsNaN(Width) ? 0 : Width, double.IsNaN(Height) ? 0 : Height);

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
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
        else if (change.Property == Motion.ReduceMotionProperty)
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
        var run = ((Visual)this).IsAttachedToVisualTree() && IsEffectivelyVisible && !Motion.GetReduceMotion(this);
        if (run == _timer.IsEnabled)
        {
            if (!run && _rotation.Angle != 0 && Motion.GetReduceMotion(this))
            {
                _rotation.Angle = 0;
            }

            return;
        }

        if (run)
        {
            _last = _clock.GetTimestamp();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            if (Motion.GetReduceMotion(this))
            {
                _rotation.Angle = 0;
            }
        }
    }
}
