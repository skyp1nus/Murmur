using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Input.Interop;
using Murmur.Capture.Windows.Platform;
using Murmur.Core.Platform;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// <see cref="WindowsMotionPreference"/>. The first group replaces the system call and touches nothing native; the Windows group
/// reads the real <c>SPI_GETCLIENTAREAANIMATION</c> and drives the hidden window with a <em>sent</em> <c>WM_SETTINGCHANGE</c> (what the system does; Windows refuses to <em>post</em> that message,
/// which is how the first CI run found out). Sending is synchronous, so the tests never sleep to wait for the window thread.
/// </summary>
public sealed class WindowsMotionPreferenceTests
{
    private const string WindowsOnly = "The reduce-motion tests of the real system call run only on Windows.";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>The window is created on the thread a moment after the first subscription: waits for it, with the state in the message.</summary>
    private static nint WaitForWindow(WindowsMotionPreference preference)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (preference.Window == 0 && DateTime.UtcNow < deadline && !Cancel.IsCancellationRequested)
        {
            Thread.Sleep(5);
        }

        Assert.True(preference.Window != 0, $"The hidden window was not created within {Timeout.TotalSeconds} s ({State(preference)}).");
        return preference.Window;
    }

    private static string State(WindowsMotionPreference preference) =>
        $"window=0x{preference.Window:X}, threadAlive={preference.HasLiveThread}, reduceMotion={preference.ReduceMotion}";

    /// <summary>
    /// Sends <c>WM_SETTINGCHANGE</c> to the window and returns once its handler has run on the window thread. Failure
    /// (a dead window, a hung thread) fails the test with the Win32 error and the state.
    /// </summary>
    private static void SendSettingChange(WindowsMotionPreference preference, nint window, uint message = MotionNativeMethods.WmSettingChange)
    {
        var ok = MotionTestInterop.Send(window, message, 0, out var error);
        Assert.True(ok, $"Sending 0x{message:X} to 0x{window:X} failed with Win32 error {error} ({State(preference)}).");
    }

    /// <summary>Counts the <see cref="IMotionPreference.Changed"/> events and remembers the thread of each.</summary>
    private sealed class Events
    {
        private readonly SemaphoreSlim _raised = new(0);
        private readonly List<int> _threads = [];

        public int Count
        {
            get
            {
                lock (_threads)
                {
                    return _threads.Count;
                }
            }
        }

        public int[] Threads
        {
            get
            {
                lock (_threads)
                {
                    return [.. _threads];
                }
            }
        }

        public void Raise()
        {
            lock (_threads)
            {
                _threads.Add(Environment.CurrentManagedThreadId);
            }

            _raised.Release();
        }

        /// <summary>Waits (bounded) for one more event than <paramref name="seen"/>.</summary>
        public bool WaitForNext(int seen)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (Count <= seen)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !_raised.Wait(left, Cancel))
                {
                    return Count > seen;
                }
            }

            return true;
        }
    }

    // ---- no native calls: any OS ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ReduceMotion_IsTheOppositeOfAnimationEffects(bool animationsEnabled, bool expected)
    {
        using var preference = new WindowsMotionPreference(() => animationsEnabled);

        Assert.Equal(expected, preference.ReduceMotion);
    }

    [Fact]
    public void ReduceMotion_AsksTheSystemEveryTime()
    {
        var enabled = true;
        using var preference = new WindowsMotionPreference(() => enabled);

        Assert.False(preference.ReduceMotion);
        enabled = false;
        Assert.True(preference.ReduceMotion);
    }

    [Fact]
    public void NewInstance_TouchesNothingNative_AndDisposeIsRepeatable()
    {
        var preference = new WindowsMotionPreference(() => true);

        Assert.False(preference.HasLiveThread);
        Assert.Equal(0, preference.Window);
        preference.Dispose();
        preference.Dispose();
    }

    [Fact]
    public void SubscribingAfterDispose_DoesNothing()
    {
        var preference = new WindowsMotionPreference(() => true);
        preference.Dispose();

        preference.Changed += static () => { };

        Assert.False(preference.HasLiveThread);
    }

    [Fact]
    public void CreateMotionPreference_IsTheWindowsOne()
    {
        var preference = WindowsCapture.CreateMotionPreference();

        Assert.IsType<WindowsMotionPreference>(preference);
        (preference as IDisposable)?.Dispose();
    }

    // ---- real system: Windows only ---------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Windows")]
    public void RealSystemCall_AnswersWithoutThrowing_AndAgreesWithADirectRead()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var preference = new WindowsMotionPreference();

        var first = preference.ReduceMotion;

        Assert.Equal(!MotionNativeMethods.ClientAreaAnimationEnabled(), first);
        Assert.Equal(first, preference.ReduceMotion);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void SettingChange_RaisesChangedOnce_OnlyWhenTheValueDiffers()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var enabled = true;
        using var preference = new WindowsMotionPreference(() => Volatile.Read(ref enabled));
        var events = new Events();
        preference.Changed += events.Raise;
        var window = WaitForWindow(preference);
        Assert.True(preference.HasLiveThread, State(preference));

        // Unrelated settings changes: the value is the same, nothing is raised.
        SendSettingChange(preference, window);
        Assert.Equal(0, events.Count);

        Volatile.Write(ref enabled, false);
        SendSettingChange(preference, window);
        Assert.True(events.WaitForNext(0), $"No event after the value flipped to reduced ({State(preference)}, events={events.Count}).");
        Assert.True(preference.ReduceMotion);

        // The same value again: no second event.
        SendSettingChange(preference, window);
        Assert.Equal(1, events.Count);

        Volatile.Write(ref enabled, true);
        SendSettingChange(preference, window);
        Assert.True(events.WaitForNext(1), $"No event after the value flipped back ({State(preference)}, events={events.Count}).");
        Assert.False(preference.ReduceMotion);

        // Raised on the window's own thread, never on the caller's.
        var threads = events.Threads;
        Assert.Equal(2, threads.Length);
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, threads);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void Dispose_EndsTheWindowThread_AndStopsTheEvents()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var enabled = true;
        var preference = new WindowsMotionPreference(() => Volatile.Read(ref enabled));
        var events = new Events();
        preference.Changed += events.Raise;
        var window = WaitForWindow(preference);
        Assert.True(preference.HasLiveThread, State(preference));

        preference.Dispose();

        Assert.False(preference.HasLiveThread, "The window thread outlived Dispose.");
        Assert.Equal(0, preference.Window);
        Volatile.Write(ref enabled, false);

        // The window is gone: the send fails, and nothing is raised.
        Assert.False(MotionTestInterop.Send(window, MotionNativeMethods.WmSettingChange, 0, out _));
        Assert.Equal(0, events.Count);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void ASubscriberThatThrows_DoesNotStopTheOthers_NorTheWindow()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var enabled = true;
        using var preference = new WindowsMotionPreference(() => Volatile.Read(ref enabled));
        var events = new Events();
        preference.Changed += static () => throw new InvalidOperationException("boom");
        preference.Changed += events.Raise;
        var window = WaitForWindow(preference);

        Volatile.Write(ref enabled, false);
        SendSettingChange(preference, window);
        Assert.True(events.WaitForNext(0), $"The second subscriber was not reached ({State(preference)}, events={events.Count}).");

        // The window still hears the next change.
        Volatile.Write(ref enabled, true);
        SendSettingChange(preference, window);
        Assert.True(events.WaitForNext(1), $"The window stopped hearing changes ({State(preference)}, events={events.Count}).");
        Assert.True(preference.HasLiveThread, State(preference));
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void TheWindow_IsTopLevel_AndHearsTheSystemBroadcast()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var enabled = true;
        using var preference = new WindowsMotionPreference(() => Volatile.Read(ref enabled));
        var events = new Events();
        preference.Changed += events.Raise;
        var window = WaitForWindow(preference);

        // A message-only window has HWND_MESSAGE as its parent and never gets broadcasts; this one hangs off the desktop.
        Assert.Equal(MotionTestInterop.GetDesktopWindow(), MotionTestInterop.GetAncestor(window, MotionTestInterop.GaParent));

        // What Windows does when the setting changes: SPI_SETCLIENTAREAANIMATION, broadcast to every top-level window.
        Volatile.Write(ref enabled, false);
        MotionTestInterop.Broadcast(MotionNativeMethods.WmSettingChange, 0x1043);

        Assert.True(events.WaitForNext(0), $"The broadcast did not reach the window ({State(preference)}, events={events.Count}).");
        Assert.True(preference.ReduceMotion);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void AForeignShutdownMessage_DoesNotEndTheWindow()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var enabled = true;
        using var preference = new WindowsMotionPreference(() => Volatile.Read(ref enabled));
        var events = new Events();
        preference.Changed += events.Raise;
        var window = WaitForWindow(preference);

        // WM_APP + 2 is the private shutdown message; any process may send it to a top-level window.
        SendSettingChange(preference, window, InputConstants.WmShutdown);
        Assert.Equal(window, preference.Window);

        Volatile.Write(ref enabled, false);
        SendSettingChange(preference, window);

        Assert.True(events.WaitForNext(0), $"The window stopped hearing changes after a foreign shutdown message ({State(preference)}, events={events.Count}).");
        Assert.True(preference.HasLiveThread, State(preference));
        Assert.Equal(window, preference.Window);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void DisposeInsideAChangedHandler_EndsTheThread_WithoutDeadlock()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var enabled = true;
        var preference = new WindowsMotionPreference(() => Volatile.Read(ref enabled));
        var events = new Events();
        preference.Changed += () =>
        {
            preference.Dispose();
            events.Raise();
        };
        var window = WaitForWindow(preference);

        Volatile.Write(ref enabled, false);
        SendSettingChange(preference, window);

        Assert.True(events.WaitForNext(0), $"The handler did not run ({State(preference)}, events={events.Count}).");
        var deadline = DateTime.UtcNow + Timeout;
        while (preference.HasLiveThread && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
        }

        Assert.False(preference.HasLiveThread, $"The window thread did not end after Dispose inside the handler ({State(preference)}).");
        Assert.Equal(0, preference.Window);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void Subscribing_DoesNotWaitForTheWindow_AndADisposeRightAfterLeavesNoThread()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        for (var i = 0; i < 20; i++)
        {
            var preference = new WindowsMotionPreference(() => true);
            preference.Changed += static () => { };
            preference.Dispose();

            var deadline = DateTime.UtcNow + Timeout;
            while (preference.HasLiveThread && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(5);
            }

            Assert.False(preference.HasLiveThread, $"Round {i}: the window thread outlived Dispose ({State(preference)}).");
            Assert.Equal(0, preference.Window);
        }
    }
}

