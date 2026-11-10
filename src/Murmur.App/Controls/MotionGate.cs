using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Murmur.App.Controls;

/// <summary>
/// Decides when a control's decorative motion may run: the control wants it (it is active), it is attached, motion is allowed
/// (<see cref="Motion.ReduceMotionProperty"/>) and it can be seen (effectively visible, its window not minimized). The owner tells
/// the gate when the first three change; whether it can be seen has no public event, so while the motion is wanted a slow check
/// (twice a second) notices a hidden or minimized control, and a hidden one runs no animation at all.
/// </summary>
/// <remarks>The callback runs only when the gate opens or closes; nothing is allocated on a check.</remarks>
internal sealed class MotionGate
{
    /// <summary>How often a wanted motion checks whether its control is still seen.</summary>
    internal static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(500);

    private readonly Control _owner;
    private readonly Action<bool> _changed;
    private readonly DispatcherTimer _watch;
    private bool _wanted;

    /// <param name="owner">The control that moves.</param>
    /// <param name="changed">Called with <c>true</c> when the motion may start and <c>false</c> when it must stop.</param>
    public MotionGate(Control owner, Action<bool> changed)
    {
        _owner = owner;
        _changed = changed;
        _watch = new DispatcherTimer(DispatcherPriority.Background) { Interval = WatchInterval };
        _watch.Tick += (_, _) => Check();
    }

    /// <summary>The motion runs.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>The slow visibility check runs (the motion is wanted, attached and allowed).</summary>
    public bool IsWatching => _watch.IsEnabled;

    /// <summary>The control is attached, and seen: effectively visible and not in a minimized window.</summary>
    public static bool IsShown(Visual visual)
    {
        ArgumentNullException.ThrowIfNull(visual);
        return visual.IsAttachedToVisualTree() && visual.IsEffectivelyVisible
            && (TopLevel.GetTopLevel(visual) as Window)?.WindowState != WindowState.Minimized;
    }

    /// <summary>The owner wants the motion (or not) now; call it as well when it attaches or the motion setting changes.</summary>
    public void Update(bool wanted)
    {
        _wanted = wanted;
        Check();
    }

    /// <summary>
    /// The owner is leaving the visual tree: the gate closes (call it from <c>OnDetachedFromVisualTree</c>, where the control still
    /// counts as attached).
    /// </summary>
    public void Detach() => Apply(eligible: false);

    /// <summary>Opens or closes the gate for what is true now (the slow check calls it; internal for tests).</summary>
    internal void Check() =>
        Apply(_wanted && ((Visual)_owner).IsAttachedToVisualTree() && !Motion.GetReduceMotion(_owner));

    private void Apply(bool eligible)
    {
        if (eligible != _watch.IsEnabled)
        {
            if (eligible)
            {
                _watch.Start();
            }
            else
            {
                _watch.Stop();
            }
        }

        var open = eligible && IsShown(_owner);
        if (open != IsOpen)
        {
            IsOpen = open;
            _changed(open);
        }
    }
}
