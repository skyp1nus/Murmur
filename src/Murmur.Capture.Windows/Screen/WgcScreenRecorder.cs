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
/// <b>Encoding.</b> The display is scaled to fit <see cref="ScreenCaptureOptions.MaxHeight"/> (aspect kept, never
/// upscaled: 2560x1440 becomes 1920x1080) and encoded as H.264 High with peak-constrained VBR (mean
/// <see cref="ScreenCaptureOptions.BitrateKbps"/>, peak twice that; CBR if the encoder refuses VBR) with a keyframe at
/// least every 2 s of media time, static screens included (<see cref="KeyFrameDescription"/>,
/// <see cref="KeyFrameSchedule"/>). The GPU zero-copy path runs at <see cref="ScreenCaptureOptions.FrameRate"/> (60 by
/// default); CPU readback is capped at <see cref="FramePathPolicy.CpuMaxFrameRate"/> with the bit rate scaled down
/// alike (<see cref="FramePathPolicy.Settings"/>), so <see cref="EncoderFrameRate"/> and
/// <see cref="RateControlDescription"/> say what a segment really uses.
/// </para>
/// <para>
/// <b>Frame path.</b> FrameArrived (WGC worker thread) drains the pool through raw ABI calls, rate-limits to
/// <see cref="EncoderFrameRate"/>, takes a sample from the segment's <see cref="IFramePath"/>, fills it
/// and enqueues it in a bounded lock-free ring; a dedicated encoder thread timestamps (QPC timeline relative to the
/// segment start) and writes the samples. The handler never blocks on the encoder: an exhausted sample pool or a full
/// ring drops the frame and counts it. Both loops are allocation-free in steady state. A failing copy step is named in
/// the fault (e.g. "GPU frame copy failed at QueryInterface(ID3D11VideoDevice) ...").
/// </para>
/// <para>
/// <b>Exhausted sample pool.</b> Drops for an empty pool are counted in <see cref="FramesPoolExhausted"/> as well. When
/// they persist (see <see cref="PoolExhaustionTracker"/>: nothing hands the samples back) the segment raises one
/// non-fatal fault naming the frame path, and on the GPU path every later segment of this recorder uses CPU readback
/// (<see cref="FramePath"/> says so). The running segment keeps its encoder: its file and input type are fixed, so
/// there is no clean switch mid-file; frames resume as soon as samples come back.
/// </para>
/// <para>
/// <b>Timeline.</b> The file has a variable frame rate and its timeline is wall-clock time: the fragmented-MP4 sink
/// stores only sample durations (no base decode time), so the encoder thread holds the newest frame back and writes it
/// once the next frame (or the end of the segment) tells how long it was on screen (see <see cref="HeldFrameTiming"/>).
/// The first frame starts at 0, the last one ends at the stop time. A crash keeps the file up to its last complete
/// fragment: it loses the held frame and the fragment being filled, measured at under 2 s on a static screen (one
/// repeat a second; ScreenCrashRecoveryTests), less with moving content.
/// </para>
/// <para>
/// <b>Segment clock.</b> Media time 0 of a segment file is the moment <see cref="StartSegmentAsync"/> is called and the
/// file ends at the moment <see cref="StopSegmentAsync"/> is called (the contract of <see cref="IScreenRecorder"/>: the
/// caller places the file on its own timeline at the times of its calls). Both times are read first thing in the call,
/// before waiting for the gate, scheduling the work or setting anything up, so the time start-up needs (D3D11 device,
/// Media Foundation encoder, frame pool, capture session: ~0.45 s on a GPU path) is covered by the first frame and the
/// time finalizing needs is not part of the file. Frames captured after the stop call are not written
/// (<see cref="RepeatPolicy.IsAfterEnd"/>).
/// </para>
/// <para>
/// <b>Static screens.</b> WGC only delivers frames when the screen changes; the encoder thread repeats the held frame
/// once a second while nothing arrives, so the file keeps growing by whole fragments for crash safety (a repeat that
/// finds no free sample is skipped and counted in <see cref="RepeatsSkipped"/>; the timeline stays right; a failed copy
/// is a fatal fault like a failed captured frame). Repeats are only written
/// while the segment accepts frames: after a fatal fault (display disconnected, frame pool or GPU failure) nothing
/// extends the timeline any more and the last frame ends at the fault time, so the file never shows a frozen frame as
/// if it were live content (see <see cref="RepeatPolicy"/>).
/// </para>
/// <para>
/// <b>Segments without frames.</b> WGC may deliver no frame at all (headless or non-composited session, locked
/// workstation). Media Foundation cannot finalize an MP4 without samples, so when nothing reached the encoder by the
/// time the segment stops the encoder thread writes one opaque black frame at sample time 0 that lasts until the stop
/// (see <see cref="PlaceholderFrame"/>), which normally makes the file a valid one-frame MP4. If Finalize still fails
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
/// repeated frames are counted in none of them. Captured frames that reach the encoder thread after the stop call
/// belong to no segment: they are neither encoded nor dropped but counted in <see cref="FramesAfterStop"/>, so
/// <c>FramesCaptured == FramesEncoded + FramesDropped + FramesAfterStop</c> up to the frames still in flight.
/// Counters accumulate over the recorder's lifetime.
/// </para>
/// <para>
/// <b>Lifetime.</b> The D3D11 device and Media Foundation are created on the first segment and kept until
/// <see cref="DisposeAsync"/> (the device is recreated if it was lost); the capture session, sample pool, encoder and
/// encoder thread live for one segment. Faults are raised on the thread pool, at most one fatal and one non-fatal
/// fault per segment (a warning never hides a later fatal fault).
/// </para>
/// </summary>
internal sealed class WgcScreenRecorder : IScreenRecorder
{
    /// <summary>Frames between the FrameArrived handler and the encoder thread: 133 ms at 60 fps.</summary>
    internal const int QueueCapacity = 8;

