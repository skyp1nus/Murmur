using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;

namespace Murmur.App.Controls;

/// <summary>The easing of a key frame segment, as CSS names it (the canvas mockups use these).</summary>
internal enum LoopEasing
{
    /// <summary>CSS <c>linear</c>.</summary>
    Linear,

    /// <summary>CSS <c>ease-in-out</c>: <c>cubic-bezier(0.42, 0, 0.58, 1)</c>.</summary>
    EaseInOut,

    /// <summary>CSS <c>ease-out</c>: <c>cubic-bezier(0, 0, 0.58, 1)</c>.</summary>
    EaseOut,

    /// <summary>A cubic ease-out, <c>1 - (1 - t)³</c>.</summary>
    CubicOut,
}

/// <summary>
/// One key frame of a looping animation: where in the period it is (0..1), the value there, and the easing of the segment that
/// ends at it (CSS applies the timing function per key frame interval the same way).
/// </summary>
internal readonly record struct LoopKey(float Progress, float Value, LoopEasing Easing = LoopEasing.Linear);

/// <summary>
/// Looping decorative motion on the compositor (the render thread): a key frame animation of a composition visual's property that
/// repeats forever, started once and stopped by writing a rest value. The UI thread does nothing per frame.
/// </summary>
/// <remarks>
/// <para>
/// <b>Start</b> builds the animation (a few small objects: it happens when the motion starts, not per frame) and hands it to the
/// visual. The compositor evaluates it every frame at the display rate.
/// </para>
/// <para>
/// <b>Hold</b> stops it. The compositor keeps the last animated value when an animation ends, and
/// <see cref="CompositionObject.StopAnimation"/> reaches into the render thread's objects at once, ahead of a start that may still
/// be waiting in the same batch. A direct value written through the batch does both jobs in order: the compositor drops the running
/// animation and shows the value. The UI-side value never moved (animations run on the render thread), so the write is forced by
/// setting a different value first; only the last one is sent.
/// </para>
/// <para>Nothing here reads the time: a test checks the shape of the loop through <see cref="Sample"/>.</para>
/// </remarks>
internal static class CompositionLoop
{
    public const string RotationAngle = "RotationAngle";
    public const string Scale = "Scale";
    public const string Opacity = "Opacity";

