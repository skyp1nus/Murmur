using Avalonia.Animation.Easings;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;

namespace Murmur.App.Controls;

/// <summary>
/// Level-driven motion on the compositor: implicit animations that turn every new value of a composition visual's property into a
/// short linear glide from where the visual is now to the new value. The UI thread sets a sampled value (20 to 30 times a second);
/// the render thread interpolates to it every frame, so the motion is as smooth as the display, and a new sample that arrives mid-glide
/// starts from the value on screen (no jump).
/// </summary>
/// <remarks>
/// Set the collection on the visual while the samples come and clear it (<c>null</c>) before writing a rest value that should show at
/// once. Every set value starts one glide: Avalonia builds the render thread's copy of the animation then (a few hundred bytes, at the
/// sample rate, never per frame), so values too small to see should not be set at all.
/// </remarks>
internal static class CompositionGlide
{
    /// <summary>A collection that glides <paramref name="properties"/> (<c>Scale</c>, <c>Opacity</c> or <c>RotationAngle</c>) over <paramref name="duration"/>.</summary>
    public static ImplicitAnimationCollection Create(Compositor compositor, TimeSpan duration, params ReadOnlySpan<string> properties)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        var collection = compositor.CreateImplicitAnimationCollection();
        foreach (var property in properties)
        {
            KeyFrameAnimation animation = property == CompositionLoop.Scale
                ? compositor.CreateVector3DKeyFrameAnimation()
                : compositor.CreateScalarKeyFrameAnimation();
            animation.InsertExpressionKeyFrame(1f, "this.FinalValue", new LinearEasing());
            animation.Duration = duration;
            animation.Target = property;

            // In a group of one: Avalonia then builds the render thread's copy once per value, not twice.
            var group = compositor.CreateAnimationGroup();
            group.Add(animation);
            collection[property] = group;
        }

        return collection;
    }
}
