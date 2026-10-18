using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Screen;

/// <summary>How the encoder spends its bits (CODECAPI_AVEncCommonRateControlMode).</summary>
internal enum RateControl
{
    /// <summary>Constant bit rate: every frame gets about BitrateKbps / FrameRate, padded if it needs less.</summary>
    Cbr,

    /// <summary>
    /// Peak-constrained VBR: BitrateKbps is the mean, <see cref="H264EncoderSettings.PeakFactor"/> times it the peak. A
    /// static screen's repeats shrink to a few hundred bytes instead of being padded to the CBR frame size.
    /// </summary>
    PeakConstrainedVbr,
}

/// <summary>Encoder settings of one segment.</summary>
internal readonly record struct H264EncoderSettings(PixelSize FrameSize, int FrameRate, int BitrateKbps, RateControl RateControl = RateControl.PeakConstrainedVbr)
{
    /// <summary>Peak bit rate of <see cref="RateControl.PeakConstrainedVbr"/> as a multiple of the mean.</summary>
    public const int PeakFactor = 2;

    /// <summary>
    /// The encoder's GOP: 2 s of frames at <see cref="FrameRate"/>. It counts frames, and the file has a variable frame
    /// rate (a static screen gets about one repeat a second), so the GOP alone does not bound the time between keyframes:
    /// <see cref="H264SinkWriter"/> forces an IDR frame at least every <see cref="KeyFrameSchedule.DefaultInterval"/> of
    /// media time (see <see cref="KeyFrameSchedule"/>); the GOP is what remains on an encoder that cannot be told.
    /// </summary>
    public uint GopSize => (uint)Math.Max(1, FrameRate * 2);

    /// <summary>
    /// The longest media time (100 ns) between keyframes the writer enforces by forcing IDR frames
    /// (<see cref="KeyFrameSchedule"/>); 0: never force (only the GOP).
    /// </summary>
    public long KeyFrameInterval { get; init; } = KeyFrameSchedule.DefaultInterval;

    /// <summary>The mean (CBR: the constant) bit rate in bits per second.</summary>
    public uint MeanBitrate => (uint)Math.Min((long)BitrateKbps * 1000, uint.MaxValue);

    /// <summary>The peak bit rate of peak-constrained VBR in bits per second.</summary>
    public uint PeakBitrate => (uint)Math.Min((long)BitrateKbps * 1000 * PeakFactor, uint.MaxValue);

    /// <summary>The CODECAPI_AVEncCommonRateControlMode value of <see cref="RateControl"/>.</summary>
    public uint RateControlMode => RateControl == RateControl.Cbr ? ScreenConstants.RateControlCbr : ScreenConstants.RateControlPeakConstrainedVbr;
}

/// <summary>
/// The encoder's rate control as read back from the MFT (ICodecAPI::GetValue) after BeginWriting: what it actually
/// uses, whatever was asked for. A null field could not be read (no ICodecAPI, the property is not supported).
/// </summary>
internal readonly record struct RateControlReadback(uint? Mode, uint? MeanBitrate, uint? MaxBitrate)
{
    public static readonly RateControlReadback Unknown = new(null, null, null);
}

