using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Murmur.Capture.Windows.Audio;
using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Core.Calls;
using Murmur.Core.Capture;

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
        // Runs anywhere: off Windows the runner's thread fails to join COM and ends by itself (and is then no longer running);
        // Stop and Start never wait for it.
        var windows = OperatingSystem.IsWindows();
        using var monitor = new WasapiAudioSessionMonitor(Own, () => new FakeEnumerator(), TimeSpan.FromMilliseconds(30));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 50; i++)
        {
            monitor.Start();
            Assert.True(!windows || monitor.IsRunning);
            monitor.Stop();
            Assert.False(monitor.IsRunning);
        }

        monitor.Start();
        monitor.Start();
        Assert.True(!windows || monitor.IsRunning);
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
    public async Task OnWindows_ItsOwnPlayback_IsNeverReported()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The audio sessions are read only on Windows.");
        Assert.SkipUnless(AudioEndpoints.HasActive(AudioDeviceKind.SystemOutput), "No active render endpoint on this machine.");

        // A real render stream of this process that writes only zeros: an active session, but nothing audible.
        using var dir = new TempDir();
        var silence = dir.Write("silence.wav", TestWav.Pcm16(new short[16_000 * 4]));
        using var player = new WindowsCapture().CreatePlayer();
        await player.OpenAsync([silence], TestContext.Current.CancellationToken);

        // A monitor that does not know this process is its own proves the session is really there ...
        using var observer = new WasapiAudioSessionMonitor(ownProcessId: 1);
        using var own = new WasapiAudioSessionMonitor();
        observer.Start();
        own.Start();
        player.Play();
        try
        {
            var seen = SpinWait.SpinUntil(() => observer.Snapshot().Rendering.Any(r => r.ProcessId == Environment.ProcessId), TimeSpan.FromSeconds(3));
            Assert.True(seen, "The silent render stream of this process never showed up as a session.");
            Thread.Sleep(300);

            // ... and the monitor of this very process never reports it.
            Assert.DoesNotContain(own.Snapshot().Rendering, r => r.ProcessId == Environment.ProcessId);
        }
        finally
        {
            player.Pause();
        }
    }

    // ---- Process names ---------------------------------------------------------------------------------------------------

    [Fact]
    public void AProcessIdReusedByAnotherProcess_IsResolvedAgain()
    {
        var starts = new Dictionary<int, long> { [1234] = 100 };
        var names = new Dictionary<int, string> { [1234] = "ms-teams" };
        var lookups = 0;
        var cache = new ProcessNameCache(pid => starts[pid], pid =>
        {
            lookups++;
            return names[pid];
        });

        Assert.Equal("ms-teams", cache.NameOf(1234));
        Assert.Equal("ms-teams", cache.NameOf(1234));
        Assert.Equal(1, lookups);

        // Teams exited; Chrome's audio process got the same id (a different start time) before any read dropped the entry.
        starts[1234] = 200;
        names[1234] = "chrome";

        Assert.Equal("chrome", cache.NameOf(1234));
        Assert.Equal(2, lookups);
    }

    [Fact]
    public void AProcessWithoutAKnownStartTime_IsNeverServedFromTheCache()
    {
        var name = "a";
        var cache = new ProcessNameCache(_ => 0, _ => name);

        Assert.Equal("a", cache.NameOf(7));
        name = "b";
        Assert.Equal("b", cache.NameOf(7));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Retain_DropsTheProcessesThatAreNotRenderingAnyMore()
    {
        var cache = new ProcessNameCache(_ => 5, pid => "p" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        cache.NameOf(1);
        cache.NameOf(2);
        cache.NameOf(3);

        cache.Retain([new RenderingProcess(2, "p2")]);

        Assert.Equal(1, cache.Count);
        cache.Retain([]);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void TheDefaultResolver_NamesThisProcess_AndAnUnknownIdGetsAPlaceholder()
    {
        var cache = new ProcessNameCache();

        Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().ProcessName, cache.NameOf(Environment.ProcessId));
        Assert.Equal("process 2147483000", cache.NameOf(2_147_483_000));
    }

    // ---- Failures of the audio service -------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Windows")]
    public void OnWindows_WhenTheEnumeratorCannotBeCreated_TheMonitorIsNotRunning_AndStartTriesAgain()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The audio sessions are read only on Windows.");
        var attempts = 0;
        using var monitor = new WasapiAudioSessionMonitor(Own, () =>
        {
            Interlocked.Increment(ref attempts);
            throw new COMException("no audio service", WasapiConstants.AudclntEServiceNotRunning);
        }, TimeSpan.FromMilliseconds(20));

        monitor.Start();
        Assert.True(SpinWait.SpinUntil(() => !monitor.IsRunning, TimeSpan.FromSeconds(5)), "The failed runner stayed current.");

        monitor.Start();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref attempts) == 2, TimeSpan.FromSeconds(5)), "Start did not try again.");
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void OnWindows_WhenTheAudioServiceIsDown_TheEndpointsAreLookedUpAgain_UntilItIsBack()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The audio sessions are read only on Windows.");
        var fake = new FakeEnumerator { FailUntilCall = 3 };
        using var monitor = new WasapiAudioSessionMonitor(Own, () => fake, TimeSpan.FromMilliseconds(30));

        monitor.Start();

        // No event ever arrives: only the retry timer can bring the later look-ups.
        Assert.True(SpinWait.SpinUntil(() => fake.DefaultLookups >= 6, TimeSpan.FromSeconds(10)), $"Only {fake.DefaultLookups} look-ups.");
        Assert.True(monitor.IsRunning);
        Assert.False(monitor.Snapshot().MicrophoneInUse);
        Assert.Empty(monitor.Snapshot().Rendering);

        // Once the service answers (E_NOTFOUND: no device) the look-ups settle: no retry storm.
        var settled = fake.DefaultLookups;
        Thread.Sleep(400);
        Assert.Equal(settled, fake.DefaultLookups);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void OnWindows_Stop_UnregistersTheCallback()
    {
        // (That the unregister comes before CoUninitialize is guaranteed by the Run / RunCore structure; a test cannot tell, as the
        // runtime already puts a new thread in the MTA and EnterMta only adds a reference.)
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The audio sessions are read only on Windows.");
        var fake = new FakeEnumerator();
        using var monitor = new WasapiAudioSessionMonitor(Own, () => fake, TimeSpan.FromMilliseconds(30));
        monitor.Start();
        Assert.True(SpinWait.SpinUntil(() => fake.DefaultLookups > 0, TimeSpan.FromSeconds(5)));

        monitor.Stop();

        Assert.True(SpinWait.SpinUntil(() => fake.Unregistered, TimeSpan.FromSeconds(5)), "The notification client was never unregistered.");
        Assert.True(fake.Registered);
    }

    [Fact]
    [Trait("Category", "Windows")]
    public void OnWindows_WhenAnEndpointCannotBeOpenedYet_ItIsOpenedByTheRetryTimer_WithoutAnyDeviceEvent()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The audio sessions are read only on Windows.");
        var manager = new FakeSessionManager();
        var fake = new FakeEnumerator { ActivateFailures = 3, Manager = manager };
        using var monitor = new WasapiAudioSessionMonitor(Own, () => fake, TimeSpan.FromMilliseconds(30));

        monitor.Start();

        // Activate fails (the audio service is coming back); nothing announces its return, only the timer can retry.
        Assert.True(SpinWait.SpinUntil(() => manager.Registrations >= 1, TimeSpan.FromSeconds(10)), $"Activate was tried {fake.Activations} times.");
        Assert.Equal(4, fake.Activations);
        Assert.True(monitor.IsRunning);

        // Opened: the retries stop.
        Thread.Sleep(300);
        Assert.Equal(4, fake.Activations);
        Assert.Equal(1, manager.Registrations);
    }

    /// <summary>An MMDevice enumerator with no devices (E_NOTFOUND) that fails with "audio service not running" for its first calls.</summary>
    private sealed unsafe class FakeEnumerator : IMMDeviceEnumerator
    {
        private int _lookups;

        public int FailUntilCall { get; init; }

        public int DefaultLookups => Volatile.Read(ref _lookups);

        public bool Registered { get; private set; }

        public bool Unregistered { get; private set; }

        /// <summary>When set, there is one render device whose session manager cannot be activated this many times.</summary>
        public int? ActivateFailures { get; init; }

        public FakeSessionManager? Manager { get; init; }

        public int Activations => Volatile.Read(ref _activations);

        private int _activations;

        public int EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices)
        {
            devices = 0;
            return WasapiConstants.ENotFound;
        }

        public int GetDefaultAudioEndpoint(int dataFlow, int role, out nint endpoint)
        {
            endpoint = 0;
            if (Interlocked.Increment(ref _lookups) <= FailUntilCall)
            {
                return WasapiConstants.AudclntEServiceNotRunning;
            }

            if (ActivateFailures is null || dataFlow != WasapiConstants.ERender)
            {
                return WasapiConstants.ENotFound;
            }

            endpoint = NewDevice();
            return 0;
        }

        public int GetDevice(string id, out nint device)
        {
            device = ActivateFailures is null ? 0 : NewDevice();
            return device == 0 ? WasapiConstants.ENotFound : 0;
        }

        private nint NewDevice() =>
            (nint)ComInterfaceMarshaller<IMMDevice>.ConvertToUnmanaged(new FakeSessionDevice(() => Interlocked.Increment(ref _activations) > ActivateFailures!.Value ? Manager : null));

        public int RegisterEndpointNotificationCallback(nint client)
        {
            Registered = true;
            return 0;
        }

        public int UnregisterEndpointNotificationCallback(nint client)
        {
            Unregistered = true;
            return 0;
        }
    }
}

