using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Capture.Windows.Screen.Interop;
using Murmur.Core.Capture;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Murmur.Capture.Windows.Screen;

/// <summary>
/// Screen recorder: Windows.Graphics.Capture (free-threaded frame pool on a shared D3D11 device) -> encoder samples
/// (see <see cref="EncoderInput"/>) -> Media Foundation sink writer (H.264) -> fragmented MP4.
/// <para>
/// <b>Encoder input.</b> With a hardware H.264 encoder on a real GPU the path is zero-copy: frames are copied/scaled on
/// the GPU into pooled D3D11 textures (<see cref="GpuFrameCopier"/>) that go straight to the hardware MFT. Without one
/// (software renderer such as WARP / the Basic Render Driver, no video support, no hardware MFT, or the hardware
/// writer could not be set up or picked a software encoder) frames are read back into system-memory RGB32 samples
/// (<see cref="CpuFrameCopier"/>) for the software encoder; <see cref="ScreenRecorderStats.Encoder"/> then says
/// "CPU readback" and <see cref="FramePath"/> says why (see <see cref="FramePathPolicy"/>).
/// </para>
/// <para>
/// <b>Frame path.</b> FrameArrived (WGC worker thread) drains the pool through raw ABI calls, rate-limits to
/// <see cref="ScreenCaptureOptions.FrameRate"/>, takes a sample from the segment's <see cref="IFramePath"/>, fills it
/// and enqueues it in a bounded lock-free ring; a dedicated encoder thread timestamps (QPC timeline relative to the
/// segment start) and writes the samples. The handler never blocks on the encoder: an exhausted sample pool or a full
/// ring drops the frame and counts it. Both loops are allocation-free in steady state. A failing copy step is named in
/// the fault (e.g. "GPU frame copy failed at QueryInterface(ID3D11VideoDevice) ...").
/// </para>
/// <para>
/// <b>Static screens.</b> WGC only delivers frames when the screen changes; the encoder thread repeats the last frame
/// once a second while nothing arrives and once more at stop, so the file's timeline covers the whole segment (and
/// fragments keep being written for crash safety). Repeats are only written while the segment accepts frames: after a
/// fatal fault (display disconnected, frame pool or GPU failure) nothing extends the timeline any more and the tail stops
/// at the fault time, so the file never shows a frozen frame as if it were live content (see <see cref="RepeatPolicy"/>).
/// </para>
/// <para>
/// <b>Segments without frames.</b> WGC may deliver no frame at all (headless or non-composited session, locked
/// workstation). Media Foundation cannot finalize an MP4 without samples, so when nothing reached the encoder by the
/// time the segment stops the encoder thread writes one opaque black frame at sample time 0 (see
/// <see cref="PlaceholderFrame"/>), which normally makes the file a valid one-frame MP4. If Finalize still fails
/// (MF_E_SINK_NO_SAMPLES_PROCESSED, or any failure of a segment without captured frames; the placeholder may have been
/// rejected) the segment ends as an empty one: the writer is released, the partial file (not a valid MP4) is deleted and
/// stopping does not throw (see <see cref="SegmentCompletion"/>). <see cref="ScreenRecorderStats.FramesCaptured"/>
/// stays 0, so callers can tell; <see cref="LastSegmentEmpty"/> and <see cref="LastSegmentDiagnostic"/> report the
/// placeholder outcome and the HRESULTs, the placeholder is counted only in <see cref="PlaceholderFrames"/>, and
/// <see cref="FrameArrivedEvents"/> counts raw FrameArrived callbacks.
/// </para>
/// <para>
/// <b>Stats.</b> <see cref="ScreenRecorderStats.FramesCaptured"/> counts frames admitted by the rate limiter (frames
/// above the target rate are not counted), <see cref="ScreenRecorderStats.FramesEncoded"/> the captured frames handed
/// to the encoder, <see cref="ScreenRecorderStats.FramesDropped"/> captured frames lost to back-pressure or errors;
/// repeated frames are counted in none of them. Counters accumulate over the recorder's lifetime.
/// </para>
/// <para>
/// <b>Lifetime.</b> The D3D11 device and Media Foundation are created on the first segment and kept until
/// <see cref="DisposeAsync"/> (the device is recreated if it was lost); the capture session, sample pool, encoder and
/// encoder thread live for one segment. Faults are raised on the thread pool, at most once per segment.
/// </para>
/// UNVERIFIED-ON-WINDOWS until the Windows-trait tests run in CI.
/// </summary>
internal sealed class WgcScreenRecorder : IScreenRecorder
{
    internal const int QueueCapacity = 4;
    internal const uint InitialSamples = 4;
    internal const uint MaximumSamples = 12;

