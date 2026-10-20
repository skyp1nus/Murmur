using System.Text;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.Core.Sessions;

namespace Murmur.App.ViewModels;

/// <summary>
/// One row of the library search: the session title, a time chip and the matching transcript snippet. A title match
/// has no time (the row shows the session date instead of a snippet). Clicking the row opens the session there.
/// </summary>
public sealed partial class SearchResultViewModel
{
    /// <summary>Longer snippets are cut at a word boundary and end with an ellipsis.</summary>
    public const int MaxSnippetLength = 180;

    public SearchResultViewModel(SessionRecord record, TimeSpan? at, string snippet, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(snippet);
        ArgumentNullException.ThrowIfNull(time);
        Record = record;
        At = at;
        Title = string.IsNullOrWhiteSpace(record.Title) ? Strings.Common_UntitledRecording : record.Title;
        DateText = UiFormat.Date(record.StartedAt, time);
        TimeText = at is { } moment ? UiFormat.Clock(moment) : string.Empty;
        Snippet = at is null ? DateText : Shorten(snippet);
    }

    public SessionRecord Record { get; }

    public SessionId Session => Record.Id;

    /// <summary>Where the match is, or <c>null</c> for a title match.</summary>
    public TimeSpan? At { get; }

    public string Title { get; }

    public string DateText { get; }

    /// <summary>Geist Mono time chip, e.g. "12:34"; empty for a title match.</summary>
    public string TimeText { get; }

    public bool HasTime => At is not null;

    public string Snippet { get; }

    /// <summary>Where the row sits in its card (set by the library when it lays the results out).</summary>
    public RowPosition Position { get; set; } = RowPosition.Only;

    public bool HasTopCorners => Position is RowPosition.First or RowPosition.Only;

    public bool HasBottomCorners => Position is RowPosition.Last or RowPosition.Only;

    /// <summary>Opens the session at the match.</summary>
    public Action<SearchResultViewModel>? OpenAction { get; init; }

    [RelayCommand]
    private void Open() => OpenAction?.Invoke(this);

    /// <summary>Collapses whitespace and cuts long text at a word boundary.</summary>
    internal static string Shorten(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, MaxSnippetLength + 1));
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
            if (builder.Length > MaxSnippetLength)
            {
                break;
            }
        }

        if (builder.Length <= MaxSnippetLength)
        {
            return builder.ToString();
        }

        var cut = builder.ToString(0, MaxSnippetLength);
        var space = cut.LastIndexOf(' ');
        if (space > MaxSnippetLength / 2)
        {
            cut = cut[..space];
        }

        return cut.TrimEnd(',', '.', ';', ':', ' ') + "…";
    }
}