/// <summary>Formats the rate control diagnostics of a segment's encoder (pure).</summary>
internal static class RateControlNaming
{
    public static string Mode(uint mode) => mode switch
    {
        ScreenConstants.RateControlCbr => "CBR",
        ScreenConstants.RateControlPeakConstrainedVbr => "peak-constrained VBR",
        ScreenConstants.RateControlUnconstrainedVbr => "unconstrained VBR",
        ScreenConstants.RateControlQuality => "quality VBR",
        _ => "rate control mode " + mode.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// E.g. "peak-constrained VBR, mean 12000 kbps, peak 24000 kbps", "CBR, 12000 kbps (peak-constrained VBR was
    /// rejected: ...)" or "requested peak-constrained VBR, mean 12000 kbps (the encoder does not report its rate
    /// control)".
    /// </summary>
    public static string Describe(H264EncoderSettings settings, RateControlReadback readback, string? fallbackReason)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        string text;
        if (readback.Mode is { } mode)
        {
            var mean = readback.MeanBitrate ?? settings.MeanBitrate;
            text = mode == ScreenConstants.RateControlCbr
                ? string.Create(invariant, $"CBR, {mean / 1000} kbps")
                : readback.MaxBitrate is { } peak && peak > 0 && mode == ScreenConstants.RateControlPeakConstrainedVbr
                    ? string.Create(invariant, $"{Mode(mode)}, mean {mean / 1000} kbps, peak {peak / 1000} kbps")
                    : string.Create(invariant, $"{Mode(mode)}, mean {mean / 1000} kbps");
        }
        else
        {
            text = string.Create(invariant, $"requested {Mode(settings.RateControlMode)}, mean {settings.MeanBitrate / 1000} kbps (the encoder does not report its rate control)");
        }

        return fallbackReason is null ? text : text + " (" + Mode(ScreenConstants.RateControlPeakConstrainedVbr) + " was rejected: " + fallbackReason + ")";
    }

    /// <summary>True when the encoder reported a VBR mode (anything but CBR).</summary>
    public static bool IsVbr(RateControlReadback readback) => readback.Mode is { } mode && mode != ScreenConstants.RateControlCbr;
}

/// <summary>
/// Which encoder set-up failures give the requested VBR configuration up for CBR (pure). Only the steps that apply the
/// encoding parameters raise <see cref="RateControlRejectedException"/> (IMFSinkWriter::SetInputMediaType hands them to
/// the MFT's ICodecAPI and negotiates the MFT, BeginWriting starts it), and only with an HRESULT an encoder or the sink
/// writer gives for a parameter or configuration it does not take. Anything else (the file cannot be created, no
/// encoder at all, a lost device, E_FAIL) is not about the rate control: retrying with CBR would only renegotiate the
/// MFT for nothing and hide the real failure.
/// </summary>
internal static class RateControlFallbackPolicy
{
    public static bool ShouldRetryWithCbr(RateControl requested, int hr) => requested != RateControl.Cbr && IsRejection(hr);

    /// <summary>True for the HRESULTs of a refused parameter or configuration.</summary>
    public static bool IsRejection(int hr) => hr is ScreenConstants.EInvalidArg
        or ScreenConstants.ENotImpl
        or ScreenConstants.MfEInvalidMediaType
        or ScreenConstants.MfEInvalidType
        or ScreenConstants.MfEAttributeNotFound
        or ScreenConstants.MfETopoCodecNotFound
        or ScreenConstants.MfETransformTypeNotSet
        or (>= ScreenConstants.VfwECodecApiFirst and <= ScreenConstants.VfwECodecApiLast);
}

/// <summary>A step that applies the encoding parameters refused them (see <see cref="RateControlFallbackPolicy"/>).</summary>
internal sealed class RateControlRejectedException(string message, int hr) : COMException(message, hr);

/// <summary>The encoder could not be set up; <see cref="Exception.InnerException"/> is an earlier failure that led to the last attempt.</summary>
internal sealed class EncoderSetupException : COMException
{
    public EncoderSetupException(string message, int hr, Exception inner)
        : base(message, inner)
    {
        HResult = hr;
    }
}

/// <summary>Formats <see cref="H264SinkWriter.KeyFrameDescription"/> (pure).</summary>
internal static class KeyFrameNaming
{
    /// <param name="requested">Keyframes were to be forced (<see cref="H264EncoderSettings.KeyFrameInterval"/> &gt; 0).</param>
    /// <param name="forced">The encoder takes the requests (<see cref="H264SinkWriter.ForcesKeyFrames"/>).</param>
    public static string Describe(bool requested, bool forced, long interval, uint gopSize, long forcedCount, long failures)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        if (!requested)
        {
            return string.Create(invariant, $"GOP of {gopSize} frames (not forced)");
        }

