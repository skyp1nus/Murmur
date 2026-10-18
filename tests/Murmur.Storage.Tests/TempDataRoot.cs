namespace Murmur.Storage.Tests;

/// <summary>A unique temp data root per test, deleted on dispose.</summary>
internal sealed class TempDataRoot : IDisposable
{
    public TempDataRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "murmur-storage-tests", Guid.NewGuid().ToString("N"));
    }

    public string Path { get; }

    public SqliteStorage Open() => new(Path);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
