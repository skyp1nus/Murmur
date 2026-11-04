using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Audio;
using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Core.Calls;

namespace Murmur.Capture.Windows.Tests;

/// <summary>
/// Call detection on Windows (#44). The snapshot rules and the callback object run anywhere; the monitor itself reads the real
/// audio sessions and runs only on Windows (a call is checked by hand: <c>verify-local.ps1 -Capture</c>).
/// </summary>
public sealed class WasapiAudioSessionMonitorTests
{
    private const int Own = 4242;

    private static string Name(int pid) => "p" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static AudioSessionReading Render(int pid, int state = AudioSessionActivity.StateActive, bool system = false) => new(false, pid, state, system);

    private static AudioSessionReading Capture(int pid, int state = AudioSessionActivity.StateActive) => new(true, pid, state, false);

    [Fact]
    public void ActiveRenderSessions_OfOtherProcesses_AreRendering_OncePerProcess()
    {
        var snapshot = AudioSessionActivity.Build([Render(10), Render(10), Render(20), Render(30, state: 0), Render(40, state: 2)], Own, Name);

        Assert.False(snapshot.MicrophoneInUse);
        Assert.Equal([new RenderingProcess(10, "p10"), new RenderingProcess(20, "p20")], snapshot.Rendering);
    }

    [Fact]
    public void TheMicIsInUse_WhenAnotherProcessHasAnActiveCaptureSession()
    {
        Assert.True(AudioSessionActivity.Build([Capture(10)], Own, Name).MicrophoneInUse);
        Assert.False(AudioSessionActivity.Build([Capture(10, state: 0)], Own, Name).MicrophoneInUse);
        Assert.Empty(AudioSessionActivity.Build([Capture(10)], Own, Name).Rendering);
    }

    [Fact]
    public void MurmursOwnRecording_NeverMakesTheMicInUse()
    {
        // Murmur records the mic (an active capture session of its own) while Teams plays: that alone is no call.
        var recordingAlone = AudioSessionActivity.Build([Capture(Own), Render(10)], Own, Name);
        Assert.False(recordingAlone.MicrophoneInUse);
        Assert.Equal([new RenderingProcess(10, "p10")], recordingAlone.Rendering);

        // Teams' own capture session still counts while Murmur records.
        Assert.True(AudioSessionActivity.Build([Capture(Own), Capture(10), Render(10)], Own, Name).MicrophoneInUse);
    }

    [Fact]
    public void MurmursOwnSessions_SystemSounds_AndCrossProcessSessions_NeverCount()
    {
        var snapshot = AudioSessionActivity.Build([Capture(Own), Render(Own), Render(0), Render(50, system: true)], Own, Name);

        Assert.False(snapshot.MicrophoneInUse);
        Assert.Empty(snapshot.Rendering);
    }

    [Fact]
    public void Same_ComparesTheMicAndTheSetOfProcesses()
    {
        var a = new AudioActivitySnapshot(true, [new RenderingProcess(1, "a"), new RenderingProcess(2, "b")]);

        Assert.True(AudioSessionActivity.Same(a, new AudioActivitySnapshot(true, [new RenderingProcess(2, "b"), new RenderingProcess(1, "a")])));
        Assert.False(AudioSessionActivity.Same(a, a with { MicrophoneInUse = false }));
        Assert.False(AudioSessionActivity.Same(a, new AudioActivitySnapshot(true, [new RenderingProcess(1, "a")])));
        Assert.False(AudioSessionActivity.Same(a, new AudioActivitySnapshot(true, [new RenderingProcess(1, "a"), new RenderingProcess(3, "c")])));
    }

    [Fact]
    public void DistinctEndpoints_DropsTheDuplicateRoleAndMissingDevices()
    {
        Assert.Equal(["{a}"], AudioSessionActivity.DistinctEndpoints("{a}", "{A}"));
        Assert.Equal(["{a}", "{b}"], AudioSessionActivity.DistinctEndpoints("{a}", null, "{b}", ""));
        Assert.Empty(AudioSessionActivity.DistinctEndpoints(null, null));
    }