    /// <summary>GPU samples created up front; the allocator adds more on demand, up to <see cref="MaximumSamples"/>.</summary>
    internal const uint InitialSamples = 4;

    /// <summary>
    /// The most GPU samples of a segment: 400 ms of frames at 60 fps (queue + held frame + the one being filled + what the
    /// hardware encoder holds, with room for a busy GPU). Samples beyond <see cref="InitialSamples"/> are only created when
    /// needed (video memory, not system memory); a real-time 1080p60 encode stays within the initial 4 (GpuEncoder1080p60Tests,
    /// WgcScreenRecorder1080p60Tests measure the headroom).
    /// </summary>
    internal const uint MaximumSamples = 24;

    /// <summary>
    /// System-memory samples the CPU path creates up front: the held frame, the one being filled and the one the
    /// encoder is converting, which is all a pipeline that keeps up needs. Every sample is a whole encoder-size frame of
    /// RAM (8.3 MB at 1080p); more are created only while the encoder falls behind.
    /// </summary>
    internal const int InitialCpuSamples = 3;

    /// <summary>
    /// RGB32 samples the CPU path's encoder pipeline holds at most: the sink writer's color converter takes one input
    /// sample at a time and the software encoder works on its own NV12 copies, so one, with one more for the hand-over.
    /// </summary>
    internal const int CpuEncoderHeldSamples = 2;

    /// <summary>
    /// The most system-memory samples of a segment: a full queue + the held frame + the one being filled + what the
    /// encoder holds, so a stall of the encoder fills the queue before the pool runs dry (frames are then dropped at the
    /// queue, not lost to an empty pool that looks like a leak). 12 x 8.3 MB at 1080p, reached only during such a stall.
    /// </summary>
    internal const int MaximumCpuSamples = QueueCapacity + 2 + CpuEncoderHeldSamples;
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
    private long _framesPoolExhausted;
    private long _repeatsSkipped;
    private long _framesAfterStop;
    private readonly int _cpuSampleLimit = MaximumCpuSamples;
    private volatile string? _gpuPathDisabled;

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

