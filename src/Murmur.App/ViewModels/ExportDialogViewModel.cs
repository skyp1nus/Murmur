using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Murmur.App.Resources;
using Murmur.App.Services;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.App.ViewModels;

/// <summary>The chapter the playhead is in, as a range of the export: its title and where it starts and ends on the session timeline.</summary>
internal readonly record struct ExportChapterRange(string Title, TimeSpan Start, TimeSpan End);

/// <summary>
/// The export dialog (Idea6Export.dc.html): the format, what to include, the range, a live preview and Copy / Save. The preview is
/// formatted off the UI thread, a short while after the last change (so ticking boxes in a row costs one run), and a run that is
/// overtaken by a newer change is dropped. Copy and Save format the whole text again, from the same options.
/// </summary>
public sealed partial class ExportDialogViewModel : ObservableObject, IDisposable
{
    /// <summary>The preview shows at most this many characters (a three-hour transcript is megabytes; the file has all of it).</summary>
    internal const int MaxPreviewChars = 40_000;

    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(150);

    private readonly SessionRecord _record;
    private readonly Transcript _transcript;
    private readonly SessionSummary? _summary;
    private readonly ExportLabels _labels;
    private readonly IFileSaver _saver;
    private readonly ExportChapterRange? _chapter;
    private readonly TimeSpan _debounce;
    private CancellationTokenSource? _previewCts;
    private bool _disposed;

    private bool _summaryOn = true;
    private bool _actionItemsOn = true;
    private bool _timestampsOn = true;
    private bool _speakerNamesOn = true;

    internal ExportDialogViewModel(
        SessionRecord record,
        Transcript transcript,
        SessionSummary? summary,
        ExportLabels labels,
        IFileSaver saver,
        ExportChapterRange? chapter = null,
        TimeSpan? debounce = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(saver);
        _record = record;
        _transcript = transcript;
        _summary = summary;
        _labels = labels;
        _saver = saver;
        _chapter = chapter;
        _debounce = debounce ?? DefaultDebounce;

        Title = string.IsNullOrWhiteSpace(record.Title) ? Strings.Common_UntitledRecording : record.Title;
        WholeSpan = RangeText(TimeSpan.Zero, EndOfRecording(record, transcript));
        if (chapter is { } part)
        {
            ChapterLabel = UiText.Format(Strings.Export_RangeChapter, part.Title);
            ChapterSpan = RangeText(part.Start, part.End);
        }

        Preview = string.Empty;
        PreviewTask = RefreshPreviewAsync(immediately: true);
    }