/// <summary>An endpoint device whose session manager activates only once <c>manager</c> answers (null = the audio service is down).</summary>
[GeneratedComClass]
internal sealed unsafe partial class FakeSessionDevice(Func<FakeSessionManager?> manager) : IMMDevice
{
    public int Activate(in Guid iid, uint clsCtx, nint activationParams, out nint ppInterface)
    {
        ppInterface = 0;
        if (manager() is not { } m)
        {
            return WasapiConstants.AudclntEServiceNotRunning;
        }

        ppInterface = (nint)ComInterfaceMarshaller<IAudioSessionManager2>.ConvertToUnmanaged(m);
        return 0;
    }

    public int OpenPropertyStore(uint stgmAccess, out nint properties)
    {
        properties = 0;
        return FakeCom.ENotImpl;
    }

    public int GetId(out nint id)
    {
        id = Marshal.StringToCoTaskMemUni("{fake-render}");
        return 0;
    }

    public int GetState(out uint state)
    {
        state = 1;
        return 0;
    }
}

/// <summary>A session manager with no sessions that counts the notification registrations it gets.</summary>
[GeneratedComClass]
internal sealed unsafe partial class FakeSessionManager : IAudioSessionManager2
{
    private int _registrations;

    public int Registrations => Volatile.Read(ref _registrations);

