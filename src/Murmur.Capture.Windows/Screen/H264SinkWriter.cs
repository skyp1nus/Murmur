using System.Runtime.InteropServices;
using Murmur.Capture.Windows.Audio.Interop;
using Murmur.Capture.Windows.Screen.Interop;

namespace Murmur.Capture.Windows.Screen;

/// <summary>Encoder settings of one segment.</summary>
internal readonly record struct H264EncoderSettings(PixelSize FrameSize, int FrameRate, int BitrateKbps)
{
    /// <summary>Keyframe interval: 2 s, so fragments (and crash-recoverable data) are at most about 2 s apart.</summary>
    public uint GopSize => (uint)Math.Max(1, FrameRate * 2);
}

/// <summary>
/// Media Foundation sink writer producing a fragmented MP4 (MFTranscodeContainerType_FMPEG4: every fragment is
/// self-contained, so a crash leaves a playable file up to the last fragment) with one H.264 stream. Two configurations
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
internal sealed unsafe class H264SinkWriter : IDisposable
{
    private const int OpenAttempts = 10;
    private static readonly TimeSpan OpenRetryDelay = TimeSpan.FromMilliseconds(50);

    private nint _writer;
    private readonly uint _stream;

    private H264SinkWriter(nint writer, uint stream, string encoder, bool? isHardware, EncoderInput input)
    {
        _writer = writer;
        _stream = stream;
        Encoder = encoder;
        IsHardware = isHardware;
        Input = input;
    }

    /// <summary>Which encoder the sink writer picked, for <see cref="Murmur.Core.Capture.ScreenRecorderStats.Encoder"/>.</summary>
    public string Encoder { get; }

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
    /// </summary>
    /// <exception cref="COMException">The configuration could not be set up.</exception>
    public static H264SinkWriter Create(string path, H264EncoderSettings settings, EncoderInput input, nint deviceManager)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (input == EncoderInput.Gpu && deviceManager == 0)
        {
            throw new ArgumentException("The GPU configuration needs the DXGI device manager.", nameof(deviceManager));
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return input == EncoderInput.Gpu
                    ? CreateOnce(path, settings, hardware: true, deviceManager, input)
                    : CreateOnce(path, settings, hardware: false, 0, input);
            }
            catch (COMException ex) when (ex.HResult is ScreenConstants.ESharingViolation or ScreenConstants.ELockViolation && attempt < OpenAttempts)
            {
                Thread.Sleep(OpenRetryDelay);
            }
        }
    }

    private static H264SinkWriter CreateOnce(string path, H264EncoderSettings settings, bool hardware, nint deviceManager, EncoderInput input)
    {
        nint attributes = 0, writer = 0, output = 0, inputType = 0, encoding = 0;
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
            SetUInt32(output, ScreenConstants.MfMtAvgBitrate, (uint)Math.Min((long)settings.BitrateKbps * 1000, uint.MaxValue));
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

            ComHelpers.Check(ScreenNativeMethods.MFCreateAttributes(&encoding, 1), "MFCreateAttributes");
            SetUInt32(encoding, ScreenConstants.CodecApiAVEncMpvGopSize, settings.GopSize);
            ComHelpers.Check(
                IMFSinkWriter.SetInputMediaType(writer, stream, inputType, encoding),
                input == EncoderInput.Gpu ? "IMFSinkWriter::SetInputMediaType(ARGB32, D3D11)" : "IMFSinkWriter::SetInputMediaType(RGB32, system memory)");
            ComHelpers.Check(IMFSinkWriter.BeginWriting(writer), "IMFSinkWriter::BeginWriting");

            var (encoder, isHardware) = DescribeEncoder(writer, stream, hardware, input == EncoderInput.Gpu);
            var result = new H264SinkWriter(writer, stream, encoder, isHardware, input);
            writer = 0;
            return result;
        }
        finally
        {
            ComCall.Release(ref encoding);
            ComCall.Release(ref inputType);
            ComCall.Release(ref output);
            ComCall.Release(ref writer);
            ComCall.Release(ref attributes);
        }
    }

    /// <summary>Hands one sample to the encoder; the caller keeps its own reference. Allocation-free.</summary>
    public int WriteSample(nint sample) => IMFSinkWriter.WriteSample(_writer, _stream, sample);

    /// <summary>Flushes the encoder and completes the file (IMFSinkWriter::Finalize).</summary>
    public int FinalizeFile() => IMFSinkWriter.Finalize(_writer);

    public void Dispose() => ComCall.Release(ref _writer);

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
