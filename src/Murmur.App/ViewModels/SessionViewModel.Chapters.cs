using CommunityToolkit.Mvvm.ComponentModel;
using Murmur.Core.Sessions;

namespace Murmur.App.ViewModels;

/// <summary>
/// The chapter strip above the player: the chapters Gemma made of the transcript (<see cref="Core.Intelligence.IChapterGenerator"/>, stored in
/// <see cref="IChapterStore"/>), with the one the playhead is in marked. They are made together with the summary and replaced when it is
/// regenerated (see <c>RunSummaryAsync</c>); the strip is hidden while there are none.
/// </summary>
public sealed partial class SessionViewModel
{
    private ChapterViewModel[] _chapterRows = [];
    private int _currentChapter = -1;

    /// <summary>The chapters in timeline order; empty when the recording has none (the strip is hidden).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChapters))]
    public partial IReadOnlyList<ChapterViewModel> Chapters { get; private set; } = [];

    /// <summary>The chapter the playhead is in, or <c>null</c> without chapters.</summary>
    [ObservableProperty]
    public partial ChapterViewModel? CurrentChapter { get; private set; }

    public bool HasChapters => Chapters.Count > 0;

    private async Task LoadChaptersAsync()
    {
        if (_disposed)
        {
            return;
        }

        IReadOnlyList<Chapter> chapters = [];
        if (_services.Chapters is { } store)
        {
            try
            {
                chapters = await store.GetChaptersAsync(Id, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception)
            {
                // Chapters that cannot be read are as good as none: the strip stays hidden.
            }
        }

        if (!_disposed)
        {
            ShowChapters(chapters);
        }
    }

    /// <summary>Replaces the strip with these chapters (ordered by start) and marks the one the playhead is in.</summary>
    private void ShowChapters(IReadOnlyList<Chapter> chapters)
    {
        var rows = new ChapterViewModel[chapters.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = new ChapterViewModel(chapters[i], SeekToChapter);
        }

        Array.Sort(rows, static (a, b) => a.Start.CompareTo(b.Start));
        _chapterRows = rows;
        _currentChapter = -1;
        CurrentChapter = null;
        Chapters = rows;
        UpdateCurrentChapter(Position);
    }

    /// <summary>A click on a segment: audio and video jump to the start of the chapter (play or pause stays as it is).</summary>
    private void SeekToChapter(TimeSpan start) => SeekTo(start);

    /// <summary>
    /// Moves the mark to the chapter that contains <paramref name="position"/>: the last one that started. Called with every position
    /// change, so it allocates nothing and touches the rows only when the chapter changes (a binary search over at most a dozen rows).
    /// </summary>
    internal void UpdateCurrentChapter(TimeSpan position)
    {
        var rows = _chapterRows;
        var low = 0;
        var high = rows.Length - 1;
        var index = -1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            if (rows[middle].Start <= position)
            {
                index = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (index == _currentChapter)
        {
            return;
        }

        if (_currentChapter >= 0)
        {
            rows[_currentChapter].IsCurrent = false;
        }

        _currentChapter = index;
        if (index >= 0)
        {
            rows[index].IsCurrent = true;
        }

        CurrentChapter = index >= 0 ? rows[index] : null;
    }
}
