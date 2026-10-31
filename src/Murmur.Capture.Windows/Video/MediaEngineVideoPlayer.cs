using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Capture.Windows.Screen;
using Murmur.Capture.Windows.Screen.Interop;
using Murmur.Capture.Windows.Video.Interop;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Video;

/// <summary>
/// <see cref="IVideoPlayer"/> on the Media Foundation Media Engine in frame-server mode (no window): muted, never
/// auto-playing, one engine per open file, output format DXGI_FORMAT_B8G8R8A8_UNORM. On a real GPU with video support
/// the engine decodes with D3D11 through an IMFDXGIDeviceManager and draws frames into a BGRA render target that is read
/// back through a staging texture; without one (WARP, the Basic Render Driver, no D3D11; probed once per process) it
/// decodes in software and draws into a WIC bitmap. If an open fails on the D3D11 output, it is retried on WIC; when that
/// works, WIC stays the choice of this player.
/// </summary>
/// <remarks>
/// <para><b>Open.</b> <see cref="OpenAsync"/> closes what is open, creates an engine and sets the source (which starts
/// loading it), and completes on MF_MEDIA_ENGINE_EVENT_LOADEDMETADATA (paused at 0, <see cref="Duration"/> and
/// <see cref="Size"/> known) or fails on MF_MEDIA_ENGINE_EVENT_ERROR (<see cref="InvalidDataException"/> for a file that
/// cannot be decoded, <see cref="IOException"/> otherwise; a missing file: <see cref="FileNotFoundException"/> at once).
/// A Close, another open or the token cancels it (<see cref="OperationCanceledException"/>). The continuation runs on the
/// caller's synchronization context (the UI thread).</para>
/// <para><b>Seeks.</b> One seek is in flight at a time: a seek requested before the engine reported the previous one done
/// (MF_MEDIA_ENGINE_EVENT_SEEKED) is queued, only the latest queued target is kept, and it is sent on the next
/// <see cref="Position"/> / <see cref="TryCopyFrame"/> call after the previous one completed. While a seek is in flight or
/// queued, <see cref="Position"/> is its target. Rarely a finished paused seek leaves the old picture up (no new time
/// stamp is ever presented); <see cref="SeekRecovery"/> then copies the frame again and, if needed, repeats the seek.</para>
/// <para><b>Frames.</b> <see cref="TryCopyFrame"/> asks the engine for the presented frame (OnVideoStreamTick); when it
/// is new (another time stamp, the first frame, a completed seek) or the requested size changed, TransferVideoFrame scales
/// it into the target surface and the rows are copied into the caller's buffer. Allocation-free once the surface exists
/// for the size. Call it on every render tick while playing: in frame-server mode the engine presents frames when they
/// are pulled. Alpha is whatever the engine writes (opaque on the D3D11 path); show the picture as opaque.</para>
/// <para><b>Threads.</b> Call it from one thread. Engine events arrive on Media Foundation threads and only touch
/// thread-safe state; <see cref="Failed"/> is raised there (subscribers must marshal, and must not block on the thread
/// that calls <see cref="Close"/> or <see cref="Dispose"/>).</para>
/// UNVERIFIED-ON-WINDOWS until the Windows-trait tests run in CI.
/// </remarks>
public sealed class MediaEngineVideoPlayer : IVideoPlayer, IVideoPlayerControls
{
    /// <summary>Consecutive failed transfers of a due frame before <see cref="Failed"/> is raised (once per file).</summary>
    internal const int TransferFailuresBeforeReport = 60;

    /// <summary>The longest wait for MF_MEDIA_ENGINE_EVENT_SEEKED before a queued seek is sent anyway.</summary>
    internal static readonly TimeSpan SeekTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long a finished paused seek may show no new frame before the recovery steps in (<see cref="SeekRecovery"/>).</summary>
    internal static readonly TimeSpan SeekRecoveryDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>The engine has a frame for the current position (HTML HAVE_CURRENT_DATA).</summary>
    private const ushort HaveCurrentData = 2;

