using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Murmur.Core.Calls;

namespace Murmur.Capture.Windows.Audio;

/// <summary>
/// Process names of the rendering processes, cached by process id AND start time: Windows reuses process ids, so a name cached for
/// a process that has exited must never be reported for the unrelated process that got its id (a toast saying "Teams" for a Meet
/// call in Chrome, or an auto-recording started for the wrong call). Used by one thread only.
/// </summary>
internal sealed class ProcessNameCache
{
    private readonly Dictionary<int, Entry> _entries = [];
    private readonly Func<int, long> _startOf;
    private readonly Func<int, string> _nameOf;

    public ProcessNameCache()
        : this(ProcessStartTicks, ProcessName)
    {
    }

    /// <param name="startOf">The start time of a process in ticks (a different value for a reused id); 0 or less when unknown.</param>
    /// <param name="nameOf">The process name of a process id.</param>
    public ProcessNameCache(Func<int, long> startOf, Func<int, string> nameOf)
    {
        _startOf = startOf;
        _nameOf = nameOf;
    }

    public int Count => _entries.Count;

    /// <summary>The name of the process; resolved again when the id now belongs to a process with another start time.</summary>
    public string NameOf(int processId)
    {
        var start = _startOf(processId);
        if (start > 0 && _entries.TryGetValue(processId, out var entry) && entry.Start == start)
        {
            return entry.Name;
        }

        var name = _nameOf(processId);
        if (start > 0)
        {
            _entries[processId] = new Entry(start, name);
        }
        else
        {
            _entries.Remove(processId); // the start time is unknown (protected or gone): never trust an old name
        }

        return name;
    }

    /// <summary>Drops every cached process that is not rendering any more (its id may be reused by another process).</summary>
    public void Retain(IReadOnlyList<RenderingProcess> rendering)
    {
        if (_entries.Count == 0)
        {
            return;
        }

        List<int>? gone = null;
        foreach (var processId in _entries.Keys)
        {
            var found = false;
            foreach (var process in rendering)
            {
                if (process.ProcessId == processId)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                (gone ??= []).Add(processId);
            }
        }

        if (gone is not null)
        {
            foreach (var processId in gone)
            {
                _entries.Remove(processId);
            }
        }
    }

    private static long ProcessStartTicks(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return 0;
        }
    }

    private static string ProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return "process " + processId.ToString(CultureInfo.InvariantCulture);
        }
    }

    private readonly record struct Entry(long Start, string Name);
}
