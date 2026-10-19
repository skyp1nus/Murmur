using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using Murmur.Core.Models;

namespace Murmur.Models;

/// <summary>
/// <see cref="IModelManager"/> over the file system and HTTP. Layout (below <see cref="ModelsDirectory"/>):
/// <c>sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/</c>, <c>silero_vad.onnx</c>, <c>llama.cpp/</c>, <c>gguf/</c> + <c>gguf/models.json</c>.
/// </summary>
/// <remarks>
/// Downloads go to <c>.staging/&lt;id&gt;/dl/&lt;file&gt;.part</c> and are renamed to <c>&lt;file&gt;</c> once complete and verified; the
/// finished set is moved into place in one step (a folder is swapped, a single file is renamed), so a half-installed
/// item never looks installed. A failed install faults the returned task with <see cref="ModelInstallException"/> and
/// leaves <see cref="ModelInstallState.Failed"/> in <see cref="GetStatus"/> until the next attempt. Installing an item
/// that is already installed only (re)activates it in <c>models.json</c> and touches no files or network: to repair a
/// damaged install, <see cref="RemoveAsync"/> the item first and install it again. A joiner of a running install waits
/// for the install started by the first caller: only that caller's token cancels the download. Disposing the manager
/// cancels running installs (their partial downloads are kept).
/// </remarks>
public sealed class ModelManager : IModelManager, IDisposable
{
    private const long RecommendBigChatFromBytes = 32_000_000_000L;
    private const string StagingDirectoryName = ".staging";

    private readonly Dictionary<string, ModelCatalogItem> _items;
    private readonly List<ModelCatalogItem> _catalogItems;
    private readonly List<ModelInfo> _infos;
    private readonly ModelDownloader _downloader;
    private readonly ModelsManifestStore _manifest;
    private readonly Func<string, long> _freeSpace;
    private readonly Func<long> _totalMemory;
    private readonly TimeSpan _progressInterval;
    private readonly bool _reportHashes;
    private readonly HttpClient? _ownedClient;
    private readonly Action<string>? _beforeRemove;
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    private readonly object _gate = new();
    private readonly Dictionary<string, Operation> _operations = [];
    private readonly HashSet<string> _removing = [];
    private readonly Dictionary<string, string> _errors = [];
    private readonly Dictionary<string, long> _lastTotals = [];
    private readonly Dictionary<string, IReadOnlyList<ModelFileReport>> _reports = [];

    public ModelManager(ModelManagerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.ModelsDirectory);

        ModelsDirectory = options.ModelsDirectory;
        var catalog = options.Catalog ?? ModelCatalog.ForCurrentOs();
        _items = new Dictionary<string, ModelCatalogItem>(StringComparer.Ordinal);
        foreach (var item in catalog)
        {
            if (!_items.TryAdd(item.Id, item))
            {
                throw new ArgumentException($"Duplicate catalog id '{item.Id}'", nameof(options));
            }
        }

        _catalogItems = [.. catalog];
        _infos = [.. catalog.Select(item => item.Info)];