    /// <summary>Where the text of "Copy" goes (the clipboard of the window); without it Copy is disabled.</summary>
    public Func<string, Task>? Clipboard
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(CanCopy));
            CopyCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>The title of the recording, next to the dialog's own title.</summary>
    public string Title { get; }

    /// <summary>The format on the radio pills.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMarkdown), nameof(IsPlainText), nameof(IsSrt), nameof(IsDocx), nameof(IsSubtitles))]
    [NotifyPropertyChangedFor(nameof(SummaryEnabled), nameof(ActionItemsEnabled), nameof(TimestampsEnabled))]
    [NotifyPropertyChangedFor(nameof(IncludeSummary), nameof(IncludeActionItems), nameof(IncludeTimestamps))]
    [NotifyPropertyChangedFor(nameof(PreviewIsMono), nameof(SaveLabel), nameof(FileName), nameof(CanCopy))]
    public partial ExportFormat Format { get; set; } = ExportFormat.Markdown;

    /// <summary>"Whole recording" (<c>true</c>) or "Chapter: ..." (<c>false</c>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChapterRange), nameof(HasSpeech), nameof(CanCopy), nameof(CanSave))]
    public partial bool IsWholeRange { get; set; } = true;

    /// <summary>The preview text (the export, built only up to <see cref="MaxPreviewChars"/>).</summary>
    [ObservableProperty]
    public partial string Preview { get; private set; }

    /// <summary>The line next to the buttons: "Copied to clipboard", "Saved ...", an error; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string Status { get; private set; } = string.Empty;

    /// <summary>A save is in progress (the buttons wait).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopy), nameof(CanSave))]
    public partial bool IsBusy { get; private set; }

    public bool HasStatus => Status.Length > 0;

    /// <summary>The three radio pills of the format that are not Markdown; each is checked while the format is chosen.</summary>
    public bool IsMarkdown
    {
        get => Format == ExportFormat.Markdown;
        set => Choose(value, ExportFormat.Markdown);
    }

    public bool IsPlainText
    {
        get => Format == ExportFormat.PlainText;
        set => Choose(value, ExportFormat.PlainText);
    }

    public bool IsSrt
    {
        get => Format == ExportFormat.Srt;
        set => Choose(value, ExportFormat.Srt);
    }

    public bool IsDocx
    {
        get => Format == ExportFormat.Docx;
        set => Choose(value, ExportFormat.Docx);
    }

    /// <summary>The format has no summary, no action items and always has its times.</summary>
    public bool IsSubtitles => Format is ExportFormat.Srt or ExportFormat.WebVtt;

    /// <summary>"Subtitles carry only the spoken lines." is shown while the locks are on.</summary>
    public bool SummaryEnabled => !IsSubtitles;

    public bool ActionItemsEnabled => !IsSubtitles;

    public bool TimestampsEnabled => !IsSubtitles;

    /// <summary>The Summary box: shown unchecked for subtitles; what the user chose is kept for the other formats.</summary>
    public bool IncludeSummary
    {
        get => !IsSubtitles && _summaryOn;
        set => Set(ref _summaryOn, value, nameof(IncludeSummary));
    }

    public bool IncludeActionItems
    {
        get => !IsSubtitles && _actionItemsOn;
        set => Set(ref _actionItemsOn, value, nameof(IncludeActionItems));
    }

    /// <summary>The Timestamps box: shown checked (and locked) for subtitles.</summary>
    public bool IncludeTimestamps
    {
        get => IsSubtitles || _timestampsOn;
        set => Set(ref _timestampsOn, value, nameof(IncludeTimestamps));
    }

    public bool IncludeSpeakerNames
    {
        get => _speakerNamesOn;
        set => Set(ref _speakerNamesOn, value, nameof(IncludeSpeakerNames));
    }

    /// <summary>The chapter option exists: the recording has chapters and the playhead is in one.</summary>
    public bool HasChapterRange => _chapter is not null;

    public bool IsChapterRange
    {
        get => !IsWholeRange;
        set
        {
            if (value && HasChapterRange)
            {
                IsWholeRange = false;
            }
        }
    }

    /// <summary>"Chapter: PDF export"; empty without a chapter option.</summary>
    public string ChapterLabel { get; } = string.Empty;

    /// <summary>"12:15 – 24:30" (Geist Mono).</summary>
    public string ChapterSpan { get; } = string.Empty;

    /// <summary>"00:00 – 47:12".</summary>
    public string WholeSpan { get; }

    /// <summary>The extensions shown under the names of the formats.</summary>
    public string MarkdownExtension => TranscriptExport.Extension(ExportFormat.Markdown);

    public string PlainTextExtension => TranscriptExport.Extension(ExportFormat.PlainText);

    public string SrtExtension => TranscriptExport.Extension(ExportFormat.Srt);

    public string DocxExtension => TranscriptExport.Extension(ExportFormat.Docx);

    /// <summary>The name the file is offered under, with the extension of the format.</summary>
    public string FileName => FileNames.Safe(Title) + TranscriptExport.Extension(Format);

    /// <summary>"Save .md".</summary>
    public string SaveLabel => UiText.Format(Strings.Export_SaveAs, TranscriptExport.Extension(Format));

    /// <summary>The preview is set in Geist Mono (SRT, plain text); Markdown and Word use the text face.</summary>
    public bool PreviewIsMono => Format is not (ExportFormat.Markdown or ExportFormat.Docx);

    /// <summary>A .docx cannot be put on the clipboard as text.</summary>
    public bool CanCopy => Clipboard is not null && Format != ExportFormat.Docx && !IsBusy && HasSpeech;

    public bool CanSave => !IsBusy && HasSpeech;

    /// <summary><c>false</c> while the range holds no speech: the preview shows the empty-state line and there is nothing to copy or save.</summary>
    public bool HasSpeech => Range is not { } part || _transcript.Segments.Any(segment => Overlaps(part, segment));

    /// <summary>The last preview run (for tests); completes when the preview shows the current options.</summary>
    internal Task PreviewTask { get; private set; }

    /// <summary>The last copy or save (for tests).</summary>
    internal Task ActionTask { get; private set; } = Task.CompletedTask;

    /// <summary>Raised when the dialog should go away (Esc, the close button, the scrim).</summary>
    public event Action? CloseRequested;

    /// <summary>The options as the formatter takes them.</summary>
    internal ExportOptions Options => new(IncludeSummary, IncludeActionItems, IncludeTimestamps, IncludeSpeakerNames);

    internal ExportRange? Range => !IsWholeRange && _chapter is { } part ? new ExportRange(part.Start, part.End) : null;

    [RelayCommand]
    private void Close()
    {
        CloseRequested?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void Copy() => ActionTask = CopyAsync();

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => ActionTask = SaveAsync();

    partial void OnFormatChanged(ExportFormat value)
    {
        Status = string.Empty;
        CopyCommand.NotifyCanExecuteChanged();
        PreviewTask = RefreshPreviewAsync(immediately: false);
    }

    partial void OnIsWholeRangeChanged(bool value)
    {
        Status = string.Empty;
        CopyCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        PreviewTask = RefreshPreviewAsync(immediately: false);
    }

    partial void OnIsBusyChanged(bool value)
    {
        CopyCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void Choose(bool value, ExportFormat format)
    {
        // A radio button reports "unchecked" when another one is chosen; only the choice counts.
        if (value)
        {
            Format = format;
        }
    }

    private void Set(ref bool field, bool value, string property)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(property);
        Status = string.Empty;
        PreviewTask = RefreshPreviewAsync(immediately: false);
    }

    private async Task RefreshPreviewAsync(bool immediately)
    {
        var previous = _previewCts;
        var source = new CancellationTokenSource();
        _previewCts = source;
        previous?.Cancel();
        previous?.Dispose();
        var token = source.Token;
        var format = Format;
        var options = Options;
        var range = Range;
        string text;
        try
        {
            if (!immediately && _debounce > TimeSpan.Zero)
            {
                await Task.Delay(_debounce, token).ConfigureAwait(true);
            }

            text = await Task.Run(() => PreviewOf(format, options, range, token), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // A newer change took over: the build stopped at the next segment.
            return;
        }
        catch (Exception)
        {
            // A formatter that fails must not leave the task faulted and the preview stale: the dialog says so where the text would be.
            text = Strings.Export_PreviewFailed;
        }

        if (!token.IsCancellationRequested && !_disposed)
        {
            Preview = text;
        }
    }

    /// <summary>The preview: the export built up to <see cref="MaxPreviewChars"/> characters, or the empty-state line when the range holds no speech.</summary>
    private string PreviewOf(ExportFormat format, ExportOptions options, ExportRange? range, CancellationToken cancellationToken)
    {
        if (range is { } part && !_transcript.Segments.Any(segment => Overlaps(part, segment)))
        {
            return Strings.Export_PreviewEmpty;
        }

        // The formatter stops building at the budget: a three-hour transcript is never formatted whole for the preview.
        return TranscriptExport.Format(format, _record with { Title = Title }, _transcript, _summary, _labels, options, range, MaxPreviewChars, cancellationToken);
    }

    /// <summary>The same test the formatter makes: the segment overlaps <c>[Start, End)</c>, a segment without length counts as a point.</summary>
    private static bool Overlaps(ExportRange range, TranscriptSegment segment) =>
        range.End > range.Start
        && segment.Start < range.End
        && (segment.End > segment.Start ? segment.End > range.Start : segment.Start >= range.Start);

    /// <summary>The text of the export as the file will have it (a .docx is shown as its Markdown).</summary>
    private string Build(ExportFormat format, ExportOptions options, ExportRange? range) =>
        TranscriptExport.Format(format, _record with { Title = Title }, _transcript, _summary, _labels, options, range);

    /// <summary>
    /// Where the recording ends: its length, or where the last words end when they run past it (or the length is not known). The whole
    /// recording and the last chapter both end here.
    /// </summary>
    internal static TimeSpan EndOfRecording(SessionRecord record, Transcript transcript)
    {
        var end = record.Duration;
        foreach (var segment in transcript.Segments)
        {
            if (segment.End > end)
            {
                end = segment.End;
            }
        }

        return end;
    }

    private async Task CopyAsync()
    {
        if (Clipboard is not { } clipboard || Format == ExportFormat.Docx)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var format = Format;
            var options = Options;
            var range = Range;
            var text = await Task.Run(() => Build(format, options, range)).ConfigureAwait(true);
            await clipboard(text).ConfigureAwait(true);
            Status = Strings.Export_Copied;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = UiText.Format(Strings.Export_CopyFailed, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            var format = Format;
            var options = Options;
            var range = Range;
            var extension = TranscriptExport.Extension(format);
            var name = FileNames.Safe(Title) + extension;
            var typeName = TypeName(format);
            string? saved;
            if (format == ExportFormat.Docx)
            {
                var bytes = await Task.Run(() => TranscriptExport.ToDocx(_record with { Title = Title }, _transcript, _summary, _labels, options, range)).ConfigureAwait(true);
                saved = await _saver.SaveBytesAsync(Strings.Session_ExportTitle, name, typeName, extension, bytes).ConfigureAwait(true);
            }
            else
            {
                var text = await Task.Run(() => Build(format, options, range)).ConfigureAwait(true);
                saved = await _saver.SaveTextAsync(Strings.Session_ExportTitle, name, typeName, extension, text).ConfigureAwait(true);
            }

            Status = saved is null ? string.Empty : UiText.Format(Strings.Session_ExportSaved, saved);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The dialog, the file system and the stream can each fail in their own way (NotSupportedException, a COM error ...).
            Status = UiText.Format(Strings.Session_ExportFailed, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string TypeName(ExportFormat format) => format switch
    {
        ExportFormat.Markdown => Strings.Session_ExportMarkdown,
        ExportFormat.PlainText => Strings.Export_FormatPlainText,
        ExportFormat.Srt => Strings.Session_ExportSrt,
        ExportFormat.WebVtt => Strings.Session_ExportWebVtt,
        _ => Strings.Export_FormatWord,
    };

    /// <summary><c>12:15 – 24:30</c> (an en dash with spaces, as in the mockup).</summary>
    private static string RangeText(TimeSpan start, TimeSpan end)
    {
        // From an hour on both ends carry hours ("0:24:30 – 1:00:00"), so the two read alike.
        var hours = end >= TimeSpan.FromHours(1);
        return Time(start, hours) + " – " + Time(end, hours);
    }

    private static string Time(TimeSpan time, bool withHours) =>
        withHours
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
            : UiFormat.Clock(time);

    public void Dispose()
    {
        _disposed = true;
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
    }
}
