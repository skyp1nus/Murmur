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

        Assert.True(preference.HasLiveThread);
        var window = preference.Window;
        Assert.NotEqual(0, window);

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
        var window = preference.Window;
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
        var window = preference.Window;

        Volatile.Write(ref enabled, false);
        InputNativeMethods.PostMessageW(window, MotionNativeMethods.WmSettingChange, 0, 0);
        Assert.True(second.Wait(Timeout, Cancel));

        // The window still hears the next change.
        Volatile.Write(ref enabled, true);
        InputNativeMethods.PostMessageW(window, MotionNativeMethods.WmSettingChange, 0, 0);
        Assert.True(second.Wait(Timeout, Cancel));
        Assert.True(preference.HasLiveThread);
    }
}