    private const int RecoveryStepKinds = (int)SeekRecoveryStep.PlayPause + 1;
    private const int RecoveryLogSize = 24;

    private readonly VideoOutput _requested;

    private EngineSession? _session;
    private bool _open;
    private int _openGeneration;
    private TimeSpan _duration;
    private VideoSize _size;
    private FrameChangeTracker _frames = new();
    private SeekQueue _seeks = new((long)(SeekTimeout.TotalSeconds * Stopwatch.Frequency));
    private SeekRecovery _recovery = new((long)(SeekRecoveryDelay.TotalSeconds * Stopwatch.Frequency));
    private int _transferFailures;
    private int _lastTickResult;
    private long _lastTickPts;
    private readonly int[] _stepsTaken = new int[RecoveryStepKinds];
    private readonly int[] _stepsHealed = new int[RecoveryStepKinds];
    private readonly RecoveryLogEntry[] _recoveryLog = new RecoveryLogEntry[RecoveryLogSize];
    private int _recoveryLogCount;
    private double _playbackRate = 1;

    private GpuDevice? _gpu;
    private nint _wicFactory;
    private bool _preferWic;
    private int _resourceLost;
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

            PumpSeeks(session);
            var position = _seeks.IsBusy ? _seeks.LatestTarget : SecondsToTime(IMFMediaEngine.GetCurrentTime(session.Engine));
            return _duration > TimeSpan.Zero && position > _duration ? _duration : position;
        }
    }

    public bool IsPlaying =>
        _open && _session is { } session && IMFMediaEngine.IsPaused(session.Engine) == 0 && IMFMediaEngine.IsEnded(session.Engine) == 0;

    /// <summary>What the D3D11 hardware of this machine offers (probed once per process; diagnostics and tests).</summary>
    internal static GpuCapability GpuCapability => GpuProbe.Capability;

    /// <summary>The output of the open file (diagnostics and tests).</summary>
    internal VideoOutput Output => _session?.Output ?? VideoOutput.Auto;

    /// <summary>The D3D11 device, e.g. "hardware D3D11 FL 11.1 on '...'" (diagnostics).</summary>
    internal string DeviceDescription => _gpu?.Description ?? GpuProbe.Capability.Description;

    /// <summary>The HRESULT of the last failed frame transfer or copy, 0 when the last one succeeded (diagnostics).</summary>
    internal int LastTransferResult { get; private set; }

    /// <summary>How often each <see cref="SeekRecoveryStep"/> was taken since the file was opened (diagnostics and tests).</summary>
    internal int RecoveryStepsTaken => SumOf(_stepsTaken);

    /// <summary>How often a seek was healed by the step it followed (see <see cref="SeekRecovery.TakeHealedBy"/>).</summary>
    internal int RecoveryHealed => SumOf(_stepsHealed);
    /// <summary>The HRESULT of the last refused playback rate, 0 when the last rate was accepted (diagnostics).</summary>
    internal int LastRateResult { get; private set; }

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
        var generation = ++_openGeneration;
        CloseSession();
        if (Interlocked.Exchange(ref _resourceLost, 0) != 0)
        {
            ReleaseGpu(); // the device was removed: the next D3D11 open creates a new one
        }

        VideoRuntime.EnsureStarted();
        var output = ChooseOutput();
        try
        {
            await OpenWithAsync(path, output, generation, cancellationToken);
        }
        catch (Exception ex) when (generation == _openGeneration && !_disposed && output == VideoOutput.Direct3D
            && _requested == VideoOutput.Auto && ex is InvalidDataException or IOException or COMException)
        {
            // The D3D11 pipeline may be what failed (driver, decoder): try software decoding into WIC. When that works,
            // WIC stays this player's output; when it fails too, the file is the problem and its error is thrown.
            EnsureWicFactory();
            await OpenWithAsync(path, VideoOutput.Wic, generation, cancellationToken);
            if (generation == _openGeneration)
            {
                _preferWic = true;
                ReleaseGpu();
            }
        }
    }

    public void Close()
    {
        if (_disposed)
        {
            return;
        }

        _openGeneration++;
        CloseSession();
    }

    public void Play()
    {
        _recovery.Disarm(); // playing presents frames on its own
        Command(static engine => IMFMediaEngine.Play(engine), "IMFMediaEngine::Play");
    }

    public void Pause() => Command(static engine => IMFMediaEngine.Pause(engine), "IMFMediaEngine::Pause");

    public void Seek(TimeSpan position)
    {
        var session = OpenSession();
        var target = position < TimeSpan.Zero ? TimeSpan.Zero : _duration > TimeSpan.Zero && position > _duration ? _duration : position;
        _recovery.Arm(target, _frames.PresentedPts);
        if (_seeks.Request(target, Volatile.Read(ref session.SeekedEvents), Stopwatch.GetTimestamp()))
        {
            SendSeek(session, target);
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

        PumpSeeks(session);
        long pts = -1;
        var tick = IMFMediaEngine.OnVideoStreamTick(session.Engine, &pts);
        _lastTickResult = tick;
        _lastTickPts = pts;
        _frames.Tick(tick, pts, observePeriod: !_seeks.IsBusy && !_recovery.IsArmed);
        var forced = _recovery.IsArmed && RecoverSeek(session);
        var events = Volatile.Read(ref session.PictureEvents);
        if (!forced && !_frames.IsDue(width, height, events))
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
            // Before the first picture an early event may make a frame look due: only count real failures.
            if ((_frames.HasCopy || tick == 0) && ++_transferFailures == TransferFailuresBeforeReport)
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
        _openGeneration++;
        CloseSession();
        _target?.Dispose();
        _target = null;
        _gpu?.Dispose();
        _gpu = null;
        ComCall.Release(ref _wicFactory);
    }

    /// <summary>
    /// The engine's state for test and log output: ready state, paused / ended / seeking, current time, the seek queue
    /// and the last engine events with their time since the open. Allocates; not for the render tick.
    /// </summary>
    internal string DescribeState()
    {
        if (_session is not { } session)
        {
            return "closed";
        }

        var engine = session.Engine;
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{session.Output} output, readyState {IMFMediaEngine.GetReadyState(engine)}, ");
        text.Append(CultureInfo.InvariantCulture, $"paused {IMFMediaEngine.IsPaused(engine) != 0}, ended {IMFMediaEngine.IsEnded(engine) != 0}, ");
        text.Append(CultureInfo.InvariantCulture, $"seeking {IMFMediaEngine.IsSeeking(engine) != 0}, currentTime {IMFMediaEngine.GetCurrentTime(engine):F3} s, ");
        text.Append(CultureInfo.InvariantCulture, $"duration {IMFMediaEngine.GetDuration(engine):F3} s, seek queue {(_seeks.IsBusy ? _seeks.LatestTarget.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture) + " s" : "idle")}, ");
        text.Append(CultureInfo.InvariantCulture, $"rate {_playbackRate} (last result 0x{LastRateResult:X8}), ");
        text.Append(CultureInfo.InvariantCulture, $"last copied pts {_frames.CopiedPts?.ToString(CultureInfo.InvariantCulture) ?? "none"}, ");
        text.Append(CultureInfo.InvariantCulture, $"{DescribeRecovery()}; events: ");
        text.Append(session.DescribeEvents());
        return text.ToString();
    }

    /// <summary>Media time from the engine's seconds (NaN, negative or infinite: 0).</summary>
    internal static TimeSpan SecondsToTime(double seconds) =>
        double.IsFinite(seconds) && seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromTicks((long)(seconds * TimeSpan.TicksPerSecond))
            : TimeSpan.Zero;

    private static TimeSpan ReadDuration(nint engine) => SecondsToTime(IMFMediaEngine.GetDuration(engine));

    private async ValueTask OpenWithAsync(string path, VideoOutput output, int generation, CancellationToken cancellationToken)
    {
        var session = EngineSession.Create(path, output, output == VideoOutput.Direct3D ? _gpu!.DeviceManager : 0, OnFailed, OnResourceLost);
        _session = session;
        try
        {
            await session.Loaded.Task.WaitAsync(cancellationToken);
            if (_session != session || generation != _openGeneration)
            {
                throw new OperationCanceledException("The open was superseded by a newer open or a close.");
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
            _seeks.Reset();
            _recovery.Reset();
            _transferFailures = 0;
            LastTransferResult = 0;
            ResetRecoveryDiagnostics();
            _open = true;
            if (_playbackRate != 1)
            {
                ApplyPlaybackRate(session);
            }
        }
        catch
        {
            // Whatever happened, this open's engine must not survive it; a newer open's session is left alone.
            if (_session == session)
            {
                CloseSession();
            }
            else
            {
                session.Dispose();
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

        var capability = _requested == VideoOutput.Auto ? GpuProbe.Capability : null;
        var output = VideoOutputPolicy.Choose(_requested, capability?.DeviceCreated ?? true, capability?.SoftwareRenderer ?? false, capability?.VideoSupport ?? true);
        if (output == VideoOutput.Wic)
        {
            EnsureWicFactory();
            return output;
        }

        if (_gpu is null)
        {
            try
            {
                _gpu = GpuDevice.Create();
            }
            catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException)
            {
                throw new NotSupportedException($"No Direct3D 11 device could be created for video playback: {ex.Message}", ex);
            }
        }

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

    /// <summary>Drops the D3D11 device and a target made on it (only while no D3D11 session is open).</summary>
    private void ReleaseGpu()
    {
        if (_session?.Output == VideoOutput.Direct3D)
        {
            return;
        }

        if (_target is D3D11FrameTarget)
        {
            _target.Dispose();
            _target = null;
        }

        _gpu?.Dispose();
        _gpu = null;
    }

    private void CloseSession()
    {
        var session = _session;
        _session = null;
        _open = false;
        _duration = TimeSpan.Zero;
        _size = default;
        _frames.Reset();
        _seeks.Reset();
        _recovery.Reset();
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

    /// <summary>
    /// The seek recovery (see <see cref="SeekRecovery"/>); true when the presented frame must be copied now although the
    /// engine reports no new one. Seeks go through the seek queue like any other. Allocation-free (the log is a fixed ring).
    /// </summary>
    private bool RecoverSeek(EngineSession session)
    {
        var now = Stopwatch.GetTimestamp();
        var due = _recovery.IsDue(now, _seeks.IsBusy, _frames.PresentedPts, _frames.FramePeriod);
        var healedBy = _recovery.TakeHealedBy();
        if (healedBy != SeekRecoveryStep.None)
        {
            _stepsHealed[(int)healedBy]++;
            Log(session, now, healedBy, healed: true, hr: 0);
        }

        if (!due)
        {
            return false;
        }

        var engine = session.Engine;
        var settled = IMFMediaEngine.IsPaused(engine) != 0 && IMFMediaEngine.IsSeeking(engine) == 0
            && IMFMediaEngine.GetReadyState(engine) >= HaveCurrentData;
        var step = _recovery.Take(settled, now);
        if (step == SeekRecoveryStep.None)
        {
            return false;
        }

        _stepsTaken[(int)step]++;
        var hr = 0;
        var copy = false;
        switch (step)
        {
            case SeekRecoveryStep.Retransfer:
                copy = true;
                break;
            case SeekRecoveryStep.SeekExNormal:
                hr = RecoverySeek(session, step, VideoConstants.SeekModeNormal, now);
                break;
            case SeekRecoveryStep.SeekExApproximate:
                hr = RecoverySeek(session, step, VideoConstants.SeekModeApproximate, now);
                break;
            case SeekRecoveryStep.FrameStep:
                hr = session.EngineEx is var ex && ex != 0 ? IMFMediaEngineEx.FrameStep(ex, 1) : unchecked((int)0x80004002); // E_NOINTERFACE
                break;
            case SeekRecoveryStep.PlayPause:
                hr = IMFMediaEngine.Play(engine);
                _ = IMFMediaEngine.Pause(engine);
                break;
            default: // SeekAway, SeekBack, FrameStepBack
                hr = RecoverySeek(session, step, -1, now);
                break;
        }

        Log(session, now, step, healed: false, hr);
        return copy;
    }

    /// <summary>A recovery seek (mode -1: SetCurrentTime, else SetCurrentTimeEx) through the seek queue; the HRESULT, 0 when queued.</summary>
    private int RecoverySeek(EngineSession session, SeekRecoveryStep step, int mode, long now)
    {
        var target = _recovery.TargetFor(step);
        if (_duration > TimeSpan.Zero && target > _duration)
        {
            target = _duration;
        }

        return _seeks.Request(target, Volatile.Read(ref session.SeekedEvents), now) ? SendRecoverySeek(session, target, mode) : 0;
    }

    private void Log(EngineSession session, long now, SeekRecoveryStep step, bool healed, int hr)
    {
        var engine = session.Engine;
        _recoveryLog[_recoveryLogCount++ % RecoveryLogSize] = new RecoveryLogEntry(
            now - session.Created, step, healed, hr, _lastTickResult, _lastTickPts,
            Volatile.Read(ref session.SeekedEvents), session.SeeksSent, Volatile.Read(ref session.PictureEvents),
            IMFMediaEngine.GetCurrentTime(engine), IMFMediaEngine.IsSeeking(engine) != 0);
    }

    private void ResetRecoveryDiagnostics()
    {
        Array.Clear(_stepsTaken);
        Array.Clear(_stepsHealed);
        _recoveryLogCount = 0;
    }

    private static int SumOf(int[] counts)
    {
        var sum = 0;
        foreach (var count in counts)
        {
            sum += count;
        }

        return sum;
    }

    /// <summary>
    /// What the seek recovery did since the open: steps taken and seeks healed per step, and the log of the last steps with
    /// the engine state at each (SEEKED events against seeks sent, picture events, the OnVideoStreamTick result and pts).
    /// Allocates; not for the render tick.
    /// </summary>
    internal string DescribeRecovery()
    {
        var text = new StringBuilder("seek recovery: taken [");
        AppendCounts(text, _stepsTaken);
        text.Append("], healed by [");
        AppendCounts(text, _stepsHealed);
        text.Append("], log [");
        var first = Math.Max(0, _recoveryLogCount - RecoveryLogSize);
        for (var i = first; i < _recoveryLogCount; i++)
        {
            var e = _recoveryLog[i % RecoveryLogSize];
            text.Append(i > first ? "; " : string.Empty);
            text.Append(CultureInfo.InvariantCulture, $"+{e.Elapsed * 1000 / Stopwatch.Frequency} ms {(e.Healed ? "HEALED by " : string.Empty)}{e.Step}");
            text.Append(CultureInfo.InvariantCulture, $" hr 0x{e.Hr:X8}, tick 0x{e.TickResult:X8} pts {e.TickPts}, SEEKED {e.Seeked}/sent {e.Sent}, pictures {e.Pictures}, currentTime {e.CurrentTime:F3}, seeking {e.Seeking}");
        }

        text.Append(']');
        return text.ToString();
    }

    private static void AppendCounts(StringBuilder text, int[] counts)
    {
        var any = false;
        for (var i = 1; i < counts.Length; i++)
        {
            if (counts[i] != 0)
            {
                text.Append(CultureInfo.InvariantCulture, $"{(any ? ", " : string.Empty)}{(SeekRecoveryStep)i} {counts[i]}");
                any = true;
            }
        }

        text.Append(any ? string.Empty : "none");
    }

    private readonly record struct RecoveryLogEntry(
        long Elapsed, SeekRecoveryStep Step, bool Healed, int Hr, int TickResult, long TickPts,
        int Seeked, int Sent, int Pictures, double CurrentTime, bool Seeking);

    /// <summary>Sends the queued seek once the one in flight is done. Allocation-free.</summary>
    private void PumpSeeks(EngineSession session)
    {
        if (_seeks.IsBusy && _seeks.TryNext(Volatile.Read(ref session.SeekedEvents), Stopwatch.GetTimestamp(), out var target))
        {
            SendSeek(session, target);
        }
    }

    private static void SendSeek(EngineSession session, TimeSpan target)
    {
        var hr = SendRecoverySeek(session, target, -1);
        if (hr < 0)
        {
            session.RaiseFailed($"IMFMediaEngine::SetCurrentTime failed: {ComHelpers.Describe(hr)}");
        }
    }

    /// <summary>
    /// SetCurrentTime (<paramref name="mode"/> -1) or IMFMediaEngineEx::SetCurrentTimeEx with the seek mode; without the Ex
    /// interface the latter is E_NOINTERFACE. The HRESULT is returned, not reported (recovery steps are only tries).
    /// </summary>
    private static int SendRecoverySeek(EngineSession session, TimeSpan target, int mode)
    {
        session.SeeksSent++;
        var seconds = target.Ticks / (double)TimeSpan.TicksPerSecond;
        if (mode < 0)
        {
            return IMFMediaEngine.SetCurrentTime(session.Engine, seconds);
        }

        return session.EngineEx is var ex && ex != 0 ? IMFMediaEngineEx.SetCurrentTimeEx(ex, seconds, mode) : unchecked((int)0x80004002);
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

    private void OnResourceLost() => Volatile.Write(ref _resourceLost, 1);

    /// <summary>
    /// The rate the open file plays at (1 = real time), kept for the files opened later. The session view sets it to the speed of
    /// the audio, which is the master clock: a video that plays at 1x while the audio runs at 2x would drift out of tolerance
    /// and be re-sought over and over. Clamped to [0.25, 4]; a failure is reported through <see cref="Failed"/>, not thrown.
    /// The engine keeps the rate across Play, Pause and seeks (HTML playbackRate semantics), so a Play-then-Pause recovery step does not reset it.
    /// </summary>
    public void SetPlaybackRate(double rate)
    {
        _playbackRate = double.IsNaN(rate) ? 1 : Math.Clamp(rate, 0.25, 4);
        if (_open && _session is { } session)
        {
            ApplyPlaybackRate(session);
        }
    }

    /// <summary>
    /// A refused rate (e.g. MF_E_UNSUPPORTED_RATE at 2x) is not a failure of the segment: <see cref="Failed"/> would make the view
    /// give the segment up. The engine goes back to 1x, <see cref="LastRateResult"/> keeps the HRESULT, and the audio clock keeps the
    /// picture in step by seeking.
    /// </summary>
    private void ApplyPlaybackRate(EngineSession session)
    {
        var engine = session.Engine;
        LastRateResult = PlaybackRateRequest.Apply(
            _playbackRate,
            rate => IMFMediaEngine.SetDefaultPlaybackRate(engine, rate),
            rate => IMFMediaEngine.SetPlaybackRate(engine, rate));
    }

    /// <summary>
    /// One Media Engine with its notify callback, for one file. Created and disposed on the player's thread; engine events
    /// reach <see cref="OnEngineEvent"/> on Media Foundation threads until <see cref="Dispose"/> detaches it.
    /// </summary>
    private sealed unsafe class EngineSession : IMediaEngineEvents, IDisposable
    {
        private const int EventLogSize = 48;

        private readonly string _path;
        private readonly Action<string> _failed;
        private readonly Action _resourceLost;
        private readonly long _created = Stopwatch.GetTimestamp();
        private nint _engineEx;
        private bool _engineExQueried;
        private readonly (long Timestamp, uint Event, nuint Param1, uint Param2)[] _events = new (long, uint, nuint, uint)[EventLogSize];
        private readonly Lock _eventsGate = new();
        private int _eventCount;
        private MediaEngineNotify? _notify;
        private nint _engine;
        private volatile bool _detached;
        private int _failedRaised;

        // Written by Media Foundation threads.
        public int PictureEvents;
        public int SeekedEvents;
        public int DurationChanged;

        // Written by the player's thread: the SetCurrentTime / SetCurrentTimeEx calls made (diagnostics).
        public int SeeksSent;

        private EngineSession(string path, VideoOutput output, Action<string> failed, Action resourceLost)
        {
            _path = path;
            Output = output;
            _failed = failed;
            _resourceLost = resourceLost;
        }

        public nint Engine => _engine;

        /// <summary>The Stopwatch timestamp of the creation.</summary>
        public long Created => _created;

        /// <summary>IMFMediaEngineEx of the engine (QueryInterface once, UI thread); 0 when the engine has none.</summary>
        public nint EngineEx
        {
            get
            {
                if (!_engineExQueried && _engine != 0)
                {
                    _engineExQueried = true;
                    _engineEx = ComCall.Query(_engine, VideoConstants.IidIMFMediaEngineEx);
                }

                return _engineEx;
            }
        }

        public VideoOutput Output { get; }

        /// <summary>LOADEDMETADATA (result), ERROR before it (exception) or a close (cancelled).</summary>
        public TaskCompletionSource Loaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <exception cref="COMException">Creating the engine or setting its source failed.</exception>
        public static EngineSession Create(string path, VideoOutput output, nint deviceManager, Action<string> failed, Action resourceLost)
        {
            var session = new EngineSession(path, output, failed, resourceLost);
            nint attributes = 0, factory = 0, notify = 0, engine = 0, url = 0;
            try
            {
                session._notify = new MediaEngineNotify(session);
                notify = (nint)ComInterfaceMarshaller<IMFMediaEngineNotify>.ConvertToUnmanaged(session._notify);

                ComHelpers.Check(ScreenNativeMethods.MFCreateAttributes(&attributes, 3), "MFCreateAttributes");
                var key = VideoConstants.MfMediaEngineCallback;
                ComHelpers.Check(IMFAttributes.SetUnknown(attributes, &key, notify), "IMFAttributes::SetUnknown(MF_MEDIA_ENGINE_CALLBACK)");
                key = VideoConstants.MfMediaEngineVideoOutputFormat;
                ComHelpers.Check(IMFAttributes.SetUINT32(attributes, &key, ScreenConstants.DxgiFormatB8G8R8A8Unorm), "IMFAttributes::SetUINT32(MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT)");
                if (output == VideoOutput.Direct3D)
                {
                    key = VideoConstants.MfMediaEngineDxgiManager;
                    ComHelpers.Check(IMFAttributes.SetUnknown(attributes, &key, deviceManager), "IMFAttributes::SetUnknown(MF_MEDIA_ENGINE_DXGI_MANAGER)");
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

                // Setting the source starts loading it (the HTML src semantics); IMFMediaEngine::Load would restart it.
                ComHelpers.Check(IMFMediaEngine.SetSource(session._engine, (char*)url), "IMFMediaEngine::SetSource");
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

            lock (_eventsGate)
            {
                _events[_eventCount % EventLogSize] = (Stopwatch.GetTimestamp(), mediaEvent, param1, param2);
                _eventCount++;
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
                case VideoConstants.EventSeeked:
                    Interlocked.Increment(ref SeekedEvents);
                    Interlocked.Increment(ref PictureEvents);
                    break;
                case VideoConstants.EventLoadedData:
                case VideoConstants.EventFirstFrameReady:
                case VideoConstants.EventFormatChange:
                    Interlocked.Increment(ref PictureEvents);
                    break;
                case VideoConstants.EventDurationChange:
                    Volatile.Write(ref DurationChanged, 1);
                    break;
                case VideoConstants.EventResourceLost:
                    _resourceLost();
                    RaiseFailed($"Playing '{System.IO.Path.GetFileName(_path)}' failed: the video resources were lost (device removed).");
                    break;
                case VideoConstants.EventStreamRenderingError:
                    RaiseFailed($"Playing '{System.IO.Path.GetFileName(_path)}' failed: stream rendering error (HRESULT 0x{param2:X8}).");
                    break;
            }
        }

        /// <summary>The last engine events, oldest first, e.g. "+12 ms LOADSTART, +80 ms LOADEDMETADATA".</summary>
        public string DescribeEvents()
        {
            lock (_eventsGate)
            {
                var text = new StringBuilder();
                var first = Math.Max(0, _eventCount - EventLogSize);
                for (var i = first; i < _eventCount; i++)
                {
                    var (timestamp, mediaEvent, param1, param2) = _events[i % EventLogSize];
                    if (text.Length > 0)
                    {
                        text.Append(", ");
                    }

                    text.Append(CultureInfo.InvariantCulture, $"+{(timestamp - _created) * 1000 / Stopwatch.Frequency} ms {MediaEngineEvents.Name(mediaEvent)}");
                    if (param1 != 0 || param2 != 0)
                    {
                        text.Append(CultureInfo.InvariantCulture, $"({param1}, 0x{param2:X8})");
                    }
                }

                return text.Length == 0 ? "none" : text.ToString();
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
                ComCall.Release(ref _engineEx);
                IMFMediaEngine.Shutdown(_engine);
                ComCall.Release(ref _engine);
            }
        }
    }
}

/// <summary>Names of MF_MEDIA_ENGINE_EVENT values for logs (pure).</summary>
internal static class MediaEngineEvents
{
    public static string Name(uint mediaEvent) => mediaEvent switch
    {
        1 => "LOADSTART",
        2 => "PROGRESS",
        3 => "SUSPEND",
        4 => "ABORT",
        5 => "ERROR",
        6 => "EMPTIED",
        7 => "STALLED",
        8 => "PLAY",
        9 => "PAUSE",
        10 => "LOADEDMETADATA",
        11 => "LOADEDDATA",
        12 => "WAITING",
        13 => "PLAYING",
        14 => "CANPLAY",
        15 => "CANPLAYTHROUGH",
        16 => "SEEKING",
        17 => "SEEKED",
        18 => "TIMEUPDATE",
        19 => "ENDED",
        20 => "RATECHANGE",
        21 => "DURATIONCHANGE",
        22 => "VOLUMECHANGE",
        1000 => "FORMATCHANGE",
        1001 => "PURGEQUEUEDEVENTS",
        1002 => "TIMELINE_MARKER",
        1003 => "BALANCECHANGE",
        1004 => "DOWNLOADCOMPLETE",
        1005 => "BUFFERINGSTARTED",
        1006 => "BUFFERINGENDED",
        1007 => "FRAMESTEPCOMPLETED",
        1008 => "NOTIFYSTABLESTATE",
        1009 => "FIRSTFRAMEREADY",
        1010 => "TRACKSCHANGE",
        1011 => "OPMINFO",
        1012 => "RESOURCELOST",
        1013 => "DELAYLOADEVENT_CHANGED",
        1014 => "STREAMRENDERINGERROR",
        1015 => "SUPPORTEDRATES_CHANGED",
        1016 => "AUDIOENDPOINTCHANGE",
        _ => mediaEvent.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>Probes the D3D11 hardware once per process (a device is created, classified and released).</summary>
internal static class GpuProbe
{
    private static readonly Lazy<GpuCapability> Probe = new(Run, LazyThreadSafetyMode.ExecutionAndPublication);

    public static GpuCapability Capability => Probe.Value;

    private static GpuCapability Run()
    {
        try
        {
            using var device = GpuDevice.Create();
            return new GpuCapability(true, device.IsSoftwareRenderer, device.HasVideoSupport, device.Description);
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            return new GpuCapability(false, true, false, $"no D3D11 device ({ex.Message})");
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
