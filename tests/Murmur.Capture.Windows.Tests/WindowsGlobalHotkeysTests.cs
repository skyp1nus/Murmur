using System.Diagnostics;
using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Input;
using Murmur.Core.Input;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// <see cref="WindowsGlobalHotkeys"/>. The first group touches nothing native and runs on any OS; the Windows group
/// registers real system-wide hotkeys (distinct, unlikely F13..F24 combinations per test, so parallel
/// tests and the user's own hotkeys do not collide) and simulates presses with SendInput. Presses are skipped, not failed,
/// when the runner has no interactive input desktop.
/// </summary>
public sealed class WindowsGlobalHotkeysTests
{
    private const string WindowsOnly = "Global hotkey tests run only on Windows.";
    private static readonly TimeSpan PressTimeout = TimeSpan.FromSeconds(2);

    // ---- no native calls: any OS ----------------------------------------------------------------------------------------

    [Fact]
    public void NewInstance_IsSupported_AndTouchesNothingNative()
    {
        using var hotkeys = new WindowsGlobalHotkeys();

        Assert.True(hotkeys.IsSupported);
        Assert.False(hotkeys.HasLiveThread);
        Assert.Equal(0, hotkeys.LastError);
        Assert.Null(hotkeys.LastFault);
    }

    [Fact]
    public void ClearingAndInvalidText_DoNotStartTheThread()
    {
        using var hotkeys = new WindowsGlobalHotkeys();

        Assert.Equal(HotkeyRegistration.Cleared, hotkeys.Register(HotkeyAction.ToggleRecording, null));
        Assert.Equal(HotkeyRegistration.Invalid, hotkeys.Register(HotkeyAction.ToggleRecording, "R"));
        Assert.Equal(HotkeyRegistration.Invalid, hotkeys.Register(HotkeyAction.ShowApp, "Shift+R"));
        Assert.Equal(HotkeyRegistration.Invalid, hotkeys.Register(HotkeyAction.ShowApp, "Ctrl+Banana"));
        Assert.False(hotkeys.HasLiveThread);
    }

    [Fact]
    public void Register_UndefinedAction_Throws()
    {
        using var hotkeys = new WindowsGlobalHotkeys();

        Assert.Throws<ArgumentOutOfRangeException>(() => hotkeys.Register((HotkeyAction)99, "Ctrl+Alt+F9"));
    }

    [Fact]
    public void Dispose_IsIdempotent_AndRegisterAfterDisposeThrows()
    {
        var hotkeys = new WindowsGlobalHotkeys();

        hotkeys.Dispose();
        hotkeys.Dispose();

        Assert.Throws<ObjectDisposedException>(() => hotkeys.Register(HotkeyAction.ToggleRecording, "Ctrl+Alt+F9"));
        Assert.Throws<ObjectDisposedException>(() => hotkeys.Register(HotkeyAction.ToggleRecording, null));
    }

    [Fact]
    public void Factory_ReturnsAnUnstartedImplementation()
    {
        using var hotkeys = new WindowsCapture().CreateGlobalHotkeys();

        Assert.IsType<WindowsGlobalHotkeys>(hotkeys);
        Assert.True(hotkeys.IsSupported);
    }

    // ---- real RegisterHotKey: Windows only ---------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Windows")]
    public void Register_Gesture_IsRegistered_OnADedicatedBackgroundThread()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var hotkeys = new WindowsGlobalHotkeys();