        var http = options.HttpClient;
        if (http is null)
        {
            // Default handler: honours the system proxy. No overall timeout (multi-GB bodies), the stall timeout applies.
            // PooledConnectionLifetime: a long-lived process must notice DNS changes of the hosts (CDN rotation).
            http = _ownedClient = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) }) { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Murmur-ModelManager/1.0");
        }

        _downloader = new ModelDownloader(
            http,
            options.RetryDelays ?? [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)],
            options.StallTimeout);
        _manifest = new ModelsManifestStore(options.ModelsDirectory);
        _freeSpace = options.FreeSpaceProbe ?? DefaultFreeSpace;
        _totalMemory = options.TotalMemoryProbe ?? (() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        _progressInterval = options.ProgressInterval;
        _reportHashes = options.ReportHashes;
        _beforeRemove = options.BeforeRemove;
    }

    /// <summary>The catalog for this OS on the real hosts, installing into <paramref name="modelsDirectory"/>.</summary>
    public ModelManager(string modelsDirectory)
        : this(new ModelManagerOptions { ModelsDirectory = modelsDirectory })
    {
    }

    public event Action<ModelStatus>? StatusChanged;

    public string ModelsDirectory { get; }

    public IReadOnlyList<ModelInfo> Catalog => _infos;

    /// <summary>The catalog entries with their sources, for tools.</summary>
    public IReadOnlyList<ModelCatalogItem> Items => _catalogItems;

    /// <summary>The big Gemma (26B) from 32 GB of physical memory, else the small one (E4B).</summary>
    public string RecommendedChatModelId
    {
        get
        {
            var big = _totalMemory() >= RecommendBigChatFromBytes && _items.ContainsKey(ModelCatalog.Chat26BId);
            if (big)
            {
                return ModelCatalog.Chat26BId;
            }

            if (_items.ContainsKey(ModelCatalog.ChatE4BId))
            {
                return ModelCatalog.ChatE4BId;
            }

            return _infos.First(info => info.Kind == ModelKind.Chat).Id;
        }
    }

    public long AvailableFreeSpaceBytes => _freeSpace(ModelsDirectory);

    public ModelStatus GetStatus(string id)
    {
        var item = Find(id);
        lock (_gate)
        {
            return BuildStatusLocked(item);
        }
    }

    /// <summary>The id of the chat or embedding model that <c>models.json</c> names, or <c>null</c>.</summary>
    public string? ActiveModelId(ModelKind kind)
    {
        if (kind is not (ModelKind.Chat or ModelKind.Embedding) || _manifest.ActivePath(kind) is null)
        {
            return null;
        }

        foreach (var item in _items.Values)
        {
            if (item.Info.Kind == kind && _manifest.IsActive(kind, TargetFullPath(item)))
            {
                return item.Id;
            }
        }

        return null;
    }

    /// <summary>The files the last install of the item in this process downloaded (empty before one ran).</summary>
    public IReadOnlyList<ModelFileReport> GetLastInstallReport(string id)
    {
        Find(id);
        lock (_gate)
        {
            return _reports.TryGetValue(id, out var report) ? report : [];
        }
    }

    /// <summary>
    /// Asks the servers for the size of every file of the item without downloading it (a one-byte range request): the
    /// first source of a file that answers wins. Fails, listing every source, when none answers.
    /// </summary>
    public async Task<IReadOnlyList<ModelFileProbe>> ProbeAsync(string id, CancellationToken cancellationToken)
    {
        var item = Find(id);
        var result = new List<ModelFileProbe>(item.Files.Count);
        foreach (var file in item.Files)
        {
            var choice = await ChooseMirrorAsync(file, cancellationToken).ConfigureAwait(false);
            result.Add(new ModelFileProbe(choice.Mirror.Url, choice.Total >= 0 ? choice.Total : null, choice.Tried));
        }

        return result;
    }

    public Task InstallAsync(string id, CancellationToken cancellationToken)
    {
        var item = Find(id);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return Task.FromException(new ObjectDisposedException(nameof(ModelManager)));
        }

        lock (_gate)
        {
            if (_operations.TryGetValue(id, out var running))
            {
                return cancellationToken.CanBeCanceled ? running.Task.WaitAsync(cancellationToken) : running.Task;
            }

            if (_removing.Contains(id))
            {
                return Task.FromException(new InvalidOperationException($"Model '{id}' is being removed"));
            }

            var operation = new Operation(item);
            _operations[id] = operation;
            _errors.Remove(id);

            // Started under the lock on purpose: RunAsync takes it again in its finally block, so the task is
            // assigned before anyone can observe the operation as finished.
            operation.Task = Task.Run(() => RunAsync(operation, cancellationToken), CancellationToken.None);
            return operation.Task;
        }
    }

    public Task RemoveAsync(string id, CancellationToken cancellationToken)
    {
        var item = Find(id);
        lock (_gate)
        {
            if (_operations.ContainsKey(id))
            {
                return Task.FromException(new InvalidOperationException($"Model '{id}' is being installed"));
            }

            if (!_removing.Add(id))
            {
                return Task.FromException(new InvalidOperationException($"Model '{id}' is already being removed"));
            }
        }

        return Task.Run(
            () =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _beforeRemove?.Invoke(id);
                    DeleteItem(item);
                }
                finally
                {
                    ModelStatus status;
                    lock (_gate)
                    {
                        _removing.Remove(id);
                        _errors.Remove(id);
                        status = BuildStatusLocked(item);
                    }

                    Raise(status);
                }
            },
            CancellationToken.None);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel(); // running installs stop and keep their partial downloads
        _ownedClient?.Dispose();
    }

    private static long DefaultFreeSpace(string directory)
    {
        try
        {
            var path = Path.GetFullPath(directory);
            while (!Directory.Exists(path) && Path.GetDirectoryName(path) is { } parent)
            {
                path = parent;
            }

            return new DriveInfo(path).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return long.MaxValue; // unknown: do not block the install
        }
    }

    private static bool NonEmptyFile(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    private static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private sealed record MirrorChoice(ModelMirror Mirror, long Total, IReadOnlyList<ModelCandidateProbe> Tried);

    /// <summary>
    /// Asks the sources of a file in order and takes the first that answers with a length (else the first that answers
    /// at all). A source that does not answer, is empty, or reports a size other than the pinned one is skipped.
    /// </summary>
    private async Task<MirrorChoice> ChooseMirrorAsync(ModelFileSpec spec, CancellationToken cancellationToken)
    {
        var sources = spec.Sources;
        var tried = new List<ModelCandidateProbe>(sources.Count);
        MirrorChoice? withoutLength = null;
        foreach (var mirror in sources)
        {
            try
            {
                var total = await _downloader.ProbeAsync(mirror.Url, cancellationToken).ConfigureAwait(false);
                if (mirror.SizeBytes is { } expected && total >= 0 && total != expected)
                {
                    tried.Add(new ModelCandidateProbe(mirror.Url, total, $"the server reports {total} bytes, expected {expected}"));
                    continue;
                }

                if (total == 0)
                {
                    tried.Add(new ModelCandidateProbe(mirror.Url, 0, "the file is empty"));
                    continue;
                }

                tried.Add(new ModelCandidateProbe(mirror.Url, total >= 0 ? total : null, null));
                if (total >= 0)
                {
                    return new MirrorChoice(mirror, total, tried);
                }

                withoutLength ??= new MirrorChoice(mirror, total, tried);
            }
            catch (ModelInstallException ex) when (sources.Count > 1)
            {
                tried.Add(new ModelCandidateProbe(mirror.Url, null, ex.Message));
            }
        }

        return withoutLength
            ?? throw new ModelInstallException(
                $"None of the {sources.Count} sources of {spec.RelativePath} answered: {string.Join("; ", tried.Select(t => $"{t.Url}: {t.Error}"))}");
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        const int BufferBytes = 1 << 20;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferBytes), cancellationToken).ConfigureAwait(false)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
            }

            return Convert.ToHexStringLower(hasher.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private ModelCatalogItem Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _items.TryGetValue(id, out var item) ? item : throw new ArgumentException($"Unknown model id '{id}'", nameof(id));
    }

    private string TargetFullPath(ModelCatalogItem item) => Path.GetFullPath(Path.Combine(ModelsDirectory, item.TargetPath));

    private string StagingRoot(ModelCatalogItem item) => Path.GetFullPath(Path.Combine(ModelsDirectory, StagingDirectoryName, item.Id));

    private string DownloadDirectory(ModelCatalogItem item) => Path.Combine(StagingRoot(item), "dl");

    private bool IsInstalled(ModelCatalogItem item)
    {
        var target = TargetFullPath(item);
        if (!item.IsDirectory)
        {
            return NonEmptyFile(target);
        }

        if (!Directory.Exists(target))
        {
            return false;
        }

        if (item.Packaging != ModelPackaging.Files)
        {
            return NonEmptyFile(Path.Combine(target, item.ExecutableName!));
        }

        return item.Files.All(file => NonEmptyFile(Path.Combine(target, file.RelativePath)));
    }

    private long InstalledBytes(ModelCatalogItem item)
    {
        var target = TargetFullPath(item);
        if (!item.IsDirectory)
        {
            return Length(target);
        }

        return Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Sum(Length);
    }

    private long PartialBytes(ModelCatalogItem item)
    {
        var dl = DownloadDirectory(item);
        long sum = 0;
        foreach (var file in item.Files)
        {
            var path = Path.Combine(dl, file.RelativePath);
            sum += Length(path) + Length(path + ".part");
        }

        return sum;
    }

    private ModelStatus BuildStatusLocked(ModelCatalogItem item)
    {
        if (_operations.TryGetValue(item.Id, out var operation))
        {
            return operation.Snapshot();
        }

        if (IsInstalled(item))
        {
            var size = InstalledBytes(item);
            return new ModelStatus(item.Info, ModelInstallState.Installed, size, size, null);
        }

        var partial = PartialBytes(item);
        long? total = _lastTotals.TryGetValue(item.Id, out var known) ? known : null;
        return _errors.TryGetValue(item.Id, out var error)
            ? new ModelStatus(item.Info, ModelInstallState.Failed, partial, total, error)
            : new ModelStatus(item.Info, ModelInstallState.NotInstalled, partial, total, null);
    }

    private void Raise(ModelStatus status)
    {
        var handler = StatusChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(status);
        }
        catch (Exception)
        {
            // A faulty subscriber must not break an install or a removal.
        }
    }

    private void RaiseProgress(Operation operation, bool force)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && Stopwatch.GetElapsedTime(operation.LastEmit, now) < _progressInterval)
        {
            return;
        }

        operation.LastEmit = now;
        Raise(operation.Snapshot());
    }

    private async Task RunAsync(Operation operation, CancellationToken callerToken)
    {
        var item = operation.Item;
        string? error = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _lifetime.Token);
        var cancellationToken = linked.Token;
        try
        {
            // An item that is already installed only gets (re)activated: no "Downloading" flicker for the UI.
            if (!IsInstalled(item))
            {
                RaiseProgress(operation, force: true);
            }

            await InstallCoreAsync(operation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // partial downloads stay for the next attempt
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Whatever broke while the install was being cancelled (e.g. the client being disposed) is a cancellation.
            throw new OperationCanceledException(cancellationToken);
        }
        catch (ModelInstallException ex)
        {
            error = ex.Message;
            throw;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            throw new ModelInstallException($"Installing '{item.Id}' failed: {ex.Message}", ex);
        }
        finally
        {
            ModelStatus status;
            lock (_gate)
            {
                _operations.Remove(item.Id); // also releases the disk space it reserved
                if (operation.KnownTotal is { } total)
                {
                    _lastTotals[item.Id] = total;
                }

                if (error is not null)
                {
                    _errors[item.Id] = error;
                }

                status = BuildStatusLocked(item);
            }

            Raise(status);
        }
    }

    private async Task InstallCoreAsync(Operation operation, CancellationToken cancellationToken)
    {
        var item = operation.Item;
        cancellationToken.ThrowIfCancellationRequested();

        if (IsInstalled(item))
        {
            Activate(item, null);
            lock (_gate)
            {
                _reports.Remove(item.Id); // nothing was downloaded this time
            }

            return;
        }

        var root = StagingRoot(item);
        var dl = DownloadDirectory(item);
        Directory.CreateDirectory(dl);

        var files = item.Files;
        var complete = new bool[files.Count];
        var chosen = new ModelMirror[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            var spec = files[i];
            var final = Path.Combine(dl, spec.RelativePath);
            var part = final + ".part";
            if (NonEmptyFile(final))
            {
                // Finished by an earlier run; which source it came from is not recorded, so assume the first.
                chosen[i] = spec.Sources[0];
                complete[i] = true;
                operation.Set(i, Length(final), Length(final));
                continue;
            }

            operation.Set(i, Length(part), -1);
            var choice = await ChooseMirrorAsync(spec, cancellationToken).ConfigureAwait(false);
            chosen[i] = choice.Mirror;

            // A part that another source started is not continued from this one.
            ModelDownloader.DiscardForeignPart(part, choice.Mirror.Url);
            operation.Set(i, Length(part), choice.Total);
        }

        EnsureFreeSpace(operation, complete);

        var reports = new List<ModelFileReport>(files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var spec = files[i];
            var final = Path.Combine(dl, spec.RelativePath);
            var part = final + ".part";
            var index = i;
            var mirror = chosen[i];

            if (!complete[i])
            {
                await _downloader.DownloadAsync(
                    mirror.Url,
                    part,
                    operation.TotalOf(i),
                    position =>
                    {
                        operation.SetDone(index, position);
                        RaiseProgress(operation, force: false);
                    },
                    total => operation.SetTotal(index, total),
                    cancellationToken).ConfigureAwait(false);
            }

            var path = complete[i] ? final : part;
            var length = Length(path);
            if (length == 0)
            {
                ModelDownloader.DiscardPart(path);
                throw new ModelInstallException($"{mirror.Url} downloaded as an empty file");
            }

            if (mirror.SizeBytes is { } wanted && length != wanted)
            {
                ModelDownloader.DiscardPart(path);
                throw new ModelInstallException($"{mirror.Url}: expected {wanted} bytes, got {length}");
            }

            string? hash = null;
            if (mirror.Sha256 is not null || _reportHashes)
            {
                hash = await Sha256Async(path, cancellationToken).ConfigureAwait(false);
                if (mirror.Sha256 is not null && !string.Equals(hash, mirror.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    ModelDownloader.DiscardPart(path);
                    throw new ModelInstallException($"{mirror.Url}: SHA-256 mismatch (expected {mirror.Sha256.ToLowerInvariant()}, got {hash})");
                }
            }

            if (!complete[i])
            {
                FileOps.MoveFile(part, final, overwrite: true);
                File.Delete(part + ".validator");
            }

            operation.Set(i, length, length);
            reports.Add(new ModelFileReport(mirror.Url, spec.RelativePath, length, hash));
        }

        // A crash between the rename of a file and the deletion of its validator leaves the validator behind: it must
        // not travel into the installed folder.
        foreach (var stray in Directory.EnumerateFiles(dl, "*.validator", SearchOption.AllDirectories))
        {
            File.Delete(stray);
        }

        await PlaceAsync(item, root, dl, cancellationToken).ConfigureAwait(false);
        Activate(item, chosen[0]);

        lock (_gate)
        {
            _reports[item.Id] = reports;
        }

        TryDeleteStaging(root);
    }

    /// <summary>
    /// Fails fast when the remaining download (plus ~4x the archive for its extraction) does not fit next to what the
    /// other running installs still need. The need is reserved on success, so parallel installs do not all see the
    /// same free space.
    /// </summary>
    private void EnsureFreeSpace(Operation operation, bool[] complete)
    {
        var item = operation.Item;
        long remaining = 0;
        var allKnown = true;
        for (var i = 0; i < complete.Length; i++)
        {
            if (complete[i])
            {
                continue;
            }

            var total = operation.TotalOf(i);
            if (total < 0)
            {
                allKnown = false;
                break;
            }

            remaining += total - operation.DoneOf(i);
        }

        long archiveBytes = 0;
        if (!allKnown)
        {
            remaining = Math.Max(0, item.Info.SizeBytes - operation.BytesDone); // only the approximate size is known
            archiveBytes = item.Packaging != ModelPackaging.Files ? item.Info.SizeBytes : 0; // only archives are extracted
        }
        else if (item.Packaging != ModelPackaging.Files)
        {
            archiveBytes = operation.TotalOf(0);
        }

        remaining += 4 * archiveBytes; // extracted copy plus the staged folder

        lock (_gate)
        {
            long others = 0;
            foreach (var other in _operations.Values)
            {
                if (!ReferenceEquals(other, operation))
                {
                    others += other.ReservedRemaining;
                }
            }

            var free = _freeSpace(ModelsDirectory);
            if (free < remaining + others)
            {
                throw new ModelInstallException(
                    $"Not enough disk space for '{item.Id}': {remaining} bytes needed (+{others} reserved by other installs), {free} free in {ModelsDirectory}");
            }

            operation.Reserve(remaining);
        }
    }

    /// <summary>Moves the complete download into its final place; the previous content stays until the very last step.</summary>
    private async Task PlaceAsync(ModelCatalogItem item, string root, string dl, CancellationToken cancellationToken)
    {
        var target = TargetFullPath(item);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        if (item.Packaging == ModelPackaging.Files)
        {
            if (item.IsDirectory)
            {
                ReplaceDirectory(dl, target, Path.Combine(root, "old"));
            }
            else
            {
                FileOps.MoveFile(Path.Combine(dl, item.Files[0].RelativePath), target, overwrite: true);
            }

            return;
        }

        var archive = Path.Combine(dl, item.Files[0].RelativePath);
        var extract = Path.Combine(root, "x");
        if (Directory.Exists(extract))
        {
            Directory.Delete(extract, recursive: true);
        }

        string serverDirectory;
        try
        {
            serverDirectory = await ArchiveExtractor.ExtractAsync(archive, item.Packaging, item.ExecutableName!, extract, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A broken archive would fail the same way again: download it afresh next time. The clean-up must not
            // hide the original error.
            try
            {
                ModelDownloader.DiscardPart(archive);
                Directory.Delete(extract, recursive: true);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }

        ArchiveExtractor.MakeExecutable(Path.Combine(serverDirectory, item.ExecutableName!));
        var ready = Path.Combine(root, "ready");
        if (Directory.Exists(ready))
        {
            Directory.Delete(ready, recursive: true);
        }

        FileOps.MoveDirectory(serverDirectory, ready);
        ReplaceDirectory(ready, target, Path.Combine(root, "old"));
    }

    private static void ReplaceDirectory(string source, string target, string backup)
    {
        if (Directory.Exists(backup))
        {
            Directory.Delete(backup, recursive: true);
        }

        var hadOld = Directory.Exists(target);
        if (hadOld)
        {
            FileOps.MoveDirectory(target, backup);
        }

        try
        {
            FileOps.MoveDirectory(source, target);
        }
        catch
        {
            if (hadOld && !Directory.Exists(target))
            {
                FileOps.MoveDirectory(backup, target);
            }

            throw;
        }
    }

    /// <summary>Makes a chat or embedding model the active one; <paramref name="source"/> is the mirror it came from, if known.</summary>
    private void Activate(ModelCatalogItem item, ModelMirror? source)
    {
        if (item.Info.Kind is ModelKind.Chat or ModelKind.Embedding)
        {
            _manifest.SetActive(
                item.Info.Kind,
                TargetFullPath(item),
                source?.Repo ?? item.Repo,
                source?.FileName ?? Path.GetFileName(item.TargetPath),
                keepRecordedSource: source is null);
        }
    }

    private void TryDeleteStaging(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
            var staging = Path.GetDirectoryName(root)!;
            if (!Directory.EnumerateFileSystemEntries(staging).Any())
            {
                Directory.Delete(staging);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftovers are harmless: they are cleaned up by the next install or removal of the item.
        }
    }

    private void DeleteItem(ModelCatalogItem item)
    {
        var target = TargetFullPath(item);
        var root = StagingRoot(item);
        try
        {
            // Step 1 is a move into the item's trash: atomic, and it fails (changing nothing) when something such as a
            // running llama-server.exe holds the folder, instead of leaving it half deleted.
            if (item.IsDirectory ? Directory.Exists(target) : File.Exists(target))
            {
                var trash = Path.Combine(root, "trash");
                Directory.CreateDirectory(root);
                if (Directory.Exists(trash))
                {
                    Directory.Delete(trash, recursive: true);
                }

                Directory.CreateDirectory(trash);
                if (item.IsDirectory)
                {
                    FileOps.MoveDirectory(target, Path.Combine(trash, "item"));
                }
                else
                {
                    FileOps.MoveFile(target, Path.Combine(trash, "item"), overwrite: true);
                }
            }

            // The item is gone from its place now: the manifest must not name it any longer.
            if (item.Info.Kind is ModelKind.Chat or ModelKind.Embedding)
            {
                _manifest.ClearIfActive(item.Info.Kind, target);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModelInstallException($"Removing '{item.Id}' failed: {ex.Message}", ex);
        }

        // Housekeeping after a successful removal: a concurrent install may be creating its own folder in there.
        try
        {
            var staging = Path.GetDirectoryName(root)!;
            if (Directory.Exists(staging) && !Directory.EnumerateFileSystemEntries(staging).Any())
            {
                Directory.Delete(staging);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        lock (_gate)
        {
            _lastTotals.Remove(item.Id);
        }
    }

    /// <summary>One running install: progress counters shared between the install flow and <see cref="GetStatus"/>.</summary>
    private sealed class Operation(ModelCatalogItem item)
    {
        private readonly long[] _done = new long[item.Files.Count];
        private readonly long[] _total = [.. Enumerable.Repeat(-1L, item.Files.Count)];

        public ModelCatalogItem Item { get; } = item;

        public Task Task { get; set; } = Task.CompletedTask;

        /// <summary>Timestamp of the last event; touched only by the install flow.</summary>
        public long LastEmit { get; set; }

        public long BytesDone
        {
            get
            {
                long sum = 0;
                for (var i = 0; i < _done.Length; i++)
                {
                    sum += Volatile.Read(ref _done[i]);
                }

                return sum;
            }
        }

        public long? KnownTotal
        {
            get
            {
                long sum = 0;
                for (var i = 0; i < _total.Length; i++)
                {
                    var total = Volatile.Read(ref _total[i]);
                    if (total < 0)
                    {
                        return null;
                    }

                    sum += total;
                }

                return sum;
            }
        }

        private long _reserved;
        private long _reservedAtBytes;

        /// <summary>Bytes this install still needs on disk, as reserved when it started (shrinks as it downloads).</summary>
        public long ReservedRemaining => Math.Max(0, Volatile.Read(ref _reserved) - Math.Max(0, BytesDone - Volatile.Read(ref _reservedAtBytes)));

        public void Reserve(long bytes)
        {
            Volatile.Write(ref _reservedAtBytes, BytesDone);
            Volatile.Write(ref _reserved, bytes);
        }

        public long TotalOf(int index) => Volatile.Read(ref _total[index]);

        public long DoneOf(int index) => Volatile.Read(ref _done[index]);

        public void Set(int index, long done, long total)
        {
            Volatile.Write(ref _done[index], done);
            Volatile.Write(ref _total[index], total);
        }

        public void SetDone(int index, long done) => Volatile.Write(ref _done[index], done);

        public void SetTotal(int index, long total) => Volatile.Write(ref _total[index], total);

        public ModelStatus Snapshot() => new(Item.Info, ModelInstallState.Downloading, BytesDone, KnownTotal, null);
    }
}