    public int GetAudioSessionControl(nint audioSessionGuid, uint streamFlags, out nint sessionControl)
    {
        sessionControl = 0;
        return FakeCom.ENotImpl;
    }

    public int GetSimpleAudioVolume(nint audioSessionGuid, uint streamFlags, out nint audioVolume)
    {
        audioVolume = 0;
        return FakeCom.ENotImpl;
    }

    public int GetSessionEnumerator(out nint sessionEnum)
    {
        sessionEnum = (nint)ComInterfaceMarshaller<IAudioSessionEnumerator>.ConvertToUnmanaged(new FakeNoSessions());
        return 0;
    }

    public int RegisterSessionNotification(nint sessionNotification)
    {
        Interlocked.Increment(ref _registrations);
        return 0;
    }

    public int UnregisterSessionNotification(nint sessionNotification) => 0;

    public int RegisterDuckNotification(nint sessionId, nint duckNotification) => FakeCom.ENotImpl;

    public int UnregisterDuckNotification(nint duckNotification) => FakeCom.ENotImpl;
}

[GeneratedComClass]
internal sealed partial class FakeNoSessions : IAudioSessionEnumerator
{
    public int GetCount(out int sessionCount)
    {
        sessionCount = 0;
        return 0;
    }

    public int GetSession(int index, out nint session)
    {
        session = 0;
        return FakeCom.ENotImpl;
    }
}
