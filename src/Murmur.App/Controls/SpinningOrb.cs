using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;

namespace Murmur.App.Controls;

/// <summary>
/// The small orb of the status bar and the Activity popover: an <see cref="OrbDisc"/> that turns once in nine seconds (the canvas:
/// <c>spin 9s linear infinite</c>) while a recording runs. It turns only while <see cref="IsActive"/>, attached, seen and with motion
/// allowed (<see cref="MotionGate"/>); otherwise it holds still at angle 0.
/// </summary>
/// <remarks>
/// The turn is a compositor animation of the <c>RotationAngle</c> of the orb's composition visual that repeats forever
/// (<see cref="CompositionLoop"/>): it is started once, runs on the render thread at the display rate, and the UI thread does nothing
/// per frame. When the orb stops, the angle is written back to 0, which also ends the animation. A test clock (<see cref="Clock"/>)
/// replaces the animation with frames that <see cref="Tick"/> computes, so a board or a test always draws the same angle.
/// </remarks>
public sealed class SpinningOrb : Decorator
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<SpinningOrb, bool>(nameof(IsActive));

    /// <summary>A full turn takes nine seconds, like the mockup.</summary>
    internal static readonly TimeSpan Period = TimeSpan.FromSeconds(9);

    private readonly MotionGate _gate;
    private TimeProvider _clock = TimeProvider.System;
    private bool _manual;
    private bool _looping;
    private long _start;

    // The size of the last arrange: Bounds is only updated after ArrangeOverride returns, so a loop started from there must not read it.
    private Size _arranged;

    public SpinningOrb()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Child = new OrbDisc();
        _gate = new MotionGate(this, OnGateChanged);
    }

    /// <summary>The orb belongs to something that is running (a recording): it turns. Bind it to the state; it stays still otherwise.</summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>The orb turns: active, attached, seen and with motion allowed (for tests).</summary>
    internal bool IsSpinning => _gate.IsOpen;

    /// <summary>The compositor animation runs (for tests): the orb turns and no test clock drives it.</summary>
    internal bool IsLooping => _looping;

    /// <summary>The gate's slow visibility check (for tests).</summary>
    internal MotionGate Gate => _gate;

    /// <summary>The angle of the composition visual in radians (for tests); <c>null</c> without one. It stays 0 while the compositor turns it.</summary>
    internal float? Angle => ElementComposition.GetElementVisual(this)?.RotationAngle;

    /// <summary>
    /// The clock of the turn; the system clock unless a test replaces it. A new clock restarts the turn, and any clock other than the
    /// system one stops the compositor animation: the orb then shows the angle <see cref="Tick"/> computes for that clock.
    /// </summary>
    internal TimeProvider Clock
    {
        get => _clock;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _clock = value;
            _start = value.GetTimestamp();
            _manual = !ReferenceEquals(value, TimeProvider.System);
            if (_manual)
            {
                StopLoop();
            }
            else if (_gate.IsOpen)
            {
                StartLoop();
            }
        }
    }

    /// <summary>The angle in radians at <paramref name="seconds"/> after the start: one linear turn per <see cref="Period"/>, in [0, 2π).</summary>
    internal static double AngleAt(double seconds)
    {
        var turns = seconds / Period.TotalSeconds;
        return (turns - Math.Floor(turns)) * (2 * Math.PI);
    }

    /// <summary>One frame of a test clock: the angle for the time since the start. Does nothing while the compositor turns the orb.</summary>
    internal void Tick()
    {
        if (!_looping && ElementComposition.GetElementVisual(this) is { } visual)
        {
            visual.RotationAngle = (float)AngleAt(_clock.GetElapsedTime(_start).TotalSeconds);
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsNaN(Width) ? 0 : Width, double.IsNaN(Height) ? 0 : Height);

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        _arranged = finalSize;
        if (ElementComposition.GetElementVisual(this) is { } visual)
        {
            visual.CenterPoint = new Vector3D(finalSize.Width / 2, finalSize.Height / 2, 0);
            if (_gate.IsOpen && !_manual && !_looping)
            {
                StartLoop();
            }
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
        else if (change.Property == IsActiveProperty || change.Property == Motion.ReduceMotionProperty || change.Property == IsVisibleProperty)
        {
            _gate.Update(IsActive);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _gate.Update(IsActive);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _gate.Detach();
    }

    private void OnGateChanged(bool open)
    {
        if (open)
        {
            _start = _clock.GetTimestamp();
            if (!_manual)
            {
                StartLoop();
            }
        }
        else
        {
            StopLoop();
            if (ElementComposition.GetElementVisual(this) is { } visual)
            {
                CompositionLoop.HoldAngle(visual, 0);
            }
        }
    }

    /// <summary>Starts the forever turn on the compositor, once the composition visual exists (otherwise the next arrange does).</summary>
    private void StartLoop()
    {
        if (_looping || ElementComposition.GetElementVisual(this) is not { } visual)
        {
            return;
        }

        visual.CenterPoint = new Vector3D(_arranged.Width / 2, _arranged.Height / 2, 0);
        CompositionLoop.StartSpin(visual, Period);
        _looping = true;
    }

    /// <summary>Ends the compositor turn at angle 0.</summary>
    private void StopLoop()
    {
        if (!_looping)
        {
            return;
        }

        _looping = false;
        if (ElementComposition.GetElementVisual(this) is { } visual)
        {
            CompositionLoop.HoldAngle(visual, 0);
        }
    }
}
