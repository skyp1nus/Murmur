using System.Buffers.Binary;
using Murmur.Core.Capture;

namespace Murmur.Capture.Windows.Audio;

/// <summary>Sample encodings of a WASAPI mix format that <see cref="SampleConverter"/> turns into float.</summary>
internal enum SampleType
{
    /// <summary>IEEE float, 32-bit.</summary>
    Float32,

    /// <summary>Signed 16-bit integer PCM.</summary>
    Pcm16,

    /// <summary>Signed 24-bit integer PCM packed in 3 bytes.</summary>
    Pcm24,

    /// <summary>Signed integer PCM in a 32-bit container (32 valid bits, or 24/20 valid bits left-justified).</summary>
    Pcm32,
}

/// <summary>Parsed WASAPI mix format.</summary>
internal readonly record struct MixFormat(
    int SampleRate,
    int Channels,
    SampleType SampleType,
    int BlockAlign,
    int ValidBitsPerSample,
    uint ChannelMask,
    bool IsExtensible)
{
    public int BytesPerSample => SampleConverter.BytesPerSample(SampleType);

    public AudioFormat AudioFormat => new(SampleRate, Channels);

    public override string ToString() =>
        $"{SampleRate} Hz, {Channels} ch, {SampleType} ({ValidBitsPerSample} valid bits){(IsExtensible ? ", WAVE_FORMAT_EXTENSIBLE" : "")}";
}

/// <summary>
/// Parses <c>WAVEFORMATEX</c> / <c>WAVEFORMATEXTENSIBLE</c> from raw bytes (mmreg.h, <c>#pragma pack(1)</c>).
/// Pure and platform-neutral so it is unit-testable anywhere; little-endian like every Windows target.
/// </summary>
internal static class WaveFormatParser
{
    // mmreg.h WAVEFORMATEX (pack 1): wFormatTag@0, nChannels@2, nSamplesPerSec@4, nAvgBytesPerSec@8,
    // nBlockAlign@12, wBitsPerSample@14, cbSize@16; sizeof = 18.
    public const int WaveFormatExSize = 18;

    // mmreg.h WAVEFORMATEXTENSIBLE (pack 1): Format@0 (18), Samples.wValidBitsPerSample@18, dwChannelMask@20,
    // SubFormat@24 (GUID, 16); sizeof = 40, so cbSize >= 22.
    public const int WaveFormatExtensibleSize = 40;
    public const int ExtensibleExtraSize = WaveFormatExtensibleSize - WaveFormatExSize;

    public const ushort WaveFormatPcm = 0x0001;        // mmreg.h WAVE_FORMAT_PCM
    public const ushort WaveFormatIeeeFloat = 0x0003;  // mmreg.h WAVE_FORMAT_IEEE_FLOAT
    public const ushort WaveFormatExtensible = 0xFFFE; // mmreg.h WAVE_FORMAT_EXTENSIBLE

    // ksmedia.h / mmreg.h: DEFINE_WAVEFORMATEX_GUID(x) = {x, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71}
    public static readonly Guid KsDataFormatSubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");
    public static readonly Guid KsDataFormatSubtypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    /// <summary>Total size in bytes of the structure starting with <paramref name="header"/> (18 + cbSize).</summary>
    public static int GetTotalSize(ReadOnlySpan<byte> header)
    {
        if (header.Length < WaveFormatExSize)
        {
            throw new ArgumentException($"A WAVEFORMATEX needs {WaveFormatExSize} bytes, got {header.Length}.", nameof(header));
        }

        return WaveFormatExSize + BinaryPrimitives.ReadUInt16LittleEndian(header[16..]);
    }

    /// <summary>Parses a mix format or throws <see cref="NotSupportedException"/> with the reason.</summary>
    public static MixFormat Parse(ReadOnlySpan<byte> bytes) =>
        TryParse(bytes, out var format, out var error) ? format : throw new NotSupportedException(error);

    public static bool TryParse(ReadOnlySpan<byte> bytes, out MixFormat format, out string error)
    {
        format = default;
        if (bytes.Length < WaveFormatExSize)
        {
            error = $"WAVEFORMATEX truncated: {bytes.Length} bytes (< {WaveFormatExSize}).";
            return false;
        }

        var tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
        int cbSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes[16..]);

        if (channels == 0)
        {
            error = "Mix format has 0 channels.";
            return false;
        }

        if (sampleRate == 0 || sampleRate > 1_536_000)
        {
            error = $"Mix format has an invalid sample rate {sampleRate}.";
            return false;
        }

        var isExtensible = tag == WaveFormatExtensible;
        var validBits = bits;
        uint channelMask = 0;
        bool isFloat;
        bool isPcm;
        if (isExtensible)
        {
            if (cbSize < ExtensibleExtraSize || bytes.Length < WaveFormatExtensibleSize)
            {
                error = $"WAVE_FORMAT_EXTENSIBLE with cbSize={cbSize} and {bytes.Length} bytes (needs cbSize >= {ExtensibleExtraSize}, {WaveFormatExtensibleSize} bytes).";
                return false;
            }

            validBits = BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]);
            channelMask = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]);
            var subFormat = new Guid(bytes.Slice(24, 16));
            isFloat = subFormat == KsDataFormatSubtypeIeeeFloat;
            isPcm = subFormat == KsDataFormatSubtypePcm;
            if (!isFloat && !isPcm)
            {
                error = $"Unsupported WAVE_FORMAT_EXTENSIBLE sub-format {subFormat}.";
                return false;
            }

            if (validBits == 0)
            {
                validBits = bits; // wReserved = 0: every container bit is valid
            }
        }
        else
        {
            isFloat = tag == WaveFormatIeeeFloat;
            isPcm = tag == WaveFormatPcm;
            if (!isFloat && !isPcm)
            {
                error = $"Unsupported wave format tag 0x{tag:X4}.";
                return false;
            }
        }

        SampleType sampleType;
        if (isFloat && bits == 32)
        {
            sampleType = SampleType.Float32;
        }
        else if (isPcm && bits == 16)
        {
            sampleType = SampleType.Pcm16;
        }
        else if (isPcm && bits == 24)
        {
            sampleType = SampleType.Pcm24;
        }
        else if (isPcm && bits == 32)
        {
            sampleType = SampleType.Pcm32;
        }
        else
        {
            error = $"Unsupported sample encoding: {(isFloat ? "float" : "PCM")} with {bits} bits per sample.";
            return false;
        }

        if (validBits > bits)
        {
            error = $"wValidBitsPerSample={validBits} exceeds wBitsPerSample={bits}.";
            return false;
        }

        var expectedBlockAlign = channels * (bits / 8);
        if (blockAlign != expectedBlockAlign)
        {
            error = $"Unexpected nBlockAlign={blockAlign} for {channels} ch x {bits} bit (expected {expectedBlockAlign}).";
            return false;
        }

        format = new MixFormat((int)sampleRate, channels, sampleType, blockAlign, validBits, channelMask, isExtensible);
        error = string.Empty;
        return true;
    }
}
