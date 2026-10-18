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
/// self-contained, so a crash leaves a playable file up to the last fragment) with one H.264 stream fed by
/// D3D11 ARGB32 samples. Creation tries, in order: hardware MFTs with the DXGI device manager, software MFTs with
/// the device manager, software MFTs without it (the sink writer then reads the textures back itself).
/// Not thread-safe: owned by the encoder thread after <see cref="Create"/>.
/// UNVERIFIED-ON-WINDOWS until the Windows-trait tests run in CI.
/// </summary>
internal sealed unsafe class H264SinkWriter : IDisposable
{
    private nint _writer;
    private readonly uint _stream;

    private H264SinkWriter(nint writer, uint stream, string encoder)
    {
        _writer = writer;
        _stream = stream;
        Encoder = encoder;
    }

    /// <summary>Which encoder the sink writer picked, for <see cref="Murmur.Core.Capture.ScreenRecorderStats.Encoder"/>.</summary>
    public string Encoder { get; }

    /// <exception cref="COMException">No configuration could be set up; the HRESULT of the last attempt.</exception>
    public static H264SinkWriter Create(string path, H264EncoderSettings settings, nint deviceManager)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ReadOnlySpan<(bool Hardware, bool UseDeviceManager)> attempts = [(true, true), (false, true), (false, false)];
        COMException? last = null;
        foreach (var (hardware, useManager) in attempts)
        {
            try
            {
                return CreateOnce(path, settings, hardware, useManager ? deviceManager : 0);
            }
            catch (COMException ex)
            {
                last = ex;
            }
        }

        throw last!;
    }

    private static H264SinkWriter CreateOnce(string path, H264EncoderSettings settings, bool hardware, nint deviceManager)
    {
        nint attributes = 0, writer = 0, output = 0, input = 0, encoding = 0;
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

            ComHelpers.Check(ScreenNativeMethods.MFCreateMediaType(&input), "MFCreateMediaType");
            SetGuid(input, ScreenConstants.MfMtMajorType, ScreenConstants.MfMediaTypeVideo);
            SetGuid(input, ScreenConstants.MfMtSubtype, ScreenConstants.MfVideoFormatArgb32);
            SetUInt32(input, ScreenConstants.MfMtInterlaceMode, ScreenConstants.MfVideoInterlaceProgressive);
            SetUInt32(input, ScreenConstants.MfMtAllSamplesIndependent, 1);
            SetUInt64(input, ScreenConstants.MfMtFrameSize, size);
            SetUInt64(input, ScreenConstants.MfMtFrameRate, rate);
            SetUInt64(input, ScreenConstants.MfMtPixelAspectRatio, aspect);

            ComHelpers.Check(ScreenNativeMethods.MFCreateAttributes(&encoding, 1), "MFCreateAttributes");
            SetUInt32(encoding, ScreenConstants.CodecApiAVEncMpvGopSize, settings.GopSize);
            ComHelpers.Check(IMFSinkWriter.SetInputMediaType(writer, stream, input, encoding), "IMFSinkWriter::SetInputMediaType(ARGB32)");
            ComHelpers.Check(IMFSinkWriter.BeginWriting(writer), "IMFSinkWriter::BeginWriting");

            var encoder = DescribeEncoder(writer, stream, hardware, deviceManager != 0);
            var result = new H264SinkWriter(writer, stream, encoder);
            writer = 0;
            return result;
        }
        finally
        {
            ComCall.Release(ref encoding);
            ComCall.Release(ref input);
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

    private static string DescribeEncoder(nint writer, uint stream, bool hardwareAllowed, bool gpuInput)
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

        return EncoderNaming.Describe(isHardware, name, gpuInput);
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
