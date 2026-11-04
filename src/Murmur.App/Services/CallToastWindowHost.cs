using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Platform;

namespace Murmur.App.Services;

/// <summary>
/// Shows the call toast as a <see cref="CallToastWindow"/>: one at a time, a new toast replaces the old window. The window follows
/// the system's reduce-motion preference like the main window (its orb holds still either way; nothing in it animates for
/// decoration while motion is reduced).
/// </summary>
internal sealed class CallToastWindowHost : ICallToastHost
{
    private readonly IMotionPreference? _motion;
    private CallToastWindow? _window;
    private MotionFollower? _follower;

    /// <param name="motion">The system's reduce-motion preference; <c>null</c> = never reduced.</param>
    public CallToastWindowHost(IMotionPreference? motion = null) => _motion = motion;

    /// <summary>The window on screen (for tests).</summary>
    internal CallToastWindow? Window => _window;

    public void Show(CallToastViewModel toast)
    {
        ArgumentNullException.ThrowIfNull(toast);
        Close();
        var window = new CallToastWindow { DataContext = toast };
        if (_motion is { } motion)
        {
            _follower = MotionFollower.Follow(window, motion);
        }

        _window = window;
        window.Show();
    }

    public void Close()
    {
        _follower?.Dispose();
        _follower = null;
        if (_window is { } window)
        {
            _window = null;
            window.Close();
        }
    }
}