    /// <summary>System-memory samples of the CPU path: queue + last frame + the one being filled + what the software encoder holds.</summary>
    internal const int InitialCpuSamples = 3;
    internal const int MaximumCpuSamples = 8;
    internal const int MaxFramesPerEvent = 4;
    internal static readonly long RepeatInterval = MediaTime.TicksPerSecond;
    private const int EncoderWaitMilliseconds = 250;
    private static readonly TimeSpan EncoderJoinTimeout = TimeSpan.FromSeconds(30);

    private readonly ScreenCaptureOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TypedEventHandler<Direct3D11CaptureFramePool, object> _frameArrived;
    private GpuDevice? _gpu;
    private Segment? _segment;
    private volatile bool _recording;
    private volatile string _encoder = EncoderNaming.None;
    private bool _disposed;
    private int _inHandler;
    private long _framesCaptured;
    private long _framesEncoded;
    private long _framesDropped;
    private long _framesRateLimited;
    private long _framesRepeated;
    private long _placeholderFrames;
    private long _frameArrivedEvents;

    public WgcScreenRecorder(ScreenCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.FrameRate, 1, nameof(options.FrameRate));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.FrameRate, 240, nameof(options.FrameRate));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxHeight, 2, nameof(options.MaxHeight));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.BitrateKbps, nameof(options.BitrateKbps));
        _options = options;
        _frameArrived = OnFrameArrived;
    }

    public bool IsRecording => _recording;

    public ScreenRecorderStats Stats => new(
        Interlocked.Read(ref _framesCaptured),
        Interlocked.Read(ref _framesEncoded),
        Interlocked.Read(ref _framesDropped),
        _encoder);

    /// <summary>Frames WGC delivered above the target frame rate (diagnostics; not part of <see cref="Stats"/>).</summary>
    internal long FramesRateLimited => Interlocked.Read(ref _framesRateLimited);

    /// <summary>Repeated frames written for static screens and the segment tail (diagnostics).</summary>
    internal long FramesRepeated => Interlocked.Read(ref _framesRepeated);

    /// <summary>Black placeholder frames written for segments in which no frame reached the encoder (diagnostics).</summary>
    internal long PlaceholderFrames => Interlocked.Read(ref _placeholderFrames);

    /// <summary>FrameArrived callbacks WGC raised, before any filtering (diagnostics: 0 means WGC delivered nothing).</summary>
    internal long FrameArrivedEvents => Interlocked.Read(ref _frameArrivedEvents);

    /// <summary>The D3D11 device in use, e.g. "hardware D3D11 FL 11.1" (diagnostics; null before the first segment).</summary>
    internal string? DeviceDescription => _gpu?.Description;

    /// <summary>Encoder frame size of the current or last segment (diagnostics).</summary>
    internal PixelSize LastEncoderSize { get; private set; }

    /// <summary>
    /// The encoder input of the current or last segment and why it was chosen, e.g. "GPU zero-copy (1 hardware H.264
    /// encoder MFT)" or "CPU readback: software renderer ..." (diagnostics; null before the first segment).
    /// </summary>
    internal string? FramePath { get; private set; }

    /// <summary>Tests only: use the CPU readback path even when a hardware encoder is available.</summary>
    internal bool ForceCpuReadback { get; init; }

    /// <summary>True when the last stopped segment ended without a file (see <see cref="SegmentOutcome.Empty"/>).</summary>
    internal bool LastSegmentEmpty { get; private set; }

    /// <summary>
    /// How the last stopped segment ended: captured samples, placeholder outcome, Finalize HRESULT and what happened to
    /// the file (diagnostics; null before the first stop).
    /// </summary>
    internal string? LastSegmentDiagnostic { get; private set; }

    public event Action<CaptureFault>? Faulted;

    /// <exception cref="NotSupportedException">Windows.Graphics.Capture is not supported on this system.</exception>
    /// <exception cref="ArgumentException"><see cref="ScreenCaptureOptions.DisplayId"/> names no connected display.</exception>
    /// <exception cref="COMException">D3D11, Media Foundation or WGC set-up failed.</exception>
    public async ValueTask StartSegmentAsync(string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_recording)
            {
                throw new InvalidOperationException("A segment is already being recorded.");
            }

            var path = Path.GetFullPath(filePath);
            LastSegmentEmpty = false;
            LastSegmentDiagnostic = null;
            await Task.Run(() => StartCore(path), CancellationToken.None).ConfigureAwait(false);
            _recording = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <remarks>
    /// A segment in which WGC delivered no frame does not throw: it finalizes with one black placeholder frame, or, if
    /// Media Foundation still cannot finalize it, ends without a file (<see cref="LastSegmentEmpty"/>).
    /// </remarks>
    /// <exception cref="COMException">Finalizing a segment with captured frames failed (encoder or disk failure), or the encoder hung.</exception>
    public async ValueTask StopSegmentAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_recording || _segment is not { } segment)
            {
                return;
            }

            try
            {
                await Task.Run(() => StopCore(segment), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _recording = false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_recording && _segment is { } segment)
            {
                try
                {
                    await Task.Run(() => StopCore(segment)).ConfigureAwait(false);
                }
                catch (COMException)
                {
                    // Disposal must not throw; the segment file is as complete as Finalize could make it.
                }
                finally
                {
                    _recording = false;
                }
            }

            _gpu?.Dispose();
            _gpu = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- segment set-up and teardown

    private void StartCore(string path)
    {
        if (!CaptureInterop.IsSupported())
        {
            throw new NotSupportedException("Windows.Graphics.Capture is not supported on this system.");
        }

        MediaFoundationRuntime.EnsureStarted();
        if (_gpu is { IsLost: true })
        {
            _gpu.Dispose();
            _gpu = null;
        }

        _gpu ??= GpuDevice.Create();
        var gpu = _gpu;
        var display = Displays.Find(_options.DisplayId);
        var segment = new Segment(_options.FrameRate, path);
        try
        {
            segment.Item = CaptureInterop.CreateItemForMonitor(display.Monitor);
            var itemSize = segment.Item.Size;
            if (itemSize.Width <= 0 || itemSize.Height <= 0)
            {
                throw new COMException($"The capture item of {display.Info.Id} has no size ({itemSize.Width}x{itemSize.Height}).", ScreenConstants.EFail);
            }

            segment.PoolSize = itemSize;
            segment.EncoderSize = EncoderGeometry.FitEncoderSize(itemSize.Width, itemSize.Height, _options.MaxHeight);
            LastEncoderSize = segment.EncoderSize;
            FramePath = OpenEncoder(segment, gpu, path, new H264EncoderSettings(segment.EncoderSize, _options.FrameRate, _options.BitrateKbps), ForceCpuReadback);
            _encoder = segment.Writer!.Encoder;

            segment.Pool = Direct3D11CaptureFramePool.CreateFreeThreaded(gpu.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, itemSize);
            segment.PoolAbi = CaptureInterop.GetFramePoolAbi(segment.Pool);
            segment.Session = segment.Pool.CreateCaptureSession(segment.Item);
            CaptureInterop.Configure(segment.Session);
            segment.ItemClosed = (_, _) => RaiseFault(segment, "The captured display was disconnected.", isFatal: true);
            segment.Item.Closed += segment.ItemClosed;

            segment.Timeline.Reset(MediaTime.Now());
            segment.EncoderThread = new Thread(() => EncoderLoop(segment))
            {
                IsBackground = true,
                Name = "Murmur screen encoder",
            };
            segment.EncoderThread.Start();

            segment.Pool.FrameArrived += _frameArrived;
            Volatile.Write(ref _segment, segment);
            segment.Accepting = true;
            segment.Session.StartCapture();
        }
        catch
        {
            Teardown(segment);
            throw;
        }
    }

    private void StopCore(Segment segment)
    {
        segment.Accepting = false;
        Volatile.Write(ref _segment, null);
        try
        {
            segment.Session?.Dispose();
        }
        catch (Exception)
        {
            // Closing the session of a lost device or a closed item can fail; Teardown still finalizes the file.
        }
        finally
        {
            Teardown(segment);
        }

        // The writer is released now (unless the encoder hung, which fails anyway), so an empty segment's file can go.
        var report = new SegmentReport(segment.FinalizeResult, segment.EncoderHung, segment.CapturedWritten, segment.Placeholder);
        try
        {
            var result = SegmentCompletion.Complete(segment.Path, report);
            LastSegmentEmpty = result.Empty;
            LastSegmentDiagnostic = result.Diagnostic;
        }
        catch (COMException ex)
        {
            LastSegmentEmpty = false;
            LastSegmentDiagnostic = ex.Message;
            throw;
        }
    }

    /// <summary>Stops capture, lets the encoder thread finish and finalize, and releases everything of the segment. Never throws.</summary>
    private void Teardown(Segment segment)
    {
        segment.Accepting = false;
        if (ReferenceEquals(Volatile.Read(ref _segment), segment))
        {
            Volatile.Write(ref _segment, null);
        }

        if (segment.Pool is not null)
        {
            try
            {
                segment.Pool.FrameArrived -= _frameArrived;
            }
            catch (Exception)
            {
                // The pool is being torn down anyway.
            }
        }

        // A FrameArrived invocation that already started sees _segment == null or finishes its frame first.
        var spin = new SpinWait();
        var deadline = Environment.TickCount64 + 2_000;
        while (Volatile.Read(ref _inHandler) != 0 && Environment.TickCount64 < deadline)
        {
            spin.SpinOnce();
        }

        if (segment.EncoderThread is { } thread)
        {
            segment.StopTicks = MediaTime.Now();
            Volatile.Write(ref segment.StopRequested, true);
            segment.Wake.Set();
            if (!thread.Join(EncoderJoinTimeout))
            {
                RaiseFault(segment, "The screen encoder did not finish within 30 s; the segment file may be incomplete.", isFatal: true);
                segment.FinalizeResult = ScreenConstants.EFail;
                segment.EncoderHung = true;
                return; // leak the encoder objects rather than release them under a running thread
            }
        }
        else
        {
            segment.FinalizeResult = ScreenConstants.SOk;
        }

        if (segment.Item is not null && segment.ItemClosed is not null)
        {
            try
            {
                segment.Item.Closed -= segment.ItemClosed;
            }
            catch (Exception)
            {
                // Ignore: the item may already be closed.
            }
        }

        try
        {
            segment.Session?.Dispose();
            segment.Pool?.Dispose();
        }
        catch (Exception)
        {
            // Closing WGC objects of a lost device can fail; nothing left to do.
        }

        ComCall.Release(ref segment.PoolAbi);
        segment.Writer?.Dispose();
        segment.Frames?.Dispose(); // after the writer: Media Foundation has released the samples by now
        segment.Wake.Dispose();
    }

    /// <summary>
    /// Opens the segment's sink writer and frame path: zero-copy GPU input when <see cref="FramePathPolicy"/> allows it
    /// and the hardware writer comes up with a hardware encoder, CPU readback otherwise. Returns the
    /// <see cref="FramePath"/> diagnostic.
    /// </summary>
    /// <exception cref="COMException">Not even the CPU readback configuration could be set up.</exception>
    private static string OpenEncoder(Segment segment, GpuDevice gpu, string path, H264EncoderSettings settings, bool forceCpu)
    {
        var hardwareEncoders = !forceCpu && gpu.HasVideoSupport && !gpu.IsSoftwareRenderer ? HardwareEncoders.CountH264() : 0;
        var reason = forceCpu
            ? "CPU readback: forced"
            : FramePathPolicy.Describe(gpu.HasVideoSupport, gpu.IsSoftwareRenderer, hardwareEncoders);
        if (!forceCpu && FramePathPolicy.Choose(gpu.HasVideoSupport, gpu.IsSoftwareRenderer, hardwareEncoders) == EncoderInput.Gpu)
        {
            try
            {
                segment.Frames = GpuFrameCopier.Create(gpu.Device, gpu.Context, gpu.DeviceManager, settings.FrameSize, settings.FrameRate, InitialSamples, MaximumSamples);
                segment.Writer = H264SinkWriter.Create(path, settings, EncoderInput.Gpu, gpu.DeviceManager);
                if (segment.Writer.IsHardware != false)
                {
                    return reason;
                }

                // A hardware MFT exists, but not for this device's adapter: a software encoder fed D3D11 samples is the
                // combination that failed on WARP, so read the frames back instead.
                reason = "CPU readback: the zero-copy writer picked a software encoder (" + segment.Writer.Encoder + ")";
            }
            catch (COMException ex)
            {
                reason = "CPU readback: the zero-copy hardware encoder could not be set up (" + ex.Message + ")";
            }

            segment.Writer?.Dispose();
            segment.Writer = null;
            segment.Frames?.Dispose();
            segment.Frames = null;
        }

        segment.Frames = new CpuFrameCopier(gpu.Device, gpu.Context, settings.FrameSize, MediaFoundationSampleFactory.Instance, InitialCpuSamples, MaximumCpuSamples);
        segment.Writer = H264SinkWriter.Create(path, settings, EncoderInput.Cpu, 0);
        return reason;
    }

    // ---------------------------------------------------------------- frame path (WGC worker thread)

    private unsafe void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Interlocked.Increment(ref _frameArrivedEvents);
        if (Interlocked.CompareExchange(ref _inHandler, 1, 0) != 0)
        {
            return; // another invocation is draining the pool
        }

        Segment? segment = null;
        try
        {
            segment = Volatile.Read(ref _segment);
            if (segment is null || segment.PoolAbi == 0)
            {
                return;
            }

            for (var i = 0; i < MaxFramesPerEvent; i++)
            {
                nint frame = 0;
                var hr = IDirect3D11CaptureFramePool.TryGetNextFrame(segment.PoolAbi, &frame);
                if (hr < 0)
                {
                    RaiseFault(segment, $"Direct3D11CaptureFramePool.TryGetNextFrame failed: {ComHelpers.Describe(hr)}", isFatal: true);
                    return;
                }

                if (frame == 0)
                {
                    break;
                }

                try
                {
                    ProcessFrame(segment, frame);
                }
                finally
                {
                    CaptureInterop.CloseFrame(ref frame);
                }
            }

            if (segment.RecreatePending)
            {
                segment.RecreatePending = false;
                var size = new SizeInt32 { Width = segment.PendingSize.Width, Height = segment.PendingSize.Height };
                segment.Pool!.Recreate(_gpu!.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                segment.PoolSize = size;
            }
        }
        catch (Exception ex)
        {
            if (segment is not null)
            {
                RaiseFault(segment, "Screen frame handling failed: " + ex.Message, isFatal: true);
            }
        }
        finally
        {
            Volatile.Write(ref _inHandler, 0);
        }
    }

    private unsafe void ProcessFrame(Segment segment, nint frame)
    {
        long ticks;
        SizeInt32Abi content;
        if (IDirect3D11CaptureFrame.get_SystemRelativeTime(frame, &ticks) < 0
            || IDirect3D11CaptureFrame.get_ContentSize(frame, &content) < 0)
        {
            return;
        }

        if ((content.Width != segment.PoolSize.Width || content.Height != segment.PoolSize.Height) && content.Width > 0 && content.Height > 0)
        {
            segment.PendingSize = content;
            segment.RecreatePending = true;
        }

        if (!segment.Accepting)
        {
            return;
        }

        if (!segment.Limiter.TryAccept(ticks))
        {
            Interlocked.Increment(ref _framesRateLimited);
            return;
        }

        Interlocked.Increment(ref _framesCaptured);
        var frames = segment.Frames!;
        nint sample;
        int hr;
        lock (segment.CopyLock)
        {
            hr = frames.AllocateSample(out sample);
        }

        if (hr < 0 || sample == 0)
        {
            Interlocked.Increment(ref _framesDropped);
            if (hr != ScreenConstants.MfESampleAllocatorEmpty)
            {
                RaiseFault(segment, $"Allocating an encoder sample failed ({FrameCopyNaming.Path(frames.Input)}): {ComHelpers.Describe(hr)}", isFatal: true);
            }

            return;
        }

        FrameFailure failure;
        hr = CaptureInterop.GetFrameTexture(frame, out var texture, out var failedStep);
        if (hr >= 0)
        {
            lock (segment.CopyLock)
            {
                hr = frames.CopyFrame(sample, texture, new PixelSize(content.Width, content.Height));
                failure = frames.LastFailure;
            }

            ComCall.Release(ref texture);
        }
        else
        {
            failure = new FrameFailure(failedStep, hr);
        }

        if (hr < 0)
        {
            ComCall.Release(ref sample);
            Interlocked.Increment(ref _framesDropped);
            RaiseFault(segment, FrameCopyNaming.Fault(frames.Input, failure, hr), isFatal: true);
            return;
        }

        if (!segment.Queue.TryEnqueue(new QueuedFrame(sample, ticks)))
        {
            ComCall.Release(ref sample);
            Interlocked.Increment(ref _framesDropped);
            return;
        }

        segment.Wake.Set();
    }

    // ---------------------------------------------------------------- encoder thread

    private void EncoderLoop(Segment segment)
    {
        using var apartment = ComHelpers.EnterMta();
        try
        {
            while (true)
            {
                segment.Wake.WaitOne(EncoderWaitMilliseconds);
                Drain(segment);
                if (Volatile.Read(ref segment.StopRequested))
                {
                    Drain(segment);
                    break;
                }

                // Repeats are only written while the segment accepts frames: after a fatal fault the file must end
                // at the fault instead of showing a frozen last frame (RepeatPolicy).
                var now = MediaTime.Now();
                if (RepeatPolicy.ShouldRepeat(
                    segment.LastSample != 0,
                    segment.WriterHealthy,
                    segment.Accepting,
                    now,
                    segment.LastWriteTicks,
                    Interlocked.Read(ref segment.FaultTicks),
                    RepeatInterval))
                {
                    Repeat(segment, now);
                }
            }

            // Tail: extend the file to the stop time (or to the fault time) so its duration matches what was captured.
            if (RepeatPolicy.TryGetTail(
                segment.LastSample != 0,
                segment.WriterHealthy,
                segment.StopTicks,
                Interlocked.Read(ref segment.FaultTicks),
                segment.FrameDuration,
                segment.Timeline.Start,
                segment.Timeline.LastSampleTime,
                out var tail))
            {
                Repeat(segment, tail);
            }
        }
        catch (Exception ex)
        {
            RaiseFault(segment, "Screen encoder failed: " + ex.Message, isFatal: true);
        }
        finally
        {
            while (segment.Queue.TryDequeue(out var left))
            {
                var sample = left.Sample;
                ComCall.Release(ref sample);
                Interlocked.Increment(ref _framesDropped);
            }

            // Media Foundation cannot finalize a file without samples: give an empty segment a black frame at time 0.
            if (PlaceholderFrame.ShouldWrite(segment.LastSample != 0, segment.WriterHealthy))
            {
                segment.Placeholder = WritePlaceholder(segment);
            }
            else if (segment.LastSample == 0)
            {
                segment.Placeholder = new PlaceholderOutcome(PlaceholderStage.SkippedWriterFailed, ScreenConstants.SOk);
            }

            ComCall.Release(ref segment.LastSample);
            segment.FinalizeResult = segment.Writer is { } writer ? writer.FinalizeFile() : ScreenConstants.EFail;
        }
    }

    private void Drain(Segment segment)
    {
        while (segment.Queue.TryDequeue(out var queued))
        {
            if (!segment.WriterHealthy)
            {
                var sample = queued.Sample;
                ComCall.Release(ref sample);
                Interlocked.Increment(ref _framesDropped);
                continue;
            }

            Write(segment, queued.Sample, queued.Ticks, SampleKind.Captured);
        }
    }

    private void Repeat(Segment segment, long ticks)
    {
        nint sample;
        int hr;
        lock (segment.CopyLock)
        {
            hr = segment.Frames!.AllocateSample(out sample);
            if (hr >= 0 && sample != 0)
            {
                hr = segment.Frames.CopySample(sample, segment.LastSample);
            }
        }

        if (hr < 0 || sample == 0)
        {
            ComCall.Release(ref sample);
            return;
        }

        Write(segment, sample, ticks, SampleKind.Repeated);
    }

    /// <summary>Writes the black placeholder frame at sample time 0 (see <see cref="PlaceholderFrame"/>). Never throws.</summary>
    private PlaceholderOutcome WritePlaceholder(Segment segment)
    {
        nint sample = 0;
        try
        {
            var frames = segment.Frames!;
            int hr;
            lock (segment.CopyLock)
            {
                hr = frames.AllocateSample(out sample);
            }

            if (hr < 0 || sample == 0)
            {
                RaiseFault(segment, $"No frame was captured and the placeholder sample could not be allocated: {ComHelpers.Describe(hr)}", isFatal: true);
                return new PlaceholderOutcome(PlaceholderStage.AllocateFailed, hr < 0 ? hr : ScreenConstants.EFail);
            }

            FrameFailure failure;
            lock (segment.CopyLock)
            {
                hr = frames.FillBlack(sample);
                failure = frames.LastFailure;
            }

            if (hr < 0)
            {
                ComCall.Release(ref sample);
                RaiseFault(segment, $"No frame was captured and the placeholder frame could not be drawn ({FrameCopyNaming.Fault(frames.Input, failure, hr)})", isFatal: true);
                return new PlaceholderOutcome(PlaceholderStage.FillFailed, hr);
            }

            var owned = sample;
            sample = 0; // Write takes ownership
            hr = Write(segment, owned, PlaceholderFrame.CaptureTicks(segment.Timeline), SampleKind.Placeholder);
            return new PlaceholderOutcome(hr < 0 ? PlaceholderStage.WriteFailed : PlaceholderStage.Written, hr);
        }
        catch (Exception ex)
        {
            ComCall.Release(ref sample);
            RaiseFault(segment, "No frame was captured and the placeholder frame failed: " + ex.Message, isFatal: true);
            return new PlaceholderOutcome(PlaceholderStage.Exception, ex.HResult);
        }
    }

    /// <summary>Timestamps and writes one sample; takes ownership of <paramref name="sample"/>. Returns the WriteSample HRESULT.</summary>
    private int Write(Segment segment, nint sample, long ticks, SampleKind kind)
    {
        var time = segment.Timeline.ToSampleTime(ticks);
        var step = "IMFSample::SetSampleTime";
        var hr = IMFSample.SetSampleTime(sample, time);
        if (hr >= 0)
        {
            step = "IMFSample::SetSampleDuration";
            hr = IMFSample.SetSampleDuration(sample, segment.FrameDuration);
        }

        if (hr >= 0)
        {
            step = "IMFSinkWriter::WriteSample";
            hr = segment.Writer!.WriteSample(sample);
        }

        if (hr < 0)
        {
            ComCall.Release(ref sample);
            segment.WriterHealthy = false;
            segment.Accepting = false;
            if (kind == SampleKind.Captured)
            {
                Interlocked.Increment(ref _framesDropped);
            }

            RaiseFault(segment, $"{step} failed ({FrameCopyNaming.Path(segment.Frames!.Input)} input): {ComHelpers.Describe(hr)}", isFatal: true);
            return hr;
        }

        switch (kind)
        {
            case SampleKind.Captured:
                Interlocked.Increment(ref _framesEncoded);
                segment.CapturedWritten++;
                break;
            case SampleKind.Repeated:
                Interlocked.Increment(ref _framesRepeated);
                break;
            default:
                Interlocked.Increment(ref _placeholderFrames);
                break;
        }

        ComCall.Release(ref segment.LastSample);
        segment.LastSample = sample;
        segment.LastWriteTicks = MediaTime.Now();
        return hr;
    }

    private void RaiseFault(Segment segment, string message, bool isFatal)
    {
        if (isFatal)
        {
            // Record the first fatal fault's time before Accepting drops, so the encoder thread never repeats past it.
            Interlocked.CompareExchange(ref segment.FaultTicks, MediaTime.Now(), RepeatPolicy.NoFault);
            segment.Accepting = false;
        }

        if (Interlocked.Exchange(ref segment.FaultRaised, 1) != 0)
        {
            return;
        }

        var fault = new CaptureFault("screen", message, isFatal);
        ThreadPool.UnsafeQueueUserWorkItem(static state => state.Recorder.Faulted?.Invoke(state.Fault), (Recorder: this, Fault: fault), preferLocal: false);
    }

    private readonly record struct QueuedFrame(nint Sample, long Ticks);

    private enum SampleKind
    {
        Captured,
        Repeated,
        Placeholder,
    }

    /// <summary>Everything that lives for one segment.</summary>
    private sealed class Segment(int frameRate, string path)
    {
        public readonly string Path = path;
        public readonly SpscRing<QueuedFrame> Queue = new(QueueCapacity);
        public readonly AutoResetEvent Wake = new(false);
        public readonly Lock CopyLock = new();
        public readonly FrameRateLimiter Limiter = new(frameRate);
        public readonly SegmentTimeline Timeline = new();
        public readonly long FrameDuration = MediaTime.FrameDuration(frameRate);

        public GraphicsCaptureItem? Item;
        public TypedEventHandler<GraphicsCaptureItem, object>? ItemClosed;
        public Direct3D11CaptureFramePool? Pool;
        public GraphicsCaptureSession? Session;
        public nint PoolAbi;
        public IFramePath? Frames;
        public H264SinkWriter? Writer;
        public Thread? EncoderThread;
        public SizeInt32 PoolSize;
        public PixelSize EncoderSize;

        // FrameArrived thread
        public volatile bool Accepting;
        public bool RecreatePending;
        public SizeInt32Abi PendingSize;

        // Encoder thread
        public nint LastSample;
        public long LastWriteTicks;
        public volatile bool WriterHealthy = true;
        public long CapturedWritten;
        public PlaceholderOutcome Placeholder = PlaceholderOutcome.NotNeeded;

        // Stop hand-shake
        public bool StopRequested;
        public long StopTicks;
        public int FinalizeResult;
        public bool EncoderHung;
        public int FaultRaised;
        public long FaultTicks = RepeatPolicy.NoFault; // QPC time of the first fatal fault (Interlocked)
    }
}
