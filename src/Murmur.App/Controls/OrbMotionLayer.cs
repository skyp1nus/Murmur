using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.VisualTree;

namespace Murmur.App.Controls;

/// <summary>What an <see cref="OrbMotionLayer"/> does while it is active.</summary>
public enum OrbMotionKind
{
    /// <summary>Turns once in nine seconds, linear (the floating orb while recording).</summary>
    Spin,

    /// <summary>A soft ring goes out every three seconds: the layer grows to 1.9 times its size and fades away (a copy of the orb under it).</summary>
    Ping,
}

/// <summary>
/// One moving layer of the floating orb (#46): its child turns or pings while <see cref="IsActive"/> and when motion is allowed
/// (<see cref="Motion.ReduceMotionProperty"/>). Both are keyframe animations of the composition visual that repeat forever: the
/// compositor advances them, the UI thread does no layout, no drawing and no allocation per frame, and no timer runs. Inactive,
/// the layer holds its rest frame: the spinner at angle 0, the ping invisible.
/// </summary>
public sealed class OrbMotionLayer : Decorator
{
    public static readonly StyledProperty<OrbMotionKind> KindProperty =
        AvaloniaProperty.Register<OrbMotionLayer, OrbMotionKind>(nameof(Kind));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<OrbMotionLayer, bool>(nameof(IsActive));

    /// <summary>A full turn takes nine seconds.</summary>
    internal static readonly TimeSpan SpinPeriod = TimeSpan.FromSeconds(9);

    /// <summary>A ping goes out every three seconds.</summary>
    internal static readonly TimeSpan PingPeriod = TimeSpan.FromSeconds(3);

    /// <summary>The ring ends at 1.9 times the orb and is gone at 60 % of the period.</summary>
    internal const float PingScale = 1.9f;

    internal const float PingOpacity = 0.55f;
    private const float PingFadedAt = 0.6f;

    private bool _running;
    private int _starts;

    public OrbMotionLayer()
    {
        IsHitTestVisible = false;
        Focusable = false;
    }

    /// <summary>Spin (the orb itself) or ping (the ring behind it).</summary>
    public OrbMotionKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>The layer moves (bind to the recording state); it holds still otherwise.</summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>The animation is running on the compositor (for tests).</summary>
    internal bool IsRunning => _running;

    /// <summary>How many times an animation was started (for tests: a property change that does not change the state starts nothing).</summary>
    internal int Starts => _starts;

    /// <summary>The rotation of the layer at rest, in radians (for tests); <c>null</c> while it runs or has no composition visual.</summary>
    internal float? RestAngle => ElementComposition.GetElementVisual(this) is { } visual && !_running ? visual.RotationAngle : null;

    protected override Size MeasureOverride(Size availableSize)
    {
        Child?.Measure(availableSize);
        return Child?.DesiredSize ?? default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        if (ElementComposition.GetElementVisual(this) is { } visual)
        {
            visual.CenterPoint = new Vector3D(finalSize.Width / 2, finalSize.Height / 2, 0);
        }

        // The composition visual may not exist yet when the layer is attached: start as soon as the first layout has it.
        Update();
        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty || change.Property == KindProperty || change.Property == Motion.ReduceMotionProperty)
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
        var visual = ElementComposition.GetElementVisual(this);
        if (run && _running)
        {
            return;
        }

        if (run)
        {
            if (visual is null)
            {
                return;
            }

            Start(visual);
            _running = true;
            _starts++;
        }
        else if (_running || Kind == OrbMotionKind.Ping)
        {
            _running = false;
            Stop(visual);
        }
    }

    private void Start(CompositionVisual visual)
    {
        visual.CenterPoint = new Vector3D(Bounds.Width / 2, Bounds.Height / 2, 0);
        var compositor = visual.Compositor;
        if (Kind == OrbMotionKind.Spin)
        {
            var turn = compositor.CreateScalarKeyFrameAnimation();
            turn.InsertKeyFrame(0f, 0f);
            turn.InsertKeyFrame(1f, MathF.Tau, new LinearEasing());
            turn.Duration = SpinPeriod;
            turn.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("RotationAngle", turn);
            return;
        }

        var grow = compositor.CreateVector3DKeyFrameAnimation();
        grow.InsertKeyFrame(0f, new Vector3D(1, 1, 1));
        grow.InsertKeyFrame(PingFadedAt, new Vector3D(PingScale, PingScale, 1), new CubicEaseOut());
        grow.InsertKeyFrame(1f, new Vector3D(PingScale, PingScale, 1));
        grow.Duration = PingPeriod;
        grow.IterationBehavior = AnimationIterationBehavior.Forever;

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, PingOpacity);
        fade.InsertKeyFrame(PingFadedAt, 0f, new CubicEaseOut());
        fade.InsertKeyFrame(1f, 0f);
        fade.Duration = PingPeriod;
        fade.IterationBehavior = AnimationIterationBehavior.Forever;

        visual.StartAnimation("Scale", grow);
        visual.StartAnimation("Opacity", fade);
    }

    private void Stop(CompositionVisual? visual)
    {
        if (visual is null)
        {
            return;
        }

        if (Kind == OrbMotionKind.Spin)
        {
            visual.StopAnimation("RotationAngle");
            visual.RotationAngle = 0;
            return;
        }

        visual.StopAnimation("Scale");
        visual.StopAnimation("Opacity");
        visual.Scale = new Vector3D(1, 1, 1);

        // A ping at rest is not seen: the ring exists only while it goes out.
        visual.Opacity = 0;
    }
}
