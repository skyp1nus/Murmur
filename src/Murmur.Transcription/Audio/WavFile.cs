using System.Buffers.Binary;

namespace Murmur.Transcription.Audio;

/// <summary>
/// Whole-file WAV helpers for short clips (bench input, tests): read any PCM 8/16/24/32 or float 32/64 file as mono,
/// write 16 kHz mono PCM16/float32. Recordings are streamed with <see cref="WavReader"/> instead.
/// </summary>
internal static class WavFile
{
    /// <summary>Reads the whole file and downmixes it to mono.</summary>
    public static (float[] Samples, int SampleRate) ReadMono(string path)
    {
        using var stream = File.OpenRead(path);
        var header = WavHeader.Read(stream);
        if (header.DataLength > 512L * 1024 * 1024)
        {
            throw new InvalidDataException("WAV file too large to load at once; stream it with WavReader.");
        }

        var data = new byte[header.DataLength];
        var read = stream.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);
        var frames = read / header.BlockAlign;
        var mono = new float[frames];
        var bytesPerSample = header.BitsPerSample / 8;
        var scale = 1f / header.Channels;
        for (var f = 0; f < frames; f++)
        {
            var sum = 0f;
            for (var c = 0; c < header.Channels; c++)
            {
                sum += DecodeSample(data.AsSpan((f * header.Channels + c) * bytesPerSample, bytesPerSample), header);
            }

            mono[f] = sum * scale;
        }

        return (mono, header.SampleRate);
    }

    private static float DecodeSample(ReadOnlySpan<byte> b, WavHeader h) => (h.Encoding, h.BitsPerSample) switch
    {
        (WavEncoding.Pcm, 8) => (b[0] - 128) / 128f,
        (WavEncoding.Pcm, 16) => BinaryPrimitives.ReadInt16LittleEndian(b) / 32768f,
        (WavEncoding.Pcm, 24) => (b[0] | (b[1] << 8) | ((sbyte)b[2] << 16)) / 8388608f,
        (WavEncoding.Pcm, 32) => (float)(BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648.0),
        (WavEncoding.IeeeFloat, 32) => BinaryPrimitives.ReadSingleLittleEndian(b),
        (WavEncoding.IeeeFloat, 64) => (float)BinaryPrimitives.ReadDoubleLittleEndian(b),
        _ => throw new InvalidDataException($"Unsupported WAV sample format: {h.Describe()}."),
    };

    /// <summary>Encodes mono samples as a canonical 44-byte-header WAV (PCM16 or IEEE float32).</summary>
    public static byte[] Encode(ReadOnlySpan<float> samples, int sampleRate, bool asFloat = false, int channels = 1)
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
        BinaryPrimitives.WriteUInt16LittleEndian(s[20..], (ushort)(asFloat ? 3 : 1));
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
                BinaryPrimitives.WriteInt16LittleEndian(d[(i * 2)..], (short)MathF.Round(Math.Clamp(samples[i], -1f, 1f) * 32767f));
            }
        }

        return file;
    }

    /// <summary>Streams a 16 kHz mono source into a PCM16 WAV file without materialising it.</summary>
    public static void Write(string path, ISampleSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        const int block = 16 * 1024;
        var header = Encode(ReadOnlySpan<float>.Empty, WavReader.RequiredSampleRate);
        var dataSize = checked((uint)(source.TotalSamples * 2));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 36 + dataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), dataSize);

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
        file.Write(header);
        var samples = new float[block];
        var bytes = new byte[block * 2];
        int n;
        while ((n = source.Read(samples)) > 0)
        {
            for (var i = 0; i < n; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)MathF.Round(Math.Clamp(samples[i], -1f, 1f) * 32767f));
            }

            file.Write(bytes, 0, n * 2);
        }
    }

    public static void Write(string path, ReadOnlySpan<float> samples, int sampleRate, bool asFloat = false) =>
        File.WriteAllBytes(path, Encode(samples, sampleRate, asFloat));

    /// <summary>Reads a clip of any rate/channel count and converts it to 16 kHz mono.</summary>
    public static float[] ReadAs16kMono(string path)
    {
        var (samples, rate) = ReadMono(path);
        return rate == WavReader.RequiredSampleRate ? samples : new SincResampler(rate, WavReader.RequiredSampleRate).Process(samples);
    }
}
