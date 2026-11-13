using System.Text;

namespace Murmur.Storage.Tests;

/// <summary>An in-memory logs folder: counts every operation, remembers the threads they ran on, and dates files by the test clock.</summary>
internal sealed class FakeLogFileSystem(TimeProvider time) : ILogFileSystem
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (StringBuilder Text, DateTimeOffset LastWrite)> _files = new(StringComparer.Ordinal);

    /// <summary>Every call of any method, listing included.</summary>
    public int Operations { get; private set; }

    /// <summary>Appends and writes only.</summary>
    public int Writes { get; private set; }

    public HashSet<int> Threads { get; } = [];

    /// <summary>The test clock at each append.</summary>
    public List<DateTimeOffset> AppendTimes { get; } = [];

    public IReadOnlyList<LogFileEntry> List(string directory)
    {
        lock (_gate)
        {
            Touch();
            return ListCore(directory);
        }
    }

    private List<LogFileEntry> ListCore(string directory)
    {
        lock (_gate)
        {
            var prefix = directory + Path.DirectorySeparatorChar;
            return _files
                .Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(f => new LogFileEntry(f.Key[prefix.Length..], Encoding.UTF8.GetByteCount(f.Value.Text.ToString()), f.Value.LastWrite))
                .ToList();
        }
    }

    public void Append(string path, string text)
    {
        lock (_gate)
        {
            Touch();
            Writes++;
            AppendTimes.Add(time.GetUtcNow());
            if (_files.TryGetValue(path, out var file))
            {
                file.Text.Append(text);
                _files[path] = (file.Text, time.GetUtcNow());
            }
            else
            {
                _files[path] = (new StringBuilder(text), time.GetUtcNow());
            }
        }
    }

    public void Write(string path, string text)
    {
        lock (_gate)
        {
            Touch();
            Writes++;
            _files[path] = (new StringBuilder(text), time.GetUtcNow());
        }
    }

    public string? Read(string path, int maxChars)
    {
        lock (_gate)
        {
            Touch();
            if (!_files.TryGetValue(path, out var file))
            {
                return null;
            }

            var text = file.Text.ToString();
            return text.Length <= maxChars ? text : text[..maxChars];
        }
    }

    public void Delete(string path)
    {
        lock (_gate)
        {
            Touch();
            _files.Remove(path);
        }
    }

    /// <summary>Puts a file in place without counting it (the folder a previous run left).</summary>
    public void Seed(string directory, string name, string text, DateTimeOffset lastWrite)
    {
        lock (_gate)
        {
            _files[Path.Combine(directory, name)] = (new StringBuilder(text), lastWrite);
        }
    }

    public string? Text(string directory, string name)
    {
        lock (_gate)
        {
            return _files.TryGetValue(Path.Combine(directory, name), out var file) ? file.Text.ToString() : null;
        }
    }

    public IReadOnlyList<string> Names(string directory) => ListCore(directory).Select(f => f.Name).Order(StringComparer.Ordinal).ToList();

    public long TotalBytes(string directory) => ListCore(directory).Sum(f => f.Length);

    public long Length(string directory, string name) => ListCore(directory).Single(f => f.Name == name).Length;

    public void ResetCounters()
    {
        lock (_gate)
        {
            Operations = 0;
            Writes = 0;
            Threads.Clear();
        }
    }

    private void Touch()
    {
        Operations++;
        Threads.Add(Environment.CurrentManagedThreadId);
    }
}
