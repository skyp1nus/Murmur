using System.Collections;

namespace Murmur.Core.Settings;

/// <summary>
/// A read-only list of process names with value equality (case-insensitive, in order), so a record that holds one, such as
/// <see cref="AppSettings.CallAllowlist"/>, stays equal to its copy read back from a file. Names are normalized when the list is
/// built: trimmed, without a trailing <c>.exe</c>, blanks and case-insensitive duplicates dropped (the first spelling stays).
/// </summary>
public sealed class ProcessNameList : IReadOnlyList<string>, IEquatable<ProcessNameList>
{
    private readonly string[] _names;

    private ProcessNameList(string[] names) => _names = names;

    /// <summary>The empty list.</summary>
    public static ProcessNameList Empty { get; } = new([]);

    public int Count => _names.Length;

    public string this[int index] => _names[index];

    /// <summary>The normalized list of <paramref name="names"/>; <c>null</c> or nothing usable gives <see cref="Empty"/>.</summary>
    public static ProcessNameList From(IEnumerable<string?>? names)
    {
        if (names is ProcessNameList list)
        {
            return list;
        }

        if (names is null)
        {
            return Empty;
        }

        var kept = new List<string>();
        foreach (var name in names)
        {
            if (name is null)
            {
                continue;
            }

            var normalized = Normalize(name);
            if (normalized.Length > 0 && !Contains(kept, normalized))
            {
                kept.Add(normalized);
            }
        }

        return kept.Count == 0 ? Empty : new ProcessNameList([.. kept]);
    }

    /// <summary>Whether <paramref name="processName"/> is in the list (case-insensitive, <c>.exe</c> ignored).</summary>
    public bool Contains(string? processName) => processName is not null && Contains(_names, Normalize(processName));

    /// <summary>The list with <paramref name="processName"/> added at the end (the same list when it is already there).</summary>
    public ProcessNameList Add(string processName)
    {
        ArgumentNullException.ThrowIfNull(processName);
        return Contains(processName) ? this : From([.. _names, processName]);
    }

    /// <summary>The list without <paramref name="processName"/> (the same list when it is not there).</summary>
    public ProcessNameList Remove(string processName)
    {
        ArgumentNullException.ThrowIfNull(processName);
        if (!Contains(processName))
        {
            return this;
        }

        var normalized = Normalize(processName);
        var kept = new List<string>(_names.Length);
        foreach (var name in _names)
        {
            if (!string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(name);
            }
        }

        return From(kept);
    }

    public bool Equals(ProcessNameList? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other._names.Length != _names.Length)
        {
            return false;
        }

        for (var i = 0; i < _names.Length; i++)
        {
            if (!string.Equals(_names[i], other._names[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is ProcessNameList other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var name in _names)
        {
            hash.Add(name, StringComparer.OrdinalIgnoreCase);
        }

        return hash.ToHashCode();
    }

    public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)_names).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => string.Join(", ", _names);

    private static string Normalize(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4].TrimEnd() : trimmed;
    }

    private static bool Contains(IReadOnlyList<string> names, string normalized)
    {
        foreach (var name in names)
        {
            if (string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
