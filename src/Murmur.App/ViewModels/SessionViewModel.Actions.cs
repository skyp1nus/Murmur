using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.App.Services;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.App.ViewModels;

/// <summary>
/// What the buttons of the top bar do: open the export dialog (Markdown with the summary, text, SRT, Word), rename the recording,
/// show its folder, transcribe it again and delete it.
/// </summary>
public sealed partial class SessionViewModel
{
    /// <summary>The recording was deleted (its files are gone): the shell goes back to the library.</summary>
    public event Action? Deleted;

    /// <summary>Where the export dialog is shown; the view sets it (it needs the window). <c>null</c> = export is not available.</summary>
    public IFileSaver? FileSaver { get; set; }

    /// <summary>The result of the last action in words ("Saved Client demo.md", an error); <c>null</c> when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionMessage))]
    public partial string? ActionMessage { get; private set; }

    public bool HasActionMessage => ActionMessage is not null;

    /// <summary>Export needs a transcript on screen and a place to save it.</summary>
    public bool CanExport => FileSaver is not null && _transcript is { Segments.Count: > 0 };

    /// <summary>The recording can be renamed and deleted (the store is wired).</summary>
    public bool CanEditRecording => _services.Sessions is not null;

    /// <summary>"Re-transcribe" can start: there is a transcript to redo or a reason to try again, and the speech model is installed.</summary>
    public bool CanRetranscribe => _processing.IsAsrInstalled && (CanRetry || Status == TranscriptStatus.Available);

    /// <summary>
    /// The recording can be deleted: not while the speech pipeline works on it (the worker holds its audio open, so the folder could not
    /// be removed, and a queued job would write for a session that is gone).
    /// </summary>
    public bool CanDelete => CanEditRecording && Status != TranscriptStatus.Processing;

    /// <summary>"Show in folder" is offered.</summary>
    public bool CanShowFolder => _services.Folders is not null;

    /// <summary>The Export button was pressed: the view shows this dialog (a modal over the window). It closes itself with <see cref="ExportDialogViewModel.CloseRequested"/>.</summary>
    public event Action<ExportDialogViewModel>? ExportDialogRequested;

    /// <summary>The last export (for tests); completes when the file is written or the dialog was cancelled.</summary>
    internal Task ExportTask { get; private set; } = Task.CompletedTask;

    /// <summary>The last delete or rename (for tests).</summary>
    internal Task EditTask { get; private set; } = Task.CompletedTask;

    private bool CanRunExport() => CanExport;

    /// <summary>The Export button: opens the export dialog.</summary>
    [RelayCommand(CanExecute = nameof(CanRunExport))]
    private void OpenExport()
    {
        if (CreateExportDialog() is { } dialog)
        {
            ExportDialogRequested?.Invoke(dialog);
        }
    }

    /// <summary>
    /// The dialog for this recording: the transcript on screen, the summary when there is one, the names of the speakers, and the chapter the
    /// playhead is in as a second range (it ends where the next chapter starts, the last one at the end of the recording). <c>null</c> without
    /// a transcript or a place to save.
    /// </summary>
    internal ExportDialogViewModel? CreateExportDialog()
    {
        if (FileSaver is not { } saver || _transcript is not { } transcript)
        {
            ActionMessage = Strings.Session_ExportNoTranscript;
            return null;
        }

        ExportChapterRange? chapter = null;
        if (CurrentChapter is { } current)
        {
            var chapters = Chapters;
            var next = chapters.FirstOrDefault(c => c.Start > current.Start);
            var end = next?.Start ?? EndOfRecording(transcript);
            if (end > current.Start)
            {
                chapter = new ExportChapterRange(current.Title, current.Start, end);
            }
        }

        return new ExportDialogViewModel(_record with { Title = Title }, transcript, _stored, ExportTexts(_speakerNames), saver, chapter);
    }

    /// <summary>The length of the recording, or where its last words end when the length is not known.</summary>
    private TimeSpan EndOfRecording(Transcript transcript)
    {
        var end = Duration;
        foreach (var segment in transcript.Segments)
        {
            if (segment.End > end)
            {
                end = segment.End;
            }
        }

        return end;
    }

    [RelayCommand(CanExecute = nameof(CanRunExport))]
    private void ExportMarkdown() => ExportTask = ExportAsync(ExportFormat.Markdown);

    [RelayCommand(CanExecute = nameof(CanRunExport))]
    private void ExportText() => ExportTask = ExportAsync(ExportFormat.PlainText);

    [RelayCommand(CanExecute = nameof(CanRunExport))]
    private void ExportSrt() => ExportTask = ExportAsync(ExportFormat.Srt);

    [RelayCommand(CanExecute = nameof(CanRunExport))]
    private void ExportWebVtt() => ExportTask = ExportAsync(ExportFormat.WebVtt);

