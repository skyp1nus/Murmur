using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Audio;

/// <summary>
/// One WASAPI shared-mode stream: a capture endpoint (microphone) or a render endpoint in loopback mode.
/// Event-driven (AUDCLNT_STREAMFLAGS_EVENTCALLBACK, ~20 ms buffer) on a dedicated MTA thread registered with the
/// MMCSS "Audio" task. Delivers the native mix format as interleaved float and honours the wall-clock continuity
/// contract of <see cref="IAudioSink"/>: gaps (idle loopback, discontinuities, the tail at stop) are filled with
/// <see cref="IAudioSink.WriteSilence"/> and resumed packets are placed by their QPC position (see <see cref="CaptureClock"/>).
/// All COM objects used for streaming are created, used and released on the capture thread.
/// UNVERIFIED-ON-WINDOWS until the Windows-trait tests run in CI.
/// </summary>
internal sealed unsafe class WasapiAudioSource : IAudioSource
{
    /// <summary>Requested endpoint buffer: 20 ms in REFERENCE_TIME (100 ns) units.</summary>
    internal const long RequestedBufferDuration = 200_000;

    /// <summary>Wake-up period when no packet arrives (idle loopback): bounds the latency of the silence fill.</summary>
    internal const uint WaitTimeoutMilliseconds = 100;

    private readonly Lock _gate = new();
    private readonly byte[] _mixFormatBytes;
    private readonly MixFormat _mixFormat;
    private readonly bool _loopback;
    private Thread? _thread;
    private nint _stopEvent;
    private int _faulted;
    private bool _disposed;

    public WasapiAudioSource(EndpointInfo endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        Device = endpoint.Device;
        _mixFormatBytes = endpoint.MixFormatBytes;
        _mixFormat = endpoint.MixFormat;
        _loopback = endpoint.Device.Kind == AudioDeviceKind.SystemOutput;
        Format = _mixFormat.AudioFormat;
    }

    public AudioDevice Device { get; }

    public AudioFormat Format { get; }

    internal MixFormat MixFormat => _mixFormat;

    public event Action<CaptureFault>? Faulted;

    public void Start(IAudioSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null)
            {
                throw new InvalidOperationException("The audio source is already started.");
            }

            var stopEvent = NativeMethods.CreateEventW(0, 1, 0, 0); // manual reset, non-signalled
            if (stopEvent == 0)
            {
                throw new COMException("CreateEventW(stop) failed.", Marshal.GetHRForLastWin32Error());
            }

            var run = new CaptureRun(sink, stopEvent);
            var thread = new Thread(Run)
            {
                IsBackground = true,
                Name = _loopback ? "Murmur WASAPI loopback" : "Murmur WASAPI microphone",
                Priority = ThreadPriority.Highest,
            };
            Volatile.Write(ref _faulted, 0);

            // Publish before the thread runs: once Ready is set the capture thread may fault (first Drain, or the
            // sink throwing on its first Write) and a Faulted handler may call Stop() on the capture thread before
            // this method returns. Stop must then recognise its own thread instead of trying to join it.
            _stopEvent = stopEvent;
            Volatile.Write(ref _thread, thread);
            thread.Start(run);
            run.Ready.Wait(); // not disposed: the capture thread may still be returning from Set()