/// <summary>user32 calls only the tests of the hidden window need.</summary>
internal static unsafe partial class MotionTestInterop
{
    public const uint GaParent = 1;

    private const nint HwndBroadcast = 0xFFFF;
    private const uint SmtoAbortIfHung = 0x0002;

    [LibraryImport("user32.dll")]
    public static partial nint GetDesktopWindow();

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static partial nint SendMessageTimeoutW(nint window, uint message, nuint wParam, nint lParam, uint flags, uint timeout, nuint* result);

    /// <summary>Broadcasts <paramref name="message"/> to every top-level window, waiting at most a second for each.</summary>
    public static void Broadcast(uint message, nuint wParam)
    {
        nuint result;
        SendMessageTimeoutW(HwndBroadcast, message, wParam, 0, SmtoAbortIfHung, 1000, &result);
    }

    /// <summary>Sends <paramref name="message"/> to one window and waits (at most 5 s) until its procedure returned; false = it failed or timed out.</summary>
    public static bool Send(nint window, uint message, nuint wParam, out int error)
    {
        nuint result;
        var ok = SendMessageTimeoutW(window, message, wParam, 0, SmtoAbortIfHung, 5000, &result) != 0;
        error = ok ? 0 : Marshal.GetLastPInvokeError();
        return ok;
    }
}
