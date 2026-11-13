using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Core.Calls;

namespace Murmur.Capture.Windows.Audio;

/// <summary>
/// The Windows audio-session activity for call detection (#44): the sessions of the default render and capture endpoints (console
/// and communications roles), read with <c>IAudioSessionManager2</c> / <c>IAudioSessionEnumerator</c> / <c>IAudioSessionControl2</c>.
/// Nothing leaves the PC; no audio is read, only which process has a session in which state.
/// </summary>
/// <remarks>
/// <para><b>Events, not polling.</b> One background MTA thread owns every COM object. It registers an
/// <c>IAudioSessionNotification</c> on each endpoint (new sessions), an <c>IAudioSessionEvents</c> on each session (state changes,
/// disconnects) and an <c>IMMNotificationClient</c> (default device changes). Every callback only wakes the thread, which re-reads
/// the sessions, publishes a new <see cref="Snapshot"/> and raises <see cref="Changed"/> when it differs. Between events the thread
/// sleeps; while stopped there is no thread at all.</para>
/// <para><b>Coalescing.</b> After a wake the thread waits <see cref="Settle"/> for the burst of callbacks a single change brings
/// (a call app opens several sessions at once) and reads once.</para>
/// <para>Mic in use = another process has an active session on a default capture endpoint; rendering = processes with an active
/// session on a default render endpoint (not the system sounds, not this process).</para>
/// <para><b>Stop never waits.</b> It only signals the thread and returns (see <see cref="IAudioSessionMonitor.Stop"/>): the call
/// detector stops the monitor under its own lock and a call handler may run on this thread. Each start gets its own runner (thread,
/// COM objects, wake event), so a start right after a stop never shares anything with the runner that is still winding down, and a
/// stopped runner can no longer publish a snapshot or raise <see cref="Changed"/>.</para>
/// </remarks>
public sealed unsafe class WasapiAudioSessionMonitor : IAudioSessionMonitor
{
    /// <summary>How long a wake waits for the rest of a burst of session callbacks before the sessions are read.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(100);

    // mmdeviceapi.h: enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }
    private const int ECommunications = 2;

    private static readonly Guid IidIAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly AudioActivitySnapshot Idle = new(false, []);

    /// <summary>How long the thread waits before it looks the endpoints up again after the audio service failed.</summary>
    internal static readonly TimeSpan DefaultRetry = TimeSpan.FromSeconds(3);

    private readonly int _ownProcessId;
    private readonly Func<IMMDeviceEnumerator> _createEnumerator;
    private readonly TimeSpan _retry;
    private readonly Lock _gate = new();
    private AudioActivitySnapshot _snapshot = Idle;
    private Runner? _runner;
    private bool _disposed;

    /// <param name="ownProcessId">Murmur's own process; <see cref="Environment.ProcessId"/> by default.</param>
    public WasapiAudioSessionMonitor(int? ownProcessId = null)
        : this(ownProcessId ?? Environment.ProcessId, AudioEndpoints.CreateEnumerator, DefaultRetry)
    {
    }

    /// <summary>For tests: the MMDevice enumerator and the retry interval are supplied.</summary>
    internal WasapiAudioSessionMonitor(int ownProcessId, Func<IMMDeviceEnumerator> createEnumerator, TimeSpan retry)
    {
        _ownProcessId = ownProcessId;
        _createEnumerator = createEnumerator;
        _retry = retry;
    }

    public bool IsSupported => true;

    public event Action? Changed;