    /// <summary>Tells the buttons that the transcript, or the place to save it, changed.</summary>
    internal void RefreshExport()
    {
        OnPropertyChanged(nameof(CanExport));
        OpenExportCommand.NotifyCanExecuteChanged();
        ExportMarkdownCommand.NotifyCanExecuteChanged();
        ExportTextCommand.NotifyCanExecuteChanged();
        ExportSrtCommand.NotifyCanExecuteChanged();
        ExportWebVttCommand.NotifyCanExecuteChanged();
    }

    private async Task ExportAsync(ExportFormat format)
    {
        if (FileSaver is not { } saver || _transcript is not { } transcript)
        {
            ActionMessage = Strings.Session_ExportNoTranscript;
            return;
        }

        try
        {
            var labels = ExportTexts(_speakerNames);
            var summary = format == ExportFormat.Markdown ? _stored : null;
            var content = TranscriptExport.Format(format, _record with { Title = Title }, transcript, summary, labels);
            var extension = TranscriptExport.Extension(format);
            var saved = await saver.SaveTextAsync(
                Strings.Session_ExportTitle,
                FileNames.Safe(Title) + extension,
                ExportTypeName(format),
                extension,
                content).ConfigureAwait(true);
            ActionMessage = saved is null ? null : UiText.Format(Strings.Session_ExportSaved, saved);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The dialog, the file system and the stream can each fail in their own way (NotSupportedException, a COM error ...).
            ActionMessage = UiText.Format(Strings.Session_ExportFailed, ex.Message);
        }
    }

    /// <summary>The speakers (the names the user chose, else "You" / "Others") and the headings of the exported file, in the UI language.</summary>
    internal static ExportLabels ExportTexts(SpeakerNames? names = null) => new(
        SpeakerLabels.Of(Speaker.Me, names?.Me),
        SpeakerLabels.Of(Speaker.Others, names?.Others),
        Strings.Session_TabSummary,
        Strings.Session_SummaryOverview,
        Strings.Session_SummaryKeyPoints,
        Strings.Session_SummaryActionItems,
        Strings.Session_TabTranscript,
        Strings.Common_UntitledRecording);

    private static string ExportTypeName(ExportFormat format) => format switch
    {
        ExportFormat.Markdown => Strings.Session_ExportMarkdown,
        ExportFormat.PlainText => Strings.Session_ExportText,
        ExportFormat.Srt => Strings.Session_ExportSrt,
        _ => Strings.Session_ExportWebVtt,
    };

    /// <summary>Gives the recording a new title; a blank one is ignored. The breadcrumb and the library follow.</summary>
    internal async Task RenameAsync(string title)
    {
        var trimmed = title.Trim();
        if (trimmed.Length == 0 || trimmed == Title || _services.Sessions is not { } store)
        {
            return;
        }

        try
        {
            await store.RenameSessionAsync(Id, trimmed, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or IOException or InvalidOperationException)
        {
            ActionMessage = UiText.Format(Strings.Session_RenameFailed, ex.Message);
            return;
        }

        Title = trimmed;
        ActionMessage = null;
    }

    /// <summary>Opens the recording's folder in the file manager.</summary>
    [RelayCommand]
    private void ShowInFolder()
    {
        if (_services.Folders is not { } folders)
        {
            return;
        }

        var error = folders.TryOpen(_files.Directory);
        ActionMessage = error is null ? null : UiText.Format(Strings.Session_ShowFolderFailed, error);
    }

    /// <summary>Deletes the recording with its files, transcript and summary. The players let go of the files first (Windows keeps open files).</summary>
    internal async Task DeleteAsync()
    {
        if (_services.Sessions is not { } store || !CanDelete)
        {
            return;
        }

        CancelSummary();
        ReleaseMedia();
        try
        {
            await store.DeleteSessionAsync(Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            // The recording is still there: the player and the picture come back, the view is as it was.
            ActionMessage = UiText.Format(Strings.Session_DeleteFailed, ex.Message);
            if (!_disposed)
            {
                await ReopenMediaAsync().ConfigureAwait(true);
            }

            return;
        }

        Deleted?.Invoke();
    }

    /// <summary>Starts <see cref="DeleteAsync"/> (the confirmation is the menu's).</summary>
    internal void StartDelete() => EditTask = DeleteAsync();

    internal void StartRename(string title) => EditTask = RenameAsync(title);
}

/// <summary>File names for exports: the title without the characters Windows does not allow.</summary>
internal static class FileNames
{
    public static string Safe(string title)
    {
        // The characters Windows does not allow in a file name, on every OS (the file may be copied there).
        const string invalid = "<>:\"/\\|?*";
        var chars = title.Trim().Select(c => invalid.Contains(c, StringComparison.Ordinal) || char.IsControl(c) ? '_' : c).ToArray();
        var name = new string(chars).Trim(' ', '.');
        if (name.Length > 80)
        {
            name = name[..80].TrimEnd(' ', '.');
        }

        return name.Length == 0 ? "recording" : name;
    }
}
