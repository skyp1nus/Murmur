using System.Buffers.Binary;

namespace Murmur.Core.Audio;

/// <summary>Fixes the header of a WAV file whose writer died before its final flush.</summary>
public static class WavRepair
{
    /// <summary>
    /// Rewrites the RIFF and data chunk sizes from the real file length, dropping a partial trailing sample frame.
    /// The data chunk is assumed to be the last chunk (true for <see cref="WavPcm16Writer"/> files).
    /// </summary>
    /// <param name="duration">Audio duration after the repair.</param>
    /// <returns><c>false</c> when the file is missing or is not a readable PCM/float WAV.</returns>
    public static bool TryRepair(string path, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            if (!WavLayout.TryRead(stream, out var layout))
            {
                return false;
            }

            var frames = layout.FramesInFile(stream.Length);
            var dataBytes = Math.Min(frames * layout.BlockAlign, uint.MaxValue - layout.DataOffset);
            frames = dataBytes / layout.BlockAlign;
            dataBytes = frames * layout.BlockAlign;
            var length = layout.DataOffset + dataBytes;

            if (stream.Length != length)
            {
                stream.SetLength(length);
            }

            Span<byte> field = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(field, (uint)(length - 8));
            stream.Position = 4;
            stream.Write(field);

            BinaryPrimitives.WriteUInt32LittleEndian(field, (uint)dataBytes);
            stream.Position = layout.DataOffset - 4;
            stream.Write(field);
            stream.Flush(flushToDisk: true);

            duration = TimeSpan.FromSeconds((double)frames / layout.SampleRate);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
