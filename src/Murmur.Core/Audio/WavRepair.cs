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
            return IsBusy(ex) ? WavRepairResult.Busy : WavRepairResult.Invalid;
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
        catch (IOException ex) when (IsBusy(ex))
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

    internal static FileStream OpenForRepair(string path) =>
        new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

    /// <summary>
    /// Whether someone else holds the file right now. Windows: ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION.
    /// Unix: .NET takes an advisory <c>flock</c> and reports a conflict as an <see cref="IOException"/> whose HResult is
    /// the errno EWOULDBLOCK (EAGAIN); any other I/O error (EROFS, EIO, ...) will not go away by waiting.
    /// </summary>
    private static bool IsBusy(IOException ex) => OperatingSystem.IsWindows()
        ? ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)
        : ex.HResult == (OperatingSystem.IsLinux() ? 11 : 35);
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
