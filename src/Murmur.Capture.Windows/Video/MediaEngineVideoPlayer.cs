using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Capture.Windows.Screen;
using Murmur.Capture.Windows.Screen.Interop;
using Murmur.Capture.Windows.Video.Interop;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Video;

/// <summary>
/// <see cref="IVideoPlayer"/> on the Media Foundation Media Engine in frame-server mode (no window): muted, never
/// auto-playing, one engine per open file. On a real GPU with video support the engine decodes with D3D11 through an
/// IMFDXGIDeviceManager and draws frames into a BGRA render target (output format DXGI_FORMAT_B8G8R8A8_UNORM) that is read
/// back through a staging texture; without one (WARP, the Basic Render Driver, no D3D11) it decodes in software and draws
/// into a WIC bitmap. If an open fails on the D3D11 output, it is retried on WIC; when that works, WIC stays the choice of
/// this player.
/// </summary>
/// <remarks>
/// <para><b>Open.</b> <see cref="OpenAsync"/> closes what is open, creates an engine, sets the source and loads it, and
/// completes on MF_MEDIA_ENGINE_EVENT_LOADEDMETADATA (paused at 0, <see cref="Duration"/> and <see cref="Size"/> known)
/// or fails on MF_MEDIA_ENGINE_EVENT_ERROR (<see cref="InvalidDataException"/> for a file that cannot be decoded,
/// <see cref="IOException"/> otherwise; a missing file: <see cref="FileNotFoundException"/> at once). A Close, another
/// open or the token cancels it (<see cref="OperationCanceledException"/>). The continuation runs on the caller's
/// synchronization context (the UI thread).</para>
/// <para><b>Frames.</b> <see cref="TryCopyFrame"/> asks the engine for the presented frame (OnVideoStreamTick); when it
/// is new (another time stamp, the first frame, a completed seek) or the requested size changed, TransferVideoFrame scales
/// it into the target surface and the rows are copied into the caller's buffer. Allocation-free once the surface exists
/// for the size. Alpha is whatever the engine writes (opaque on the D3D11 path); show the picture as opaque.</para>
/// <para><b>Threads.</b> Call it from one thread. Engine events arrive on Media Foundation threads and only touch
/// thread-safe state; <see cref="Failed"/> is raised there (subscribers must marshal, and must not block on the thread
/// that calls <see cref="Close"/> or <see cref="Dispose"/>).</para>
/// UNVERIFIED-ON-WINDOWS until the Windows-trait tests run in CI.
/// </remarks>
public sealed class MediaEngineVideoPlayer : IVideoPlayer
{
    /// <summary>Consecutive failed transfers of a due frame before <see cref="Failed"/> is raised (once per file).</summary>
    internal const int TransferFailuresBeforeReport = 60;

    private readonly VideoOutput _requested;

    private EngineSession? _session;
    private bool _open;
    private TimeSpan _duration;
    private VideoSize _size;
    private FrameChangeTracker _frames = new();
    private int _transferFailures;

    private GpuDevice? _gpu;
    private bool _gpuUnavailable;
    private string? _deviceDescription;
    private nint _wicFactory;
    private bool _preferWic;
    private IFrameTarget? _target;
    private bool _disposed;

    public MediaEngineVideoPlayer()
        : this(VideoOutput.Auto)
    {
    }

    internal MediaEngineVideoPlayer(VideoOutput output)
    {
        _requested = output;
    }

    public event Action<string>? Failed;

    public bool IsOpen => _open;

    public TimeSpan Duration
    {
        get
        {
            if (_open && _session is { } session && Interlocked.Exchange(ref session.DurationChanged, 0) != 0)
            {
                _duration = ReadDuration(session.Engine);
            }

            return _open ? _duration : TimeSpan.Zero;
        }
    }

    public VideoSize Size => _open ? _size : default;

    public TimeSpan Position
    {
        get
        {
            if (!_open || _session is not { } session)
            {
                return TimeSpan.Zero;
            }

            var position = SecondsToTime(IMFMediaEngine.GetCurrentTime(session.Engine));
            return _duration > TimeSpan.Zero && position > _duration ? _duration : position;
        }
    }

