using Avalonia;
using Avalonia.Controls;

namespace Murmur.App.Controls;

/// <summary>
/// The "reduce motion" setting as an inherited attached property: set on the window (see <c>MotionFollower</c>) it reaches every
/// control below it, and a control that animates for decoration (the orbs) reads it and holds still. Nothing static, nothing
/// reflective: a control only listens to <see cref="ReduceMotionProperty"/> in its own <c>OnPropertyChanged</c>.
/// </summary>
public static class Motion
{
    /// <summary>True while decorative animation must hold still; level meters, progress and the like ignore it.</summary>
    public static readonly AttachedProperty<bool> ReduceMotionProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, bool>("ReduceMotion", defaultValue: false, inherits: true);

    public static bool GetReduceMotion(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.GetValue(ReduceMotionProperty);
    }

    public static void SetReduceMotion(Control control, bool value)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.SetValue(ReduceMotionProperty, value);
    }
}
