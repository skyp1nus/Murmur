using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Murmur.Core.Diagnostics;

namespace Murmur.Storage;

/// <summary>
/// The <see cref="IAppLog"/> of the app, in <see cref="StoragePaths.LogsDirectory"/> (<c>%LOCALAPPDATA%\Murmur\logs</c>).
/// </summary>
/// <remarks>
/// <para><b>Memory first.</b> Every event goes into a preallocated ring of <see cref="RingCapacity"/> entries (the strings the caller
/// passed, by reference, and the time): an <see cref="LogLevel.Info"/> event costs one short lock and allocates nothing, and a healthy
/// session never touches the disk.</para>
/// <para><b>Warnings and errors</b> are also queued and appended to the daily file <c>murmur-yyyy-MM-dd.log</c> by a timer of the
/// <see cref="TimeProvider"/>, at most once per <see cref="BatchInterval"/> (the first one right away), and by <see cref="Flush"/>; never on
/// the caller's thread. One short line per event: <c>2026-11-13T12:00:00Z WARN capture screen.fault screen-stopped</c>, the detail scrubbed
/// (<see cref="LogText.Scrub"/>). More than <see cref="MaxPending"/> events between two appends are counted, not kept.</para>
/// <para><b>Crashes</b> (<see cref="Crash"/>) write <c>crash-yyyyMMdd-HHmmss-fff.txt</c> at once: the exception (type, scrubbed message,
/// stack), the versions, the caller's context lines and the ring. A crash file newer than the last clean exit or dismissal
/// (<see cref="MarkCleanExit"/>, <see cref="MarkCrashSeen"/>: the name in <c>last-exit</c>, written only when a crash file exists) is the
/// next start's <see cref="UnseenCrash"/>.</para>
/// <para><b>Limits.</b> A file holds at most <see cref="MaxFileBytes"/> (then <c>murmur-yyyy-MM-dd.2.log</c> and so on), the folder at most
/// <see cref="MaxFolderBytes"/> (the oldest file goes first), and files older than <see cref="MaxAge"/> are deleted once at start.</para>
/// </remarks>
public sealed class FileAppLog : IAppLog, IDisposable
{
    public const int RingCapacity = 256;
    public const long MaxFileBytes = 256 * 1024;
    public const long MaxFolderBytes = 2 * 1024 * 1024;
    internal const int MaxPending = 256;
    internal const string MarkerFileName = "last-exit";
    internal const string LogPrefix = "murmur-";
    internal const string LogExtension = ".log";
    internal const string CrashPrefix = "crash-";
    internal const string CrashExtension = ".txt";
    private const string CrashTimeFormat = "yyyyMMdd-HHmmss-fff";
    private const int MaxSnapshotCrashChars = 64 * 1024;
    private const int MaxMessageChars = 500;
    private const int MaxFrameChars = 300;
    private const int MaxExceptionDepth = 5;
    private const int MaxInnerPerLevel = 8;
    private const string Truncated = "... (cut to the file size limit)\n";

    public static readonly TimeSpan BatchInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly ILogFileSystem _files;
    private readonly Func<string>? _context;
    private readonly ITimer _timer;

    // Guarded by _gate (short, never across I/O): the ring, the queue of warnings and the timer's state.
    private readonly Lock _gate = new();
    private readonly Entry[] _ring = new Entry[RingCapacity];
    private readonly Entry[] _pending = new Entry[MaxPending];
    private long _written;
    private int _pendingCount;
    private int _dropped;
    private bool _scheduled;
    private bool _disposed;
    private long _lastAppendTicks = long.MinValue;

    // Guarded by _io: everything that touches the disk, and what the log knows of the folder.
    private readonly Lock _io = new();
    private string? _marker;
    private string? _newestCrash;
    private volatile string? _unseenCrash;
    private Exception? _lastCrash;

