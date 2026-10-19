using System.ComponentModel;
using System.Diagnostics;

namespace Murmur.App.Services;

/// <summary>Opens a folder in the OS file manager.</summary>
public interface IFolderLauncher
{
    /// <summary>Opens <paramref name="path"/>; returns an error text (English, for logs and inline messages) or <c>null</c> on success.</summary>
    string? TryOpen(string path);
}

/// <summary>
/// <see cref="IFolderLauncher"/> over the OS shell: <c>explorer.exe</c> on Windows, <c>open</c> on macOS, <c>xdg-open</c>
/// elsewhere. The path is passed as one argument (no shell parsing). UNVERIFIED-ON-WINDOWS: only the Linux branch has been run.
/// </summary>
public sealed class SystemFolderLauncher : IFolderLauncher
{
    public static readonly SystemFolderLauncher Instance = new();

    public string? TryOpen(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "The folder path is empty.";
        }

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add(path);

            // The launcher exits at once (explorer even reports exit code 1 on success); nobody waits for it.
            using var process = Process.Start(info);
            return process is null ? "The file manager did not start." : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return ex.Message;
        }
    }
}