    /// <summary>Whether a runner is current (started and not stopped); for tests.</summary>
    internal bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _runner is not null;
            }
        }
    }

    public AudioActivitySnapshot Snapshot() => Volatile.Read(ref _snapshot);

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runner is not null)
            {
                return;
            }

            _runner = new Runner(this);
            _runner.Begin();
        }
    }

    /// <summary>Signals the thread to release its COM objects and end, and returns at once (never waits for it).</summary>
    public void Stop()
    {
        Runner? runner;
        lock (_gate)
        {
            runner = _runner;
            _runner = null;
            Volatile.Write(ref _snapshot, Idle);
        }

        runner?.RequestStop();
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            _disposed = true;
        }
    }

    /// <summary>A runner's new reading: published (and <see cref="Changed"/> raised) only while that runner is the current one.</summary>
    private void Publish(Runner runner, AudioActivitySnapshot next)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_runner, runner) || AudioSessionActivity.Same(Volatile.Read(ref _snapshot), next))
            {
                return;
            }

            Volatile.Write(ref _snapshot, next);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// A runner's thread ended by itself (its setup failed): it is no longer current, so <see cref="IsRunning"/> is false and
    /// <see cref="Start"/> can try again. A no-op when <see cref="Stop"/> already retired it.
    /// </summary>
    private void Retire(Runner runner)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_runner, runner))
            {
                _runner = null;
                Volatile.Write(ref _snapshot, Idle);
            }
        }
    }

    /// <summary>One start of the monitor: its thread and everything that thread owns.</summary>
    private sealed class Runner(WasapiAudioSessionMonitor owner)
    {
        private readonly AutoResetEvent _wake = new(true);
        private readonly ProcessNameCache _names = new();
        private readonly List<Endpoint> _endpoints = [];
        private readonly int _ownProcessId = owner._ownProcessId;
        private volatile bool _stopping;
        private volatile bool _endpointsDirty = true;
        private SessionCallbacks? _callbacks;
        private nint _notification;
        private nint _events;
        private nint _client;

        public void Begin() => new Thread(Run) { IsBackground = true, Name = "Murmur audio sessions" }.Start();

        public void RequestStop()
        {
            _stopping = true;
            try
            {
                _wake.Set();
            }
            catch (ObjectDisposedException)
            {
                // The thread has already ended.
            }
        }

        private void Wake(bool endpoints)
        {
            if (endpoints)
            {
                _endpointsDirty = true;
            }

            try
            {
                _wake.Set();
            }
            catch (ObjectDisposedException)
            {
                // The thread has already ended.
            }
        }

        private void Run()
        {
            try
            {
                // The apartment outlives everything RunCore releases: CoUninitialize comes last.
                using var apartment = ComHelpers.EnterMta();
                RunCore();
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Audio session monitor stopped: " + ex.Message);
            }
            finally
            {
                // A thread that ended by itself leaves nothing published and is no longer "running", so a later Start retries.
                owner.Publish(this, Idle);
                owner.Retire(this);
            }
        }

        private void RunCore()
        {
            var wake = _wake;
            IMMDeviceEnumerator? enumerator = null;
            try
            {
                enumerator = owner._createEnumerator();
                _callbacks = new SessionCallbacks(Wake);
                _notification = (nint)ComInterfaceMarshaller<IAudioSessionNotification>.ConvertToUnmanaged(_callbacks);
                _events = (nint)ComInterfaceMarshaller<IAudioSessionEvents>.ConvertToUnmanaged(_callbacks);
                _client = (nint)ComInterfaceMarshaller<IMMNotificationClient>.ConvertToUnmanaged(_callbacks);
                ComHelpers.Check(enumerator.RegisterEndpointNotificationCallback(_client), "IMMDeviceEnumerator.RegisterEndpointNotificationCallback");

                while (true)
                {
                    // While the endpoints could not be read (the audio service restarted) no event may ever come: look again by timer.
                    if (_endpointsDirty)
                    {
                        wake.WaitOne(owner._retry);
                    }
                    else
                    {
                        wake.WaitOne();
                    }

                    if (_stopping)
                    {
                        break;
                    }

                    // A burst of callbacks (several sessions of one app) is read once: sleep through it (a wait on the event would
                    // end with the burst's second callback), then clear the wake-ups it left, the read covers them.
                    Thread.Sleep(Settle);
                    wake.Reset();
                    if (_stopping)
                    {
                        break;
                    }

                    try
                    {
                        if (_endpointsDirty)
                        {
                            _endpointsDirty = false;
                            OpenEndpoints(enumerator);
                        }

                        owner.Publish(this, Read());
                    }
                    catch (COMException ex)
                    {
                        // The audio service restarted or a device went away: what was published is stale, so report no activity
                        // (a running call must end) and look the endpoints up again on the next event or retry.
                        _endpointsDirty = true;
                        owner.Publish(this, Idle);
                        Trace.WriteLine("Audio session monitor: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Audio session monitor stopped: " + ex.Message);
            }
            finally
            {
                _callbacks?.Detach();
                CloseEndpoints();
                if (enumerator is not null)
                {
                    if (_client != 0)
                    {
                        enumerator.UnregisterEndpointNotificationCallback(_client);
                    }

                    ComHelpers.Release(enumerator);
                }

                ReleasePointer(ref _client);
                ReleasePointer(ref _events);
                ReleasePointer(ref _notification);
                _callbacks = null;
                _wake.Dispose();
            }
        }

        private static void ReleasePointer(ref nint pointer)
        {
            if (pointer != 0)
            {
                Marshal.Release(pointer);
                pointer = 0;
            }
        }

        // ---- Endpoints ---------------------------------------------------------------------------------------------------------

        private void OpenEndpoints(IMMDeviceEnumerator enumerator)
        {
            CloseEndpoints();
            foreach (var (flow, capture) in new[] { (WasapiConstants.ERender, false), (WasapiConstants.ECapture, true) })
            {
                var ids = AudioSessionActivity.DistinctEndpoints(DefaultId(enumerator, flow, WasapiConstants.EConsole), DefaultId(enumerator, flow, ECommunications));
                foreach (var id in ids)
                {
                    if (OpenEndpoint(enumerator, id, capture) is { } endpoint)
                    {
                        _endpoints.Add(endpoint);
                    }
                }
            }
        }

        private static string? DefaultId(IMMDeviceEnumerator enumerator, int flow, int role)
        {
            var hr = enumerator.GetDefaultAudioEndpoint(flow, role, out var pDevice);
            if (hr == WasapiConstants.ENotFound)
            {
                return null; // no endpoint of this kind
            }

            // Any other failure (the audio service is down) is not "no device": it is retried.
            ComHelpers.Check(hr, "IMMDeviceEnumerator.GetDefaultAudioEndpoint");
            if (pDevice == 0)
            {
                return null;
            }

            var device = ComHelpers.Wrap<IMMDevice>(pDevice, "IMMDeviceEnumerator.GetDefaultAudioEndpoint");
            try
            {
                if (device.GetId(out var pId) < 0 || pId == 0)
                {
                    return null;
                }

                try
                {
                    return Marshal.PtrToStringUni(pId);
                }
                finally
                {
                    NativeMethods.CoTaskMemFree(pId);
                }
            }
            finally
            {
                ComHelpers.Release(device);
            }
        }

        private Endpoint? OpenEndpoint(IMMDeviceEnumerator enumerator, string id, bool capture)
        {
            if (enumerator.GetDevice(id, out var pDevice) < 0)
            {
                return null;
            }

            var device = ComHelpers.Wrap<IMMDevice>(pDevice, "IMMDeviceEnumerator.GetDevice");
            try
            {
                if (device.Activate(in IidIAudioSessionManager2, WasapiConstants.ClsctxAll, 0, out var pManager) < 0 || pManager == 0)
                {
                    return null; // this endpoint cannot be watched (gone, disabled): the others still are
                }

                var manager = ComHelpers.Wrap<IAudioSessionManager2>(pManager, "IMMDevice.Activate(IAudioSessionManager2)");
                var endpoint = new Endpoint(manager, capture);

                // The session notifications only arrive after the sessions have been enumerated once (documented WASAPI behaviour).
                ReleaseEnumerator(manager);
                if (manager.RegisterSessionNotification(_notification) >= 0)
                {
                    endpoint.Registered = true;
                }

                return endpoint;
            }
            finally
            {
                ComHelpers.Release(device);
            }
        }

        private static void ReleaseEnumerator(IAudioSessionManager2 manager)
        {
            if (manager.GetSessionEnumerator(out var pEnum) >= 0 && pEnum != 0)
            {
                ComHelpers.Release(ComHelpers.Wrap<IAudioSessionEnumerator>(pEnum, "IAudioSessionManager2.GetSessionEnumerator"));
            }
        }

        private void CloseEndpoints()
        {
            foreach (var endpoint in _endpoints)
            {
                foreach (var session in endpoint.Sessions.Values)
                {
                    session.Control.UnregisterAudioSessionNotification(_events);
                    ComHelpers.Release(session.Control);
                }

                endpoint.Sessions.Clear();
                if (endpoint.Registered)
                {
                    endpoint.Manager.UnregisterSessionNotification(_notification);
                }

                ComHelpers.Release(endpoint.Manager);
            }

            _endpoints.Clear();
        }

        // ---- Sessions ----------------------------------------------------------------------------------------------------------

        /// <summary>Reads every session of the open endpoints, follows the new ones (state events) and lets go of the vanished ones.</summary>
        private AudioActivitySnapshot Read()
        {
            var readings = new List<AudioSessionReading>();
            foreach (var endpoint in _endpoints)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                ComHelpers.Check(endpoint.Manager.GetSessionEnumerator(out var pEnum), "IAudioSessionManager2.GetSessionEnumerator");
                var sessions = ComHelpers.Wrap<IAudioSessionEnumerator>(pEnum, "IAudioSessionManager2.GetSessionEnumerator");
                try
                {
                    ComHelpers.Check(sessions.GetCount(out var count), "IAudioSessionEnumerator.GetCount");
                    for (var i = 0; i < count; i++)
                    {
                        if (sessions.GetSession(i, out var pSession) < 0 || pSession == 0)
                        {
                            continue;
                        }

                        var control = ComHelpers.Wrap<IAudioSessionControl2>(pSession, "IAudioSessionEnumerator.GetSession");
                        var keep = false;
                        try
                        {
                            if (ReadSession(control) is not { } reading)
                            {
                                continue;
                            }

                            seen.Add(reading.Key);
                            readings.Add(reading.Reading with { IsCapture = endpoint.IsCapture });
                            if (!endpoint.Sessions.ContainsKey(reading.Key) && control.RegisterAudioSessionNotification(_events) >= 0)
                            {
                                endpoint.Sessions[reading.Key] = new Session(control);
                                keep = true;
                            }
                        }
                        finally
                        {
                            if (!keep)
                            {
                                ComHelpers.Release(control);
                            }
                        }
                    }
                }
                finally
                {
                    ComHelpers.Release(sessions);
                }

                foreach (var key in endpoint.Sessions.Keys.Where(k => !seen.Contains(k)).ToArray())
                {
                    var gone = endpoint.Sessions[key];
                    gone.Control.UnregisterAudioSessionNotification(_events);
                    ComHelpers.Release(gone.Control);
                    endpoint.Sessions.Remove(key);
                }
            }

            var snapshot = AudioSessionActivity.Build(readings, _ownProcessId, _names.NameOf);
            _names.Retain(snapshot.Rendering);
            return snapshot;
        }

        private static (string Key, AudioSessionReading Reading)? ReadSession(IAudioSessionControl2 control)
        {
            if (control.GetSessionInstanceIdentifier(out var pId) < 0 || pId == 0)
            {
                return null;
            }

            string key;
            try
            {
                key = Marshal.PtrToStringUni(pId) ?? string.Empty;
            }
            finally
            {
                NativeMethods.CoTaskMemFree(pId);
            }

            var state = control.GetState(out var s) >= 0 ? s : 0;
            var hr = control.GetProcessId(out var pid);
            var processId = hr == WasapiConstants.SOk ? (int)pid : 0; // AUDCLNT_S_NO_SINGLE_PROCESS: a cross-process session
            var system = control.IsSystemSoundsSession() == WasapiConstants.SOk;
            return (key, new AudioSessionReading(false, processId, state, system));
        }

        private sealed class Endpoint(IAudioSessionManager2 manager, bool isCapture)
        {
            public IAudioSessionManager2 Manager { get; } = manager;

            public bool IsCapture { get; } = isCapture;

            public bool Registered { get; set; }

            public Dictionary<string, Session> Sessions { get; } = new(StringComparer.Ordinal);
        }

        private sealed record Session(IAudioSessionControl2 Control);
    }
}
