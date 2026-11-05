using System.Buffers;
using System.Buffers.Binary;
using Murmur.Core.Audio;
using Murmur.Core.Capture;

namespace Murmur.Core.Import;

/// <summary>
/// Managed decoder for WAV files (works on every platform): any PCM (8/16/24/32-bit) or IEEE-float (32/64-bit) WAV of
/// any rate and channel count, including <c>WAVE_FORMAT_EXTENSIBLE</c>, down-mixed and resampled by
/// <see cref="StreamingResampler"/> to the recorder's track format (<see cref="AudioFormat.Asr"/>, 16-bit PCM).
/// </summary>
/// <remarks>
/// A valid format chunk with another encoding (ADPCM, µ-law...) is <see cref="NotSupportedException"/>; a file that is
/// not RIFF/WAVE, has a damaged or truncated format chunk, no data chunk or no audio is <see cref="InvalidDataException"/>. Decoding runs on the thread pool, streams in blocks (pooled buffers, no
/// allocation per block), writes <c>&lt;destination&gt;.part</c> and renames it when complete, so a cancelled or failed
/// decode never leaves a WAV that looks finished.
/// </remarks>
public sealed class WavMediaDecoder : IMediaDecoder
{
    /// <summary>Frames read per block.</summary>
    internal const int BlockFrames = 8192;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.Ordinal) { ".wav" };

    public IReadOnlySet<string> Extensions => SupportedExtensions;

    public ValueTask<MediaProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new ValueTask<MediaProbe>(Task.Run(
            () =>
            {
                using var reader = Open(path);
                return new MediaProbe(path, MediaKind.Audio, new FileInfo(path).Length, reader.Duration);
            },
            cancellationToken));
    }

    public ValueTask DecodeAudioAsync(string path, string destinationWav, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(destinationWav);
        return new ValueTask(Task.Run(() => Decode(path, destinationWav, progress, cancellationToken), cancellationToken));
    }

    /// <summary>The synchronous decode (also the benchmark's entry point).</summary>
    internal static void Decode(string path, string destinationWav, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var part = destinationWav + ".part";
        float[]? input = null;
        float[]? output = null;
        try
        {
            using (var reader = Open(path))
            {
                var channels = reader.Layout.Channels;
                var resampler = new StreamingResampler(reader.Format, AudioFormat.Asr.SampleRate);
                input = ArrayPool<float>.Shared.Rent(BlockFrames * channels);
                output = ArrayPool<float>.Shared.Rent(Math.Max(resampler.MaxOutputSamples(BlockFrames), resampler.MaxFlushSamples));
                var total = Math.Max(reader.FrameCount, 1);
                long done = 0;
                var reported = -1;

                using var writer = CreateWriter(part);
                int frames;
                while ((frames = reader.Read(input.AsSpan(0, BlockFrames * channels))) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var produced = resampler.Process(input.AsSpan(0, frames * channels), output);
                    writer.Append(output.AsSpan(0, produced));
                    done += frames;

                    // At most one report per percent.
                    var percent = (int)(done * 100 / total);
                    if (percent != reported && percent < 100)
                    {
                        reported = percent;
                        progress?.Report(percent / 100.0);
                    }
                }

                var tail = resampler.Flush(output);
                writer.Append(output.AsSpan(0, tail));
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(part, destinationWav, overwrite: true);
            progress?.Report(1);
        }
        catch
        {
            TryDelete(part);
            throw;
        }
        finally
        {
            if (input is not null)
            {
                ArrayPool<float>.Shared.Return(input);
            }

            if (output is not null)
            {
                ArrayPool<float>.Shared.Return(output);
            }
        }
    }

    /// <summary>A 16 kHz mono PCM16 writer over a new <paramref name="path"/>; the stream is disposed when the writer cannot be made.</summary>
    internal static WavPcm16Writer CreateWriter(string path)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 64 * 1024);
        try
        {
            return new WavPcm16Writer(stream, AudioFormat.Asr.SampleRate, AudioFormat.Asr.Channels);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <exception cref="NotSupportedException">A RIFF/WAVE file with a valid format chunk whose encoding is not PCM or IEEE float.</exception>
    /// <exception cref="InvalidDataException">Not a RIFF/WAVE file, a damaged or truncated format chunk, no data chunk, or no audio.</exception>
    private static WavReader Open(string path)
    {
        var name = Path.GetFileName(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        try
        {
            Classify(stream, name);
            WavReader reader;
            try
            {
                reader = WavReader.Open(stream);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException($"'{name}' is a damaged WAV file.", ex);
            }

            if (reader.FrameCount == 0)
            {
                reader.Dispose();
                throw new InvalidDataException($"'{name}' holds no audio.");
            }

            return reader;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Tells a format we do not decode (a valid <c>fmt </c> chunk with another encoding: ADPCM, µ-law...) from a damaged
    /// file (no RIFF/WAVE header, a short or inconsistent <c>fmt </c> chunk, no <c>data</c> chunk).
    /// </summary>
    internal static void Classify(Stream stream, string name)
    {
        const ushort Pcm = 1;
        const ushort Float = 3;
        const ushort Extensible = 0xFFFE;
        const int MaxHeaderScan = 1 << 20;

        stream.Position = 0;
        Span<byte> riff = stackalloc byte[12];
        if (stream.ReadAtLeast(riff, riff.Length, throwOnEndOfStream: false) < riff.Length
            || !riff[..4].SequenceEqual("RIFF"u8) || !riff[8..].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException($"'{name}' is not a RIFF/WAVE file.");
        }

        Span<byte> chunk = stackalloc byte[8];
        Span<byte> fmt = stackalloc byte[40];
        var haveFormat = false;
        while (stream.Position < MaxHeaderScan && stream.ReadAtLeast(chunk, chunk.Length, throwOnEndOfStream: false) == chunk.Length)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                var take = (int)Math.Min(size, (uint)fmt.Length);
                if (size < 16 || stream.ReadAtLeast(fmt[..take], take, throwOnEndOfStream: false) < take)
                {
                    throw new InvalidDataException($"'{name}' has a damaged format chunk.");
                }

                var tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                var channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                var rate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
                var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
                if (tag == Extensible)
                {
                    if (take < 26)
                    {
                        throw new InvalidDataException($"'{name}' has a damaged format chunk.");
                    }

                    tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]); // first two bytes of the sub-format GUID
                }

                if (channels == 0 || rate == 0 || blockAlign == 0)
                {
                    throw new InvalidDataException($"'{name}' has a damaged format chunk.");
                }

                var supported = (tag == Pcm && bits is 8 or 16 or 24 or 32) || (tag == Float && bits is 32 or 64);
                if (!supported)
                {
                    throw new NotSupportedException($"'{name}' is a compressed or unknown WAV encoding (format 0x{tag:X4}, {bits} bits).");
                }

                if (blockAlign != channels * (bits / 8))
                {
                    throw new InvalidDataException($"'{name}' has a damaged format chunk.");
                }

                stream.Position += size - take + (size & 1);
                haveFormat = true;
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                if (!haveFormat)
                {
                    break;
                }

                return;
            }
            else
            {
                stream.Position += size + (size & 1);
            }
        }

        throw new InvalidDataException(haveFormat ? $"'{name}' has no data chunk." : $"'{name}' has no format chunk.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover .part never looks like a finished track.
        }
    }
}