        Assert.False(hotkeys.HasLiveThread);
        Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(HotkeyAction.ToggleRecording, Gesture.Next()));
        Assert.True(hotkeys.HasLiveThread);
        Assert.Equal(0, hotkeys.LastError);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void SameGesture_ForASecondAction_IsTaken_AndTheFirstKeepsIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var hotkeys = new WindowsGlobalHotkeys();
        var gesture = Gesture.Next();

        Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(HotkeyAction.ToggleRecording, gesture));
        Assert.Equal(HotkeyRegistration.Taken, hotkeys.Register(HotkeyAction.ToggleScreen, gesture));
        Assert.Equal(1409, hotkeys.LastError); // ERROR_HOTKEY_ALREADY_REGISTERED

        using var other = new WindowsGlobalHotkeys();
        Assert.Equal(HotkeyRegistration.Taken, other.Register(HotkeyAction.ShowApp, gesture)); // still held by the first action
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void TogglePause_RegistersLikeTheOtherActions_AndRaisesItsOwnAction()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var hotkeys = new WindowsGlobalHotkeys();
        var gesture = Gesture.Next();
        var pressedActions = new List<HotkeyAction>();
        using var pressed = new ManualResetEventSlim(false);
        hotkeys.Pressed += action =>
        {
            lock (pressedActions)
            {
                pressedActions.Add(action);
            }

            pressed.Set();
        };

        Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(HotkeyAction.TogglePause, gesture));
        Assert.Equal(HotkeyRegistration.Taken, hotkeys.Register(HotkeyAction.ShowApp, gesture));

        PressOrSkip(gesture, pressed);

        lock (pressedActions)
        {
            Assert.Equal([HotkeyAction.TogglePause], pressedActions);
        }

        Assert.Equal(HotkeyRegistration.Cleared, hotkeys.Register(HotkeyAction.TogglePause, null));
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void SameGesture_FromAnotherInstance_IsTaken_WithWin32Error1409()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var first = new WindowsGlobalHotkeys();
        using var second = new WindowsGlobalHotkeys();
        var gesture = Gesture.Next();

        Assert.Equal(HotkeyRegistration.Registered, first.Register(HotkeyAction.ToggleRecording, gesture));
        Assert.Equal(HotkeyRegistration.Taken, second.Register(HotkeyAction.ToggleRecording, gesture));

        Assert.Equal(1409, second.LastError);
        Assert.NotNull(second.LastFault);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void ReRegister_ReplacesTheGesture_AndFreesTheOldOne()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var hotkeys = new WindowsGlobalHotkeys();
        using var probe = new WindowsGlobalHotkeys();
        var old = Gesture.Next();
        var replacement = Gesture.Next();

        Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(HotkeyAction.ToggleRecording, old));
        Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(HotkeyAction.ToggleRecording, replacement));

        Assert.Equal(HotkeyRegistration.Registered, probe.Register(HotkeyAction.ShowApp, old));
        Assert.Equal(HotkeyRegistration.Taken, probe.Register(HotkeyAction.ToggleScreen, replacement));
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void ReRegister_TheSameGesture_Succeeds()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var hotkeys = new WindowsGlobalHotkeys();
        var gesture = Gesture.Next();

        Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(HotkeyAction.ToggleRecording, gesture));
        Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(HotkeyAction.ToggleRecording, gesture));
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void Null_ClearsTheAction_AndFreesItsGesture()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var hotkeys = new WindowsGlobalHotkeys();
        using var probe = new WindowsGlobalHotkeys();
        var gesture = Gesture.Next();
        Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(HotkeyAction.ToggleScreen, gesture));

        Assert.Equal(HotkeyRegistration.Cleared, hotkeys.Register(HotkeyAction.ToggleScreen, null));

        Assert.Equal(HotkeyRegistration.Registered, probe.Register(HotkeyAction.ToggleScreen, gesture));
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void FailedRegister_LeavesTheActionWithoutAHotkey()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var hotkeys = new WindowsGlobalHotkeys();
        using var holder = new WindowsGlobalHotkeys();
        using var probe = new WindowsGlobalHotkeys();
        var mine = Gesture.Next();
        var foreign = Gesture.Next();
        Assert.Equal(HotkeyRegistration.Registered, holder.Register(HotkeyAction.ShowApp, foreign));
        Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(HotkeyAction.ToggleRecording, mine));

        Assert.Equal(HotkeyRegistration.Taken, hotkeys.Register(HotkeyAction.ToggleRecording, foreign));
        Assert.Equal(HotkeyRegistration.Invalid, hotkeys.Register(HotkeyAction.ToggleScreen, "nonsense"));

        // The previous gesture of the failed action was released.
        Assert.Equal(HotkeyRegistration.Registered, probe.Register(HotkeyAction.ToggleScreen, mine));
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void InvalidText_ReleasesThePreviousGesture()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var hotkeys = new WindowsGlobalHotkeys();
        using var probe = new WindowsGlobalHotkeys();
        var gesture = Gesture.Next();
        Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(HotkeyAction.ShowApp, gesture));

        Assert.Equal(HotkeyRegistration.Invalid, hotkeys.Register(HotkeyAction.ShowApp, "Shift+A"));

        Assert.Equal(HotkeyRegistration.Registered, probe.Register(HotkeyAction.ShowApp, gesture));
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void ManyActions_CanBeRegisteredAtOnce()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var hotkeys = new WindowsGlobalHotkeys();

        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(action, Gesture.Next()));
        }
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void Dispose_JoinsTheThread_AndReleasesTheGestures()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var hotkeys = new WindowsGlobalHotkeys();
        var a = Gesture.Next();
        var b = Gesture.Next();
        Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(HotkeyAction.ToggleRecording, a));
        Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(HotkeyAction.ShowApp, b));
        Assert.True(hotkeys.HasLiveThread);

        hotkeys.Dispose();

        Assert.False(hotkeys.HasLiveThread);
        Assert.Throws<ObjectDisposedException>(() => hotkeys.Register(HotkeyAction.ToggleScreen, Gesture.Next()));
        using var second = new WindowsGlobalHotkeys();
        Assert.Equal(HotkeyRegistration.Registered, second.Register(HotkeyAction.ToggleRecording, a));
        Assert.Equal(HotkeyRegistration.Registered, second.Register(HotkeyAction.ShowApp, b));
        hotkeys.Dispose(); // still idempotent
    }

    [Fact]
    [Trait("Category", "Windows")]
    public async Task ConcurrentRegisters_FromManyThreads_AllSucceed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var hotkeys = new WindowsGlobalHotkeys();
        // One gesture per action, sized from the enum: an action added later (TogglePause) is covered too.
        var actions = Enum.GetValues<HotkeyAction>();
        var gestures = actions.Select(_ => Gesture.Next()).ToArray();
        var results = new HotkeyRegistration[actions.Length, 20];

        var tasks = actions.Select(action => Task.Run(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                results[(int)action, i] = hotkeys.Register(action, gestures[(int)action]);
            }
        }, TestContext.Current.CancellationToken)).ToArray();
        await Task.WhenAll(tasks);

        foreach (var result in results)
        {
            Assert.Equal(HotkeyRegistration.Registered, result);
        }
    }

    // ---- simulated presses (SendInput) --------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Windows")]
    public void Press_RaisesPressedOnce_OnTheHotkeyThread()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var hotkeys = new WindowsGlobalHotkeys();
        var gesture = Gesture.Next();
        var presses = new List<(HotkeyAction Action, string? Thread, bool Background)>();
        using var pressed = new ManualResetEventSlim(false);
        hotkeys.Pressed += action =>
        {
            lock (presses)
            {
                presses.Add((action, Thread.CurrentThread.Name, Thread.CurrentThread.IsBackground));
            }

            pressed.Set();
        };
        Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(HotkeyAction.ToggleScreen, gesture));

        PressOrSkip(gesture, pressed);

        Thread.Sleep(300); // a duplicate (key repeat) would have arrived by now
        lock (presses)
        {
            var press = Assert.Single(presses);
            Assert.Equal(HotkeyAction.ToggleScreen, press.Action);
            Assert.Equal(WindowsGlobalHotkeys.ThreadName, press.Thread);
            Assert.True(press.Background);
        }
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void HeldKey_RaisesPressedOnce()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var hotkeys = new WindowsGlobalHotkeys();
        var gesture = Gesture.Next();
        var count = 0;
        using var pressed = new ManualResetEventSlim(false);
        hotkeys.Pressed += _ =>
        {
            Interlocked.Increment(ref count);
            pressed.Set();
        };
        Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(HotkeyAction.ShowApp, gesture));

        // Key-down repeated five times without a key-up, as auto-repeat does: MOD_NOREPEAT must swallow the repeats.
        PressOrSkip(gesture, pressed, repeatDowns: 5);

        Thread.Sleep(300);
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void PressesOfDifferentActions_AreTold_ApartAndAfterClearingNoMore()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var hotkeys = new WindowsGlobalHotkeys();
        var g1 = Gesture.Next();
        var g2 = Gesture.Next();
        var seen = new List<HotkeyAction>();
        using var pressed = new AutoResetEvent(false);
        hotkeys.Pressed += action =>
        {
            lock (seen)
            {
                seen.Add(action);
            }

            pressed.Set();
        };
        Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(HotkeyAction.ToggleRecording, g1));
        Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(HotkeyAction.ShowApp, g2));

        SkipUnlessPressWorks(g1, pressed);
        Assert.True(KeyboardSimulator.Press(g2));
        Assert.True(pressed.WaitOne(PressTimeout));
        Assert.Equal(HotkeyRegistration.Cleared, hotkeys.Register(HotkeyAction.ToggleRecording, null));
        KeyboardSimulator.Press(g1);
        Thread.Sleep(300);

        lock (seen)
        {
            Assert.Equal([HotkeyAction.ToggleRecording, HotkeyAction.ShowApp], seen);
        }
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void ThrowingHandler_DoesNotStopTheLoop_OrOtherHandlers()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var hotkeys = new WindowsGlobalHotkeys();
        var gesture = Gesture.Next();
        var good = 0;
        var faults = 0;
        using var pressed = new AutoResetEvent(false);
        hotkeys.Pressed += _ => throw new InvalidOperationException("boom");
        hotkeys.Pressed += _ =>
        {
            Interlocked.Increment(ref good);
            pressed.Set();
        };
        hotkeys.Faulted += _ => Interlocked.Increment(ref faults);
        Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(HotkeyAction.ToggleRecording, gesture));

        SkipUnlessPressWorks(gesture, pressed);
        Assert.True(KeyboardSimulator.Press(gesture));
        Assert.True(pressed.WaitOne(PressTimeout));

        Assert.Equal(2, Volatile.Read(ref good));
        Assert.Equal(2, Volatile.Read(ref faults));
        Assert.Contains("boom", hotkeys.LastFault!.Message);
        Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(HotkeyAction.ShowApp, Gesture.Next())); // the loop still serves requests
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void Handler_MayRegister_AndDispose_WithoutDeadlock()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var hotkeys = new WindowsGlobalHotkeys();
        var gesture = Gesture.Next();
        var other = Gesture.Next();
        HotkeyRegistration? fromHandler = null;
        using var pressed = new ManualResetEventSlim(false);
        hotkeys.Pressed += _ =>
        {
            fromHandler = hotkeys.Register(HotkeyAction.ShowApp, other);
            hotkeys.Dispose();
            pressed.Set();
        };
        Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(HotkeyAction.ToggleRecording, gesture));

        PressOrSkip(gesture, pressed);

        Assert.Equal(HotkeyRegistration.Registered, fromHandler);
        hotkeys.Dispose(); // joins the thread that ended after the handler returned
        Assert.False(hotkeys.HasLiveThread);
        using var probe = new WindowsGlobalHotkeys();
        Assert.Equal(HotkeyRegistration.Registered, probe.Register(HotkeyAction.ToggleRecording, gesture));
        Assert.Equal(HotkeyRegistration.Registered, probe.Register(HotkeyAction.ShowApp, other));
    }

    private static void PressOrSkip(string gesture, ManualResetEventSlim pressed, int repeatDowns = 1)
    {
        if (!KeyboardSimulator.Press(gesture, repeatDowns))
        {
            Assert.Skip("SendInput injected nothing: this session has no interactive input desktop.");
        }

        if (!pressed.Wait(PressTimeout))
        {
            Assert.Skip("The simulated key press did not reach the hotkey within 2 s: no interactive input desktop on this runner.");
        }
    }

    private static void SkipUnlessPressWorks(string gesture, AutoResetEvent pressed)
    {
        if (!KeyboardSimulator.Press(gesture))
        {
            Assert.Skip("SendInput injected nothing: this session has no interactive input desktop.");
        }

        if (!pressed.WaitOne(PressTimeout))
        {
            Assert.Skip("The simulated key press did not reach the hotkey within 2 s: no interactive input desktop on this runner.");
        }
    }
}