        if (!forced)
        {
            return string.Create(invariant, $"GOP of {gopSize} frames (the encoder cannot be told to start a keyframe)");
        }

        var text = string.Create(invariant, $"an IDR frame at least every {interval / (double)MediaTime.TicksPerSecond:0.###} s (forced; {forcedCount} so far");
        return failures == 0 ? text + ")" : string.Create(invariant, $"{text}, {failures} refused)");
    }
}

/// <summary>
/// Media Foundation sink writer producing a fragmented MP4 (MFTranscodeContainerType_FMPEG4) with one H.264 stream.
/// The sink writes a moof + mdat fragment every ~0.3 s of media time, or every sample when samples are longer (a
/// static screen's repeats), whether or not it starts with a keyframe; a file cut off by a crash is read up to its last
/// complete fragment and decoded from the keyframe before it, so keyframes are forced at least every 2 s of media time
/// where the GOP alone would not do it (<see cref="KeyFrameSchedule"/>; ScreenCrashRecoveryTests). Two configurations
/// (see <see cref="EncoderInput"/>):
/// <list type="bullet">
/// <item><see cref="EncoderInput.Gpu"/>: hardware MFTs with the DXGI device manager, D3D11 ARGB32 samples (zero-copy).
/// No software retry: a software encoder fed D3D11 samples (Media Foundation converts ARGB32 to NV12 on the device
/// first) is the combination whose WriteSample failed with E_INVALIDARG on windows-latest (WARP); callers fall back to
/// <see cref="EncoderInput.Cpu"/> instead.</item>
/// <item><see cref="EncoderInput.Cpu"/>: software MFTs only, no device manager, system-memory RGB32 samples (the
/// classic sink-writer input; the writer inserts the color converter in front of the Microsoft H.264 encoder).</item>
/// </list>
/// Both input types declare MF_MT_DEFAULT_STRIDE = width * 4 (top-down rows). Not thread-safe: owned by the encoder
/// thread after <see cref="Create"/>.
/// UNVERIFIED-ON-WINDOWS until the Windows-trait tests run in CI.
/// </summary>
internal sealed unsafe class H264SinkWriter : ISampleSink, IDisposable
{
    private const int OpenAttempts = 10;
    private static readonly TimeSpan OpenRetryDelay = TimeSpan.FromMilliseconds(50);

    private nint _writer;
    private nint _codec; // the encoder's ICodecAPI when it takes CODECAPI_AVEncVideoForceKeyFrame, else 0
    private readonly uint _stream;
    private KeyFrameSchedule _keyFrames;

    private H264SinkWriter(nint writer, nint codec, uint stream, string encoder, bool? isHardware, EncoderInput input, H264EncoderSettings settings, RateControlReadback readback)
    {
        _writer = writer;
        _codec = codec;
        _stream = stream;
        _keyFrames = new KeyFrameSchedule(settings.KeyFrameInterval > 0 ? settings.KeyFrameInterval : KeyFrameSchedule.DefaultInterval, settings.FrameRate);
        Encoder = encoder;
        IsHardware = isHardware;
        Input = input;
        Settings = settings;
        Readback = readback;
    }

    /// <summary>
    /// True when the encoder takes CODECAPI_AVEncVideoForceKeyFrame (and the settings ask for it), so keyframes are at most
    /// <see cref="KeyFrameSchedule.DefaultInterval"/> apart in media time; false: only the GOP of
    /// <see cref="H264EncoderSettings.GopSize"/> frames bounds them.
    /// </summary>
    public bool ForcesKeyFrames => _codec != 0;

    /// <summary>IDR frames requested so far (diagnostics; encoder thread).</summary>
    public long ForcedKeyFrames { get; private set; }

    /// <summary>Requests the encoder refused (diagnostics; the next sample asks again).</summary>
    public long ForceKeyFrameFailures { get; private set; }