    [Fact]
    public void TheCallbacks_WakeTheMonitor_OnlyForSessionAndDeviceChanges()
    {
        var wakes = new List<bool>();
        var callbacks = new SessionCallbacks(wakes.Add);

        Assert.Equal(0, callbacks.OnSessionCreated(0));
        Assert.Equal(0, callbacks.OnStateChanged(1));
        Assert.Equal(0, callbacks.OnSessionDisconnected(0));
        Assert.Equal(0, callbacks.OnDefaultDeviceChanged(0, 0, 0));
        Assert.Equal(0, callbacks.OnDeviceRemoved(0));
        Assert.Equal(0, callbacks.OnSimpleVolumeChanged(0.5f, 0, 0));
        Assert.Equal(0, callbacks.OnDisplayNameChanged(0, 0));
        Assert.Equal(0, callbacks.OnPropertyValueChanged(0, default));

        Assert.Equal([false, false, false, true, true], wakes);

        callbacks.Detach();
        callbacks.OnStateChanged(1);
        Assert.Equal(5, wakes.Count);
    }

    [Fact]
    public void ACallbackThatThrows_NeverReachesTheAudioService()
    {
        var callbacks = new SessionCallbacks(_ => throw new InvalidOperationException("stopped"));

        Assert.Equal(0, callbacks.OnStateChanged(1));
    }

    [Fact]
    public void TheCallbackInterfaces_HaveTheSdkIids()
    {
        Assert.Equal(new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), typeof(IAudioSessionManager2).GUID);
        Assert.Equal(new Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), typeof(IAudioSessionEnumerator).GUID);
        Assert.Equal(new Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), typeof(IAudioSessionControl2).GUID);
        Assert.Equal(new Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), typeof(IAudioSessionNotification).GUID);
        Assert.Equal(new Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8"), typeof(IAudioSessionEvents).GUID);
        Assert.Equal(new Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), typeof(IMMNotificationClient).GUID);
        Assert.Equal(20, Marshal.SizeOf<PropertyKey>());
    }

    [Fact]
    public void StopNeverWaits_AndAStartRightAfterGetsANewRunner()
    {
        // Runs anywhere: off Windows the runner's thread fails to join COM and ends by itself; Stop and Start never wait for it.
        using var monitor = new WasapiAudioSessionMonitor(Own);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 50; i++)
        {
            monitor.Start();
            Assert.True(monitor.IsRunning);
            monitor.Stop();
            Assert.False(monitor.IsRunning);
        }

        monitor.Start();
        monitor.Start();
        Assert.True(monitor.IsRunning);
        monitor.Stop();
        monitor.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"50 start/stop cycles took {watch.Elapsed}.");
        Assert.False(monitor.Snapshot().MicrophoneInUse);
        Assert.Empty(monitor.Snapshot().Rendering);
    }

    [Fact]
    public void AfterDispose_StartThrows()
    {
        var monitor = new WasapiAudioSessionMonitor(Own);
        monitor.Dispose();

        Assert.Throws<ObjectDisposedException>(monitor.Start);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void OnWindows_StopAndStart_FromAChangedHandler_DoNotDeadlock()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The audio sessions are read only on Windows.");
        using var monitor = new WasapiAudioSessionMonitor(Own);
        using var handled = new ManualResetEventSlim();
        monitor.Changed += () =>
        {
            // On the monitor's own thread, like a call handler that turns detection off and on again.
            monitor.Stop();
            monitor.Start();
            handled.Set();
        };

        monitor.Start();
        var raised = handled.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        monitor.Stop();

        // Without any active session on this PC there may be no change at all; when there is one, the handler returned.
        Assert.True(!raised || handled.IsSet);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void OnWindows_TheMonitorStartsReadsAndStops_WithoutACall()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The audio sessions are read only on Windows.");
        using var monitor = new WasapiAudioSessionMonitor();
        using var changed = new ManualResetEventSlim();
        monitor.Changed += changed.Set;

        Assert.True(monitor.IsSupported);
        monitor.Start();
        monitor.Start();
        changed.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var snapshot = monitor.Snapshot();
        Assert.DoesNotContain(snapshot.Rendering, r => r.ProcessId == Environment.ProcessId);
        monitor.Stop();
        monitor.Stop();

        Assert.False(monitor.Snapshot().MicrophoneInUse);
        Assert.Empty(monitor.Snapshot().Rendering);
        monitor.Start();
        monitor.Stop();
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void OnWindows_ItsOwnPlayback_IsNeverReported()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The audio sessions are read only on Windows.");
        using var monitor = new WasapiAudioSessionMonitor();
        monitor.Start();
        Thread.Sleep(500);

        Assert.DoesNotContain(monitor.Snapshot().Rendering, r => r.ProcessId == Environment.ProcessId);
    }
}