/// <summary>Unique F13..F24 combinations with Ctrl/Alt/Shift mixes (48 of them, no Win key), handed out once per process.</summary>
internal static class Gesture
{
    private static readonly string[] Mixes = ["Ctrl+Alt+Shift", "Ctrl+Alt", "Ctrl+Shift", "Alt+Shift"];
    private static int s_next;

    public static string Next()
    {
        var n = Interlocked.Increment(ref s_next);
        var key = HotkeyKey.F13 + n % 12;
        var modifiers = Mixes[n / 12 % Mixes.Length];
        return $"{modifiers}+{key}";
    }
}

/// <summary>Types a <see cref="HotkeyGesture"/> with <c>SendInput</c> (key-down for modifiers and key, then key-up in reverse).</summary>
internal static unsafe partial class KeyboardSimulator
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;

    /// <summary>False when SendInput injected fewer events than asked (no input desktop, blocked by UIPI, ...).</summary>
    public static bool Press(string gestureText, int repeatDowns = 1)
    {
        if (!HotkeyGesture.TryParse(gestureText, out var gesture))
        {
            throw new ArgumentException("Not a hotkey gesture.", nameof(gestureText));
        }

        var keys = new List<ushort>();
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Ctrl))
        {
            keys.Add(0x11); // VK_CONTROL
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            keys.Add(0x12); // VK_MENU
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            keys.Add(0x10); // VK_SHIFT
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Meta))
        {
            keys.Add(0x5B); // VK_LWIN
        }

        var key = HotkeyKeyMap.ToVirtualKey(gesture.Key);
        var events = new List<Input>();
        foreach (var modifier in keys)
        {
            events.Add(Event(modifier, up: false));
        }

        for (var i = 0; i < repeatDowns; i++)
        {
            events.Add(Event(key, up: false));
        }

        events.Add(Event(key, up: true));
        for (var i = keys.Count - 1; i >= 0; i--)
        {
            events.Add(Event(keys[i], up: true));
        }

        var array = events.ToArray();
        uint sent;
        fixed (Input* pointer = array)
        {
            sent = SendInput((uint)array.Length, pointer, sizeof(Input));
        }

        return sent == array.Length;
    }

    private static Input Event(ushort vk, bool up) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = vk, Flags = up ? KeyEventKeyUp : 0 } },
    };

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, Input* inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    // The union is as large as MOUSEINPUT (32 bytes on x64, 24 on x86); sizeof(Input) is 40 / 28.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }
}

