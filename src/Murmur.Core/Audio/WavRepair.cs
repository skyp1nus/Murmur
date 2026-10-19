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
    /// <returns>
    /// <c>false</c> when the file is missing, is not a readable PCM/float WAV, or is held open by someone else;
    /// use <see cref="Repair(string, out TimeSpan)"/> to tell those apart.
    /// </returns>
    public static bool TryRepair(string path, out TimeSpan duration) =>
        Repair(path, out duration) == WavRepairResult.Repaired;

    /// <summary>Like <see cref="TryRepair"/>, but reports why a file could not be repaired.</summary>
    public static WavRepairResult Repair(string path, out TimeSpan duration) =>
        Repair(path, OpenForRepair, out duration);

    internal static WavRepairResult Repair(string path, Func<string, Stream> open, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (!File.Exists(path))
        {
            return WavRepairResult.Missing;
        }

        Stream stream;
        try
        {
            stream = open(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return WavRepairResult.Missing;
        }
        catch (IOException ex)
        {
            // Windows reports a lock precisely. Elsewhere there is no mandatory locking, so an I/O error on open
            // is the closest thing to "busy"; a persistent one just costs the caller its retries.
            return OperatingSystem.IsWindows() && !IsLockViolation(ex) ? WavRepairResult.Invalid : WavRepairResult.Busy;
        }
        catch (UnauthorizedAccessException)
        {
            return WavRepairResult.Invalid;
        }

        try
        {
            using (stream)
            {
                if (!WavLayout.TryRead(stream, out var layout))
                {
                    return WavRepairResult.Invalid;
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
                if (stream is FileStream file)
                {
                    file.Flush(flushToDisk: true);
                }
                else
                {
                    stream.Flush();
                }

                duration = TimeSpan.FromSeconds((double)frames / layout.SampleRate);
                return WavRepairResult.Repaired;
            }
        }
        catch (IOException ex) when (OperatingSystem.IsWindows() && IsLockViolation(ex))
        {
            return WavRepairResult.Busy;
        }
        catch (IOException)
        {
            return WavRepairResult.Invalid;
        }
        catch (UnauthorizedAccessException)
        {
            return WavRepairResult.Invalid;
        }
    }

    private static FileStream OpenForRepair(string path) =>
        new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

    /// <summary>ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION (33).</summary>
    private static bool IsLockViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;
}

/// <summary>Outcome of <see cref="WavRepair.Repair(string, out TimeSpan)"/>.</summary>
public enum WavRepairResult
{
    /// <summary>The header now matches the file.</summary>
    Repaired,

    /// <summary>There is no file.</summary>
    Missing,

    /// <summary>The file exists but is not a readable PCM/float WAV (or cannot be written).</summary>
    Invalid,

    /// <summary>Someone else holds the file (sharing or lock violation); trying again later may work.</summary>
    Busy,
}
