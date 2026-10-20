using System.Formats.Tar;
using System.IO.Compression;

namespace Murmur.Models;

/// <summary>Extracts a .zip or .tar.gz into a staging folder and finds the folder that holds the runtime's executable.</summary>
internal static class ArchiveExtractor
{
    /// <summary>
    /// Extracts <paramref name="archivePath"/> into <paramref name="extractDirectory"/> and returns the folder containing
    /// <paramref name="executableName"/> (the shallowest one when there are several).
    /// </summary>
    /// <exception cref="ModelInstallException">The archive is unreadable, unsafe or does not contain the executable.</exception>
    public static async Task<string> ExtractAsync(string archivePath, ModelPackaging packaging, string executableName, string extractDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(extractDirectory);
        try
        {
            switch (packaging)
            {
                case ModelPackaging.Zip:
                    await ZipFile.ExtractToDirectoryAsync(archivePath, extractDirectory, overwriteFiles: true, cancellationToken).ConfigureAwait(false);
                    break;
                case ModelPackaging.TarGz:
                    await using (var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                    {
                        await TarFile.ExtractToDirectoryAsync(gzip, extractDirectory, overwriteFiles: true, cancellationToken).ConfigureAwait(false);
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(packaging), packaging, "Not an archive");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or UnauthorizedAccessException)
        {
            // Includes entries that would escape the target folder (zip slip): both extractors refuse them.
            throw new ModelInstallException($"Extracting {Path.GetFileName(archivePath)} failed: {ex.Message}", ex);
        }

        var server = Directory.EnumerateFiles(extractDirectory, executableName, SearchOption.AllDirectories)
            .OrderBy(path => path.Count(c => c is '/' or '\\'))
            .ThenBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
        if (server is null)
        {
            throw new ModelInstallException($"{Path.GetFileName(archivePath)} does not contain {executableName}");
        }

        return Path.GetDirectoryName(server)!;
    }

    /// <summary>Marks the file executable for its owner, group and others (no-op on Windows).</summary>
    public static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }
}
