using System.Text;

namespace Murmur.Storage;

/// <summary>One file of the logs folder.</summary>
internal readonly record struct LogFileEntry(string Name, long Length, DateTimeOffset LastWriteUtc);

/// <summary>
/// The few file operations of <see cref="FileAppLog"/>, so tests can count them, see on which thread they run and fake the file times.
/// Every method may throw; the log catches.
/// </summary>
internal interface ILogFileSystem
{
    /// <summary>The files of <paramref name="directory"/>; empty when it does not exist (it is never created by listing).</summary>
    IReadOnlyList<LogFileEntry> List(string directory);

    /// <summary>Appends UTF-8 text, creating the folder and the file as needed.</summary>
    void Append(string path, string text);

    /// <summary>Creates or replaces a file with UTF-8 text, creating the folder as needed.</summary>
    void Write(string path, string text);

    /// <summary>At most <paramref name="maxChars"/> characters of a file, or <c>null</c> when it cannot be read.</summary>
    string? Read(string path, int maxChars);

    void Delete(string path);
}

/// <summary>The real disk.</summary>
internal sealed class PhysicalLogFileSystem : ILogFileSystem
{
    public static readonly PhysicalLogFileSystem Instance = new();

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public IReadOnlyList<LogFileEntry> List(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists)
        {
            return [];
        }

        var files = new List<LogFileEntry>();
        foreach (var file in info.EnumerateFiles())
        {
            files.Add(new LogFileEntry(file.Name, file.Length, file.LastWriteTimeUtc));
        }

        return files;
    }

    public void Append(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, text, Utf8NoBom);
    }

    public void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, Utf8NoBom);
    }

    public string? Read(string path, int maxChars)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var reader = new StreamReader(path, Utf8NoBom);
        var buffer = new char[maxChars];
        var read = reader.ReadBlock(buffer, 0, maxChars);
        return new string(buffer, 0, read);
    }

    public void Delete(string path) => File.Delete(path);
}
