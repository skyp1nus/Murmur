using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.Core.Import;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;

namespace Murmur.App.ViewModels;

/// <summary>The glyph of a library row's tile: the screen when the session has video, else the microphone alone, else system audio.</summary>
public enum RowIcon
{
    SystemAudio,
    Microphone,
    Screen,

    /// <summary>A file with a download arrow: the session was made from an imported file.</summary>
    File,
}

/// <summary>Where a row sits in the card of its day (the first row has the top corners, the last the bottom ones).</summary>
public enum RowPosition
{
    Only,
    First,
    Middle,
    Last,
}

/// <summary>
/// One row of the library list. Everything is fixed for the row's lifetime except the status (which follows the processing
/// pipeline, see <see cref="ApplyProcessing"/>), the transcript's language and first line (read in the background by the
/// library), and the stored facts, which <see cref="Update"/> re-reads when the session changed behind the library's back (a
/// background recovery); a reload builds new rows.
/// </summary>
public sealed partial class SessionItemViewModel : ObservableObject
{
    private readonly Action<SessionItemViewModel>? _open;
    private SessionProcessingStatus? _live;
    private ImportItem? _import;

    /// <param name="import">The import queue's entry of an imported session (<c>null</c> = none); see <see cref="ApplyImport"/>.</param>
    public SessionItemViewModel(
        SessionRecord record,
        TimeProvider time,
        SessionProcessingStatus? processing = null,
        Action<SessionItemViewModel>? open = null,
        ImportItem? import = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(time);
        _open = open;
        Id = record.Id;
        Record = record;
        Title = string.Empty;
        DateText = string.Empty;
        DurationText = string.Empty;
        ClockText = string.Empty;
        TimeText = string.Empty;
        SourcesText = string.Empty;
        _import = import;
        Apply(record, time, processing);
    }

    /// <summary>The stored session this row shows (handed to the recording view when the row is opened).</summary>
    public SessionRecord Record { get; private set; }

    public SessionId Id { get; }

    [ObservableProperty]
    public partial string Title { get; private set; }

    [ObservableProperty]
    public partial string DateText { get; private set; }

    /// <summary><c>HH:MM:SS</c>, Geist Mono.</summary>
    [ObservableProperty]
    public partial string DurationText { get; private set; }

    /// <summary>The duration as the library shows it: <c>47:12</c>, or <c>1:02:15</c> from an hour on.</summary>
    [ObservableProperty]
    public partial string ClockText { get; private set; }

