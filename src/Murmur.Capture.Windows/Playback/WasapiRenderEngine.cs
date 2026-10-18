using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Audio;
using Murmur.Capture.Windows.Audio.Interop;

namespace Murmur.Capture.Windows.Playback;

/// <summary>
/// One WASAPI shared-mode render stream on the default render endpoint (eRender, eConsole). The client format is the
/// tracks' own mono PCM16 (16 kHz for sessions) and the audio engine converts it to the mix format
/// (AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY), so there is no resampler here.
/// Event-driven (AUDCLNT_STREAMFLAGS_EVENTCALLBACK, ~50 ms buffer) on a dedicated MTA thread registered with the MMCSS
/// "Playback" task. Every COM object is created, used and released on that thread; Play/Pause/Seek are posted to it
/// as commands (a native auto-reset event) and the caller waits for the HRESULT.
/// The fill path (GetCurrentPadding, GetBuffer, <see cref="TrackMixer.Render"/>, ReleaseBuffer) allocates nothing;
/// the only render-thread allocation is the thread-pool work item that reports an end or a fault, once per event.
/// UNVERIFIED-ON-WINDOWS until the Windows-trait tests run in CI.
/// </summary>
internal sealed unsafe class WasapiRenderEngine : IPlaybackEngine
{
    /// <summary>Requested endpoint buffer: 50 ms in REFERENCE_TIME (100 ns) units.</summary>
    internal const long RequestedBufferDuration = 500_000;

    /// <summary>Fallback wake-up while running in case an event is missed.</summary>
    internal const uint WaitTimeoutMilliseconds = 100;

    internal const uint StreamFlags =
        WasapiConstants.AudclntStreamFlagsEventCallback
        | WasapiConstants.AudclntStreamFlagsAutoConvertPcm
        | WasapiConstants.AudclntStreamFlagsSrcDefaultQuality;

    private readonly TrackMixer _mixer;
    private readonly IPlaybackEngineOwner _owner;
    private readonly byte[] _format;
    private readonly Lock _commandGate = new();
    private readonly ManualResetEventSlim _commandDone = new(false);
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;
    private readonly nint _commandEvent;

    // Command slot (guarded by _commandGate).
    private Command _command;
    private long _commandFrame;
    private bool _commandPlay;
    private int _commandResult;
    private bool _pending;
    private bool _exited;
    private int _exitResult = WasapiConstants.EFail;
    private long _generation;

    // Render-thread state.
    private IAudioClient? _client;
    private IAudioRenderClient? _render;
    private uint _bufferFrames;
    private long _startFrame;
    private long _submitted;
    private bool _running;
    private bool _drained;
    private long _appliedGeneration;
    private Exception? _startError;

    private long _playedFrame;
    private int _disposed;

