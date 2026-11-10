using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;

namespace Murmur.App.Controls;

/// <summary>
/// The moving orb of the floating recorder (#46): a <see cref="GlassOrbControl"/> that turns once in nine seconds, and behind it a ring
/// (a copy of the orb as a plain <see cref="OrbDisc"/>, so no halo is clipped) that goes out every three seconds, grows to 1.9 times
/// its size and fades: the mockup's <c>spin 9s linear infinite</c> and <c>ping 3s ease-out infinite</c>. Both move only while
/// <see cref="IsActive"/>, attached, seen and with motion allowed (<see cref="MotionGate"/>); otherwise the orb holds still at angle 0
/// and the ring is not seen.
/// </summary>
/// <remarks>
/// <para>The motion is three compositor animations that repeat forever (<see cref="CompositionLoop"/>): the <c>RotationAngle</c> of the
/// orb's composition visual and the <c>Scale</c> and <c>Opacity</c> of the ring's. They are started once when the motion may run and
/// run on the render thread at the display rate; the UI thread does nothing per frame (only the gate's check twice a second while the
/// orb is wanted). When the motion stops, the rest values are written, which also ends the animations. The inner glass orb stays
/// inactive (no breathing, no level source), so it costs nothing either.</para>
/// <para>A test clock (<see cref="Clock"/>) replaces the animations with frames that <see cref="Tick"/> computes from the same key
/// frames, so a board or a test always draws the same picture.</para>
/// <para>The orb carries no timer or other readout: "no timer" in the issue means no elapsed-time text on it.</para>
/// </remarks>
public sealed class FloatingOrbDisc : Panel
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<FloatingOrbDisc, bool>(nameof(IsActive));

    /// <summary>A full turn takes nine seconds.</summary>
    internal static readonly TimeSpan SpinPeriod = TimeSpan.FromSeconds(9);

    /// <summary>A ping goes out every three seconds.</summary>
    internal static readonly TimeSpan PingPeriod = TimeSpan.FromSeconds(3);

    /// <summary>The ring ends at 1.9 times the orb.</summary>
    internal const float PingScale = 1.9f;

    /// <summary>The ring's opacity when it starts.</summary>
    internal const float PingOpacity = 0.55f;

    /// <summary>The ring is gone at 60 % of the period.</summary>
    internal const float PingActiveShare = 0.6f;

    /// <summary>The mockup's <c>@keyframes ping</c> scale: 1 at 0 %, 1.9 at 60 % and 100 %, CSS <c>ease-out</c>.</summary>
    internal static readonly LoopKey[] PingScaleKeys =
        [new(0, 1), new(PingActiveShare, PingScale, LoopEasing.EaseOut), new(1, PingScale, LoopEasing.EaseOut)];

    /// <summary>The mockup's <c>@keyframes ping</c> opacity: .55 at 0 %, 0 at 60 % and 100 %, CSS <c>ease-out</c>.</summary>
    internal static readonly LoopKey[] PingOpacityKeys =
        [new(0, PingOpacity), new(PingActiveShare, 0, LoopEasing.EaseOut), new(1, 0, LoopEasing.EaseOut)];

    // The ring rests unseen: its control's opacity is 0, so whenever the renderer copies the control's properties to the composition
    // visual it writes the rest value (equal to the UI-side value, so a running loop is not disturbed). The loops and the test clock
    // animate the visual's opacity only.
    private readonly OrbDisc _ping = new() { IsHitTestVisible = false, Opacity = 0 };
    private readonly GlassOrbControl _orb = new() { IsHitTestVisible = false };
    private readonly MotionGate _gate;
    private TimeProvider _clock = TimeProvider.System;
    private bool _manual;
    private bool _looping;
    private long _start;

    // The size of the last arrange: Bounds is only updated after ArrangeOverride returns, so a loop started from there must not read it.
    private Size _arranged;

    public FloatingOrbDisc()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Children.Add(_ping);
        Children.Add(_orb);
        _gate = new MotionGate(this, OnGateChanged);
    }

    /// <summary>The orb moves (bind it to the recording state); it holds still otherwise.</summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>The orb moves: active, attached, seen and with motion allowed (for tests).</summary>
    internal bool IsMoving => _gate.IsOpen;

    /// <summary>The compositor animations run (for tests): the orb moves and no test clock drives it.</summary>
    internal bool IsLooping => _looping;

    /// <summary>How many times the motion started (for tests).</summary>
    internal int Starts { get; private set; }

    /// <summary>The gate's slow visibility check (for tests).</summary>
    internal MotionGate Gate => _gate;

    /// <summary>The orb's angle in radians (for tests); <c>null</c> without a composition visual. It stays 0 while the compositor turns it.</summary>
    internal float? Angle => ElementComposition.GetElementVisual(_orb)?.RotationAngle;

    /// <summary>The ring's scale (for tests); <c>null</c> without a composition visual.</summary>
    internal double? RingScale => ElementComposition.GetElementVisual(_ping)?.Scale.X;

    /// <summary>The ring's opacity (for tests); <c>null</c> without a composition visual.</summary>
    internal double? RingOpacity => ElementComposition.GetElementVisual(_ping)?.Opacity;

    /// <summary>
    /// The clock of the motion; the system clock unless a test replaces it. A new clock restarts the motion, and any clock other than the
    /// system one stops the compositor animations: the orb then shows the frames <see cref="Tick"/> computes for that clock.
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

    /// <summary>The angle in radians at <paramref name="seconds"/> after the start: one linear turn per <see cref="SpinPeriod"/>, in [0, 2π).</summary>
    internal static double AngleAt(double seconds)
    {
        var turns = seconds / SpinPeriod.TotalSeconds;
        return (turns - Math.Floor(turns)) * (2 * Math.PI);
    }

    /// <summary>The ring's scale and opacity at <paramref name="seconds"/> after the start, from the same key frames as the compositor.</summary>
    internal static (double Scale, double Opacity) PingAt(double seconds)
    {
        var cycle = seconds / PingPeriod.TotalSeconds;
        var progress = cycle - Math.Floor(cycle);
        return (CompositionLoop.Sample(PingScaleKeys, progress), CompositionLoop.Sample(PingOpacityKeys, progress));
    }

    /// <summary>
    /// One frame of a test clock: the orb's angle and the ring's scale and opacity for the time since the start. Does nothing while the
    /// compositor moves the orb. Allocates nothing.
    /// </summary>
    internal void Tick()
    {
        if (_looping)
        {
            return;
        }

        var seconds = _clock.GetElapsedTime(_start).TotalSeconds;
        Centre(_arranged);
        if (ElementComposition.GetElementVisual(_orb) is { } orb)
        {
            orb.RotationAngle = (float)AngleAt(seconds);
        }

        if (ElementComposition.GetElementVisual(_ping) is { } ring)
        {
            var (scale, opacity) = PingAt(seconds);
            ring.Scale = new Vector3D(scale, scale, 1);
            ring.Opacity = (float)opacity;
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
        _arranged = finalSize;
        Centre(finalSize);

        // The composition visuals do not exist yet when the control is attached: the loops start at the first arrange after that.
        if (_gate.IsOpen && !_manual && !_looping)
        {
            StartLoop();
        }

        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WidthProperty || change.Property == HeightProperty)
        {
            _ping.Width = _orb.Width = Width;
            _ping.Height = _orb.Height = Height;
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
        _looping = false;
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

    private void OnGateChanged(bool open)
    {
        if (open)
        {
            Starts++;
            _start = _clock.GetTimestamp();
            if (!_manual)
            {
                StartLoop();
            }
        }
        else
        {
            StopLoop();
            Rest();
        }
    }

    /// <summary>Starts the three forever loops on the compositor, once both composition visuals exist (otherwise the next arrange does).</summary>
    private void StartLoop()
    {
        if (_looping
            || ElementComposition.GetElementVisual(_orb) is not { } orb
            || ElementComposition.GetElementVisual(_ping) is not { } ring)
        {
            return;
        }

        Centre(_arranged);
        CompositionLoop.StartSpin(orb, SpinPeriod);
        CompositionLoop.StartScale(ring, PingPeriod, PingScaleKeys);
        CompositionLoop.StartOpacity(ring, PingPeriod, PingOpacityKeys);
        _looping = true;
    }

    /// <summary>Ends the compositor loops at the rest frame.</summary>
    private void StopLoop()
    {
        if (!_looping)
        {
            return;
        }

        _looping = false;
        Rest();
    }

    /// <summary>The rest frame: the orb at angle 0, the ring full size and not seen (writing it also ends a running loop).</summary>
    private void Rest()
    {
        if (ElementComposition.GetElementVisual(_orb) is { } orb)
        {
            CompositionLoop.HoldAngle(orb, 0);
        }

        if (ElementComposition.GetElementVisual(_ping) is { } ring)
        {
            CompositionLoop.HoldScale(ring, 1);
            CompositionLoop.HoldOpacity(ring, 0);
        }
    }
}