    /// <summary>A full turn (0 to 2π, linear) every <paramref name="period"/>: the spinning orbs.</summary>
    public static void StartSpin(CompositionVisual visual, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(visual);
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0f, 0f, new LinearEasing());
        animation.InsertKeyFrame(1f, MathF.Tau, new LinearEasing());
        Loop(animation, period);
        visual.StartAnimation(RotationAngle, animation);
    }

    /// <summary>A uniform scale (x and y; z stays 1) through <paramref name="keys"/> every <paramref name="period"/>.</summary>
    public static void StartScale(CompositionVisual visual, TimeSpan period, ReadOnlySpan<LoopKey> keys)
    {
        ArgumentNullException.ThrowIfNull(visual);
        var animation = visual.Compositor.CreateVector3DKeyFrameAnimation();
        foreach (var key in keys)
        {
            animation.InsertKeyFrame(key.Progress, new Vector3D(key.Value, key.Value, 1), CreateEasing(key.Easing));
        }

        Loop(animation, period);
        visual.StartAnimation(Scale, animation);
    }

    /// <summary>The opacity through <paramref name="keys"/> every <paramref name="period"/>.</summary>
    public static void StartOpacity(CompositionVisual visual, TimeSpan period, ReadOnlySpan<LoopKey> keys)
    {
        ArgumentNullException.ThrowIfNull(visual);
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        foreach (var key in keys)
        {
            animation.InsertKeyFrame(key.Progress, key.Value, CreateEasing(key.Easing));
        }

        Loop(animation, period);
        visual.StartAnimation(Opacity, animation);
    }

    /// <summary>Stops a turn and shows <paramref name="angle"/> (radians).</summary>
    public static void HoldAngle(CompositionVisual visual, float angle)
    {
        ArgumentNullException.ThrowIfNull(visual);
        visual.RotationAngle = angle + 1f;
        visual.RotationAngle = angle;
    }

    /// <summary>Stops a scale loop (or a pending one) and shows the uniform <paramref name="scale"/>.</summary>
    public static void HoldScale(CompositionVisual visual, double scale)
    {
        ArgumentNullException.ThrowIfNull(visual);
        visual.Scale = new Vector3D(scale + 1, scale + 1, 1);
        visual.Scale = new Vector3D(scale, scale, 1);
    }

    /// <summary>Stops an opacity loop (or a pending one) and shows <paramref name="opacity"/>.</summary>
    public static void HoldOpacity(CompositionVisual visual, float opacity)
    {
        ArgumentNullException.ThrowIfNull(visual);
        visual.Opacity = opacity + 1f;
        visual.Opacity = opacity;
    }

    /// <summary>
    /// The value of the loop through <paramref name="keys"/> at <paramref name="progress"/> (0..1) of its period, the way the
    /// compositor evaluates it: the segment that holds the progress, eased by the easing of its end key. Pure; for tests and for the
    /// frames a test clock draws.
    /// </summary>
    public static double Sample(ReadOnlySpan<LoopKey> keys, double progress)
    {
        if (keys.IsEmpty)
        {
            return 0;
        }

        progress -= Math.Floor(progress);
        if (progress <= keys[0].Progress)
        {
            return keys[0].Value;
        }

        for (var i = 1; i < keys.Length; i++)
        {
            if (progress <= keys[i].Progress)
            {
                var from = keys[i - 1];
                var to = keys[i];
                var span = to.Progress - from.Progress;
                var t = span <= 0 ? 1 : (progress - from.Progress) / span;
                return from.Value + ((to.Value - from.Value) * Ease(to.Easing, t));
            }
        }

        return keys[^1].Value;
    }

    /// <summary>The easing curve at <paramref name="t"/> (0..1); pure (Avalonia's spline easing keeps state, so it is not shared).</summary>
    public static double Ease(LoopEasing easing, double t) => easing switch
    {
        LoopEasing.EaseInOut => CubicBezier(0.42, 0, 0.58, 1, t),
        LoopEasing.EaseOut => CubicBezier(0, 0, 0.58, 1, t),
        LoopEasing.CubicOut => 1 - ((1 - t) * (1 - t) * (1 - t)),
        _ => t,
    };

    private static void Loop(KeyFrameAnimation animation, TimeSpan period)
    {
        animation.Duration = period;
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
    }

    // Every animation gets its own easing objects: Avalonia's spline easing keeps per-call state and the render thread evaluates it.
    private static IEasing CreateEasing(LoopEasing easing) => easing switch
    {
        LoopEasing.EaseInOut => new SplineEasing(0.42, 0, 0.58, 1),
        LoopEasing.EaseOut => new SplineEasing(0, 0, 0.58, 1),
        LoopEasing.CubicOut => new CubicEaseOut(),
        _ => new LinearEasing(),
    };

    /// <summary>A CSS <c>cubic-bezier(x1, y1, x2, y2)</c> at <paramref name="x"/>: Newton steps on x(t), then y(t).</summary>
    private static double CubicBezier(double x1, double y1, double x2, double y2, double x)
    {
        if (x <= 0)
        {
            return 0;
        }

        if (x >= 1)
        {
            return 1;
        }

        var t = x;
        for (var i = 0; i < 8; i++)
        {
            var error = Bezier(x1, x2, t) - x;
            if (Math.Abs(error) < 1e-7)
            {
                break;
            }

            var slope = BezierSlope(x1, x2, t);
            if (Math.Abs(slope) < 1e-6)
            {
                break;
            }

            t = Math.Clamp(t - (error / slope), 0, 1);
        }

        return Bezier(y1, y2, t);
    }

    private static double Bezier(double p1, double p2, double t)
    {
        var u = 1 - t;
        return (3 * u * u * t * p1) + (3 * u * t * t * p2) + (t * t * t);
    }

    private static double BezierSlope(double p1, double p2, double t)
    {
        var u = 1 - t;
        return (3 * u * u * p1) + (6 * u * t * (p2 - p1)) + (3 * t * t * (1 - p2));
    }
}
