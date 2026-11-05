using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Murmur.App.Controls;

/// <summary>
/// The moving orb of the floating recorder (#46): a <see cref="GlassOrbControl"/> that turns once in nine seconds, and behind it a ring
/// (a copy of the orb as a plain <see cref="OrbDisc"/>, so no halo is clipped) that goes out every three seconds, grows to 1.9 times
/// its size and fades. Both move only while <see cref="IsActive"/>, attached and with motion allowed
/// (<see cref="Motion.ReduceMotionProperty"/>); otherwise the orb holds still at angle 0 and the ring is not seen.
/// </summary>
/// <remarks>
/// <para>Like <see cref="SpinningOrb"/> and <see cref="OrbControl"/> it does not use compositor animations that run forever or the 60 Hz
/// animation-frame loop (an orb that stays on screen for a whole call must cost almost nothing): one 15 Hz timer, which runs only while
/// the orb moves, sets the <c>RotationAngle</c> of the orb's composition visual and the <c>Scale</c> and <c>Opacity</c> of the ring's. A
/// tick does no layout, no drawing and no allocation. The ring is out for the first 60 % of its three seconds; for the rest nothing is
/// written after the ring's rest frame, so the compositor has nothing to do for 1.2 s of every 3 s.</para>
/// <para>The orb carries no timer or other readout: "no timer" in the issue means no elapsed-time text on it.</para>
/// </remarks>
public sealed class FloatingOrbDisc : Panel
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<FloatingOrbDisc, bool>(nameof(IsActive));

    /// <summary>A full turn takes nine seconds.</summary>
    internal static readonly TimeSpan SpinPeriod = TimeSpan.FromSeconds(FloatingOrbMotion.SpinSeconds);

    /// <summary>A ping goes out every three seconds.</summary>
    internal static readonly TimeSpan PingPeriod = TimeSpan.FromSeconds(FloatingOrbMotion.PingSeconds);

    /// <summary>The ring ends at 1.9 times the orb.</summary>
    internal const double PingScale = FloatingOrbMotion.PingScale;

    /// <summary>The ring's opacity when it starts.</summary>
    internal const double PingOpacity = FloatingOrbMotion.PingOpacity;

    /// <summary>The ring is gone at 60 % of the period.</summary>
    internal const double PingActiveShare = FloatingOrbMotion.PingActiveShare;

    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000.0 / 15);

    private readonly OrbDisc _ping = new() { IsHitTestVisible = false };
    private readonly GlassOrbControl _orb = new() { IsHitTestVisible = false };
    private readonly DispatcherTimer _timer;
    private TimeProvider _clock = TimeProvider.System;
    private long _start;
    private bool _pingAtRest;

    public FloatingOrbDisc()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Children.Add(_ping);
        Children.Add(_orb);
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = FrameInterval };
        _timer.Tick += (_, _) => Tick();
        _pingAtRest = true;
    }

    /// <summary>The orb moves (bind it to the recording state); it holds still otherwise.</summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>The 15 Hz timer runs (for tests).</summary>
    internal bool IsMoving => _timer.IsEnabled;

    /// <summary>How many times the timer was started (for tests).</summary>
    internal int Starts { get; private set; }

    /// <summary>The orb's angle in radians (for tests); <c>null</c> without a composition visual.</summary>
    internal float? Angle => ElementComposition.GetElementVisual(_orb)?.RotationAngle;

    /// <summary>The ring's scale (for tests); <c>null</c> without a composition visual.</summary>
    internal double? RingScale => ElementComposition.GetElementVisual(_ping)?.Scale.X;

    /// <summary>The ring's opacity (for tests); <c>null</c> without a composition visual.</summary>
    internal double? RingOpacity => ElementComposition.GetElementVisual(_ping)?.Opacity;

    /// <summary>The clock of the motion; the system clock unless a test replaces it. A new clock restarts the motion.</summary>
    internal TimeProvider Clock
    {
        get => _clock;
        set
        {
            _clock = value;
            _start = value.GetTimestamp();
        }
    }

    /// <summary>One step at the clock's current time: the orb's angle and, while the ring is out, its scale and opacity. Allocates nothing.</summary>
    internal void Tick()
    {
        var seconds = _clock.GetElapsedTime(_start).TotalSeconds;
        if (ElementComposition.GetElementVisual(_orb) is { } orb)
        {
            orb.RotationAngle = (float)FloatingOrbMotion.Angle(seconds);
        }

        if (ElementComposition.GetElementVisual(_ping) is not { } ring)
        {
            return;
        }

        if (FloatingOrbMotion.Ping(seconds, out var scale, out var opacity))
        {
            ring.Scale = new Vector3D(scale, scale, 1);
            ring.Opacity = (float)opacity;
            _pingAtRest = false;
        }
        else if (!_pingAtRest)
        {
            RestPing(ring);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(double.IsNaN(Width) ? 0 : Width, double.IsNaN(Height) ? 0 : Height);
        _ping.Measure(size);
        _orb.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rect = new Rect(finalSize);
        _ping.Arrange(rect);
        _orb.Arrange(rect);
        Centre(finalSize);

        // The ring is hidden on its composition visual (the control's own opacity stays 1), as soon as the visual exists.
        if (_pingAtRest && ElementComposition.GetElementVisual(_ping) is { } ring)
        {
            ring.Opacity = 0;
        }
        return finalSize;
    }

    private void Centre(Size size)
    {
        var centre = new Vector3D(size.Width / 2, size.Height / 2, 0);
        if (ElementComposition.GetElementVisual(_orb) is { } orb)
        {
            orb.CenterPoint = centre;
        }

        if (ElementComposition.GetElementVisual(_ping) is { } ring)
        {
            ring.CenterPoint = centre;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WidthProperty || change.Property == HeightProperty)
        {
            _ping.Width = _orb.Width = Width;
            _ping.Height = _orb.Height = Height;
        }
        else if (change.Property == IsActiveProperty || change.Property == Motion.ReduceMotionProperty)
        {
            Update();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_pingAtRest && ElementComposition.GetElementVisual(_ping) is { } ring)
        {
            ring.Opacity = 0;
        }

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
            Centre(Bounds.Size);
            _timer.Start();
            Starts++;
            Tick();
        }
        else
        {
            _timer.Stop();
            if (ElementComposition.GetElementVisual(_orb) is { } orb)
            {
                orb.RotationAngle = 0;
            }

            if (ElementComposition.GetElementVisual(_ping) is { } ring)
            {
                RestPing(ring);
            }
        }
    }

    /// <summary>The ring's rest frame: full size, not seen.</summary>
    private void RestPing(CompositionVisual ring)
    {
        ring.Scale = new Vector3D(1, 1, 1);
        ring.Opacity = 0;
        _pingAtRest = true;
    }
}