    /// <summary>
    /// Captured frames that arrived after the stop call and were not written (diagnostics; also counted in
    /// <see cref="ScreenRecorderStats.FramesCaptured"/> but in neither encoded nor dropped).
    /// </summary>
    internal long FramesAfterStop => Interlocked.Read(ref _framesAfterStop);

    /// <summary>Repeated frames written for static screens (diagnostics).</summary>
    internal long FramesRepeated => Interlocked.Read(ref _framesRepeated);

    /// <summary>Black placeholder frames written for segments in which no frame reached the encoder (diagnostics).</summary>
    internal long PlaceholderFrames => Interlocked.Read(ref _placeholderFrames);

    /// <summary>FrameArrived callbacks WGC raised, before any filtering (diagnostics: 0 means WGC delivered nothing).</summary>
    internal long FrameArrivedEvents => Interlocked.Read(ref _frameArrivedEvents);

    /// <summary>
    /// Captured frames dropped because every encoder sample was still in use (diagnostics; also counted in
    /// <see cref="ScreenRecorderStats.FramesDropped"/>).
    /// </summary>
    internal long FramesPoolExhausted => Interlocked.Read(ref _framesPoolExhausted);

    /// <summary>
    /// Why later segments skip the GPU path after a persistent pool exhaustion, e.g. "CPU readback: the GPU
    /// zero-copy sample pool (12 samples) ran dry in an earlier segment" (diagnostics; null while the GPU path is
    /// allowed). Tests may preset it.
    /// </summary>
    internal string? GpuPathDisabled
    {
        get => _gpuPathDisabled;
        init => _gpuPathDisabled = value;
    }

    /// <summary>The D3D11 device in use, e.g. "hardware D3D11 FL 11.1" (diagnostics; null before the first segment).</summary>
    internal string? DeviceDescription => _gpu?.Description;

    /// <summary>Encoder frame size of the current or last segment (diagnostics).</summary>
    internal PixelSize LastEncoderSize { get; private set; }

    /// <summary>
    /// The encoder input of the current or last segment and why it was chosen, e.g. "GPU zero-copy (1 hardware H.264
    /// encoder MFT)" or "CPU readback: software renderer ..." (diagnostics; null before the first segment).
    /// </summary>
    internal string? FramePath { get; private set; }

    /// <summary>
    /// The frame rate the current or last segment encodes at: <see cref="ScreenCaptureOptions.FrameRate"/> on the GPU
    /// zero-copy path, at most <see cref="FramePathPolicy.CpuMaxFrameRate"/> on CPU readback (diagnostics; 0 before
    /// the first segment).
    /// </summary>
    internal int EncoderFrameRate { get; private set; }

    /// <summary>
    /// The rate control the encoder of the current or last segment reports, e.g. "peak-constrained VBR, mean 12000
    /// kbps, peak 24000 kbps" (diagnostics; null before the first segment).
    /// </summary>
    internal string? RateControlDescription { get; private set; }

    /// <summary>
    /// How the last stopped segment bounded the time between keyframes, e.g. "an IDR frame at least every 2 s (forced; 30
    /// so far)" (see <see cref="KeyFrameSchedule"/>; diagnostics: null before the first stop).
    /// </summary>
    internal string? KeyFrameDescription { get; private set; }

    /// <summary>
    /// The fewest GPU samples that were available when the last stopped segment asked for one (see
    /// <see cref="GpuFrameCopier.MinAvailableSamples"/>; diagnostics: -1 on CPU readback or before a stop).
    /// </summary>
    internal int LastPoolHeadroom { get; private set; } = -1;

