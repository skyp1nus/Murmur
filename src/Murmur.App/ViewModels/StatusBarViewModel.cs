using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.Core.Activity;
using Murmur.Core.Recording;

namespace Murmur.App.ViewModels;

/// <summary>A piece of the summary button: the word ("Transcribing", "· Downloading") and, when the job knows it, its percentage (Geist Mono).</summary>
public sealed record StatusSummaryPart(string Label, string? Percent)
{
    public bool HasPercent => Percent is not null;
}

/// <summary>The content of the Activity popover: what runs or waits right now, or the empty state. Facts only, nothing to configure.</summary>
public sealed partial class ActivityPopoverViewModel : ObservableObject
{
    public ActivityPopoverViewModel()
    {
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public ObservableCollection<ActivityRowViewModel> Rows { get; } = [];

    /// <summary>"Nothing running." is shown.</summary>
    public bool IsEmpty => Rows.Count == 0;

    public string Title => Strings.Activity_Title;

    public string Scope => Strings.Activity_Scope;

    public string EmptyText => Strings.Activity_Empty;

    /// <summary>Brings the rows to <paramref name="items"/>: rows of the same key are updated in place, the rest added, moved or removed.</summary>
    internal void Apply(IReadOnlyList<ActivityItem> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (i < Rows.Count && Rows[i].Key == item.Key)
            {
                Rows[i].Apply(item);
                continue;
            }

            var from = -1;
            for (var j = i + 1; j < Rows.Count; j++)
            {
                if (Rows[j].Key == item.Key)
                {
                    from = j;
                    break;
                }
            }

            if (from >= 0)
            {
                Rows.Move(from, i);
                Rows[i].Apply(item);
            }
            else
            {
                Rows.Insert(i, new ActivityRowViewModel(item));
            }
        }

        while (Rows.Count > items.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }
    }
}

/// <summary>
/// The 28 px status bar at the bottom of the window: "Recording 12:48" on the left (only while recording), the summary button in the
/// middle ("Transcribing 64% · Downloading 42%" or "Up to date"; it opens the Activity popover) and "All local" on the right.
/// Everything comes from <see cref="IActivityFeed"/> (facts only); the elapsed time is read from <see cref="IRecorder.Elapsed"/> by
/// <see cref="Refresh"/>, which the view calls four times a second while recording (so a second never skips), so nothing is pushed at frame rate.
/// </summary>
public sealed partial class StatusBarViewModel : ObservableObject, IDisposable
{
    private readonly IActivityFeed _feed;
    private readonly IRecorder _recorder;
    private readonly Action<Action> _post;
    private int _disposed;

    /// <param name="feed">What runs or waits.</param>
    /// <param name="recorder">The elapsed time of the recording (it excludes pauses).</param>
    /// <param name="post">Marshals the feed's change event (raised off the UI thread) to the UI thread; the UI dispatcher by default.</param>
    public StatusBarViewModel(IActivityFeed feed, IRecorder recorder, Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(recorder);
        _feed = feed;
        _recorder = recorder;
        _post = post ?? (static action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        SummaryParts = [];
        Apply(feed.Current);
        feed.Changed += OnFeedChanged;
    }

    /// <summary>The click on "Recording 12:48" asks to open the Recording screen.</summary>
    public event Action? RecordingRequested;

    public ActivityPopoverViewModel Activity { get; } = new();

    /// <summary>A recording runs (or is paused): the left part of the bar shows.</summary>
    [ObservableProperty]
    public partial bool IsRecording { get; private set; }

    /// <summary>The elapsed time of the recording: <c>12:48</c>, <c>1:02:03</c> from one hour on.</summary>
    [ObservableProperty]
    public partial string ElapsedText { get; private set; } = UiFormat.Clock(TimeSpan.Zero);

    /// <summary>The running jobs of the summary button, in order; empty when <see cref="IsUpToDate"/>.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<StatusSummaryPart> SummaryParts { get; private set; }

    /// <summary>Nothing runs or waits: the summary button says "Up to date".</summary>
    [ObservableProperty]
    public partial bool IsUpToDate { get; private set; } = true;

    /// <summary>The summary as one line (for tests and the automation name): "Transcribing 64% · Downloading 42%".</summary>
    public string SummaryText => IsUpToDate
        ? Strings.Status_UpToDate
        : string.Join(" ", SummaryParts.Select(static p => p.Percent is null ? p.Label : p.Label + " " + p.Percent));

    public string RecordingText => Strings.Activity_Recording;

    public string UpToDateText => Strings.Status_UpToDate;

    public string AllLocalText => Strings.Status_AllLocal;

    public string ActivityName => Strings.Activity_Title;

    /// <summary>Re-reads the elapsed time of the recording. Called by the view four times a second while the bar shows a recording.</summary>
    public void Refresh()
    {
        if (!IsRecording)
        {
            return;
        }

        SetElapsed(UiFormat.Clock(_recorder.Elapsed));
    }

    [RelayCommand]
    private void ShowRecording() => RecordingRequested?.Invoke();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _feed.Changed -= OnFeedChanged;
        }
    }

    private void OnFeedChanged() => _post(() =>
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            Apply(_feed.Current);
        }
    });

    private void SetElapsed(string text)
    {
        ElapsedText = text;
        foreach (var row in Activity.Rows)
        {
            if (row.IsRecording)
            {
                row.ElapsedText = text;
            }
        }
    }

    private void Apply(IReadOnlyList<ActivityItem> items)
    {
        Activity.Apply(items);
        IsRecording = items.Any(static i => i.Kind == ActivityKind.Recording);
        if (IsRecording)
        {
            SetElapsed(UiFormat.Clock(_recorder.Elapsed));
        }

        // The summary names what runs, one entry per kind (the average of the known percentages); the recording has its own place on the left.
        var parts = new List<StatusSummaryPart>();
        var seen = new HashSet<ActivityKind>();
        foreach (var item in items)
        {
            if (item.Kind == ActivityKind.Recording || item.Queued || !seen.Add(item.Kind))
            {
                continue;
            }

            var sameKind = items.Where(i => i.Kind == item.Kind && !i.Queued).ToArray();
            var known = sameKind.Where(static i => i.Progress is not null).Select(static i => i.Progress!.Value).ToArray();
            var word = ActivityRowViewModel.KindWord(item.Kind);
            parts.Add(new StatusSummaryPart(
                parts.Count == 0 ? word : "· " + word,
                known.Length > 0 ? ActivityRowViewModel.PercentOf(known.Average()) : null));
        }

        if (parts.Count == 0)
        {
            var queued = items.Count(static i => i.Queued);
            if (queued > 0)
            {
                parts.Add(new StatusSummaryPart(UiText.Format(Strings.Status_QueuedCount, queued.ToString(System.Globalization.CultureInfo.InvariantCulture)), null));
            }
        }

        // The list is replaced only when it differs, so a progress tick that changes nothing visible repaints nothing.
        if (!parts.SequenceEqual(SummaryParts))
        {
            SummaryParts = parts;
            IsUpToDate = parts.Count == 0;
            OnPropertyChanged(nameof(SummaryText));
        }
    }
}
