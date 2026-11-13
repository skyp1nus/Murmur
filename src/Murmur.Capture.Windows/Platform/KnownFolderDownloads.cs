using System.Runtime.InteropServices;
using Murmur.Core.Import;

namespace Murmur.Capture.Windows.Platform;

/// <summary>
/// <see cref="IDownloadsFolder"/> on Windows: the known folder <c>FOLDERID_Downloads</c> (<c>SHGetKnownFolderPath</c>), so a Downloads
/// folder the user moved (Properties → Location, OneDrive folder backup) is found. When the shell cannot answer or the folder does not
/// exist, <see cref="HomeDownloadsFolder"/> (<c>%USERPROFILE%\Downloads</c>) answers instead.
/// </summary>
/// <remarks>The folder is asked for on every read of <see cref="Path"/> (one cheap shell call), so a move while the app runs is noticed.</remarks>
internal sealed class KnownFolderDownloads : IDownloadsFolder
{
    private readonly Func<string?> _knownFolder;
    private readonly IDownloadsFolder _fallback;

    /// <summary>Asks the shell, falls back to the profile's Downloads folder.</summary>
    public KnownFolderDownloads()
        : this(KnownFolderNativeMethods.Downloads, new HomeDownloadsFolder())
    {
    }

    /// <summary>Asks <paramref name="knownFolder"/> instead of the shell (tests).</summary>
    internal KnownFolderDownloads(Func<string?> knownFolder, IDownloadsFolder fallback)
    {
        ArgumentNullException.ThrowIfNull(knownFolder);
        ArgumentNullException.ThrowIfNull(fallback);
        _knownFolder = knownFolder;
        _fallback = fallback;
    }

    public string? Path
    {
        get
        {
            string? folder;
            try
            {
                folder = _knownFolder();
            }
            catch (Exception ex) when (ex is COMException or ExternalException or DllNotFoundException or EntryPointNotFoundException)
            {
                folder = null;
            }

            return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) ? System.IO.Path.GetFullPath(folder) : _fallback.Path;
        }
    }
}

/// <summary>The shell's known-folder lookup (shell32 / ole32), source-generated; signatures follow the Windows SDK 10.0.26100 headers.</summary>
internal static unsafe partial class KnownFolderNativeMethods
{
    /// <summary><c>FOLDERID_Downloads</c> (KnownFolders.h).</summary>
    public static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary><c>HRESULT SHGetKnownFolderPath(REFKNOWNFOLDERID rfid, DWORD dwFlags, HANDLE hToken, PWSTR *ppszPath)</c>.</summary>
    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(Guid* folderId, uint flags, nint token, char** path);

    /// <summary><c>void CoTaskMemFree(LPVOID pv)</c>.</summary>
    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(nint memory);

    /// <summary>The Downloads folder of the current user (default flags, no impersonation token), or <c>null</c> when the call fails.</summary>
    public static string? Downloads()
    {
        var id = DownloadsId;
        char* path = null;
        try
        {
            var result = SHGetKnownFolderPath(&id, 0, 0, &path);
            return result >= 0 && path is not null ? new string(path) : null;
        }
        finally
        {
            // The string is the caller's to free whether the call succeeded or not (CoTaskMemFree(NULL) does nothing).
            CoTaskMemFree((nint)path);
        }
    }
}