    /// <param name="paths">The data folder; the log lives in its <see cref="StoragePaths.LogsDirectory"/>.</param>
    /// <param name="time">The clock of the lines and of the batching.</param>
    /// <param name="context">
    /// Lines a crash file and the diagnostics add after the versions (the app version, the main settings flags); called only then, and
    /// a failure is ignored. No user content.
    /// </param>
    public FileAppLog(StoragePaths paths, TimeProvider time, Func<string>? context = null)
        : this(NotNull(paths).LogsDirectory, time, PhysicalLogFileSystem.Instance, context)
    {
    }

    internal FileAppLog(string directory, TimeProvider time, ILogFileSystem files, Func<string>? context = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(files);
        _directory = directory;
        _time = time;
        _files = files;
        _context = context;
        _timer = time.CreateTimer(static state => ((FileAppLog)state!).OnTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Start();
    }

    public string Directory => _directory;

    /// <remarks>Read without the I/O lock (the UI reads it while a batch may be on its way to the disk).</remarks>
    public string? UnseenCrash => _unseenCrash;

    /// <summary>Appends written so far (for tests).</summary>
    internal int Appends { get; private set; }

    /// <summary>Puts the event in the ring; a warning or an error is also queued for the next batched append. Never touches the disk.</summary>
    public void Write(LogLevel level, string area, string code, string? detail = null)
    {
        try
        {
            var entry = new Entry(_time.GetUtcNow().UtcTicks, level, area ?? string.Empty, code ?? string.Empty, detail);
            lock (_gate)
            {
                _ring[(int)(_written % RingCapacity)] = entry;
                _written++;
                if (level == LogLevel.Info)
                {
                    return;
                }

                if (_pendingCount < MaxPending)
                {
                    _pending[_pendingCount++] = entry;
                }
                else
                {
                    _dropped++;
                }

                if (!_scheduled && !_disposed)
                {
                    _scheduled = true;
                    var due = _lastAppendTicks == long.MinValue
                        ? TimeSpan.Zero
                        // Clamped: a wall clock set back must not hold the batch for longer than one interval.
                        : TimeSpan.FromTicks(Math.Clamp(_lastAppendTicks + BatchInterval.Ticks - entry.UtcTicks, 0, BatchInterval.Ticks));
                    _timer.Change(due, Timeout.InfiniteTimeSpan);
                }
            }
        }
        catch (Exception)
        {
            // The log never breaks its caller.
        }
    }

    public void Crash(Exception exception, string area)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            // The UI thread's handler and the process's handler see the same exception: one crash file.
            if (ReferenceEquals(Interlocked.Exchange(ref _lastCrash, exception), exception))
            {
                return;
            }

            Write(LogLevel.Error, area, "crash", LogText.Error(exception));
            lock (_io)
            {
                var now = _time.GetUtcNow();
                var name = CrashFileName(now);
                _files.Write(Path.Combine(_directory, name), BuildCrashText(exception, area, now));
                _newestCrash = Newer(_newestCrash, name);
                AppendPending(keep: name);
            }
        }
        catch (Exception)
        {
            // Nothing more can be done for a crash that cannot be written.
        }
    }

    public void Flush()
    {
        try
        {
            lock (_io)
            {
                AppendPending(keep: null);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// The app ends normally: flushes the warnings and, when the folder holds a crash file, records the newest one as seen (so the next
    /// start shows no notice). A folder without crash files gets nothing.
    /// </summary>
    public void MarkCleanExit()
    {
        Flush();
        try
        {
            lock (_io)
            {
                if (_newestCrash is { } newest && IsNewer(newest, _marker))
                {
                    WriteMarker(newest);
                }
            }
        }
        catch (Exception)
        {
        }
    }

    public void MarkCrashSeen()
    {
        try
        {
            lock (_io)
            {
                if (_unseenCrash is null)
                {
                    return;
                }

                var name = Path.GetFileName(_unseenCrash);
                _unseenCrash = null;
                WriteMarker(Newer(name, _marker)!);
            }
        }
        catch (Exception)
        {
            // Hidden for this run anyway.
        }
    }

    public string Snapshot()
    {
        try
        {
            var now = _time.GetUtcNow();
            var text = new StringBuilder();
            text.Append("Murmur diagnostics ").Append(FormatTime(now.UtcTicks)).Append('\n');
            AppendHeader(text);
            text.Append('\n');
            AppendRing(text);

            string? crash;
            lock (_io)
            {
                crash = _newestCrash;
            }

            if (crash is not null && _files.Read(Path.Combine(_directory, crash), MaxSnapshotCrashChars) is { } content)
            {
                text.Append('\n').Append("Last crash: ").Append(crash).Append('\n').Append(content);
            }
            else
            {
                text.Append('\n').Append("No crash file.\n");
            }

            return text.ToString();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _timer.Dispose();
    }

    /// <summary>One line of the log file, as it is written.</summary>
    internal static string FormatLine(long utcTicks, LogLevel level, string area, string code, string? detail)
    {
        var line = new StringBuilder(64);
        AppendLine(line, new Entry(utcTicks, level, area, code, detail));
        return line.ToString();
    }

    private static StoragePaths NotNull(StoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths;
    }

    /// <summary>Once, at start: drops what is older than <see cref="MaxAge"/>, keeps the folder under its cap and finds an unseen crash.</summary>
    private void Start()
    {
        try
        {
            lock (_io)
            {
                var files = _files.List(_directory);
                if (files.Count == 0)
                {
                    return;
                }

                var oldest = _time.GetUtcNow() - MaxAge;
                var kept = new List<LogFileEntry>(files.Count);
                foreach (var file in files)
                {
                    if (string.Equals(file.Name, MarkerFileName, StringComparison.Ordinal))
                    {
                        _marker = (_files.Read(Path.Combine(_directory, MarkerFileName), 256) ?? string.Empty).Trim();
                    }
                    else if (IsOurs(file.Name) && file.LastWriteUtc < oldest)
                    {
                        TryDelete(file.Name);
                    }
                    else
                    {
                        kept.Add(file);
                    }
                }

                EnforceFolderCap(kept, keep: null);
                foreach (var file in kept)
                {
                    if (IsCrash(file.Name))
                    {
                        _newestCrash = Newer(_newestCrash, file.Name);
                    }
                }

                if (_newestCrash is { } newest && IsNewer(newest, _marker))
                {
                    _unseenCrash = Path.Combine(_directory, newest);
                }
            }
        }
        catch (Exception)
        {
            // A folder that cannot be read: no notice, nothing pruned.
        }
    }

    private void OnTimer()
    {
        try
        {
            lock (_io)
            {
                AppendPending(keep: null);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Takes the queued warnings and appends them as one write. Called under <see cref="_io"/>.</summary>
    private void AppendPending(string? keep)
    {
        Entry[] batch;
        int count;
        int dropped;
        long now;
        lock (_gate)
        {
            _scheduled = false;
            count = _pendingCount;
            dropped = _dropped;
            if (count == 0 && dropped == 0)
            {
                return;
            }

            now = _time.GetUtcNow().UtcTicks;
            batch = new Entry[count];
            Array.Copy(_pending, batch, count);
            Array.Clear(_pending, 0, count);
            _pendingCount = 0;
            _dropped = 0;
            _lastAppendTicks = now;
        }

        var text = new StringBuilder(count * 80);
        foreach (var entry in batch)
        {
            AppendLine(text, entry);
        }

        if (dropped > 0)
        {
            AppendLine(text, new Entry(now, LogLevel.Warning, "log", "dropped", dropped.ToString(CultureInfo.InvariantCulture)));
        }

        var content = text.ToString();
        var bytes = Encoding.UTF8.GetByteCount(content);
        var files = _files.List(_directory);
        var name = DailyFileName(files, new DateTimeOffset(now, TimeSpan.Zero), bytes);
        _files.Append(Path.Combine(_directory, name), content);
        Appends++;

        var after = new List<LogFileEntry>(files.Count + 1);
        var found = false;
        foreach (var file in files)
        {
            if (string.Equals(file.Name, name, StringComparison.Ordinal))
            {
                after.Add(file with { Length = file.Length + bytes, LastWriteUtc = new DateTimeOffset(now, TimeSpan.Zero) });
                found = true;
            }
            else
            {
                after.Add(file);
            }
        }

        if (!found)
        {
            after.Add(new LogFileEntry(name, bytes, new DateTimeOffset(now, TimeSpan.Zero)));
        }

        EnforceFolderCap(after, keep ?? name, name);
    }

    /// <summary>Today's file with room for <paramref name="bytes"/>: <c>murmur-2026-11-13.log</c>, then <c>.2.log</c>, <c>.3.log</c>...</summary>
    private static string DailyFileName(IReadOnlyList<LogFileEntry> files, DateTimeOffset now, int bytes)
    {
        var day = LogPrefix + now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        for (var part = 1; ; part++)
        {
            var name = part == 1 ? day + LogExtension : day + "." + part.ToString(CultureInfo.InvariantCulture) + LogExtension;
            var length = 0L;
            foreach (var file in files)
            {
                if (string.Equals(file.Name, name, StringComparison.Ordinal))
                {
                    length = file.Length;
                    break;
                }
            }

            // An empty file takes any batch (a batch is far below the cap: MaxPending short lines).
            if (length == 0 || length + bytes <= MaxFileBytes)
            {
                return name;
            }
        }
    }

    /// <summary>Deletes the oldest of our files until the folder is under <see cref="MaxFolderBytes"/>; the files just written stay.</summary>
    private void EnforceFolderCap(List<LogFileEntry> files, string? keep, string? alsoKeep = null)
    {
        var total = 0L;
        foreach (var file in files)
        {
            total += IsOurs(file.Name) ? file.Length : 0;
        }

        if (total <= MaxFolderBytes)
        {
            return;
        }

        files.Sort(static (a, b) =>
        {
            var byTime = a.LastWriteUtc.CompareTo(b.LastWriteUtc);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.Name, b.Name);
        });
        for (var i = 0; i < files.Count && total > MaxFolderBytes; i++)
        {
            var file = files[i];
            if (!IsOurs(file.Name)
                || string.Equals(file.Name, keep, StringComparison.Ordinal)
                || string.Equals(file.Name, alsoKeep, StringComparison.Ordinal))
            {
                continue;
            }

            if (TryDelete(file.Name))
            {
                total -= file.Length;
                files.RemoveAt(i--);
                if (string.Equals(file.Name, _newestCrash, StringComparison.Ordinal))
                {
                    _newestCrash = NewestCrash(files);
                }
            }
        }
    }

    private bool TryDelete(string name)
    {
        try
        {
            _files.Delete(Path.Combine(_directory, name));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void WriteMarker(string crashName)
    {
        _files.Write(Path.Combine(_directory, MarkerFileName), crashName);
        _marker = crashName;
    }

    /// <summary>
    /// A name that sorts after every crash file of this folder and after the one marked seen: the time to the millisecond, or, when the
    /// wall clock was set back (or two crashes share a millisecond), one millisecond after the newest known name, so a new crash is never
    /// taken for one already seen.
    /// </summary>
    private string CrashFileName(DateTimeOffset now)
    {
        var name = CrashName(now.UtcDateTime);
        var floor = Newer(_newestCrash, _marker);
        if (floor is null || IsNewer(name, floor))
        {
            return name;
        }

        return TryParseCrashTime(floor, out var at) ? CrashName(at.AddMilliseconds(1)) : name;
    }

    private static string CrashName(DateTime at) => CrashPrefix + at.ToString(CrashTimeFormat, CultureInfo.InvariantCulture) + CrashExtension;

    private static bool TryParseCrashTime(string name, out DateTime at)
    {
        at = default;
        if (!IsCrash(name) || name.Length != CrashPrefix.Length + CrashTimeFormat.Length + CrashExtension.Length)
        {
            return false;
        }

        return DateTime.TryParseExact(
            name.AsSpan(CrashPrefix.Length, CrashTimeFormat.Length),
            CrashTimeFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out at);
    }

    /// <summary>
    /// The crash file: header, exception, ring. At most <see cref="MaxFileBytes"/> of UTF-8: the exception part gives way first (the ring
    /// and the header are bounded), and the whole is cut as a last resort.
    /// </summary>
    internal string BuildCrashText(Exception exception, string area, DateTimeOffset now)
    {
        var head = new StringBuilder(1024);
        head.Append("Murmur crash ").Append(FormatTime(now.UtcTicks)).Append('\n');
        head.Append("Area: ").Append(area).Append('\n');
        AppendHeader(head);
        head.Append('\n').Append("Exception:\n");

        var error = new StringBuilder(16 * 1024);
        AppendException(error, exception, depth: 0);

        var ring = new StringBuilder(32 * 1024);
        ring.Append('\n');
        AppendRing(ring);

        var headText = head.ToString();
        var ringText = ring.ToString();
        var budget = (int)MaxFileBytes - Encoding.UTF8.GetByteCount(headText) - Encoding.UTF8.GetByteCount(ringText) - Truncated.Length;
        var errorText = error.ToString();
        if (Encoding.UTF8.GetByteCount(errorText) > Math.Max(0, budget))
        {
            errorText = CutToBytes(errorText, Math.Max(0, budget)) + Truncated;
        }

        var text = headText + errorText + ringText;
        return Encoding.UTF8.GetByteCount(text) <= MaxFileBytes ? text : CutToBytes(text, (int)MaxFileBytes - Truncated.Length) + Truncated;
    }

    /// <summary>The longest start of <paramref name="text"/> that takes at most <paramref name="maxBytes"/> of UTF-8 (no surrogate pair split).</summary>
    private static string CutToBytes(string text, int maxBytes)
    {
        if (maxBytes <= 0)
        {
            return string.Empty;
        }

        var length = Math.Min(text.Length, maxBytes);
        while (length > 0 && Encoding.UTF8.GetByteCount(text.AsSpan(0, length)) > maxBytes)
        {
            length = Math.Max(0, length - Math.Max(1, (Encoding.UTF8.GetByteCount(text.AsSpan(0, length)) - maxBytes) / 3));
        }

        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }

    private void AppendHeader(StringBuilder text)
    {
        text.Append("OS: ").Append(RuntimeInformation.OSDescription).Append(" (").Append(Environment.OSVersion.VersionString).Append(")\n");
        text.Append("Runtime: ").Append(RuntimeInformation.FrameworkDescription).Append(' ').Append(RuntimeInformation.ProcessArchitecture.ToString()).Append('\n');
        if (_context is not null)
        {
            try
            {
                var context = _context();
                if (!string.IsNullOrWhiteSpace(context))
                {
                    text.Append(context.TrimEnd()).Append('\n');
                }
            }
            catch (Exception)
            {
                // The context is a bonus.
            }
        }
    }

    private void AppendRing(StringBuilder text)
    {
        var entries = new Entry[RingCapacity];
        int count;
        lock (_gate)
        {
            count = (int)Math.Min(_written, RingCapacity);
            var first = _written - count;
            for (var i = 0; i < count; i++)
            {
                entries[i] = _ring[(int)((first + i) % RingCapacity)];
            }
        }

        text.Append("Recent events (oldest first):\n");
        for (var i = 0; i < count; i++)
        {
            AppendLine(text, entries[i]);
        }
    }

    private static void AppendException(StringBuilder text, Exception exception, int depth)
    {
        // Past the file limit nothing more can be kept: BuildCrashText cuts there anyway.
        if (text.Length > MaxFileBytes)
        {
            return;
        }

        var indent = new string(' ', depth * 2);
        // An AggregateException's message is every inner message joined (built anew, the whole tree, on each read): its inner exceptions
        // are listed below instead.
        text.Append(indent).Append(LogText.Error(exception)).Append(": ");
        if (exception is AggregateException { InnerExceptions.Count: var count })
        {
            text.Append(count.ToString(CultureInfo.InvariantCulture)).Append(" inner exceptions");
        }
        else
        {
            text.Append(LogText.Scrub(exception.Message, MaxMessageChars));
        }

        text.Append('\n');
        if (exception.StackTrace is { Length: > 0 } stack)
        {
            foreach (var frame in stack.Split('\n'))
            {
                if (frame.Trim() is { Length: > 0 } line)
                {
                    text.Append(indent).Append("  ").Append(LogText.Scrub(line, MaxFrameChars)).Append('\n');
                }
            }
        }

        if (depth >= MaxExceptionDepth)
        {
            return;
        }

        if (exception is AggregateException aggregate)
        {
            var shown = Math.Min(aggregate.InnerExceptions.Count, MaxInnerPerLevel);
            for (var i = 0; i < shown; i++)
            {
                text.Append(indent).Append("Inner:\n");
                AppendException(text, aggregate.InnerExceptions[i], depth + 1);
            }

            if (aggregate.InnerExceptions.Count > shown)
            {
                text.Append(indent).Append("... ").Append((aggregate.InnerExceptions.Count - shown).ToString(CultureInfo.InvariantCulture)).Append(" more inner exceptions\n");
            }
        }
        else if (exception.InnerException is { } inner)
        {
            text.Append(indent).Append("Inner:\n");
            AppendException(text, inner, depth + 1);
        }
    }

    private static void AppendLine(StringBuilder text, Entry entry)
    {
        text.Append(FormatTime(entry.UtcTicks)).Append(' ').Append(LevelName(entry.Level)).Append(' ');
        AppendToken(text, entry.Area);
        text.Append(' ');
        AppendToken(text, entry.Code);
        if (LogText.Scrub(entry.Detail) is { Length: > 0 } detail)
        {
            text.Append(' ').Append(detail);
        }

        text.Append('\n');
    }

    /// <summary>An area or a code: one word, whatever the caller passed.</summary>
    private static void AppendToken(StringBuilder text, string token)
    {
        if (token.Length == 0)
        {
            text.Append('-');
            return;
        }

        foreach (var c in token.AsSpan(0, Math.Min(64, token.Length)))
        {
            text.Append(char.IsWhiteSpace(c) ? '_' : c);
        }
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Info => "INFO",
        LogLevel.Warning => "WARN",
        _ => "ERROR",
    };

    private static string FormatTime(long utcTicks) =>
        new DateTime(utcTicks, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static bool IsOurs(string name) => IsCrash(name)
        || (name.StartsWith(LogPrefix, StringComparison.Ordinal) && name.EndsWith(LogExtension, StringComparison.Ordinal));

    private static bool IsCrash(string name) =>
        name.StartsWith(CrashPrefix, StringComparison.Ordinal) && name.EndsWith(CrashExtension, StringComparison.Ordinal);

    /// <summary>Crash names sort by time (a fixed-width UTC stamp).</summary>
    private static bool IsNewer(string name, string? than) => than is null || string.CompareOrdinal(name, than) > 0;

    private static string? Newer(string? a, string? b) => a is null ? b : b is null ? a : string.CompareOrdinal(a, b) >= 0 ? a : b;

    private static string? NewestCrash(List<LogFileEntry> files)
    {
        string? newest = null;
        foreach (var file in files)
        {
            if (IsCrash(file.Name))
            {
                newest = Newer(newest, file.Name);
            }
        }

        return newest;
    }

    private readonly record struct Entry(long UtcTicks, LogLevel Level, string Area, string Code, string? Detail);
}
