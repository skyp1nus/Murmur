using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.VisualTree;

namespace Murmur.App.Controls;

/// <summary>
/// The small orb of the status bar and the Activity popover: an <see cref="OrbDisc"/> that turns once in nine seconds while a
/// recording runs. It turns only while <see cref="IsActive"/>, attached and with motion allowed; otherwise it holds still at angle 0.
/// </summary>
/// <remarks>
/// The turn is a repeating keyframe animation of the composition visual (<c>RotationAngle</c>): the compositor advances it, the UI thread
/// does no work per frame and no timer runs. Under <see cref="Motion.ReduceMotionProperty"/> (or while not active) no animation exists.
/// </remarks>
public sealed class SpinningOrb : Decorator
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<SpinningOrb, bool>(nameof(IsActive));

    /// <summary>A full turn takes nine seconds, like the mockup.</summary>
    internal static readonly TimeSpan Period = TimeSpan.FromSeconds(9);

    private const string AngleProperty = "RotationAngle";

    private bool _spinning;

    public SpinningOrb()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Child = new OrbDisc();
    }

    /// <summary>The orb belongs to something that is running (a recording): it turns. Bind it to the state; it stays still otherwise.</summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>The turn animation is running on the compositor (for tests).</summary>
    internal bool IsSpinning => _spinning;

    /// <summary>The disc is at rest (angle 0), as drawn without motion (for tests); <c>null</c> without a composition visual.</summary>
    internal float? RestAngle => ElementComposition.GetElementVisual(this) is { } visual && !_spinning ? visual.RotationAngle : null;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsNaN(Width) ? 0 : Width, double.IsNaN(Height) ? 0 : Height);

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        if (ElementComposition.GetElementVisual(this) is { } visual)
        {
            visual.CenterPoint = new Vector3D(finalSize.Width / 2, finalSize.Height / 2, 0);
        }

        // The composition visual may not exist yet when the orb is attached: start as soon as the first layout has it.
        Update();
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
        if (run == _spinning)
        {
            return;
        }

        var visual = ElementComposition.GetElementVisual(this);
        if (run)
        {
            if (visual is null)
            {
                return;
            }

            visual.CenterPoint = new Vector3D(Bounds.Width / 2, Bounds.Height / 2, 0);
            var turn = visual.Compositor.CreateScalarKeyFrameAnimation();
            turn.InsertKeyFrame(0f, 0f);
            turn.InsertKeyFrame(1f, MathF.Tau, new LinearEasing());
            turn.Duration = Period;
            turn.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation(AngleProperty, turn);
            _spinning = true;
        }
        else
        {
            _spinning = false;
            if (visual is not null)
            {
                visual.StopAnimation(AngleProperty);
                visual.RotationAngle = 0;
            }
        }
    }
}