    /// <summary>E.g. "an IDR frame at least every 2 s (forced; 12 so far)" or "GOP of 60 frames (not forced)" (diagnostics; encoder thread).</summary>
    public string KeyFrameDescription => KeyFrameNaming.Describe(Settings.KeyFrameInterval > 0, ForcesKeyFrames, _keyFrames.Interval, Settings.GopSize, ForcedKeyFrames, ForceKeyFrameFailures);

    /// <summary>Which encoder the sink writer picked, for <see cref="Murmur.Core.Capture.ScreenRecorderStats.Encoder"/>.</summary>
    public string Encoder { get; }

    /// <summary>The settings the writer was opened with (after a CBR fallback: <see cref="RateControl.Cbr"/>).</summary>
    public H264EncoderSettings Settings { get; }

    /// <summary>The rate control the encoder reports (see <see cref="RateControlReadback"/>).</summary>
    public RateControlReadback Readback { get; }

    /// <summary>Why the requested VBR configuration was given up for CBR, or null.</summary>
    public string? RateControlFallback { get; private init; }

    /// <summary>The rate control diagnostics, e.g. "peak-constrained VBR, mean 12000 kbps, peak 24000 kbps".</summary>
    public string RateControlDescription => RateControlNaming.Describe(Settings, Readback, RateControlFallback);

    /// <summary>True for a hardware MFT, false for a software one, null when the encoder MFT could not be queried.</summary>
    public bool? IsHardware { get; }

    public EncoderInput Input { get; }

    /// <summary>The input subtype of <paramref name="input"/>: ARGB32 (D3D11 B8G8R8A8 textures) or RGB32 (system memory).</summary>
    public static Guid InputSubtype(EncoderInput input) =>
        input == EncoderInput.Gpu ? ScreenConstants.MfVideoFormatArgb32 : ScreenConstants.MfVideoFormatRgb32;

    /// <summary>
    /// Opens a writer for <paramref name="input"/>; <paramref name="deviceManager"/> (IMFDXGIDeviceManager) is required
    /// for <see cref="EncoderInput.Gpu"/> and ignored for <see cref="EncoderInput.Cpu"/>. Opening is retried briefly
    /// while the file is still held by a writer released just before (a fallback from the GPU configuration).
    /// <para>
    /// <b>Rate control.</b> <see cref="RateControl.PeakConstrainedVbr"/> is requested through the encoding parameters
    /// (CODECAPI_AVEncCommonRateControlMode, MeanBitRate, MaxBitRate, which the sink writer hands to the MFT's
    /// ICodecAPI); when a step that applies them refuses the configuration (<see cref="RateControlFallbackPolicy"/>), the
    /// writer is opened again with CBR (the reason is kept in <see cref="RateControlFallback"/>); any other failure is
    /// thrown as it is. Either way the encoder's actual rate control is read back (<see cref="Readback"/>): an encoder
    /// may accept the parameters and ignore them.
    /// </para>
    /// <para>
    /// <b>Keyframes.</b> When the encoder takes CODECAPI_AVEncVideoForceKeyFrame (<see cref="ForcesKeyFrames"/>),
    /// <see cref="WriteSample"/> keeps keyframes at most <see cref="H264EncoderSettings.KeyFrameInterval"/> of media time
    /// apart, also on a static screen (see <see cref="KeyFrameSchedule"/>).
    /// </para>
    /// </summary>
    /// <exception cref="COMException">The configuration could not be set up (after a CBR retry: an
    /// <see cref="EncoderSetupException"/> whose inner exception is the VBR rejection).</exception>
    public static H264SinkWriter Create(string path, H264EncoderSettings settings, EncoderInput input, nint deviceManager)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (input == EncoderInput.Gpu && deviceManager == 0)
        {
            throw new ArgumentException("The GPU configuration needs the DXGI device manager.", nameof(deviceManager));
        }

