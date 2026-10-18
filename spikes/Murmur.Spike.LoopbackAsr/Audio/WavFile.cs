using System.Buffers.Binary;

namespace Murmur.Spike.LoopbackAsr.Audio;

/// <summary>Decoded WAV content: interleaved float samples in [-1, 1].</summary>
internal sealed class WavData(float[] samples, int sampleRate, int channels)
{
    public float[] Samples { get; } = samples;

    public int SampleRate { get; } = sampleRate;

    public int Channels { get; } = channels;

    public int Frames => Samples.Length / Channels;

    public double DurationSeconds => (double)Frames / SampleRate;
}

/// <summary>Minimal RIFF/WAVE reader and writer (PCM 8/16/24/32, IEEE float 32/64, WAVE_FORMAT_EXTENSIBLE).</summary>
internal static class WavFile
{
    private const ushort FormatPcm = 0x0001;
    private const ushort FormatIeeeFloat = 0x0003;
    private const ushort FormatExtensible = 0xFFFE;

    public static WavData Read(string path) => Parse(File.ReadAllBytes(path));

    public static WavData Parse(ReadOnlySpan<byte> file)
    {
        if (file.Length < 12 || !file[..4].SequenceEqual("RIFF"u8) || !file.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }

        ushort format = 0;
        int channels = 0;
        int sampleRate = 0;
        int bits = 0;
        int blockAlign = 0;
        ReadOnlySpan<byte> data = default;
        var haveFmt = false;
        var haveData = false;

        var pos = 12;
        while (pos + 8 <= file.Length)
        {
            var id = file.Slice(pos, 4);
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(pos + 4, 4)), (uint)(file.Length - pos - 8));
            var body = file.Slice(pos + 8, size);

            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16)
                {
                    throw new InvalidDataException("fmt chunk too short.");
                }

                format = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(body[12..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                if (format == FormatExtensible)
                {
                    if (size < 40)
                    {
                        throw new InvalidDataException("WAVE_FORMAT_EXTENSIBLE fmt chunk too short.");
                    }

                    // SubFormat GUID starts at offset 24; its first two bytes are the underlying format tag
                    // (KSDATAFORMAT_SUBTYPE_PCM = 00000001-..., KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = 00000003-...).
                    format = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]);
                }

                haveFmt = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                data = body;
                haveData = true;
            }

            // Chunks are word-aligned.
            pos += 8 + size + (size & 1);
        }

        if (!haveFmt || !haveData)
        {
            throw new InvalidDataException("WAV file has no fmt or data chunk.");
        }

        if (channels <= 0 || sampleRate <= 0 || blockAlign != channels * (bits / 8))
        {
            throw new InvalidDataException($"Unsupported WAV header: channels={channels}, rate={sampleRate}, bits={bits}, blockAlign={blockAlign}.");
        }

        var sampleCount = data.Length / blockAlign * channels;
        var samples = new float[sampleCount];
        Decode(data, format, bits, samples);
        return new WavData(samples, sampleRate, channels);
    }

    private static void Decode(ReadOnlySpan<byte> data, ushort format, int bits, Span<float> dst)
    {
        switch (format, bits)
        {
            case (FormatPcm, 8):
                for (var i = 0; i < dst.Length; i++)
                {
                    dst[i] = (data[i] - 128) / 128f;
                }

                break;
            case (FormatPcm, 16):
                for (var i = 0; i < dst.Length; i++)
                {
                    dst[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]) / 32768f;
                }

                break;
            case (FormatPcm, 24):
                for (var i = 0; i < dst.Length; i++)
                {
                    var o = i * 3;
                    var v = data[o] | (data[o + 1] << 8) | ((sbyte)data[o + 2] << 16);
                    dst[i] = v / 8388608f;
                }

                break;
            case (FormatPcm, 32):
                for (var i = 0; i < dst.Length; i++)
                {
                    dst[i] = (float)(BinaryPrimitives.ReadInt32LittleEndian(data[(i * 4)..]) / 2147483648.0);
                }

                break;
            case (FormatIeeeFloat, 32):
                for (var i = 0; i < dst.Length; i++)
                {
                    dst[i] = BinaryPrimitives.ReadSingleLittleEndian(data[(i * 4)..]);
                }

                break;
            case (FormatIeeeFloat, 64):
                for (var i = 0; i < dst.Length; i++)
                {
                    dst[i] = (float)BinaryPrimitives.ReadDoubleLittleEndian(data[(i * 8)..]);
                }

                break;
            default:
                throw new InvalidDataException($"Unsupported WAV sample format: tag=0x{format:X4}, bits={bits}.");
        }
    }

    /// <summary>Writes mono or interleaved samples as PCM16 (<paramref name="asFloat"/> = false) or IEEE float32.</summary>
    public static void Write(string path, ReadOnlySpan<float> samples, int sampleRate, int channels, bool asFloat = false)
    {
        var bytes = Encode(samples, sampleRate, channels, asFloat);
        File.WriteAllBytes(path, bytes);
    }

    public static byte[] Encode(ReadOnlySpan<float> samples, int sampleRate, int channels, bool asFloat)
    {
        var bytesPerSample = asFloat ? 4 : 2;
        var dataSize = samples.Length * bytesPerSample;
        var file = new byte[44 + dataSize];
        var s = file.AsSpan();

        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)(36 + dataSize));
        "WAVE"u8.CopyTo(s[8..]);
        "fmt "u8.CopyTo(s[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(s[20..], asFloat ? FormatIeeeFloat : FormatPcm);
        BinaryPrimitives.WriteUInt16LittleEndian(s[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(s[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], (uint)(sampleRate * channels * bytesPerSample));
        BinaryPrimitives.WriteUInt16LittleEndian(s[32..], (ushort)(channels * bytesPerSample));
        BinaryPrimitives.WriteUInt16LittleEndian(s[34..], (ushort)(bytesPerSample * 8));
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], (uint)dataSize);

        var d = s[44..];
        for (var i = 0; i < samples.Length; i++)
        {
            if (asFloat)
            {
                BinaryPrimitives.WriteSingleLittleEndian(d[(i * 4)..], samples[i]);
            }
            else
            {
                var v = Math.Clamp(samples[i], -1f, 1f);
                BinaryPrimitives.WriteInt16LittleEndian(d[(i * 2)..], (short)MathF.Round(v * 32767f));
            }
        }

        return file;
    }
}
