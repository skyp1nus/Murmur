using System.Buffers.Binary;
using Murmur.Core.Import;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Import;

public sealed class WavMediaDecoderLargeHeaderTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact(Skip = "follow-up: reviewer P2 (b) - WavMediaDecoder.Classify scans only the first 1 MiB for the data chunk, so a valid WAV with larger chunks (e.g. embedded cover art in LIST/id3) before data is rejected as damaged")]
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
    }
}