        RateControlRejectedException? rejected = null;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return input == EncoderInput.Gpu
                    ? CreateOnce(path, settings, hardware: true, deviceManager, input, rejected?.Message)
                    : CreateOnce(path, settings, hardware: false, 0, input, rejected?.Message);
            }
            catch (COMException ex) when (ex.HResult is ScreenConstants.ESharingViolation or ScreenConstants.ELockViolation && attempt < OpenAttempts)
            {
                Thread.Sleep(OpenRetryDelay);
            }
            catch (RateControlRejectedException ex) when (rejected is null && settings.RateControl != RateControl.Cbr)
            {
                // The step that applies the VBR parameters refused them: retry once with CBR (the failed writer is
                // released already; a file it still holds is the sharing-violation retry above).
                rejected = ex;
                settings = settings with { RateControl = RateControl.Cbr };
            }
            catch (COMException ex) when (rejected is not null)
            {
                // CBR failed as well: report that failure, with the VBR rejection as its cause.
                throw new EncoderSetupException(ex.Message + " (after " + RateControlNaming.Mode(ScreenConstants.RateControlPeakConstrainedVbr) + " was rejected: " + rejected.Message + ")", ex.HResult, rejected);
            }
        }
    }

    private static H264SinkWriter CreateOnce(string path, H264EncoderSettings settings, bool hardware, nint deviceManager, EncoderInput input, string? fallback)
    {
        nint attributes = 0, writer = 0, output = 0, inputType = 0, encoding = 0, codec = 0;
        try
        {
            ComHelpers.Check(ScreenNativeMethods.MFCreateAttributes(&attributes, 4), "MFCreateAttributes");
            SetUInt32(attributes, ScreenConstants.MfReadwriteEnableHardwareTransforms, hardware ? 1u : 0u);
            SetGuid(attributes, ScreenConstants.MfTranscodeContainerType, ScreenConstants.MfTranscodeContainerTypeFmpeg4);
            SetUInt32(attributes, ScreenConstants.MfLowLatency, 1);
            if (deviceManager != 0)
            {
                var key = ScreenConstants.MfSinkWriterD3DManager;
                ComHelpers.Check(IMFAttributes.SetUnknown(attributes, &key, deviceManager), "IMFAttributes::SetUnknown(MF_SINK_WRITER_D3D_MANAGER)");
            }

            fixed (char* pPath = path)
            {
                ComHelpers.Check(ScreenNativeMethods.MFCreateSinkWriterFromURL(pPath, 0, attributes, &writer), "MFCreateSinkWriterFromURL");
            }

            var size = EncoderGeometry.Pack((uint)settings.FrameSize.Width, (uint)settings.FrameSize.Height);
            var rate = EncoderGeometry.Pack((uint)settings.FrameRate, 1);
            var aspect = EncoderGeometry.Pack(1, 1);

            ComHelpers.Check(ScreenNativeMethods.MFCreateMediaType(&output), "MFCreateMediaType");
            SetGuid(output, ScreenConstants.MfMtMajorType, ScreenConstants.MfMediaTypeVideo);
            SetGuid(output, ScreenConstants.MfMtSubtype, ScreenConstants.MfVideoFormatH264);
            SetUInt32(output, ScreenConstants.MfMtAvgBitrate, settings.MeanBitrate);
            SetUInt32(output, ScreenConstants.MfMtInterlaceMode, ScreenConstants.MfVideoInterlaceProgressive);
            SetUInt64(output, ScreenConstants.MfMtFrameSize, size);
            SetUInt64(output, ScreenConstants.MfMtFrameRate, rate);
            SetUInt64(output, ScreenConstants.MfMtPixelAspectRatio, aspect);
            SetUInt32(output, ScreenConstants.MfMtMpeg2Profile, ScreenConstants.H264ProfileHigh);

            uint stream;
            ComHelpers.Check(IMFSinkWriter.AddStream(writer, output, &stream), "IMFSinkWriter::AddStream(H.264)");

            ComHelpers.Check(ScreenNativeMethods.MFCreateMediaType(&inputType), "MFCreateMediaType");
            SetGuid(inputType, ScreenConstants.MfMtMajorType, ScreenConstants.MfMediaTypeVideo);
            SetGuid(inputType, ScreenConstants.MfMtSubtype, InputSubtype(input));
            SetUInt32(inputType, ScreenConstants.MfMtInterlaceMode, ScreenConstants.MfVideoInterlaceProgressive);
            SetUInt32(inputType, ScreenConstants.MfMtAllSamplesIndependent, 1);
            SetUInt32(inputType, ScreenConstants.MfMtDefaultStride, (uint)CpuFrameCopier.Stride(settings.FrameSize));
            SetUInt64(inputType, ScreenConstants.MfMtFrameSize, size);
            SetUInt64(inputType, ScreenConstants.MfMtFrameRate, rate);
            SetUInt64(inputType, ScreenConstants.MfMtPixelAspectRatio, aspect);

            ComHelpers.Check(ScreenNativeMethods.MFCreateAttributes(&encoding, 4), "MFCreateAttributes");
            SetUInt32(encoding, ScreenConstants.CodecApiAVEncMpvGopSize, settings.GopSize);
            SetUInt32(encoding, ScreenConstants.CodecApiAVEncCommonRateControlMode, settings.RateControlMode);
            SetUInt32(encoding, ScreenConstants.CodecApiAVEncCommonMeanBitRate, settings.MeanBitrate);
            if (settings.RateControl == RateControl.PeakConstrainedVbr)
            {
                SetUInt32(encoding, ScreenConstants.CodecApiAVEncCommonMaxBitRate, settings.PeakBitrate);
            }

            // The encoding parameters (rate control included) reach the MFT here, and the MFT is negotiated; BeginWriting
            // starts it. A failure of these two steps may be a refusal of the VBR parameters (RateControlFallbackPolicy).
            CheckRateControlStep(
                IMFSinkWriter.SetInputMediaType(writer, stream, inputType, encoding),
                input == EncoderInput.Gpu ? "IMFSinkWriter::SetInputMediaType(ARGB32, D3D11)" : "IMFSinkWriter::SetInputMediaType(RGB32, system memory)",
                settings);
            CheckRateControlStep(IMFSinkWriter.BeginWriting(writer), "IMFSinkWriter::BeginWriting", settings);

            var (encoder, isHardware) = DescribeEncoder(writer, stream, hardware, input == EncoderInput.Gpu);
            var readback = ReadRateControl(writer, stream);
            codec = settings.KeyFrameInterval > 0 ? KeyFrameControl(writer, stream) : 0;
            var result = new H264SinkWriter(writer, codec, stream, encoder, isHardware, input, settings, readback) { RateControlFallback = fallback };
            writer = 0;
            codec = 0;
            return result;
        }
        finally
        {
            ComCall.Release(ref codec);
            ComCall.Release(ref encoding);
            ComCall.Release(ref inputType);
            ComCall.Release(ref output);
            ComCall.Release(ref writer);
            ComCall.Release(ref attributes);
        }
    }

    /// <summary>
    /// Hands one sample, stamped with sample time <paramref name="time"/>, to the encoder; the caller keeps its own
    /// reference. Asks the encoder for an IDR frame first when <see cref="KeyFrameSchedule"/> says one is due. Allocation-free.
    /// </summary>
    public int WriteSample(nint sample, long time)
    {
        if (_codec != 0 && _keyFrames.ShouldForce(time))
        {
            if (ForceKeyFrame(_codec) >= 0)
            {
                _keyFrames.Forced(time);
                ForcedKeyFrames++;
            }
            else
            {
                ForceKeyFrameFailures++;
            }
        }

        return IMFSinkWriter.WriteSample(_writer, _stream, sample);
    }

    /// <summary>Flushes the encoder and completes the file (IMFSinkWriter::Finalize).</summary>
    public int FinalizeFile() => IMFSinkWriter.Finalize(_writer);

    public void Dispose()
    {
        ComCall.Release(ref _codec);
        ComCall.Release(ref _writer);
    }

    /// <summary>
    /// Throws for a failed step that applies the encoding parameters: <see cref="RateControlRejectedException"/> when the
    /// failure may be a refusal of the VBR parameters (<see cref="RateControlFallbackPolicy"/>), a plain
    /// <see cref="COMException"/> otherwise.
    /// </summary>
    private static void CheckRateControlStep(int hr, string step, H264EncoderSettings settings)
    {
        if (hr >= 0)
        {
            return;
        }

        var message = step + " failed: " + ComHelpers.Describe(hr);
        if (RateControlFallbackPolicy.ShouldRetryWithCbr(settings.RateControl, hr))
        {
            throw new RateControlRejectedException(message, hr);
        }

        throw new COMException(message, hr);
    }

    /// <summary>
    /// The encoder's ICodecAPI when it supports CODECAPI_AVEncVideoForceKeyFrame (the caller owns the reference), else
    /// 0. Not a hot path; never throws.
    /// </summary>
    private static nint KeyFrameControl(nint writer, uint stream)
    {
        var service = Guid.Empty; // the encoder MFT itself
        var iid = ScreenConstants.IidICodecAPI;
        var api = ScreenConstants.CodecApiAVEncVideoForceKeyFrame;
        nint codec = 0;
        if (IMFSinkWriter.GetServiceForStream(writer, stream, &service, &iid, &codec) < 0 || codec == 0)
        {
            return 0;
        }

        if (ICodecAPI.IsSupported(codec, &api) != ScreenConstants.SOk)
        {
            ComCall.Release(ref codec);
        }

        return codec;
    }

    /// <summary>CODECAPI_AVEncVideoForceKeyFrame = 1: the next input frame becomes an IDR frame. Allocation-free.</summary>
    private static int ForceKeyFrame(nint codec)
    {
        var api = ScreenConstants.CodecApiAVEncVideoForceKeyFrame;
        var value = Variant.FromUInt32(1);
        return ICodecAPI.SetValue(codec, &api, &value);
    }

    private static (string Description, bool? IsHardware) DescribeEncoder(nint writer, uint stream, bool hardwareAllowed, bool gpuInput)
    {
        // GUID_NULL: QueryInterface on the stream's encoder MFT first (IMFSinkWriter::GetServiceForStream remarks).
        var service = Guid.Empty;
        var iid = ScreenConstants.IidIMFTransform;
        nint transform = 0, attributes = 0;
        bool? isHardware = null;
        string? name = null;
        try
        {
            if (IMFSinkWriter.GetServiceForStream(writer, stream, &service, &iid, &transform) >= 0 && transform != 0
                && IMFTransform.GetAttributes(transform, &attributes) >= 0 && attributes != 0)
            {
                var urlKey = ScreenConstants.MftEnumHardwareUrlAttribute;
                uint length;
                isHardware = IMFAttributes.GetStringLength(attributes, &urlKey, &length) >= 0;
                name = GetString(attributes, ScreenConstants.MftFriendlyNameAttribute);
            }
        }
        finally
        {
            ComCall.Release(ref attributes);
            ComCall.Release(ref transform);
        }

        if (isHardware is null && !hardwareAllowed)
        {
            isHardware = false;
        }

        return (EncoderNaming.Describe(isHardware, name, gpuInput), isHardware);
    }

    /// <summary>Reads the stream encoder's rate control through its ICodecAPI (not a hot path; never throws).</summary>
    private static RateControlReadback ReadRateControl(nint writer, uint stream)
    {
        var service = Guid.Empty; // the encoder MFT itself (IMFSinkWriter::GetServiceForStream remarks)
        var iid = ScreenConstants.IidICodecAPI;
        nint codec = 0;
        try
        {
            if (IMFSinkWriter.GetServiceForStream(writer, stream, &service, &iid, &codec) < 0 || codec == 0)
            {
                return RateControlReadback.Unknown;
            }

            return new RateControlReadback(
                GetCodecValue(codec, ScreenConstants.CodecApiAVEncCommonRateControlMode),
                GetCodecValue(codec, ScreenConstants.CodecApiAVEncCommonMeanBitRate),
                GetCodecValue(codec, ScreenConstants.CodecApiAVEncCommonMaxBitRate));
        }
        finally
        {
            ComCall.Release(ref codec);
        }
    }

    private static uint? GetCodecValue(nint codec, Guid api)
    {
        if (ICodecAPI.IsSupported(codec, &api) != ScreenConstants.SOk)
        {
            return null;
        }

        var value = default(Variant);
        try
        {
            return ICodecAPI.GetValue(codec, &api, &value) >= 0 ? value.AsUInt32() : null;
        }
        finally
        {
            // The value is the caller's: an encoder may return a type that owns memory (VT_BSTR, VT_UNKNOWN, ...).
            ScreenNativeMethods.VariantClear(&value);
        }
    }

    private static string? GetString(nint attributes, Guid key)
    {
        char* value = null;
        uint length;
        if (IMFAttributes.GetAllocatedString(attributes, &key, &value, &length) < 0 || value == null)
        {
            return null;
        }

        try
        {
            return new string(value, 0, (int)length);
        }
        finally
        {
            ScreenNativeMethods.CoTaskMemFree((nint)value);
        }
    }

    private static void SetUInt32(nint attributes, Guid key, uint value) =>
        ComHelpers.Check(IMFAttributes.SetUINT32(attributes, &key, value), "IMFAttributes::SetUINT32");

    private static void SetUInt64(nint attributes, Guid key, ulong value) =>
        ComHelpers.Check(IMFAttributes.SetUINT64(attributes, &key, value), "IMFAttributes::SetUINT64");

    private static void SetGuid(nint attributes, Guid key, Guid value) =>
        ComHelpers.Check(IMFAttributes.SetGUID(attributes, &key, &value), "IMFAttributes::SetGUID");
}

