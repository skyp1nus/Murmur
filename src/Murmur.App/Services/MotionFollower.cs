using Avalonia.Controls;
using Avalonia.Threading;
using Murmur.App.Controls;
using Murmur.Core.Platform;

namespace Murmur.App.Services;

/// <summary>
/// Keeps <see cref="Motion.ReduceMotionProperty"/> of a root control (the main window) in step with the system's
/// <see cref="IMotionPreference"/>. <see cref="IMotionPreference.Changed"/> arrives off the UI thread, so the new value is
/// read and applied on the UI thread (through <c>post</c>, <see cref="Dispatcher.UIThread"/> by default).
/// </summary>
internal sealed class MotionFollower : IDisposable
{
    private readonly Control _root;
    private readonly IMotionPreference _preference;
    private readonly Action<Action> _post;
    private int _disposed;

    private MotionFollower(Control root, IMotionPreference preference, Action<Action> post)
    {
        _root = root;
        _preference = preference;
        _post = post;
    }

    /// <summary>
    /// What the app does with its window: the current value is set at once (so the first frame is already right), but the
    /// subscription, which on Windows starts a thread and a window, waits until the first frame has been drawn, like the global
    /// hotkeys, so it stays out of the start-up budget. The follower is released when the window closes.
    /// </summary>
    public static void Attach(Window window, IMotionPreference preference, Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(preference);
        Motion.SetReduceMotion(window, preference.ReduceMotion);
        MotionFollower? follower = null;
        window.Opened += (_, _) => window.RequestAnimationFrame(_ =>
        {
            if (follower is null && window.IsVisible)
            {
                follower = Follow(window, preference, post);
            }
        });
        window.Closed += (_, _) => follower?.Dispose();
    }

    /// <summary>Applies the current preference to <paramref name="root"/> now and follows its changes until disposed.</summary>
    public static MotionFollower Follow(Control root, IMotionPreference preference, Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(preference);
        var follower = new MotionFollower(root, preference, post ?? (static action => Dispatcher.UIThread.Post(action)));

        // Subscribe first, read after: a change in between is not lost.
        preference.Changed += follower.OnChanged;
        Motion.SetReduceMotion(root, preference.ReduceMotion);
        return follower;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _preference.Changed -= OnChanged;
        }
    }

    private void OnChanged() => _post(Apply);

    private void Apply()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            Motion.SetReduceMotion(_root, _preference.ReduceMotion);
        }
    }
}
