namespace Murmur.Core.Import;

/// <summary>The user's Downloads folder. Windows: SHGetKnownFolderPath(FOLDERID_Downloads) (Murmur.Capture.Windows); elsewhere and as the fallback: ~/Downloads.</summary>
public interface IDownloadsFolder
{
    /// <summary>The folder's full path, or null when it cannot be found or does not exist.</summary>
    string? Path { get; }
}

/// <summary>%USERPROFILE%/Downloads (or $HOME/Downloads); null when the folder does not exist.</summary>
/// <remarks>The folder is looked up on every read of <see cref="Path"/>, so one created or removed while the app runs is noticed.</remarks>
public sealed class HomeDownloadsFolder : IDownloadsFolder
{
    private const string FolderName = "Downloads";

    private readonly string? _home;

    /// <summary>The Downloads folder in the profile of the user the process runs as (<see cref="Environment.SpecialFolder.UserProfile"/>).</summary>
    public HomeDownloadsFolder()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    /// <summary>The Downloads folder under <paramref name="home"/> (tests); <c>null</c> or empty = no home folder.</summary>
    internal HomeDownloadsFolder(string? home)
    {
        _home = string.IsNullOrWhiteSpace(home) ? null : home;
    }

    public string? Path
    {
        get
        {
            if (_home is null)
            {
                return null;
            }

            try
            {
                var folder = System.IO.Path.GetFullPath(System.IO.Path.Combine(_home, FolderName));
                return Directory.Exists(folder) ? folder : null;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A home path the file system cannot take (invalid characters, too long): there is no Downloads folder to offer.
                return null;
            }
        }
    }
}