    public bool IsPlaying =>
        _open && _session is { } session && IMFMediaEngine.IsPaused(session.Engine) == 0 && IMFMediaEngine.IsEnded(session.Engine) == 0;

    /// <summary>The output of the open file (diagnostics and tests).</summary>
    internal VideoOutput Output => _session?.Output ?? VideoOutput.Auto;

    /// <summary>The D3D11 device behind <see cref="VideoOutput.Direct3D"/>, e.g. "hardware D3D11 FL 11.1 on '...'" (diagnostics).</summary>
    internal string? DeviceDescription => _gpu?.Description ?? _deviceDescription;

    /// <summary>The HRESULT of the last failed frame transfer or copy, 0 when the last one succeeded (diagnostics).</summary>
    internal int LastTransferResult { get; private set; }

    public async ValueTask OpenAsync(string filePath, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        var path = System.IO.Path.GetFullPath(filePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Video file not found.", path);
        }

        cancellationToken.ThrowIfCancellationRequested();
        CloseSession();
        VideoRuntime.EnsureStarted();
        var output = ChooseOutput();
        try
        {
            await OpenWithAsync(path, output, cancellationToken);
        }
        catch (Exception ex) when (output == VideoOutput.Direct3D && _requested == VideoOutput.Auto && !_disposed
            && ex is InvalidDataException or IOException or COMException)
        {
            // The D3D11 pipeline may be what failed (driver, decoder): try software decoding into WIC. When that works,
            // WIC stays this player's output; when it fails too, the file is the problem and its error is thrown.
            EnsureWicFactory();
            await OpenWithAsync(path, VideoOutput.Wic, cancellationToken);
            _preferWic = true;
        }
    }

    public void Close()
    {
        if (_disposed)
        {
            return;
        }

        CloseSession();
    }

    public void Play() => Command(static engine => IMFMediaEngine.Play(engine), "IMFMediaEngine::Play");

    public void Pause() => Command(static engine => IMFMediaEngine.Pause(engine), "IMFMediaEngine::Pause");

    public void Seek(TimeSpan position)
    {
        var session = OpenSession();
        var target = position < TimeSpan.Zero ? TimeSpan.Zero : _duration > TimeSpan.Zero && position > _duration ? _duration : position;
        var hr = IMFMediaEngine.SetCurrentTime(session.Engine, target.Ticks / (double)TimeSpan.TicksPerSecond);
        if (hr < 0)
        {
            session.RaiseFailed($"IMFMediaEngine::SetCurrentTime failed: {ComHelpers.Describe(hr)}");
        }
    }

    public unsafe bool TryCopyFrame(Span<byte> destination, int width, int height, int stride)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BgraFrame.Validate(destination.Length, width, height, stride);
        if (!_open || _session is not { } session || _target is not { } target)
        {
            return false;
        }

        long pts = -1;
        _frames.Tick(IMFMediaEngine.OnVideoStreamTick(session.Engine, &pts), pts);
        var events = Volatile.Read(ref session.PictureEvents);
        if (!_frames.IsDue(width, height, events))
        {
            return false;
        }

        var hr = target.Resize(width, height);
        if (hr >= 0)
        {
            var source = new MFVideoNormalizedRect { Left = 0, Top = 0, Right = 1, Bottom = 1 };
            var rect = new Rect32(0, 0, width, height);
            var border = new MFARGB { Alpha = 255 };
            hr = IMFMediaEngine.TransferVideoFrame(session.Engine, target.Surface, &source, &rect, &border);
        }

        if (hr >= 0)
        {
            hr = target.CopyTo(destination, stride);
        }

        LastTransferResult = hr;
        if (hr < 0)
        {
            if (++_transferFailures == TransferFailuresBeforeReport)
            {
                session.RaiseFailed($"Copying video frames failed {TransferFailuresBeforeReport} times in a row ({session.Output} output): {ComHelpers.Describe(hr)}");
            }

            return false;
        }