    /// <summary>The time of the day the recording started ("10:30"), with the weekday for the last week ("Mon, 14:00").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetaText))]
    public partial string TimeText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetaText))]
    public partial bool HasMicrophone { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetaText))]
    public partial bool HasSystemAudio { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetaText))]
    public partial bool HasScreen { get; private set; }

    /// <summary>"Mic, system audio, screen".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetaText))]
    public partial string SourcesText { get; private set; }

    /// <summary>The tile glyph.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScreenIcon))]
    [NotifyPropertyChangedFor(nameof(IsMicrophoneIcon))]
    [NotifyPropertyChangedFor(nameof(IsSystemAudioIcon))]
    [NotifyPropertyChangedFor(nameof(IsFileIcon))]
    public partial RowIcon Icon { get; private set; }

    public bool IsScreenIcon => Icon == RowIcon.Screen;

    public bool IsMicrophoneIcon => Icon == RowIcon.Microphone;

    public bool IsSystemAudioIcon => Icon == RowIcon.SystemAudio;

    public bool IsFileIcon => Icon == RowIcon.File;

    /// <summary>The session was made from an imported file (its meta line names the file instead of the recording sources).</summary>
    public bool IsImported => Record.Source == SessionSource.Imported;

    /// <summary>The name of the imported file ("Zoom_Meeting.mp4"); empty when the session is not an import or the path is unknown.</summary>
    public string OriginalName => IsImported ? FileNameOf(Record.OriginalPath) : string.Empty;

    /// <summary>
    /// An imported session that has not reached the transcription yet (waiting in the import queue, being decoded, or the pipeline's own
    /// queue): its meta line is the single word "Queued" (11, Smoke) and there is no progress bar, like the mockup's queued row.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMeta))]
    [NotifyPropertyChangedFor(nameof(QueuedText))]
    public partial bool ShowQueued { get; private set; }

    /// <summary>The ordinary meta line shows unless the queued word takes its place.</summary>
    /// <remarks>An imported row that is transcribing shows only its title and the bar, like the mockup's.</remarks>
    public bool ShowMeta => !ShowQueued && !(IsImported && ShowProgress);

    /// <summary>The row opens a session; a stand-in for a queued import has none yet (no chevron, the button is off).</summary>
    public bool CanOpen => _open is not null;

    public string QueuedText => Strings.Processing_Queued;

    /// <summary>The language of the transcript ("English"); <c>null</c> until it is known (Core stores it with the transcript only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetaText))]
    public partial string? Language { get; private set; }

    /// <summary>The first line of the transcript; <c>null</c> until it is read or when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnippet))]
    [NotifyPropertyChangedFor(nameof(ShowSnippet))]
    public partial string? Snippet { get; private set; }

    /// <summary>Processing / outcome badge ("Transcribing · 42%", "Done", "Failed", "Interrupted"); <c>null</c> = none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    [NotifyPropertyChangedFor(nameof(MetaText))]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    public partial string? StatusText { get; private set; }

    /// <summary>The share of the transcription done, 0..100; meaningful while <see cref="ShowProgress"/>.</summary>
    [ObservableProperty]
    public partial double ProgressPercent { get; private set; }

    /// <summary>The transcription is running: the bar and its text replace the line of transcript text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetaText))]
    [NotifyPropertyChangedFor(nameof(ShowSnippet))]
    [NotifyPropertyChangedFor(nameof(ShowMeta))]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    public partial bool ShowProgress { get; private set; }

    /// <summary>Where the row sits in its day card; set by the library when it groups the rows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTopCorners))]
    [NotifyPropertyChangedFor(nameof(HasBottomCorners))]
    public partial RowPosition Position { get; set; } = RowPosition.Only;

    /// <summary>The first row of a day card: it has the rounded top corners.</summary>
    public bool HasTopCorners => Position is RowPosition.First or RowPosition.Only;

    /// <summary>The last row of a day card: it has the rounded bottom corners and the bottom line.</summary>
    public bool HasBottomCorners => Position is RowPosition.Last or RowPosition.Only;

    public bool HasStatus => StatusText is not null;

    public bool HasSnippet => !string.IsNullOrWhiteSpace(Snippet);

    /// <summary>The line of transcript text shows unless the progress bar takes its place.</summary>
    public bool ShowSnippet => HasSnippet && !ShowProgress;

    /// <summary>"Transcribing · 64%" next to the bar; <c>null</c> while there is no bar.</summary>
    public string? ProgressText => ShowProgress ? StatusText : null;

    /// <summary>
    /// "10:30 · English · Mic, system audio, screen". The language is left out until the transcript says it; a state worth
    /// knowing (interrupted, failed, queued, no transcript) closes the line in the same style, "Done" and a running transcription do not.
    /// </summary>
    public string MetaText
    {
        get
        {
            if (IsImported)
            {
                return ImportedMeta();
            }

            var parts = new List<string>(4) { TimeText };
            if (!string.IsNullOrEmpty(Language))
            {
                parts.Add(Language);
            }

            if (SourcesText.Length > 0)
            {
                parts.Add(SourcesText);
            }

            if (StatusText is { } status && !ShowProgress && !string.Equals(status, Strings.Processing_Done, StringComparison.Ordinal))
            {
                parts.Add(status);
            }

            return string.Join(Strings.Library_MetaSeparator, parts);
        }
    }

    /// <summary>"Imported · English · Zoom_Meeting.mp4": the language is left out until the transcript says it; a state worth knowing closes the line.</summary>
    private string ImportedMeta()
    {
        var parts = new List<string>(4) { Strings.Library_ImportedMeta };
        if (!string.IsNullOrEmpty(Language))
        {
            parts.Add(Language);
        }

        if (OriginalName.Length > 0)
        {
            parts.Add(OriginalName);
        }

        if (StatusText is { } status && !ShowProgress && !string.Equals(status, Strings.Processing_Done, StringComparison.Ordinal))
        {
            parts.Add(status);
        }

        return string.Join(Strings.Library_MetaSeparator, parts);
    }

    /// <summary>The last path segment, with either separator (an import path of another platform is still named right).</summary>
    internal static string FileNameOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.TrimEnd('/', '\\');
        return trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
    }

    /// <summary>Opens the session.</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open() => _open?.Invoke(this);

    /// <summary>The transcript was read: its language code ("en") and first line.</summary>
    internal void ApplyTranscript(string? languageCode, string? firstLine)
    {
        Language = LanguageNames.Of(languageCode);
        Snippet = string.IsNullOrWhiteSpace(firstLine) ? null : firstLine.Trim();
    }

    /// <summary>Updates the badge from a pipeline status event. Equal text raises no notification.</summary>
    public void ApplyProcessing(SessionProcessingStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        _live = status;
        ApplyStatus();
    }

    /// <summary>Shows a newer stored version of the same session in place (the row object, and so the list, stays).</summary>
    internal void Update(SessionRecord record, TimeProvider time, SessionProcessingStatus? processing) => Apply(record, time, processing);

    private void Apply(SessionRecord record, TimeProvider time, SessionProcessingStatus? processing)
    {
        Record = record;
        _live = processing;
        Title = string.IsNullOrWhiteSpace(record.Title) ? Strings.Common_UntitledRecording : record.Title;
        DateText = UiFormat.Date(record.StartedAt, time);
        DurationText = UiFormat.Duration(record.Duration);
        // An import that is still waiting for its probe has no length yet.
        ClockText = record.Duration == TimeSpan.Zero && record.Source == SessionSource.Imported ? string.Empty : UiFormat.Clock(record.Duration);
        TimeText = UiFormat.RowTime(record.StartedAt, time);
        HasMicrophone = record.HasMicrophone;
        HasSystemAudio = record.HasSystemAudio;
        HasScreen = record.ScreenSegments.Count > 0;
        SourcesText = UiFormat.Sources(HasMicrophone, HasSystemAudio, HasScreen);
        Icon = record.Source == SessionSource.Imported ? RowIcon.File : HasScreen ? RowIcon.Screen : HasMicrophone && !HasSystemAudio ? RowIcon.Microphone : RowIcon.SystemAudio;
        OnPropertyChanged(nameof(IsImported));
        OnPropertyChanged(nameof(ShowMeta));
        OnPropertyChanged(nameof(OriginalName));
        OnPropertyChanged(nameof(MetaText));
        ApplyStatus();
    }

    private void ApplyStatus()
    {
        StatusText = ProcessingText.Badge(Record.State, _live);
        if (_live is { Stage: SessionProcessingStage.BuildingPeaks or SessionProcessingStage.Transcribing or SessionProcessingStage.Saving } running)
        {
            ProgressPercent = ProcessingText.Percent(running.Progress);
            ShowProgress = true;
        }
        else
        {
            ProgressPercent = 0;
            ShowProgress = false;
        }

        ShowQueued = IsImported && !ShowProgress && IsWaiting();
        if (ShowQueued)
        {
            // An import that is decoded is stored as "recording"; to the user it is queued.
            StatusText = Strings.Processing_Queued;
        }

        OnPropertyChanged(nameof(MetaText));
    }

    /// <summary>Still waiting for the transcription: in the import queue, being decoded (the session is "recording" until then), or in the pipeline's queue.</summary>
    private bool IsWaiting() =>
        _import is { State: ImportState.Queued or ImportState.Decoding }
        || (_import is not { State: ImportState.Failed or ImportState.Cancelled } && Record.State == SessionState.Recording)
        || _live is { Stage: SessionProcessingStage.Queued };
}
