using System.Buffers.Binary;
using Murmur.Core.Audio;
using Murmur.Core.Import;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Import;

public sealed class WavMediaDecoderLargeHeaderTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task AValidWav_WithMoreThanOneMiBOfChunksBeforeData_IsProbedAndDecoded()
    {
        var path = _dir.File("tagged.wav");
        var pcm = new byte[16_000 * 2]; // 1 s of 16 kHz mono PCM16 silence
        var list = new byte[(1 << 20) + (512 << 10)];
        using (var stream = File.Create(path))
        {
            var header = new byte[8];
            void Chunk(ReadOnlySpan<byte> id, ReadOnlySpan<byte> body)
            {
                stream.Write(id);
                BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length);
                stream.Write(header[..4]);
                stream.Write(body);
            }

            stream.Write("RIFF"u8);
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(4 + (8 + 16) + (8 + list.Length) + (8 + pcm.Length)));
            stream.Write(header[..4]);
            stream.Write("WAVE"u8);
            var fmt = new byte[16];
            BinaryPrimitives.WriteUInt16LittleEndian(fmt, 1);
            BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), 16_000);
            BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), 32_000);
            BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), 16);
            Chunk("fmt "u8, fmt);
            Chunk("LIST"u8, list);
            Chunk("data"u8, pcm);
        }

        var probe = await new WavMediaDecoder().ProbeAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(1), probe.Duration);
        var decoded = _dir.File("decoded.wav");
        await new WavMediaDecoder().DecodeAudioAsync(path, decoded, null, TestContext.Current.CancellationToken);
        Assert.Equal(16_000, AnyWav.Read(decoded).Samples.Length);
    }

    [Fact]
    public async Task AChunk_ThatIsSkippedBySize_IsNeverRead_EvenWhenItIsHuge()
    {
        // 300 MiB of LIST made sparse by SetLength: reading it into memory would be obvious, seeking over it is instant.
        var path = _dir.File("huge-list.wav");
        using (var stream = File.Create(path))
        {
            WriteHeader(stream, riffSize: 0);
            Chunk(stream, "LIST"u8, 300 << 20, body: false);
            stream.SetLength(stream.Position + (300L << 20));
            stream.Position = stream.Length;
            Chunk(stream, "data"u8, 32_000, body: true);
        }

        var probe = await new WavMediaDecoder().ProbeAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(1), probe.Duration);
    }

    [Fact]
    public async Task AChunk_WhoseSizeReachesPastTheEndOfTheFile_IsADamagedFile_AndTheSizeDoesNotOverflow()
    {
        var path = _dir.File("cut-off.wav");
        using (var stream = File.Create(path))
        {
            WriteHeader(stream, riffSize: 0);
            Chunk(stream, "LIST"u8, uint.MaxValue, body: false); // padded to an even size: 4 GiB, more than a uint holds
            stream.Write(new byte[64]);
        }

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new WavMediaDecoder().ProbeAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MoreChunksBeforeDataThanTheCap_AreADamagedFile()
    {
        var path = _dir.File("many-chunks.wav");
        using (var stream = File.Create(path))
        {
            WriteHeader(stream, riffSize: 0);
            for (var i = 0; i < WavLayout.MaxChunksBeforeData + 1; i++)
            {
                Chunk(stream, "junk"u8, 0, body: false);
            }

            Chunk(stream, "data"u8, 32_000, body: true);
        }

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new WavMediaDecoder().ProbeAsync(path, TestContext.Current.CancellationToken));
    }

    private static void WriteHeader(Stream stream, uint riffSize)
    {
        Span<byte> number = stackalloc byte[4];
        stream.Write("RIFF"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(number, riffSize);
        stream.Write(number);
        stream.Write("WAVE"u8);
        var fmt = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), 16_000);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), 32_000);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), 16);
        Chunk(stream, "fmt "u8, 16, body: false);
        stream.Write(fmt);
    }

    /// <summary>A chunk header; with <paramref name="body"/> also that many zero bytes (1 s of silence for data).</summary>
    private static void Chunk(Stream stream, ReadOnlySpan<byte> id, uint size, bool body)
    {
        Span<byte> number = stackalloc byte[4];
        stream.Write(id);
        BinaryPrimitives.WriteUInt32LittleEndian(number, size);
        stream.Write(number);
        if (body)
        {
            stream.Write(new byte[size]);
        }
    }
}
