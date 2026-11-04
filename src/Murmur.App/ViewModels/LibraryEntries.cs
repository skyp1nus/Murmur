using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Murmur.App.ViewModels;

/// <summary>Two calendar days picked in the date filter's calendar, in the order they were clicked (or the one day while the second is awaited).</summary>
public readonly record struct DateRange(DateTime First, DateTime Last);

/// <summary>The "What are we recording today?" block: the New recording card and the Ask your library card.</summary>
public sealed record LibraryHeroEntry(LibraryViewModel Library);

/// <summary>The "Recordings" heading with the search box.</summary>
public sealed record LibraryHeaderEntry(LibraryViewModel Library);

/// <summary>The date filter chips.</summary>
public sealed record LibraryFiltersEntry(LibraryViewModel Library);

/// <summary>A day heading above its card of rows ("Today", "Yesterday", "This week", "Mon, Oct 5").</summary>
public sealed record LibraryGroupEntry(string Title);

/// <summary>A line of text between the filters and the rows (the inline error, "Searching…").</summary>
public sealed record LibraryNoticeEntry(string Text);

/// <summary>The card that stands in for the rows: nothing recorded yet, or nothing matches.</summary>
public sealed record LibraryEmptyEntry(LibraryViewModel Library, bool NoMatches)
{
    public string Title => NoMatches ? Murmur.App.Resources.Strings.Library_NothingFound : Library.EmptyTitle;

    public string Text => NoMatches ? Murmur.App.Resources.Strings.Library_NoMatches : Library.EmptyText;

    /// <summary>"New recording" under the text: only when the library really is empty, not when a filter or search hides everything.</summary>
    public bool CanRecord => !NoMatches && !Library.IsFilteredNow;
}

/// <summary>
/// The rows of the library page as one flat list: the three fixed blocks first, then what the current state shows (day headings and
/// their rows, search results, the empty card). One flat list lets a single virtualised list scroll the whole page like the canvas
/// does; replacing only the tail keeps the fixed blocks, and so the focus of the search box, alive while the user types.
/// </summary>
public sealed class LibraryEntryList : ObservableCollection<object>
{
    /// <summary>The fixed blocks at the head: hero, header, filters.</summary>
    public const int HeadCount = 3;

    /// <summary>Replaces everything after <see cref="HeadCount"/> with <paramref name="tail"/> (two change notifications, however long the list).</summary>
    public void ReplaceTail(IReadOnlyList<object> tail)
    {
        ArgumentNullException.ThrowIfNull(tail);
        if (Count >= HeadCount && Count - HeadCount == tail.Count)
        {
            var same = true;
            for (var i = 0; i < tail.Count; i++)
            {
                if (!ReferenceEquals(this[HeadCount + i], tail[i]))
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return;
            }
        }

        CheckReentrancy();
        if (Count > HeadCount)
        {
            var removed = new List<object>(Count - HeadCount);
            for (var i = HeadCount; i < Count; i++)
            {
                removed.Add(this[i]);
            }

            ((List<object>)Items).RemoveRange(HeadCount, removed.Count);
            RaiseChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, HeadCount));
        }

        if (tail.Count > 0)
        {
            var added = new List<object>(tail);
            ((List<object>)Items).AddRange(added);
            RaiseChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added, HeadCount));
        }

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
    }

    /// <summary>Sets the three fixed blocks; only valid on an empty list.</summary>
    public void SetHead(object hero, object header, object filters)
    {
        Add(hero);
        Add(header);
        Add(filters);
    }

    private void RaiseChanged(NotifyCollectionChangedEventArgs args) => OnCollectionChanged(args);
}
