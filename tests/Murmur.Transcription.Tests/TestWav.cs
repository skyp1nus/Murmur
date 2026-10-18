using System.Buffers.Binary;

namespace Murmur.Transcription.Tests;

/// <summary>Builds WAV files byte by byte, including odd layouts real files have.</summary>
internal static class TestWav
{
    public const ushort Pcm = 1;
    public const ushort Float = 3;
    public const ushort Extensible = 0xFFFE;

    public static byte[] Build(
        ushort formatTag,
        int sampleRate,
        int channels,
        int bits,
        ReadOnlySpan<byte> data,
        uint? declaredDataSize = null,
        ushort subFormat = 0,
        ReadOnlySpan<byte> extraChunkBeforeData = default)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(0u); // patched below
        w.Write("WAVE"u8);

        if (!extraChunkBeforeData.IsEmpty)
        {
            w.Write("LIST"u8);
            w.Write((uint)extraChunkBeforeData.Length);
            w.Write(extraChunkBeforeData);
            if ((extraChunkBeforeData.Length & 1) == 1)
            {
                w.Write((byte)0); // pad byte
            }
        }

        var extensible = formatTag == Extensible;
        w.Write("fmt "u8);
        w.Write(extensible ? 40u : 16u);
        w.Write(formatTag);
        w.Write((ushort)channels);
        w.Write((uint)sampleRate);
        w.Write((uint)(sampleRate * channels * bits / 8));
        w.Write((ushort)(channels * bits / 8));
        w.Write((ushort)bits);
        if (extensible)
        {
            w.Write((ushort)22);
            w.Write((ushort)bits);
            w.Write(channels == 1 ? 4u : 3u);
            Span<byte> guid = stackalloc byte[16];
            new Guid($"{subFormat:X8}-0000-0010-8000-00AA00389B71").TryWriteBytes(guid);
            w.Write(guid);
        }

        w.Write("data"u8);
        w.Write(declaredDataSize ?? (uint)data.Length);
        w.Write(data);
        w.Flush();

        var bytes = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return bytes;
    }

    public static byte[] Pcm16(ReadOnlySpan<short> samples)
    {
        var data = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), samples[i]);
        }

        return data;
    }

    public static byte[] Float32(ReadOnlySpan<float> samples)
    {
        var data = new byte[samples.Length * 4];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), samples[i]);
        }

        return data;
    }

    public static float[] ReadAll(Audio.ISampleSource source, int chunk)
    {
        var all = new List<float>();
        var buffer = new float[chunk];
        int n;
        while ((n = source.Read(buffer)) > 0)
        {
            all.AddRange(buffer.AsSpan(0, n));
        }

        return [.. all];
    }
}

/// <summary>A temporary directory deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir(string? name = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "murmur-asr-tests", (name ?? "t") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
