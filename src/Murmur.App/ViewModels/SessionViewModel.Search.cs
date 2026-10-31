using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Murmur.App.ViewModels;

/// <summary>
/// "Search transcript": every occurrence of the text is marked in the rows (the canvas draws it as a filled ink word), and Enter
/// jumps to the next row that has one (the player moves there and the list scrolls). All rows stay in the list.
/// </summary>
public sealed partial class SessionViewModel
{
    private int _hitCursor = -1;

    /// <summary>What the user typed in the box.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>The rows (numbers in <see cref="Segments"/>) that contain the text, in transcript order.</summary>
    public IReadOnlyList<int> SearchHits { get; private set; } = [];

    /// <summary>The text that is marked: the trimmed query, or <c>null</c> when it is empty.</summary>
    public string? HighlightTerm => string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();

    partial void OnSearchTextChanged(string value)
    {
        _hitCursor = -1;
        ApplySearch();
    }

    /// <summary>Marks the term in the rows and finds the hits.</summary>
    private void ApplySearch()
    {
        var term = HighlightTerm;
        var hits = new List<int>();
        var rows = Segments;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            row.HighlightTerm = term;
            if (term is not null && row.Contains(term))
            {
                hits.Add(i);
            }
        }

        SearchHits = hits;
        OnPropertyChanged(nameof(SearchHits));
        OnPropertyChanged(nameof(HighlightTerm));
        NextHitCommand.NotifyCanExecuteChanged();
        PreviousHitCommand.NotifyCanExecuteChanged();
    }

    private bool HasHits() => SearchHits.Count > 0;

    /// <summary>Enter: the next row after the last jump (or after the current row) that has the text; after the last one it wraps to the first.</summary>
    [RelayCommand(CanExecute = nameof(HasHits))]
    private void NextHit() => JumpToHit(forward: true);

    /// <summary>Shift+Enter: the previous hit.</summary>
    [RelayCommand(CanExecute = nameof(HasHits))]
    private void PreviousHit() => JumpToHit(forward: false);

    private void JumpToHit(bool forward)
    {
        var hits = SearchHits;
        if (hits.Count == 0)
        {
            return;
        }

        var from = _hitCursor >= 0 ? _hitCursor : _currentSegment;
        int target;
        if (forward)
        {
            target = hits[0];
            foreach (var hit in hits)
            {
                if (hit > from)
                {
                    target = hit;
                    break;
                }
            }
        }
        else
        {
            target = hits[^1];
            for (var i = hits.Count - 1; i >= 0; i--)
            {
                if (hits[i] < from)
                {
                    target = hits[i];
                    break;
                }
            }
        }

        _hitCursor = target;
        SeekTo(Segments[target].Start);
        ScrollToSegmentRequested?.Invoke(target);
    }
}
