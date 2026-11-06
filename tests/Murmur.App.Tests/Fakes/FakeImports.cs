using Murmur.App.Services;
using Murmur.Core.Import;

namespace Murmur.App.Tests.Fakes;

/// <summary>An <see cref="IImportQueue"/> that records what it was given (and can fail), for the import dialog.</summary>
internal sealed class FakeImportQueue : IImportQueue
{
    /// <summary>The formats of the mockup's formats line (what the queue takes on Windows).</summary>
    public static readonly string[] MockupExtensions = [".mp3", ".wav", ".m4a", ".flac", ".ogg", ".mp4", ".mkv", ".mov", ".webm"];

    private readonly List<ImportRequest> _requests = [];

    public FakeImportQueue(params string[] extensions)
    {
        SupportedExtensions = new HashSet<string>(extensions.Length == 0 ? MockupExtensions : extensions, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> SupportedExtensions { get; }

    /// <summary>What <see cref="Items"/> answers (items of earlier imports); <see cref="RaiseChanged"/> tells the dialog.</summary>
    public List<ImportItem> Earlier { get; } = [];

    public IReadOnlyList<ImportItem> Items
    {
        get
        {
            Interlocked.Increment(ref _itemsReads);
            return [.. Earlier];
        }
    }

    private int _itemsReads;

    /// <summary>How often <see cref="Items"/> was read (each read is a snapshot of the queue).</summary>
    public int ItemsReads => Volatile.Read(ref _itemsReads);

    /// <summary>The subscribers of <see cref="Changed"/> (the dialog unsubscribes when disposed).</summary>
    public int Subscribers => Changed?.GetInvocationList().Length ?? 0;

    /// <summary>Raises <see cref="Changed"/> on a pool thread, as the real queue does, and waits for the handlers.</summary>
    public Task RaiseChanged() => Task.Run(() => Changed?.Invoke());

    /// <summary>When set, <see cref="EnqueueAsync"/> throws it.</summary>
    public Exception? Fail { get; set; }

    /// <summary>The calls of <see cref="EnqueueAsync"/>.</summary>
    public int Calls { get; private set; }

    /// <summary>Every request handed over, in order.</summary>
    public IReadOnlyList<ImportRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public event Action? Changed;

    /// <summary>When set, <see cref="EnqueueAsync"/> waits for it before it takes the requests (the dialog is mid-import).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async ValueTask<IReadOnlyList<ImportItem>> EnqueueAsync(IReadOnlyList<ImportRequest> requests, CancellationToken cancellationToken)
    {
        Calls++;
        if (Gate is { } gate)
        {
            await gate.Task.ConfigureAwait(false);
        }

        if (Fail is { } error)
        {
            throw error;
        }

        var items = new List<ImportItem>(requests.Count);
        lock (_requests)
        {
            foreach (var request in requests)
            {
                _requests.Add(request);
                items.Add(new ImportItem(_requests.Count, request.Path, ImportState.Queued, 0, null, null));
            }
        }

        Changed?.Invoke();
        return items;
    }

    public ValueTask CancelAsync(long id, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>
/// An <see cref="IMediaDecoder"/> whose probes answer from <see cref="Results"/> (by file name): a probe, or an exception to throw. Probes
/// can be held with <see cref="Hold"/>; the threads they ran on are recorded.
/// </summary>
internal sealed class FakeMediaDecoder : IMediaDecoder
{
    private readonly Lock _lock = new();
    private readonly List<(int Id, bool IsPool)> _threads = [];
    private readonly List<string> _probed = [];
    private TaskCompletionSource? _hold;

    public FakeMediaDecoder(params string[] extensions)
    {
        Extensions = new HashSet<string>(extensions.Length == 0 ? FakeImportQueue.MockupExtensions : extensions, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> Extensions { get; }

    /// <summary>What the probe of a file name gives: a <see cref="MediaProbe"/> (its path is replaced) or an <see cref="Exception"/> to throw.</summary>
    public Dictionary<string, object> Results { get; } = new(StringComparer.Ordinal);

    /// <summary>The threads the probes ran on: the managed thread id and whether it is a thread-pool thread.</summary>
    public IReadOnlyList<(int Id, bool IsPool)> Threads
    {
        get
        {
            lock (_lock)
            {
                return [.. _threads];
            }
        }
    }

    /// <summary>The files probed, in order.</summary>
    public IReadOnlyList<string> Probed
    {
        get
        {
            lock (_lock)
            {
                return [.. _probed];
            }
        }
    }

    /// <summary>Probes wait until <see cref="Release"/>.</summary>
    public void Hold() => _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _hold?.TrySetResult();

    public void Answer(string name, MediaKind kind, long bytes, TimeSpan duration) => Results[name] = new MediaProbe(name, kind, bytes, duration);

    public void Throw(string name, Exception error) => Results[name] = error;

    public async ValueTask<MediaProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _threads.Add((Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread));
            _probed.Add(path);
        }

        if (_hold is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return Results.GetValueOrDefault(Path.GetFileName(path)) switch
        {
            MediaProbe probe => probe with { Path = path },
            Exception error => throw error,
            _ => new MediaProbe(path, MediaKind.Audio, 1_000_000, TimeSpan.FromMinutes(1)),
        };
    }

    public ValueTask DecodeAudioAsync(string path, string destinationWav, IProgress<double>? progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The dialog only probes.");
}

/// <summary>A Downloads folder at a fixed path (or none).</summary>
internal sealed class FakeDownloadsFolder(string? path) : IDownloadsFolder
{
    public string? Path { get; set; } = path;
}

/// <summary>An <see cref="IMediaFilePicker"/> that answers with the next queued list of paths (empty = cancelled) and records the calls.</summary>
internal sealed class FakeMediaFilePicker : IMediaFilePicker
{
    private readonly Queue<IReadOnlyList<string>> _answers = new();

    public List<(string Title, string FileTypeName, IReadOnlyCollection<string> Extensions)> Calls { get; } = [];

    /// <summary>When set, the picker throws it.</summary>
    public Exception? Fail { get; set; }

    public FakeMediaFilePicker Answer(params string[] paths)
    {
        _answers.Enqueue(paths);
        return this;
    }

    public Task<IReadOnlyList<string>> PickMediaFilesAsync(string title, string fileTypeName, IReadOnlyCollection<string> extensions)
    {
        Calls.Add((title, fileTypeName, extensions));
        if (Fail is { } error)
        {
            throw error;
        }

        return Task.FromResult(_answers.TryDequeue(out var paths) ? paths : (IReadOnlyList<string>)[]);
    }
}
