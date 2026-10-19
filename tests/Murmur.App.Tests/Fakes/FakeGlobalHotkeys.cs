using Murmur.Core.Input;

namespace Murmur.App.Tests.Fakes;

/// <summary>
/// <see cref="IGlobalHotkeys"/> that records every <see cref="Register"/> call and the thread it came from. With
/// <see cref="Exclusive"/> it behaves like the system: a gesture held by one action is <see cref="HotkeyRegistration.Taken"/>
/// for another, and a failed Register leaves the action without a hotkey.
/// </summary>
internal sealed class FakeGlobalHotkeys : IGlobalHotkeys
{
    private readonly Lock _gate = new();
    private readonly List<(HotkeyAction Action, string? Gesture, int ThreadId)> _calls = [];
    private readonly Dictionary<HotkeyAction, string> _held = [];

    public bool IsSupported => true;

    /// <summary>Gestures that fail, and how (default <see cref="HotkeyRegistration.Registered"/>).</summary>
    public Dictionary<string, HotkeyRegistration> Results { get; } = [];

    /// <summary>Another action holding a gesture makes it <see cref="HotkeyRegistration.Taken"/>.</summary>
    public bool Exclusive { get; set; }

    public Exception? Failure { get; set; }

    /// <summary>Runs inside <see cref="Register"/> after the call is recorded and outside its lock (a test blocks here).</summary>
    public Action<HotkeyAction, string?>? Hook { get; set; }

    public event Action<HotkeyAction>? Pressed;

    public List<(HotkeyAction Action, string? Gesture, int ThreadId)> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public HotkeyRegistration Register(HotkeyAction action, string? gesture)
    {
        lock (_gate)
        {
            _calls.Add((action, gesture, Environment.CurrentManagedThreadId));
        }

        Hook?.Invoke(action, gesture);
        lock (_gate)
        {
            if (Failure is { } failure)
            {
                throw failure;
            }

            _held.Remove(action);
            if (gesture is null)
            {
                return HotkeyRegistration.Cleared;
            }

            if (Results.TryGetValue(gesture, out var result))
            {
                return result;
            }

            if (Exclusive && _held.Values.Contains(gesture, StringComparer.OrdinalIgnoreCase))
            {
                return HotkeyRegistration.Taken;
            }

            _held[action] = gesture;
            return HotkeyRegistration.Registered;
        }
    }

    public int SubscriberCount => Pressed?.GetInvocationList().Length ?? 0;

    /// <summary>Raises <see cref="Pressed"/> on a thread-pool thread, like the hotkey thread does, and waits for the handler to return.</summary>
    public void PressFromAnotherThread(HotkeyAction action) => Task.Run(() => Pressed?.Invoke(action)).GetAwaiter().GetResult();

    public void Dispose()
    {
    }
}
