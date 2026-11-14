namespace Murmur.Storage;

/// <summary>
/// Checks that the configured recordings folder can be used, without letting an unreachable network share (UNC / SMB)
/// hold the start of a recording for the SMB timeout. The file-system work runs on its own thread; the caller waits for
/// it at most <see cref="Timeout"/> and otherwise reports the folder as unusable so the default folder is used.
/// </summary>
internal sealed class RecordingsFolderProbe
{
    /// <summary>How long a recording start waits for the configured folder before falling back to the default one.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The reason reported when the folder did not answer in time. Names no path, so it is safe to log.</summary>
    internal const string TimedOutReason = "The recordings folder did not respond in time.";

    private readonly Func<string, string> _prepare;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private string? _pendingFolder;
    private Task<Outcome>? _pending;

    /// <param name="prepare">
    /// Blocking file-system work: creates the folder when missing and returns its full path; throws when it is unusable.
    /// Tests replace it with one that blocks or fails.
    /// </param>
    /// <param name="time">Clock of the bounded wait.</param>
    /// <param name="timeout">The bound; <see cref="DefaultTimeout"/> when <c>null</c>.</param>
    internal RecordingsFolderProbe(Func<string, string> prepare, TimeProvider time, TimeSpan? timeout = null)
    {
        _prepare = prepare;
        _time = time;
        Timeout = timeout ?? DefaultTimeout;
    }

    internal TimeSpan Timeout { get; }

    /// <summary>The production probe: the folder must be absolute and creatable.</summary>
    internal static RecordingsFolderProbe CreateDefault() => new(PrepareOnDisk, TimeProvider.System);

    /// <summary>
    /// Returns <c>null</c> on success (the existing folder in <paramref name="parent"/>), otherwise why the folder is unusable
    /// (<paramref name="parent"/> is then <c>null</c>). Never throws.
    /// While an earlier probe of the same folder is still stuck, it is waited for again instead of starting another blocked
    /// thread; a finished probe is never reused, so a later recording asks the folder anew.
    /// </summary>
    internal string? Probe(string folder, out string? parent)
    {
        parent = null;
        var task = Start(folder);
        try
        {
            var outcome = task.WaitAsync(Timeout, _time).GetAwaiter().GetResult();
            Finish(task);
            parent = outcome.Parent;
            return outcome.Failure;
        }
        catch (TimeoutException)
        {
            // The probe stays queued behind the share; it only ever ends in an Outcome (see Run), so nothing is left unobserved.
            return TimedOutReason;
        }
    }

    private Task<Outcome> Start(string folder)
    {
        lock (_gate)
        {
            if (_pending is { IsCompleted: false } running && string.Equals(_pendingFolder, folder, StringComparison.Ordinal))
            {
                return running;
            }

            _pendingFolder = folder;
            return _pending = Task.Factory.StartNew(
                () => Run(folder), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    private void Finish(Task<Outcome> task)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_pending, task))
            {
                _pending = null;
                _pendingFolder = null;
            }
        }
    }

    // Catches everything: an abandoned probe cannot fault later, and a surprising exception is just "unusable folder".
    private Outcome Run(string folder)
    {
        try
        {
            return new Outcome(_prepare(folder), null);
        }
        catch (Exception ex)
        {
            return new Outcome(null, ex.Message);
        }
    }

    private static string PrepareOnDisk(string folder)
    {
        if (!Path.IsPathFullyQualified(folder))
        {
            throw new ArgumentException("The recordings folder is not an absolute path.");
        }

        var full = Path.GetFullPath(folder);
        Directory.CreateDirectory(full);
        return full;
    }

    private readonly record struct Outcome(string? Parent, string? Failure);
}
