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
/// </remarks>
public sealed unsafe class WasapiAudioSessionMonitor : IAudioSessionMonitor
{
    /// <summary>How long a wake waits for the rest of a burst of session callbacks before the sessions are read.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(100);

    // mmdeviceapi.h: enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }
    private const int ECommunications = 2;

    private static readonly Guid IidIAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly AudioActivitySnapshot Idle = new(false, []);

    private readonly int _ownProcessId;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, string> _names = [];
    private AudioActivitySnapshot _snapshot = Idle;
    private Thread? _thread;
    private AutoResetEvent? _wake;
    private volatile bool _stopping;
    private volatile bool _endpointsDirty;
    private bool _disposed;

    // Owned by the monitor thread.
    private readonly List<Endpoint> _endpoints = [];
    private SessionCallbacks? _callbacks;
    private nint _notification;
    private nint _events;
    private nint _client;

    /// <param name="ownProcessId">Murmur's own process; <see cref="Environment.ProcessId"/> by default.</param>
    public WasapiAudioSessionMonitor(int? ownProcessId = null) => _ownProcessId = ownProcessId ?? Environment.ProcessId;

    public bool IsSupported => true;

    public event Action? Changed;

    public AudioActivitySnapshot Snapshot() => Volatile.Read(ref _snapshot);

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null)
            {
                return;
            }

            _stopping = false;
            _endpointsDirty = true;
            _wake = new AutoResetEvent(true);
            _thread = new Thread(Run) { IsBackground = true, Name = "Murmur audio sessions" };
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        AutoResetEvent? wake;
        lock (_gate)
        {
            thread = _thread;
            wake = _wake;
            _thread = null;
            _wake = null;
        }

        if (thread is null)
        {
            return;
        }

        _stopping = true;
        wake?.Set();
        thread.Join(TimeSpan.FromSeconds(5));
        wake?.Dispose();
        Volatile.Write(ref _snapshot, Idle);
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            _disposed = true;
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
            _wake?.Set();
        }
        catch (ObjectDisposedException)
        {
            // Stopped in between.
        }
    }

    private void Run()
    {
        var wake = _wake!;
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            using var apartment = ComHelpers.EnterMta();
            enumerator = AudioEndpoints.CreateEnumerator();
            _callbacks = new SessionCallbacks(Wake);
            _notification = (nint)ComInterfaceMarshaller<IAudioSessionNotification>.ConvertToUnmanaged(_callbacks);
            _events = (nint)ComInterfaceMarshaller<IAudioSessionEvents>.ConvertToUnmanaged(_callbacks);
            _client = (nint)ComInterfaceMarshaller<IMMNotificationClient>.ConvertToUnmanaged(_callbacks);
            ComHelpers.Check(enumerator.RegisterEndpointNotificationCallback(_client), "IMMDeviceEnumerator.RegisterEndpointNotificationCallback");

            while (true)
            {
                wake.WaitOne();
                if (_stopping)
                {
                    break;
                }

                // A burst of callbacks (several sessions of one app) is read once.
                wake.WaitOne(Settle);
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

                    Publish(Read());
                }
                catch (COMException ex)
                {
                    // The audio service restarted or a device went away: look the endpoints up again on the next event.
                    _endpointsDirty = true;
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

    private void Publish(AudioActivitySnapshot next)
    {
        var previous = Volatile.Read(ref _snapshot);
        if (AudioSessionActivity.Same(previous, next))
        {
            return;
        }

        Volatile.Write(ref _snapshot, next);
        Changed?.Invoke();
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
        if (enumerator.GetDefaultAudioEndpoint(flow, role, out var pDevice) < 0 || pDevice == 0)
        {
            return null; // no endpoint of this kind (E_NOTFOUND)
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
            ComHelpers.Check(device.Activate(in IidIAudioSessionManager2, WasapiConstants.ClsctxAll, 0, out var pManager), "IMMDevice.Activate(IAudioSessionManager2)");
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

        var snapshot = AudioSessionActivity.Build(readings, _ownProcessId, NameOf);
        PruneNames(snapshot);
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

    private string NameOf(int processId)
    {
        if (_names.TryGetValue(processId, out var name))
        {
            return name;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            name = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            name = "process " + processId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        _names[processId] = name;
        return name;
    }

    private void PruneNames(AudioActivitySnapshot snapshot)
    {
        if (_names.Count <= 64)
        {
            return;
        }

        var keep = snapshot.Rendering.Select(r => r.ProcessId).ToHashSet();
        foreach (var pid in _names.Keys.Where(p => !keep.Contains(p)).ToArray())
        {
            _names.Remove(pid);
        }
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