    /// <summary>
    /// System-memory samples the CPU readback path of the last stopped segment created (diagnostics: each one is a whole
    /// encoder-size frame of RAM; 0 on the GPU path or before a stop).
    /// </summary>
    internal int LastCpuSamples { get; private set; }

    /// <summary>Tests only: the rate control to ask the encoder for (the default: peak-constrained VBR).</summary>
    internal RateControl RequestedRateControl { get; init; } = RateControl.PeakConstrainedVbr;

    /// <summary>
    /// Tests only: a pause inside <see cref="StartSegmentAsync"/> before the encoder is opened, standing in for a slow
    /// encoder / capture start-up (the file must still start at the call).
    /// </summary>
    internal TimeSpan StartupDelay { get; init; }

    /// <summary>
    /// Tests only: a pause inside <see cref="StopSegmentAsync"/> before the segment is torn down, standing in for a
    /// slow stop (the file must still end at the call).
    /// </summary>
    internal TimeSpan StopDelay { get; init; }

    /// <summary>Tests only: use the CPU readback path even when a hardware encoder is available.</summary>
    internal bool ForceCpuReadback { get; init; }

    /// <summary>
    /// Tests only: after this many captured frames per segment, later WGC frames are ignored as if the screen had gone
    /// static (0: no limit).
    /// </summary>
    internal int CapturedFrameLimit { get; init; }