    private WasapiRenderEngine(TrackMixer mixer, long startFrame, IPlaybackEngineOwner owner, nint commandEvent)
    {
        _mixer = mixer;
        _owner = owner;
        _format = PlaybackTiming.Pcm16MonoFormat(mixer.SampleRate);
        _commandEvent = commandEvent;
        _mixer.Seek(startFrame);
        _startFrame = _mixer.Position;
        _drained = _mixer.AtEnd;
        _playedFrame = _startFrame;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Murmur WASAPI playback",
            Priority = ThreadPriority.Highest,
        };
    }

    private enum Command
    {
        None,
        Play,
        Pause,
        Seek,
        Shutdown,
    }

    public long PlayedFrame => Volatile.Read(ref _playedFrame);

    public long Generation
    {
        get
        {
            lock (_commandGate)
            {
                return _generation;
            }
        }
    }

    /// <summary>A <see cref="PlaybackEngineFactory"/>: opens the default render endpoint and starts the render thread.</summary>
    /// <exception cref="InvalidOperationException">No default render endpoint, or the stream could not be initialized.</exception>
    public static IPlaybackEngine Create(TrackMixer mixer, long startFrame, IPlaybackEngineOwner owner)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(owner);
        var commandEvent = NativeMethods.CreateEventW(0, 0, 0, 0); // auto reset, non-signalled
        if (commandEvent == 0)
        {
            throw new COMException("CreateEventW(command) failed.", Marshal.GetHRForLastWin32Error());
        }

        WasapiRenderEngine engine;
        try
        {
            engine = new WasapiRenderEngine(mixer, startFrame, owner, commandEvent); // seeks the tracks: file I/O
        }
        catch
        {
            NativeMethods.CloseHandle(commandEvent);
            throw;
        }

        engine._thread.Start();
        engine._ready.Wait();
        if (engine._startError is { } error)
        {
            engine._thread.Join();
            NativeMethods.CloseHandle(commandEvent);
            engine._commandDone.Dispose();
            throw new InvalidOperationException($"Could not start WASAPI playback: {error.Message}", error);
        }

        return engine; // _ready is not disposed: the render thread may still be returning from Set()
    }

    public int Play() => Send(Command.Play, 0, false);

    public int Pause() => Send(Command.Pause, 0, false);

    public int Seek(long frame, bool play) => Send(Command.Seek, frame, play);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Send(Command.Shutdown, 0, false);
        _thread.Join();
        NativeMethods.CloseHandle(_commandEvent);
        _commandDone.Dispose();
        _mixer.Dispose();
    }

    private int Send(Command command, long frame, bool play)
    {
        lock (_commandGate)
        {
            if (_exited)
            {
                return _exitResult;
            }

            _command = command;
            _commandFrame = frame;
            _commandPlay = play;
            _pending = true;
            _generation++;
            _commandDone.Reset();
        }

        NativeMethods.SetEvent(_commandEvent);
        _commandDone.Wait();
        lock (_commandGate)
        {
            return _commandResult;
        }
    }

    private void Run()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        nint audioEvent = 0;
        nint mmcss = 0;
        var comHr = NativeMethods.CoInitializeEx(0, WasapiConstants.CoinitMultithreaded);
        var started = false;
        var exitHr = WasapiConstants.EFail;
        string? fault = null;
        try
        {
            if (comHr < 0 && comHr != WasapiConstants.RpcEChangedMode)
            {
                ComHelpers.Check(comHr, "CoInitializeEx(COINIT_MULTITHREADED)");
            }

            // MMCSS "Playback" task; failure (e.g. the service is disabled) only costs scheduling priority.
            uint taskIndex = 0;
            mmcss = NativeMethods.AvSetMmThreadCharacteristicsW("Playback", ref taskIndex);

            enumerator = AudioEndpoints.CreateEnumerator();
            var hr = enumerator.GetDefaultAudioEndpoint(WasapiConstants.ERender, WasapiConstants.EConsole, out var pDevice);
            if (hr == WasapiConstants.ENotFound)
            {
                throw new InvalidOperationException("No default output (render endpoint) is available for playback.");
            }

            ComHelpers.Check(hr, "IMMDeviceEnumerator.GetDefaultAudioEndpoint(eRender)");
            device = ComHelpers.Wrap<IMMDevice>(pDevice, "IMMDeviceEnumerator.GetDefaultAudioEndpoint(eRender)");

            ComHelpers.Check(device.Activate(in WasapiConstants.IidIAudioClient, WasapiConstants.ClsctxAll, 0, out var pClient), "IMMDevice.Activate(IAudioClient)");
            _client = ComHelpers.Wrap<IAudioClient>(pClient, "IMMDevice.Activate(IAudioClient)");

            fixed (byte* pFormat = _format)
            {
                // Shared mode + event callback: periodicity must be 0.
                ComHelpers.Check(
                    _client.Initialize(WasapiConstants.AudclntShareModeShared, StreamFlags, RequestedBufferDuration, 0, (nint)pFormat, 0),
                    "IAudioClient.Initialize(SHARED, EVENTCALLBACK | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY)");
            }

            ComHelpers.Check(_client.GetBufferSize(out _bufferFrames), "IAudioClient.GetBufferSize");

            audioEvent = NativeMethods.CreateEventW(0, 0, 0, 0); // auto reset
            if (audioEvent == 0)
            {
                throw new COMException("CreateEventW(audio) failed.", Marshal.GetHRForLastWin32Error());
            }

            ComHelpers.Check(_client.SetEventHandle(audioEvent), "IAudioClient.SetEventHandle");
            ComHelpers.Check(_client.GetService(in WasapiConstants.IidIAudioRenderClient, out var pRender), "IAudioClient.GetService(IAudioRenderClient)");
            _render = ComHelpers.Wrap<IAudioRenderClient>(pRender, "IAudioClient.GetService(IAudioRenderClient)");

            started = true;
            _ready.Set();

            exitHr = Loop(audioEvent, out fault);
        }
        catch (Exception ex) when (!started)
        {
            _startError = ex;
        }
        catch (Exception ex)
        {
            // E.g. the track file vanished or was truncated mid-playback.
            fault = ex.Message;
            exitHr = ex.HResult < 0 ? ex.HResult : WasapiConstants.EFail;
        }
        finally
        {
            if (_running)
            {
                _client!.Stop();
                _running = false;
            }

            ComHelpers.Release(_render);
            ComHelpers.Release(_client);
            _render = null;
            _client = null;
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

            Exit(exitHr);
            if (!started)
            {
                _ready.Set();
            }
        }

        if (fault is not null)
        {
            ThreadPool.UnsafeQueueUserWorkItem(
                static s => s.Engine._owner.OnFault(s.Engine, s.Hr, s.Message),
                (Engine: this, Hr: exitHr, Message: fault),
                preferLocal: false);
        }
    }

    /// <summary>
    /// The steady-state loop. Returns the HRESULT the engine dies with; <paramref name="fault"/> is set when the stream
    /// failed while rendering (to be reported to the owner) and stays null on shutdown or a failed command (the caller
    /// already received that HRESULT).
    /// </summary>
    private int Loop(nint audioEvent, out string? fault)
    {
        fault = null;
        var handles = stackalloc nint[2];
        handles[0] = audioEvent;
        handles[1] = _commandEvent;
        while (true)
        {
            var wait = NativeMethods.WaitForMultipleObjects(2, handles, 0, _running ? WaitTimeoutMilliseconds : uint.MaxValue);
            if (wait == WasapiConstants.WaitFailed)
            {
                var error = Marshal.GetHRForLastWin32Error();
                fault = $"WaitForMultipleObjects failed: {ComHelpers.Describe(error)}";
                return error;
            }

            if (wait == WasapiConstants.WaitObject0 + 1)
            {
                if (!TakeCommand(out var command, out var frame, out var play))
                {
                    continue;
                }

                if (command == Command.Shutdown)
                {
                    Complete(WasapiConstants.SOk);
                    return WasapiConstants.EFail; // later commands fail: the engine is gone
                }

                var commandHr = Execute(command, frame, play);
                Complete(commandHr);
                if (commandHr < 0)
                {
                    return commandHr;
                }

                continue;
            }

            if (!_running)
            {
                continue;
            }

            var hr = Fill(out var padding);
            if (hr < 0)
            {
                fault = ComHelpers.Describe(hr);
                return hr;
            }

            if (_drained && padding == 0)
            {
                // Everything was played out: stop and report the end (off this thread).
                hr = _client!.Stop();
                _running = false;
                Volatile.Write(ref _playedFrame, _mixer.TotalFrames);
                if (hr < 0)
                {
                    fault = ComHelpers.Describe(hr);
                    return hr;
                }

                ThreadPool.UnsafeQueueUserWorkItem(
                    static s => s.Engine._owner.OnEnded(s.Engine, s.Generation),
                    (Engine: this, Generation: _appliedGeneration),
                    preferLocal: false);
            }
        }
    }

    private int Execute(Command command, long frame, bool play)
    {
        var client = _client!;
        int hr;
        switch (command)
        {
            case Command.Play:
                return Start();

            case Command.Pause:
                if (_running)
                {
                    hr = client.Stop();
                    _running = false;
                    if (hr < 0)
                    {
                        return hr;
                    }
                }

                return Publish();

            case Command.Seek:
                if (_running)
                {
                    hr = client.Stop();
                    _running = false;
                    if (hr < 0)
                    {
                        return hr;
                    }
                }

                hr = client.Reset();
                if (hr < 0)
                {
                    return hr;
                }

                _mixer.Seek(frame);
                _startFrame = _mixer.Position;
                _submitted = 0;
                _drained = _mixer.AtEnd;
                Volatile.Write(ref _playedFrame, _startFrame);
                return play ? Start() : WasapiConstants.SOk;

            default:
                return WasapiConstants.SOk;
        }
    }

    /// <summary>Prefills the endpoint buffer and starts the stream (no-op while running).</summary>
    private int Start()
    {
        if (_running)
        {
            return WasapiConstants.SOk;
        }

        var hr = Fill(out _);
        if (hr < 0)
        {
            return hr;
        }

        hr = _client!.Start();
        if (hr < 0)
        {
            return hr;
        }

        _running = true;
        return WasapiConstants.SOk;
    }

    /// <summary>Tops the endpoint buffer up with mixed frames and publishes the played position.</summary>
    private int Fill(out uint padding)
    {
        var hr = _client!.GetCurrentPadding(out padding);
        if (hr < 0)
        {
            return hr;
        }

        if (!_drained && padding < _bufferFrames)
        {
            var available = _bufferFrames - padding;
            hr = _render!.GetBuffer(available, out var data);
            if (hr < 0)
            {
                return hr;
            }

            var written = _mixer.Render(new Span<short>((void*)data, (int)available));
            hr = _render.ReleaseBuffer((uint)written, 0);
            if (hr < 0)
            {
                return hr;
            }

            _submitted += written;
            padding += (uint)written;
            _drained = _mixer.AtEnd;
        }

        Volatile.Write(ref _playedFrame, PlaybackTiming.PlayedFrame(_startFrame, _submitted, padding, _mixer.TotalFrames));
        return WasapiConstants.SOk;
    }

    /// <summary>Refreshes <see cref="PlayedFrame"/> from the current padding (valid while stopped too).</summary>
    private int Publish()
    {
        var hr = _client!.GetCurrentPadding(out var padding);
        if (hr < 0)
        {
            return hr;
        }

        Volatile.Write(ref _playedFrame, PlaybackTiming.PlayedFrame(_startFrame, _submitted, padding, _mixer.TotalFrames));
        return WasapiConstants.SOk;
    }

    private bool TakeCommand(out Command command, out long frame, out bool play)
    {
        lock (_commandGate)
        {
            command = _command;
            frame = _commandFrame;
            play = _commandPlay;
            if (!_pending)
            {
                return false;
            }

            _appliedGeneration = _generation;
            return true;
        }
    }

    private void Complete(int hr)
    {
        lock (_commandGate)
        {
            _commandResult = hr;
            _pending = false;
        }

        _commandDone.Set();
    }

    /// <summary>Marks the engine dead; a command still waiting gets the exit HRESULT.</summary>
    private void Exit(int hr)
    {
        lock (_commandGate)
        {
            _exited = true;
            _exitResult = hr < 0 ? hr : WasapiConstants.EFail;
            if (!_pending)
            {
                return;
            }

            _commandResult = _exitResult;
            _pending = false;
        }

        _commandDone.Set();
    }
}