            if (run.StartError is not null)
            {
                thread.Join();
                Volatile.Write(ref _thread, null);
                _stopEvent = 0;
                NativeMethods.CloseHandle(stopEvent);
                if (AudioAccess.IsAccessDenied(run.StartError))
                {
                    // Windows privacy settings: a clear, typed error the recorder can turn into a warning.
                    throw AudioAccess.Denied(Device.Kind, Device.Name, run.StartError);
                }

                throw new InvalidOperationException($"Could not start WASAPI capture on '{Device.Name}': {run.StartError.Message}", run.StartError);
            }
        }
    }

    public void Stop()
    {
        // Called from a Faulted handler on the capture thread: just ask the loop to end; a later Stop/Dispose joins.
        if (Volatile.Read(ref _thread) == Thread.CurrentThread)
        {
            NativeMethods.SetEvent(_stopEvent);
            return;
        }

        lock (_gate)
        {
            var thread = _thread;
            if (thread is null)
            {
                return;
            }

            NativeMethods.SetEvent(_stopEvent);
            if (thread == Thread.CurrentThread)
            {
                // Defensive: never self-join; the owner's later Stop/Dispose joins and releases the handle.
                return;
            }

            thread.Join();
            NativeMethods.CloseHandle(_stopEvent);
            _stopEvent = 0;
            Volatile.Write(ref _thread, null);
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            _disposed = true;
        }
    }

    /// <summary>
    /// Capture thread entry. Nothing may escape it (an unhandled exception on a thread ends the process): a start
    /// failure goes back to <see cref="Start"/> through <see cref="CaptureRun.StartError"/>, anything later is raised
    /// as <see cref="Faulted"/>. The outer guard only matters if the cleanup itself throws.
    /// </summary>
    private void Run(object? state)
    {
        var run = (CaptureRun)state!;
        try
        {
            RunCore(run);
        }
        catch (Exception ex)
        {
            if (!run.Started)
            {
                run.StartError ??= ex;
                run.Ready.Set();
            }
            else
            {
                RaiseFault(ex.Message);
            }
        }
    }

    private void RunCore(CaptureRun run)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        nint audioEvent = 0;
        nint mmcss = 0;
        var started = false;
        var comHr = NativeMethods.CoInitializeEx(0, WasapiConstants.CoinitMultithreaded);
        var result = WasapiConstants.SOk;
        try
        {
            if (comHr < 0 && comHr != WasapiConstants.RpcEChangedMode)
            {
                ComHelpers.Check(comHr, "CoInitializeEx(COINIT_MULTITHREADED)");
            }

            // MMCSS "Audio" task; failure (e.g. the service is disabled) only costs scheduling priority.
            uint taskIndex = 0;
            mmcss = NativeMethods.AvSetMmThreadCharacteristicsW("Audio", ref taskIndex);

            enumerator = AudioEndpoints.CreateEnumerator();
            ComHelpers.Check(enumerator.GetDevice(Device.Id, out var pDevice), "IMMDeviceEnumerator.GetDevice");
            device = ComHelpers.Wrap<IMMDevice>(pDevice, "IMMDeviceEnumerator.GetDevice");

            ComHelpers.Check(device.Activate(in WasapiConstants.IidIAudioClient, WasapiConstants.ClsctxAll, 0, out var pClient), "IMMDevice.Activate(IAudioClient)");
            client = ComHelpers.Wrap<IAudioClient>(pClient, "IMMDevice.Activate(IAudioClient)");

            var streamFlags = WasapiConstants.AudclntStreamFlagsEventCallback | (_loopback ? WasapiConstants.AudclntStreamFlagsLoopback : 0u);
            fixed (byte* pFormat = _mixFormatBytes)
            {
                // Shared mode + event callback: periodicity must be 0.
                ComHelpers.Check(
                    client.Initialize(WasapiConstants.AudclntShareModeShared, streamFlags, RequestedBufferDuration, 0, (nint)pFormat, 0),
                    _loopback ? "IAudioClient.Initialize(SHARED, EVENTCALLBACK | LOOPBACK)" : "IAudioClient.Initialize(SHARED, EVENTCALLBACK)");
            }

            ComHelpers.Check(client.GetBufferSize(out var bufferFrames), "IAudioClient.GetBufferSize");

            audioEvent = NativeMethods.CreateEventW(0, 0, 0, 0); // auto reset
            if (audioEvent == 0)
            {
                throw new COMException("CreateEventW(audio) failed.", Marshal.GetHRForLastWin32Error());
            }

            ComHelpers.Check(client.SetEventHandle(audioEvent), "IAudioClient.SetEventHandle");
            ComHelpers.Check(client.GetService(in WasapiConstants.IidIAudioCaptureClient, out var pCapture), "IAudioClient.GetService(IAudioCaptureClient)");
            capture = ComHelpers.Wrap<IAudioCaptureClient>(pCapture, "IAudioClient.GetService(IAudioCaptureClient)");

            var rate = _mixFormat.SampleRate;
            var tolerance = Math.Max(2 * (int)bufferFrames, rate / 50);  // ~2 buffer periods, >= 20 ms
            var chunk = Math.Max((int)bufferFrames, rate / 100);         // a packet never exceeds the buffer
            var pump = new CapturePump(_mixFormat, chunk, tolerance);

            if (NativeMethods.QueryPerformanceFrequency(out var qpcFrequency) == 0 || qpcFrequency <= 0)
            {
                throw new InvalidOperationException("QueryPerformanceFrequency failed.");
            }

            pump.Start(run.Sink, NowTicks(qpcFrequency));
            ComHelpers.Check(client.Start(), "IAudioClient.Start");
            started = true;
            run.Started = true;
            run.Ready.Set();

            result = Loop(pump, capture, audioEvent, run.StopEvent, qpcFrequency);
        }
        catch (Exception ex) when (!run.Started)
        {
            run.StartError = ex;
        }
        catch (Exception ex)
        {
            // Includes exceptions thrown by the sink: the stream cannot continue.
            RaiseFault(ex.Message);
        }
        finally
        {
            if (started)
            {
                client!.Stop();
            }

            ComHelpers.Release(capture);
            ComHelpers.Release(client);
            ComHelpers.Release(device);
            ComHelpers.Release(enumerator);
            if (audioEvent != 0)
            {
                NativeMethods.CloseHandle(audioEvent);
            }

            if (mmcss != 0)
            {
                NativeMethods.AvRevertMmThreadCharacteristics(mmcss);
            }

            if (comHr >= 0)
            {
                NativeMethods.CoUninitialize();
            }

            if (!run.Started)
            {
                run.Ready.Set();
            }
        }

        if (result < 0)
        {
            RaiseFault(ComHelpers.Describe(result));
        }
    }

    /// <summary>The steady-state loop. Allocation-free: no LINQ, no async, no logging, no locks.</summary>
    private static int Loop(CapturePump pump, IAudioCaptureClient capture, nint audioEvent, nint stopEvent, long qpcFrequency)
    {
        var handles = stackalloc nint[2];
        handles[0] = audioEvent;
        handles[1] = stopEvent;
        while (true)
        {
            var wait = NativeMethods.WaitForMultipleObjects(2, handles, 0, WaitTimeoutMilliseconds);
            if (wait == WasapiConstants.WaitObject0 + 1)
            {
                break;
            }

            if (wait == WasapiConstants.WaitFailed)
            {
                return Marshal.GetHRForLastWin32Error();
            }

            // Both on the audio event and on the timeout: drain whatever is there, then fill a gap if packets stopped.
            var hr = pump.Drain(capture, NowTicks(qpcFrequency));
            if (hr < 0)
            {
                return hr;
            }

            pump.Idle(NowTicks(qpcFrequency));
        }

        var last = pump.Drain(capture, NowTicks(qpcFrequency));
        if (last < 0)
        {
            return last;
        }

        pump.Finish(NowTicks(qpcFrequency));
        return WasapiConstants.SOk;
    }

    /// <summary>QPC in 100 ns units, the unit of the packets' pu64QPCPosition.</summary>
    private static long NowTicks(long qpcFrequency)
    {
        NativeMethods.QueryPerformanceCounter(out var counter);
        return (long)((Int128)counter * WasapiConstants.HundredNsPerSecond / qpcFrequency);
    }

    private void RaiseFault(string message)
    {
        if (Interlocked.Exchange(ref _faulted, 1) != 0)
        {
            return;
        }

        try
        {
            Faulted?.Invoke(new CaptureFault(_loopback ? "wasapi-loopback" : "wasapi-microphone", $"{Device.Name}: {message}", IsFatal: true));
        }
        catch
        {
            // A throwing handler must not crash the capture thread (an unhandled exception would end the process).
        }
    }

    /// <summary>State shared by <see cref="Start"/> and one run of the capture thread.</summary>
    private sealed class CaptureRun(IAudioSink sink, nint stopEvent)
    {
        public IAudioSink Sink { get; } = sink;

        public nint StopEvent { get; } = stopEvent;

        public ManualResetEventSlim Ready { get; } = new(false);

        public volatile bool Started;

        public Exception? StartError;
    }
}