    /// <summary>Tests only: the most system-memory samples the CPU readback path may create (at least 1).</summary>
    internal int CpuSampleLimit
    {
        get => _cpuSampleLimit;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _cpuSampleLimit = value;
        }
    }

    /// <summary>
    /// Static-screen repeats that were skipped because no encoder sample was free (diagnostics; the timeline is
    /// unaffected, the held frame just lasts longer, but the file grows less often). A failed copy is not a skip but a
    /// fatal fault.
    /// </summary>
    internal long RepeatsSkipped => Interlocked.Read(ref _repeatsSkipped);

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
        // Media time 0 of the file: the very first thing, so waiting for the gate and every set-up step below is covered
        // by the first frame instead of shifting the whole file late against the caller's timeline.
        var startTicks = MediaTime.Now();
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
            await Task.Run(() => StartCore(path, startTicks), CancellationToken.None).ConfigureAwait(false);
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
        // The end of the file: the very first thing, before the gate, the thread-pool hop and the spin for the frame
        // handler, none of which are part of the segment.
        var stopTicks = MediaTime.Now();
        PublishStop(stopTicks); // the encoder thread stops taking frames and repeats past this moment at once
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_recording || _segment is not { } segment)
            {
                return;
            }

            try
            {
                await Task.Run(() => StopCore(segment, stopTicks), CancellationToken.None).ConfigureAwait(false);
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
        var disposeTicks = MediaTime.Now();
        PublishStop(disposeTicks);
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
                    await Task.Run(() => StopCore(segment, disposeTicks)).ConfigureAwait(false);
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

    private void StartCore(string path, long startTicks)
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
            if (StartupDelay > TimeSpan.Zero)
            {
                Thread.Sleep(StartupDelay);
            }

            segment.Item = CaptureInterop.CreateItemForMonitor(display.Monitor);
            var itemSize = segment.Item.Size;
            if (itemSize.Width <= 0 || itemSize.Height <= 0)
            {
                throw new COMException($"The capture item of {display.Info.Id} has no size ({itemSize.Width}x{itemSize.Height}).", ScreenConstants.EFail);
            }

            segment.PoolSize = itemSize;
            segment.EncoderSize = EncoderGeometry.FitEncoderSize(itemSize.Width, itemSize.Height, _options.MaxHeight);
            LastEncoderSize = segment.EncoderSize;
            FramePath = OpenEncoder(segment, gpu, path, new H264EncoderSettings(segment.EncoderSize, _options.FrameRate, _options.BitrateKbps, RequestedRateControl), ForceCpuReadback, _gpuPathDisabled, CpuSampleLimit);
            _encoder = segment.Writer!.Encoder;
            EncoderFrameRate = segment.Writer.Settings.FrameRate;
            RateControlDescription = segment.Writer.RateControlDescription;
            LastPoolHeadroom = -1;
            LastCpuSamples = 0;
            segment.UseFrameRate(EncoderFrameRate);

            segment.Pool = Direct3D11CaptureFramePool.CreateFreeThreaded(gpu.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, itemSize);
            segment.PoolAbi = CaptureInterop.GetFramePoolAbi(segment.Pool);
            segment.Session = segment.Pool.CreateCaptureSession(segment.Item);
            CaptureInterop.Configure(segment.Session);
            segment.ItemClosed = (_, _) => RaiseFault(segment, "The captured display was disconnected.", isFatal: true);
            segment.Item.Closed += segment.ItemClosed;

            segment.Timeline.Reset(startTicks); // media time 0 = the StartSegmentAsync call, not "now" after the set-up
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

    /// <summary>
    /// Publishes the end of the running segment (if any) at the moment the stop call came in, ahead of the gate and the
    /// thread-pool hop, so the encoder thread already ignores frames and repeats after it. The first request wins.
    /// </summary>
    private void PublishStop(long stopTicks)
    {
        if (Volatile.Read(ref _segment) is { } running)
        {
            Interlocked.CompareExchange(ref running.StopTicks, stopTicks, RepeatPolicy.NoStop);
        }
    }

    private void StopCore(Segment segment, long stopTicks)
    {
        // The earliest stop request wins (normally the one published when the call came in, before the gate).
        Interlocked.CompareExchange(ref segment.StopTicks, stopTicks, RepeatPolicy.NoStop);
        segment.Accepting = false;
        Volatile.Write(ref _segment, null);
        if (StopDelay > TimeSpan.Zero)
        {
            Thread.Sleep(StopDelay);
        }

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
            // A segment that never reached StopCore (set-up failed) ends now.
            Interlocked.CompareExchange(ref segment.StopTicks, MediaTime.Now(), RepeatPolicy.NoStop);
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
        if (segment.Frames is GpuFrameCopier gpuFrames)
        {
            LastPoolHeadroom = gpuFrames.MinAvailableSamples;
        }
        else if (segment.Frames is CpuFrameCopier cpuFrames)
        {
            LastCpuSamples = cpuFrames.PooledSamples;
        }

        KeyFrameDescription = segment.Writer?.KeyFrameDescription;
        segment.Writer?.Dispose();
        segment.Frames?.Dispose(); // after the writer: Media Foundation has released the samples by now
        segment.Wake.Dispose();
    }

    /// <summary>
    /// Opens the segment's sink writer and frame path: zero-copy GPU input when <see cref="FramePathPolicy"/> allows it
    /// and the hardware writer comes up with a hardware encoder, CPU readback otherwise (also when
    /// <paramref name="gpuDisabled"/> names why an earlier segment gave the GPU path up). Returns the
    /// <see cref="FramePath"/> diagnostic.
    /// </summary>
    /// <exception cref="COMException">Not even the CPU readback configuration could be set up.</exception>
    private static string OpenEncoder(Segment segment, GpuDevice gpu, string path, H264EncoderSettings settings, bool forceCpu, string? gpuDisabled, int cpuSamples)
    {
        var skipGpu = forceCpu || gpuDisabled is not null;
        var hardwareEncoders = !skipGpu && gpu.HasVideoSupport && !gpu.IsSoftwareRenderer ? HardwareEncoders.CountH264() : 0;
        var reason = forceCpu
            ? "CPU readback: forced"
            : gpuDisabled ?? FramePathPolicy.Describe(gpu.HasVideoSupport, gpu.IsSoftwareRenderer, hardwareEncoders);
        if (!skipGpu && FramePathPolicy.Choose(gpu.HasVideoSupport, gpu.IsSoftwareRenderer, hardwareEncoders) == EncoderInput.Gpu)
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

        // CPU readback: at most FramePathPolicy.CpuMaxFrameRate (the segment's limiter and timeline follow the writer).
        var cpuSettings = FramePathPolicy.Settings(EncoderInput.Cpu, settings);
        segment.Frames = new CpuFrameCopier(gpu.Device, gpu.Context, cpuSettings.FrameSize, MediaFoundationSampleFactory.Instance, Math.Min(InitialCpuSamples, cpuSamples), cpuSamples);
        segment.Writer = H264SinkWriter.Create(path, cpuSettings, EncoderInput.Cpu, 0);
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

        if (CapturedFrameLimit > 0 && segment.CapturedAdmitted >= CapturedFrameLimit)
        {
            return; // tests: the screen "went static"
        }

        if (!segment.Limiter.TryAccept(ticks))
        {
            Interlocked.Increment(ref _framesRateLimited);
            return;
        }

        segment.CapturedAdmitted++;
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
            else
            {
                Interlocked.Increment(ref _framesPoolExhausted);
                var now = MediaTime.Now();
                if (segment.Exhaustion.OnExhausted(now))
                {
                    ReportPoolExhaustion(segment, frames.Input, segment.Exhaustion.Consecutive, segment.Exhaustion.Elapsed(now));
                }
            }

            return;
        }

        segment.Exhaustion.OnAllocated();

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
                var repeatDue = RepeatPolicy.ShouldRepeat(
                    segment.LastSample != 0,
                    segment.WriterHealthy,
                    segment.Accepting,
                    now,
                    segment.LastWriteTicks,
                    Interlocked.Read(ref segment.FaultTicks),
                    RepeatInterval);
                if (repeatDue && !RepeatPolicy.IsAfterEnd(now, Volatile.Read(ref segment.StopTicks)))
                {
                    Repeat(segment, now);
                }
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

            // The held last frame lasts until the stop time (or the fault time): the file's duration is the segment's.
            var end = RepeatPolicy.EndTicks(
                Volatile.Read(ref segment.StopRequested),
                Volatile.Read(ref segment.StopTicks),
                Interlocked.Read(ref segment.FaultTicks),
                MediaTime.Now());

            // Media Foundation cannot finalize a file without samples: give an empty segment a black frame at time 0.
            if (PlaceholderFrame.ShouldWrite(segment.AnySubmitted, segment.WriterHealthy))
            {
                segment.Placeholder = WritePlaceholder(segment, end);
            }
            else
            {
                if (!segment.AnySubmitted)
                {
                    segment.Placeholder = new PlaceholderOutcome(PlaceholderStage.SkippedWriterFailed, ScreenConstants.SOk);
                }

                CloseTimeline(segment, end);
            }

            ReleaseHeld(segment); // nothing is held unless the writer failed
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

            if (RepeatPolicy.IsAfterEnd(queued.Ticks, Volatile.Read(ref segment.StopTicks)))
            {
                // Captured after the stop call: it belongs to no segment (the held frame lasts until the stop).
                var late = queued.Sample;
                ComCall.Release(ref late);
                Interlocked.Increment(ref _framesAfterStop);
                continue;
            }

            Submit(segment, queued.Sample, queued.Ticks, SampleKind.Captured);
        }
    }

    /// <summary>
    /// Repeats the held frame at <paramref name="ticks"/>: the held frame is written (lasting until now) and its copy is
    /// held instead, so the file keeps growing by whole fragments on a static screen (crash safety). When no sample is
    /// free the repeat is skipped and counted in <see cref="RepeatsSkipped"/>; the timeline stays right regardless, as
    /// the held frame simply lasts longer. A failed allocation or copy (e.g. the device was removed) is a fatal fault,
    /// like a failed captured frame: the held frame then ends at the fault time.
    /// </summary>
    private void Repeat(Segment segment, long ticks)
    {
        var frames = segment.Frames!;
        var outcome = HeldSample.CopyForRepeat(frames, segment.CopyLock, segment.LastSample, out var sample, out var hr, out var failure);
        switch (outcome)
        {
            case RepeatCopy.Copied:
                Submit(segment, sample, ticks, SampleKind.Repeated);
                break;
            case RepeatCopy.PoolEmpty:
                Interlocked.Increment(ref _repeatsSkipped);
                break;
            default:
                RaiseFault(segment, HeldSample.RepeatFault(outcome, frames.Input, failure, hr), isFatal: true);
                break;
        }
    }

    /// <summary>
    /// Writes the black placeholder frame at sample time 0, lasting until <paramref name="end"/> (see
    /// <see cref="PlaceholderFrame"/>). Never throws.
    /// </summary>
    private PlaceholderOutcome WritePlaceholder(Segment segment, long end)
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
            sample = 0; // Submit takes ownership
            Submit(segment, owned, PlaceholderFrame.CaptureTicks(segment.Timeline), SampleKind.Placeholder);
            hr = CloseTimeline(segment, end);
            return new PlaceholderOutcome(hr < 0 ? PlaceholderStage.WriteFailed : PlaceholderStage.Written, hr);
        }
        catch (Exception ex)
        {
            ComCall.Release(ref sample);
            RaiseFault(segment, "No frame was captured and the placeholder frame failed: " + ex.Message, isFatal: true);
            return new PlaceholderOutcome(PlaceholderStage.Exception, ex.HResult);
        }
    }

    /// <summary>
    /// Puts a sample captured at <paramref name="ticks"/> on the timeline; takes ownership of <paramref name="sample"/>.
    /// The frame held so far is written with its real duration (up to this sample, see <see cref="HeldFrameTiming"/>)
    /// and this sample is held instead: it is written once the next sample or the end of the segment fixes its
    /// duration. Allocation-free.
    /// </summary>
    private void Submit(Segment segment, nint sample, long ticks, SampleKind kind)
    {
        var time = segment.Timeline.ToSampleTime(ticks);
        if (segment.Timing.Advance(time, out var heldTime, out var heldDuration))
        {
            WriteHeld(segment, heldTime, heldDuration);
        }

        if (!segment.WriterHealthy)
        {
            // Writing the held frame just failed (fault raised): nothing will be written any more.
            segment.Timing.Discard();
            ComCall.Release(ref sample);
            if (kind == SampleKind.Captured)
            {
                Interlocked.Increment(ref _framesDropped);
            }

            return;
        }

        segment.LastSample = sample;
        segment.LastKind = kind;
        segment.AnySubmitted = true;
        segment.LastWriteTicks = MediaTime.Now();
    }

    /// <summary>
    /// Writes the held frame so that it lasts until <paramref name="end"/> (capture time), ending the segment's
    /// timeline. Returns the WriteSample HRESULT, S_OK when nothing was held, E_FAIL when the writer had failed before.
    /// </summary>
    private int CloseTimeline(Segment segment, long end)
    {
        if (!segment.Timing.Close(end - segment.Timeline.Start, out var heldTime, out var heldDuration))
        {
            return ScreenConstants.SOk;
        }

        if (!segment.WriterHealthy)
        {
            ReleaseHeld(segment);
            return ScreenConstants.EFail;
        }

        return WriteHeld(segment, heldTime, heldDuration);
    }

    /// <summary>
    /// Timestamps and writes the held sample (<see cref="Segment.LastSample"/>) and releases it on every path (see
    /// <see cref="HeldSample.Write"/>). Returns the WriteSample HRESULT; a failure (a managed exception included) marks
    /// the writer failed and raises a fatal fault. Never throws.
    /// </summary>
    private int WriteHeld(Segment segment, long time, long duration)
    {
        var kind = segment.LastKind;
        var hr = HeldSample.Write(ref segment.LastSample, segment.Writer, time, duration, out var step, out var exception);
        if (hr < 0)
        {
            segment.WriterHealthy = false;
            segment.Accepting = false;
            if (kind == SampleKind.Captured)
            {
                Interlocked.Increment(ref _framesDropped);
            }

            var reason = exception is null ? ComHelpers.Describe(hr) : exception.Message;
            RaiseFault(segment, $"{step} failed ({FrameCopyNaming.Path(segment.Frames!.Input)} input): {reason}", isFatal: true);
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

        return hr;
    }

    /// <summary>Releases a held sample that will not be written (a captured one counts as dropped).</summary>
    private void ReleaseHeld(Segment segment)
    {
        if (segment.LastSample == 0)
        {
            return;
        }

        segment.Timing.Discard();
        ComCall.Release(ref segment.LastSample);
        if (segment.LastKind == SampleKind.Captured)
        {
            Interlocked.Increment(ref _framesDropped);
        }
    }

    /// <summary>
    /// A persistent pool exhaustion (once per segment, not a hot path): a non-fatal fault, and on the GPU path the
    /// following segments fall back to CPU readback.
    /// </summary>
    private void ReportPoolExhaustion(Segment segment, EncoderInput input, int drops, long elapsed)
    {
        var limit = input == EncoderInput.Gpu ? MaximumSamples : (uint)CpuSampleLimit;
        if (input == EncoderInput.Gpu)
        {
            _gpuPathDisabled = PoolExhaustionNaming.FallbackReason(input, limit);
        }

        RaiseFault(segment, PoolExhaustionNaming.Fault(input, limit, drops, elapsed), isFatal: false);
    }

    private void RaiseFault(Segment segment, string message, bool isFatal)
    {
        if (isFatal)
        {
            // Record the first fatal fault's time before Accepting drops, so the encoder thread never repeats past it.
            Interlocked.CompareExchange(ref segment.FaultTicks, MediaTime.Now(), RepeatPolicy.NoFault);
            segment.Accepting = false;
        }

        // One fatal and one non-fatal fault per segment: a warning must not swallow a later fatal fault.
        if (Interlocked.Exchange(ref isFatal ? ref segment.FaultRaised : ref segment.WarningRaised, 1) != 0)
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
        public readonly SegmentTimeline Timeline = new();
        public FrameRateLimiter Limiter = new(frameRate);
        public HeldFrameTiming Timing = new(HeldFrameTiming.SinkTimescale(frameRate));

        /// <summary>
        /// Sets the frame rate the segment's encoder runs at (CPU readback caps it); before capture and the encoder
        /// thread start.
        /// </summary>
        public void UseFrameRate(int rate)
        {
            if (rate != Limiter.FrameRate)
            {
                Limiter = new FrameRateLimiter(rate);
                Timing = new HeldFrameTiming(HeldFrameTiming.SinkTimescale(rate));
            }
        }

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
        public PoolExhaustionTracker Exhaustion;
        public bool RecreatePending;
        public SizeInt32Abi PendingSize;

        public int CapturedAdmitted;

        // Encoder thread
        public nint LastSample; // the newest frame, held back until the next one or the segment end fixes its duration
        public SampleKind LastKind;
        public bool AnySubmitted;
        public long LastWriteTicks;
        public volatile bool WriterHealthy = true;
        public long CapturedWritten;
        public PlaceholderOutcome Placeholder = PlaceholderOutcome.NotNeeded;

        // Stop hand-shake
        public bool StopRequested;
        public long StopTicks = RepeatPolicy.NoStop; // QPC time of the stop call (Volatile/Interlocked)
        public int FinalizeResult;
        public bool EncoderHung;
        public int FaultRaised;
        public int WarningRaised;
        public long FaultTicks = RepeatPolicy.NoFault; // QPC time of the first fatal fault (Interlocked)
    }
}
