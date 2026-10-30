using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Input.Interop;
using Murmur.Capture.Windows.Platform;
using Murmur.Core.Platform;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// <see cref="WindowsMotionPreference"/>. The first group replaces the system call and touches nothing native; the Windows group
/// reads the real <c>SPI_GETCLIENTAREAANIMATION</c> and drives the hidden window with a posted <c>WM_SETTINGCHANGE</c>.
/// </summary>
public sealed class WindowsMotionPreferenceTests
{
    private const string WindowsOnly = "The reduce-motion tests of the real system call run only on Windows.";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>The window is created on the thread a moment after the first subscription: waits for it.</summary>
    private static nint WaitForWindow(WindowsMotionPreference preference)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (preference.Window == 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        Assert.NotEqual(0, preference.Window);
        return preference.Window;
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
        using var raised = new SemaphoreSlim(0);
        var threads = new List<int>();
        preference.Changed += () =>
        {
            lock (threads)
            {
                threads.Add(Environment.CurrentManagedThreadId);
            }

            raised.Release();
        };

        var window = WaitForWindow(preference);
        Assert.True(preference.HasLiveThread);

        // Unrelated settings changes: the value is the same, nothing is raised.
        Assert.NotEqual(0, InputNativeMethods.PostMessageW(window, MotionNativeMethods.WmSettingChange, 0, 0));
        Assert.False(raised.Wait(TimeSpan.FromMilliseconds(300), Cancel));

        Volatile.Write(ref enabled, false);
        Assert.NotEqual(0, InputNativeMethods.PostMessageW(window, MotionNativeMethods.WmSettingChange, 0, 0));
        Assert.True(raised.Wait(Timeout, Cancel));
        Assert.True(preference.ReduceMotion);

        // The same value again: no second event.
        Assert.NotEqual(0, InputNativeMethods.PostMessageW(window, MotionNativeMethods.WmSettingChange, 0, 0));
        Assert.False(raised.Wait(TimeSpan.FromMilliseconds(300), Cancel));

        Volatile.Write(ref enabled, true);
        Assert.NotEqual(0, InputNativeMethods.PostMessageW(window, MotionNativeMethods.WmSettingChange, 0, 0));
        Assert.True(raised.Wait(Timeout, Cancel));
        Assert.False(preference.ReduceMotion);

        // Raised on the window's own thread, never on the caller's.
        lock (threads)
        {
            Assert.Equal(2, threads.Count);
            Assert.DoesNotContain(Environment.CurrentManagedThreadId, threads);
        }
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void Dispose_EndsTheWindowThread_AndStopsTheEvents()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var enabled = true;
        var preference = new WindowsMotionPreference(() => Volatile.Read(ref enabled));
        var count = 0;
        preference.Changed += () => Interlocked.Increment(ref count);
        var window = WaitForWindow(preference);
        Assert.True(preference.HasLiveThread);

        preference.Dispose();

        Assert.False(preference.HasLiveThread);
        Assert.Equal(0, preference.Window);
        Volatile.Write(ref enabled, false);
        InputNativeMethods.PostMessageW(window, MotionNativeMethods.WmSettingChange, 0, 0);
        Thread.Sleep(200);
        Assert.Equal(0, count);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void ASubscriberThatThrows_DoesNotStopTheOthers_NorTheWindow()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var enabled = true;
        using var preference = new WindowsMotionPreference(() => Volatile.Read(ref enabled));
        using var second = new SemaphoreSlim(0);
        preference.Changed += static () => throw new InvalidOperationException("boom");
        preference.Changed += () => second.Release();
        var window = WaitForWindow(preference);

        Volatile.Write(ref enabled, false);
        InputNativeMethods.PostMessageW(window, MotionNativeMethods.WmSettingChange, 0, 0);
        Assert.True(second.Wait(Timeout, Cancel));

        // The window still hears the next change.
        Volatile.Write(ref enabled, true);
        InputNativeMethods.PostMessageW(window, MotionNativeMethods.WmSettingChange, 0, 0);
        Assert.True(second.Wait(Timeout, Cancel));
        Assert.True(preference.HasLiveThread);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void TheWindow_IsTopLevel_AndHearsTheSystemBroadcast()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var enabled = true;
        using var preference = new WindowsMotionPreference(() => Volatile.Read(ref enabled));
        using var raised = new SemaphoreSlim(0);
        preference.Changed += () => raised.Release();
        var window = WaitForWindow(preference);

        // A message-only window has HWND_MESSAGE as its parent and never gets broadcasts; this one hangs off the desktop.
        Assert.Equal(MotionTestInterop.GetDesktopWindow(), MotionTestInterop.GetAncestor(window, MotionTestInterop.GaParent));

        // What Windows does when the setting changes: SPI_SETCLIENTAREAANIMATION, broadcast to every top-level window.
        Volatile.Write(ref enabled, false);
        MotionTestInterop.Broadcast(MotionNativeMethods.WmSettingChange, 0x1043);

        Assert.True(raised.Wait(Timeout, Cancel));
        Assert.True(preference.ReduceMotion);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void AForeignShutdownMessage_DoesNotEndTheWindow()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var enabled = true;
        using var preference = new WindowsMotionPreference(() => Volatile.Read(ref enabled));
        using var raised = new SemaphoreSlim(0);
        preference.Changed += () => raised.Release();
        var window = WaitForWindow(preference);

        // WM_APP + 2 is the private shutdown message; any process may post it to a top-level window.
        InputNativeMethods.PostMessageW(window, InputConstants.WmShutdown, 0, 0);
        Volatile.Write(ref enabled, false);
        InputNativeMethods.PostMessageW(window, MotionNativeMethods.WmSettingChange, 0, 0);

        Assert.True(raised.Wait(Timeout, Cancel));
        Assert.True(preference.HasLiveThread);
        Assert.Equal(window, preference.Window);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void DisposeInsideAChangedHandler_EndsTheThread_WithoutDeadlock()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var enabled = true;
        var preference = new WindowsMotionPreference(() => Volatile.Read(ref enabled));
        using var done = new SemaphoreSlim(0);
        preference.Changed += () =>
        {
            preference.Dispose();
            done.Release();
        };
        var window = WaitForWindow(preference);

        Volatile.Write(ref enabled, false);
        InputNativeMethods.PostMessageW(window, MotionNativeMethods.WmSettingChange, 0, 0);

        Assert.True(done.Wait(Timeout, Cancel));
        var deadline = DateTime.UtcNow + Timeout;
        while (preference.HasLiveThread && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        Assert.False(preference.HasLiveThread);
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

            Assert.False(preference.HasLiveThread);
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

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static partial nint SendMessageTimeoutW(nint window, uint message, nuint wParam, nint lParam, uint flags, uint timeout, nuint* result);

    /// <summary>Broadcasts <paramref name="message"/> to every top-level window, waiting at most a second for each.</summary>
    public static void Broadcast(uint message, nuint wParam)
    {
        nuint result;
        SendMessageTimeoutW(HwndBroadcast, message, wParam, 0, SmtoAbortIfHung, 1000, &result);
    }
}