/// <summary>Hardware H.264 encoder MFTs registered on this machine (decides <see cref="EncoderInput"/>).</summary>
internal static unsafe class HardwareEncoders
{
    /// <summary>
    /// Counts hardware H.264 encoder MFTs (MFTEnumEx, MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG_HARDWARE |
    /// MFT_ENUM_FLAG_SORTANDFILTER, output MFVideoFormat_H264); 0 when the enumeration fails. Not a hot path.
    /// </summary>
    public static int CountH264()
    {
        var output = new MftRegisterTypeInfo { MajorType = ScreenConstants.MfMediaTypeVideo, Subtype = ScreenConstants.MfVideoFormatH264 };
        nint* activates = null;
        uint count = 0;
        var hr = ScreenNativeMethods.MFTEnumEx(
            ScreenConstants.MftCategoryVideoEncoder,
            ScreenConstants.MftEnumFlagHardware | ScreenConstants.MftEnumFlagSortAndFilter,
            null,
            &output,
            &activates,
            &count);
        if (hr < 0)
        {
            return 0;
        }

        if (activates != null)
        {
            for (var i = 0; i < count; i++)
            {
                ComCall.Release(ref activates[i]);
            }

            ScreenNativeMethods.CoTaskMemFree((nint)activates);
        }

        return (int)Math.Min(count, int.MaxValue);
    }
}

/// <summary>Formats <see cref="Murmur.Core.Capture.ScreenRecorderStats.Encoder"/> (pure).</summary>
internal static class EncoderNaming
{
    public const string None = "none";

    public static string Describe(bool? hardware, string? friendlyName, bool gpuInput)
    {
        var kind = hardware switch
        {
            true => "hardware",
            false => "software",
            null => "hardware transforms allowed",
        };

        var name = string.IsNullOrWhiteSpace(friendlyName) ? null : friendlyName.Trim();
        var input = gpuInput ? "D3D11 input" : "CPU readback";
        return name is null ? $"Media Foundation H.264 ({kind}, {input})" : $"Media Foundation H.264 ({kind}: {name}, {input})";
    }
}
