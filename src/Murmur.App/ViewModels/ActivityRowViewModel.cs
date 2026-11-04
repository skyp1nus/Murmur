using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Murmur.App.Resources;
using Murmur.Core.Activity;

namespace Murmur.App.ViewModels;

/// <summary>
/// One row of the Activity popover: the icon of its kind, "Kind · title", an optional 3 px progress bar and the figure on the right
/// (percentage, "Queued", or the elapsed time of the recording). Rows are kept and updated in place, matched by
/// <see cref="ActivityItem.Key"/>, so a progress tick does not rebuild the list.
/// </summary>
public sealed partial class ActivityRowViewModel : ObservableObject
{
    public ActivityRowViewModel(ActivityItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Key = item.Key;
        Apply(item);
    }

    public string Key { get; }

    /// <summary>The kind in words ("Transcribing"); shown in the medium weight.</summary>
    [ObservableProperty]
    public partial string KindText { get; private set; } = string.Empty;

    /// <summary>" · title", or empty when the item has no title.</summary>
    [ObservableProperty]
    public partial string TitleSuffix { get; private set; } = string.Empty;

    /// <summary>0..100 for the bar.</summary>
    [ObservableProperty]
    public partial double Percent { get; private set; }

    /// <summary>The bar shows: the item runs and knows its progress.</summary>
    [ObservableProperty]
    public partial bool HasProgress { get; private set; }

    /// <summary>"64%" in Geist Mono, or empty.</summary>
    [ObservableProperty]
    public partial string PercentText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsQueued { get; private set; }

    /// <summary>The recording row: the spinning orb in place of an icon, the elapsed time on the right.</summary>
    [ObservableProperty]
    public partial bool IsRecording { get; private set; }

    /// <summary>The elapsed time of the recording row (<c>12:48</c>); empty on the other rows.</summary>
    [ObservableProperty]
    public partial string ElapsedText { get; set; } = string.Empty;

    // One icon per row, picked by these flags (the views stack the icons and show one).
    [ObservableProperty]
    public partial bool ShowTranscriptIcon { get; private set; }

    [ObservableProperty]
    public partial bool ShowSparkIcon { get; private set; }

    [ObservableProperty]
    public partial bool ShowDownloadIcon { get; private set; }

    [ObservableProperty]
    public partial bool ShowClockIcon { get; private set; }

    /// <summary>The row says "Queued" on the right.</summary>
    public string QueuedText => Strings.Activity_Queued;

    /// <summary>Takes the current facts of the item (same key).</summary>
    public void Apply(ActivityItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        KindText = KindWord(item.Kind);
        TitleSuffix = string.IsNullOrWhiteSpace(item.Title) ? string.Empty : " · " + item.Title;
        IsQueued = item.Queued;
        IsRecording = item.Kind == ActivityKind.Recording;
        var known = !item.Queued && item.Progress is not null;
        HasProgress = known;
        Percent = known ? Math.Round(item.Progress!.Value * 100) : 0;
        PercentText = known ? PercentOf(item.Progress!.Value) : string.Empty;
        ShowClockIcon = item.Queued;
        ShowTranscriptIcon = !item.Queued && item.Kind == ActivityKind.Transcribing;
        ShowSparkIcon = !item.Queued && item.Kind is ActivityKind.Indexing or ActivityKind.Summarizing;
        ShowDownloadIcon = !item.Queued && item.Kind == ActivityKind.DownloadingModel;
    }

    /// <summary>The word for a kind in the UI language ("Transcribing").</summary>
    internal static string KindWord(ActivityKind kind) => kind switch
    {
        ActivityKind.Recording => Strings.Activity_Recording,
        ActivityKind.Transcribing => Strings.Activity_Transcribing,
        ActivityKind.Indexing => Strings.Activity_Indexing,
        ActivityKind.Summarizing => Strings.Activity_Summarizing,
        ActivityKind.DownloadingModel => Strings.Activity_Downloading,
        _ => string.Empty,
    };

    /// <summary>"64%" for 0.64.</summary>
    internal static string PercentOf(double progress) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Round(Math.Clamp(progress, 0, 1) * 100):0}%");
}