/// <summary>
/// Create/dispose cycles leave no threads or handles behind. In the solo collection: the process-wide handle and thread
/// counts must not be moved by tests running in parallel.
/// </summary>
[Trait("Category", "Windows")]
[Collection(nameof(HandleCountCollection))]
public sealed class GlobalHotkeysHandleLeakTests(ITestOutputHelper output)
{
    [Fact]
    public void CreateRegisterDispose_20Times_DoesNotGrowThreadsOrHandles()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Global hotkey tests run only on Windows.");
        var gesture = Gesture.Next();
        for (var i = 0; i < 5; i++)
        {
            Cycle(gesture); // warm-up: window class, JIT, thread pool; the same gesture again needs the previous dispose to release it
        }

        var (threads, handles) = Measure();
        output.WriteLine($"baseline: {threads} threads, {handles} handles");

        for (var i = 0; i < 20; i++)
        {
            Cycle(gesture);
            var (t, h) = Measure();
            output.WriteLine($"cycle {i + 1}: {t} threads, {h} handles");
        }

        var (finalThreads, finalHandles) = Measure();
        Assert.InRange(finalThreads, 0, threads + 2);
        Assert.InRange(finalHandles, 0, handles + 12);
    }

    private static void Cycle(string gesture)
    {
        var hotkeys = new WindowsGlobalHotkeys();
        Assert.Equal(HotkeyRegistration.Registered, hotkeys.Register(HotkeyAction.ToggleRecording, gesture));
        hotkeys.Dispose();
        Assert.False(hotkeys.HasLiveThread);
    }

    private static (int Threads, int Handles) Measure()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        return (process.Threads.Count, process.HandleCount);
    }
}
