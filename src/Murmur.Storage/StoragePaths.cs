namespace Murmur.Storage;

/// <summary>Where Murmur keeps its data: the database and one folder per session.</summary>
public sealed class StoragePaths
{
    /// <summary>Environment variable that overrides the data root.</summary>
    public const string DataDirEnvironmentVariable = "MURMUR_DATA_DIR";

    public const string RecordingsFolderName = "Recordings";

    public const string DatabaseFileName = "murmur.db";

    public StoragePaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>Folder that contains the database and the <see cref="RecordingsDirectory"/>.</summary>
    public string Root { get; }

    public string RecordingsDirectory => Path.Combine(Root, RecordingsFolderName);

    public string DatabasePath => Path.Combine(Root, DatabaseFileName);

    /// <summary>
    /// Resolves the data root from <c>MURMUR_DATA_DIR</c>, falling back to
    /// <c>LocalApplicationData/Murmur</c> (<c>%LOCALAPPDATA%\Murmur</c> on Windows, <c>~/.local/share/Murmur</c> on Linux).
    /// </summary>
    public static StoragePaths FromEnvironment() =>
        Resolve(
            Environment.GetEnvironmentVariable(DataDirEnvironmentVariable),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>Pure resolution logic behind <see cref="FromEnvironment"/>; an empty override is ignored.</summary>
    public static StoragePaths Resolve(string? dataDirOverride, string localApplicationData)
    {
        if (!string.IsNullOrWhiteSpace(dataDirOverride))
        {
            return new StoragePaths(dataDirOverride);
        }

        return new StoragePaths(Path.Combine(localApplicationData, "Murmur"));
    }
}