        _transferFailures = 0;
        _frames.Copied(width, height, events);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseSession();
        _target?.Dispose();
        _target = null;
        _gpu?.Dispose();
        _gpu = null;
        ComCall.Release(ref _wicFactory);
    }

    /// <summary>Media time from the engine's seconds (NaN, negative or infinite: 0).</summary>
    internal static TimeSpan SecondsToTime(double seconds) =>
        double.IsFinite(seconds) && seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromTicks((long)(seconds * TimeSpan.TicksPerSecond))
            : TimeSpan.Zero;

    private static TimeSpan ReadDuration(nint engine) => SecondsToTime(IMFMediaEngine.GetDuration(engine));

    private async ValueTask OpenWithAsync(string path, VideoOutput output, CancellationToken cancellationToken)
    {
        var session = EngineSession.Create(path, output, output == VideoOutput.Direct3D ? _gpu!.DeviceManager : 0, OnFailed);
        _session = session;
        try
        {
            await session.Loaded.Task.WaitAsync(cancellationToken);
            if (_session != session)
            {
                throw new OperationCanceledException("The open was superseded.");
            }

            if (IMFMediaEngine.HasVideo(session.Engine) == 0)
            {
                throw new InvalidDataException($"'{System.IO.Path.GetFileName(path)}' has no video stream.");
            }

            Interlocked.Exchange(ref session.DurationChanged, 0);
            _duration = ReadDuration(session.Engine);
            _size = session.ReadNativeSize();
            _target = EnsureTarget(output);
            _frames.Reset();
            _transferFailures = 0;
            LastTransferResult = 0;
            _open = true;
        }
        catch
        {
            if (_session == session)
            {
                CloseSession();
            }

            throw;
        }
    }

    private VideoOutput ChooseOutput()
    {
        if (_requested == VideoOutput.Wic || _preferWic)
        {
            EnsureWicFactory();
            return VideoOutput.Wic;
        }

        if (_gpu is null && !_gpuUnavailable)
        {
            try
            {
                _gpu = GpuDevice.Create();
            }
            catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException)
            {
                _gpuUnavailable = true;
            }
        }

        var output = VideoOutputPolicy.Choose(_requested, _gpu is not null, _gpu?.IsSoftwareRenderer ?? true, _gpu?.HasVideoSupport ?? false);
        if (output == VideoOutput.Direct3D)
        {
            if (_gpu is null)
            {
                throw new NotSupportedException("No Direct3D 11 device could be created for video playback.");
            }

            return output;
        }

        if (_gpu is not null && _target is not D3D11FrameTarget)
        {
            // A software renderer (WARP, Basic Render Driver) or no video support: the device is of no use for playback.
            _deviceDescription = _gpu.Description;
            _gpu.Dispose();
            _gpu = null;
            _preferWic = true;
        }

        EnsureWicFactory();
        return output;
    }

    private unsafe void EnsureWicFactory()
    {
        if (_wicFactory != 0)
        {
            return;
        }

        var clsid = VideoConstants.ClsidWICImagingFactory;
        var iid = VideoConstants.IidIWICImagingFactory;
        nint factory = 0;
        ComHelpers.Check(VideoNativeMethods.CoCreateInstance(&clsid, 0, VideoConstants.ClsctxInprocServer, &iid, &factory), "CoCreateInstance(WICImagingFactory)");
        _wicFactory = factory;
    }

    private IFrameTarget EnsureTarget(VideoOutput output)
    {
        if (output == VideoOutput.Direct3D)
        {
            if (_target is D3D11FrameTarget d3d)
            {
                return d3d;
            }

            _target?.Dispose();
            return new D3D11FrameTarget(_gpu!.Device, _gpu.Context);
        }

        if (_target is WicFrameTarget wic)
        {
            return wic;
        }

        _target?.Dispose();
        return new WicFrameTarget(_wicFactory);
    }

    private void CloseSession()
    {
        var session = _session;
        _session = null;
        _open = false;
        _duration = TimeSpan.Zero;
        _size = default;
        _frames.Reset();
        session?.Dispose();
    }

    private EngineSession OpenSession()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_open || _session is not { } session)
        {
            throw new InvalidOperationException("Open a video first.");
        }

        return session;
    }

    private void Command(Func<nint, int> call, string what)
    {
        var session = OpenSession();
        var hr = call(session.Engine);
        if (hr < 0)
        {
            session.RaiseFailed($"{what} failed: {ComHelpers.Describe(hr)}");
        }
    }

    private void OnFailed(string message) => Failed?.Invoke(message);

    /// <summary>
    /// One Media Engine with its notify callback, for one file. Created and disposed on the player's thread; engine events
    /// reach <see cref="OnEngineEvent"/> on Media Foundation threads until <see cref="Dispose"/> detaches it.
    /// </summary>
    private sealed unsafe class EngineSession : IMediaEngineEvents, IDisposable
    {
        private readonly string _path;
        private readonly Action<string> _failed;
        private MediaEngineNotify? _notify;
        private nint _engine;
        private volatile bool _detached;
        private int _failedRaised;

        // Written by Media Foundation threads.
        public int PictureEvents;
        public int DurationChanged;

        private EngineSession(string path, VideoOutput output, Action<string> failed)
        {
            _path = path;
            Output = output;
            _failed = failed;
        }

        public nint Engine => _engine;

        public VideoOutput Output { get; }

        /// <summary>LOADEDMETADATA (result), ERROR before it (exception) or a close (cancelled).</summary>
        public TaskCompletionSource Loaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <exception cref="COMException">Creating the engine or setting its source failed.</exception>
        public static EngineSession Create(string path, VideoOutput output, nint deviceManager, Action<string> failed)
        {
            var session = new EngineSession(path, output, failed);
            nint attributes = 0, factory = 0, notify = 0, engine = 0, url = 0;
            try
            {
                session._notify = new MediaEngineNotify(session);
                notify = (nint)ComInterfaceMarshaller<IMFMediaEngineNotify>.ConvertToUnmanaged(session._notify);

                ComHelpers.Check(ScreenNativeMethods.MFCreateAttributes(&attributes, 3), "MFCreateAttributes");
                var key = VideoConstants.MfMediaEngineCallback;
                ComHelpers.Check(IMFAttributes.SetUnknown(attributes, &key, notify), "IMFAttributes::SetUnknown(MF_MEDIA_ENGINE_CALLBACK)");
                if (output == VideoOutput.Direct3D)
                {
                    key = VideoConstants.MfMediaEngineDxgiManager;
                    ComHelpers.Check(IMFAttributes.SetUnknown(attributes, &key, deviceManager), "IMFAttributes::SetUnknown(MF_MEDIA_ENGINE_DXGI_MANAGER)");
                    key = VideoConstants.MfMediaEngineVideoOutputFormat;
                    ComHelpers.Check(IMFAttributes.SetUINT32(attributes, &key, ScreenConstants.DxgiFormatB8G8R8A8Unorm), "IMFAttributes::SetUINT32(MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT)");
                }

                var clsid = VideoConstants.ClsidMFMediaEngineClassFactory;
                var iid = VideoConstants.IidIMFMediaEngineClassFactory;
                ComHelpers.Check(VideoNativeMethods.CoCreateInstance(&clsid, 0, VideoConstants.ClsctxInprocServer, &iid, &factory), "CoCreateInstance(MFMediaEngineClassFactory)");
                ComHelpers.Check(IMFMediaEngineClassFactory.CreateInstance(factory, VideoConstants.MfMediaEngineForceMute, attributes, &engine), "IMFMediaEngineClassFactory::CreateInstance");
                session._engine = engine;
                engine = 0;

                IMFMediaEngine.SetMuted(session._engine, 1);
                IMFMediaEngine.SetAutoPlay(session._engine, 0);
                IMFMediaEngine.SetLoop(session._engine, 0);
                fixed (char* pPath = path)
                {
                    url = VideoNativeMethods.SysAllocString(pPath);
                }

                if (url == 0)
                {
                    throw new OutOfMemoryException("SysAllocString failed.");
                }

                ComHelpers.Check(IMFMediaEngine.SetSource(session._engine, (char*)url), "IMFMediaEngine::SetSource");
                ComHelpers.Check(IMFMediaEngine.Load(session._engine), "IMFMediaEngine::Load");
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
            finally
            {
                if (url != 0)
                {
                    VideoNativeMethods.SysFreeString(url);
                }

                ComCall.Release(ref engine);
                ComCall.Release(ref factory);
                ComCall.Release(ref attributes);
                ComCall.Release(ref notify); // the attributes and the engine hold their own references
            }
        }

        /// <exception cref="COMException">IMFMediaEngine::GetNativeVideoSize failed.</exception>
        public VideoSize ReadNativeSize()
        {
            uint width = 0, height = 0;
            ComHelpers.Check(IMFMediaEngine.GetNativeVideoSize(_engine, &width, &height), "IMFMediaEngine::GetNativeVideoSize");
            return new VideoSize((int)width, (int)height);
        }

        public void OnEngineEvent(uint mediaEvent, nuint param1, uint param2)
        {
            if (_detached)
            {
                return;
            }

            switch (mediaEvent)
            {
                case VideoConstants.EventLoadedMetadata:
                    Loaded.TrySetResult();
                    break;
                case VideoConstants.EventError:
                    // param1 = MF_MEDIA_ENGINE_ERR, param2 = the HRESULT.
                    var code = (ushort)param1;
                    var hr = unchecked((int)param2);
                    if (!Loaded.TrySetException(MediaEngineErrors.OpenFailed(_path, code, hr)))
                    {
                        RaiseFailed($"Playing '{System.IO.Path.GetFileName(_path)}' failed: {MediaEngineErrors.Describe(code, hr)}");
                    }

                    break;
                case VideoConstants.EventLoadedData:
                case VideoConstants.EventSeeked:
                case VideoConstants.EventFirstFrameReady:
                case VideoConstants.EventFormatChange:
                    Interlocked.Increment(ref PictureEvents);
                    break;
                case VideoConstants.EventDurationChange:
                    Volatile.Write(ref DurationChanged, 1);
                    break;
                case VideoConstants.EventResourceLost:
                    RaiseFailed($"Playing '{System.IO.Path.GetFileName(_path)}' failed: the video resources were lost (device removed).");
                    break;
                case VideoConstants.EventStreamRenderingError:
                    RaiseFailed($"Playing '{System.IO.Path.GetFileName(_path)}' failed: stream rendering error (HRESULT 0x{param2:X8}).");
                    break;
            }
        }

        /// <summary>Raises the player's Failed once per file (any thread).</summary>
        public void RaiseFailed(string message)
        {
            if (!_detached && Interlocked.Exchange(ref _failedRaised, 1) == 0)
            {
                _failed(message);
            }
        }

        public void Dispose()
        {
            _detached = true;
            _notify?.Detach();
            Loaded.TrySetCanceled();
            if (_engine != 0)
            {
                IMFMediaEngine.Shutdown(_engine);
                ComCall.Release(ref _engine);
            }
        }
    }
}

/// <summary>Process-wide set-up of the video player: Media Foundation and a process MTA for COM calls from any thread.</summary>
internal static unsafe class VideoRuntime
{
    private static readonly Lock Gate = new();
    private static bool _mta;

    /// <exception cref="COMException">MFStartup failed.</exception>
    /// <exception cref="DllNotFoundException">mfplat.dll is missing.</exception>
    public static void EnsureStarted()
    {
        MediaFoundationRuntime.EnsureStarted();
        lock (Gate)
        {
            if (_mta)
            {
                return;
            }

            // Never decremented: like MFStartup, held for the life of the process. A failure only means callers must have
            // initialized COM themselves (the UI thread has).
            nint cookie;
            VideoNativeMethods.CoIncrementMTAUsage(&cookie);
            _mta = true;
        }
    }
}
