using System.IO.Compression;
using Murmur.App.Localization;
using Murmur.App.Services;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Intelligence;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.App.Tests;

/// <summary>The view model of the export dialog (#60): the preview, the locks of the subtitle format, the range, Copy and Save.</summary>
public sealed class ExportDialogTests : IDisposable
{
    private readonly IDisposable _english = UiCulture.Override("en");
    private readonly RecordingFileSaver _saver = new();
    private readonly List<string> _clipboard = [];

    private static readonly SessionRecord Record = Sessions.Make(
        "Client demo: Q4/roadmap",
        new DateTimeOffset(2026, 10, 18, 10, 30, 0, TimeSpan.Zero),
        new TimeSpan(0, 47, 12),
        state: SessionState.Ready);

    private static TranscriptSegment Segment(Speaker speaker, double start, double end, string text) =>
        new(speaker, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text, []);

    private static readonly Transcript Transcript = new(
        Record.Id,
        [
            Segment(Speaker.Me, 712, 727, "Okay, a quick run through the roadmap."),
            Segment(Speaker.Others, 728, 740, "Does search match exact words?"),
            Segment(Speaker.Me, 741, 760, "Both: full-text plus semantic."),
            Segment(Speaker.Others, 800, 812, "Great, what about PDF export?"),
        ],
        "parakeet",
        "en");

    private static readonly SessionSummary Summary = FakeSummarizer.Sample(Record.Id);

    private static readonly ExportChapterRange Chapter = new("PDF export", TimeSpan.FromSeconds(735), TimeSpan.FromSeconds(1470));

    public void Dispose() => _english.Dispose();

    private ExportDialogViewModel Create(ExportChapterRange? chapter = null, SessionSummary? summary = null, bool clipboard = true, Transcript? transcript = null, TimeSpan? debounce = null)
    {
        var vm = new ExportDialogViewModel(Record, transcript ?? Transcript, summary ?? Summary, ExportLabels.English, _saver, chapter, debounce: debounce ?? TimeSpan.Zero);
        if (clipboard)
        {
            vm.Clipboard = text =>
            {
                _clipboard.Add(text);
                return Task.CompletedTask;
            };
        }

        return vm;
    }

    private static async Task<ExportDialogViewModel> Settled(ExportDialogViewModel vm)
    {
        await vm.PreviewTask;
        return vm;
    }

    // The preview -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheDialog_StartsOnMarkdown_WithEverythingIncluded_AndItsPreview()
    {
        using var vm = await Settled(Create());

        Assert.Equal(ExportFormat.Markdown, vm.Format);
        Assert.True(vm.IncludeSummary && vm.IncludeActionItems && vm.IncludeTimestamps && vm.IncludeSpeakerNames);
        Assert.True(vm.IsWholeRange);
        Assert.Equal("Client demo: Q4/roadmap", vm.Title);
        Assert.Equal("Client demo_ Q4_roadmap.md", vm.FileName);
        Assert.Equal("Save .md", vm.SaveLabel);
        Assert.Equal(TranscriptExport.Format(ExportFormat.Markdown, Record, Transcript, Summary, ExportLabels.English, ExportOptions.Default, null, TestContext.Current.CancellationToken), vm.Preview);
        Assert.StartsWith("# Client demo: Q4/roadmap\n", vm.Preview, StringComparison.Ordinal);
        Assert.Contains("## Summary", vm.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryOption_UpdatesThePreview()
    {
        using var vm = await Settled(Create());

        vm.IncludeSummary = false;
        await vm.PreviewTask;
        Assert.DoesNotContain("## Summary", vm.Preview, StringComparison.Ordinal);
        Assert.Contains("\n## Action items\n", vm.Preview, StringComparison.Ordinal);

        vm.IncludeActionItems = false;
        await vm.PreviewTask;
        Assert.DoesNotContain("Action items", vm.Preview, StringComparison.Ordinal);

        vm.IncludeTimestamps = false;
        await vm.PreviewTask;
        Assert.DoesNotContain("`", vm.Preview, StringComparison.Ordinal);
        Assert.Contains("**You**  \nOkay", vm.Preview, StringComparison.Ordinal);

        vm.IncludeSpeakerNames = false;
        await vm.PreviewTask;
        Assert.DoesNotContain("**", vm.Preview, StringComparison.Ordinal);
        Assert.Contains("## Transcript\n\nOkay, a quick run through the roadmap.\n\nDoes search", vm.Preview, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExportFormat.PlainText, "[11:52] You: Okay")]
    [InlineData(ExportFormat.Srt, "1\n00:11:52,000 --> 00:12:07,000\nYou: Okay")]
    [InlineData(ExportFormat.Docx, "**You** `11:52`")]
    public async Task TheFormat_ChangesThePreview(ExportFormat format, string expected)
    {
        using var vm = await Settled(Create());

        vm.Format = format;
        await vm.PreviewTask;

        Assert.Contains(expected, vm.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRadioPills_ChooseTheFormat_AndAnUncheckIsNotAChoice()
    {
        using var vm = await Settled(Create());

        vm.IsPlainText = true;
        Assert.Equal(ExportFormat.PlainText, vm.Format);
        Assert.True(vm.IsPlainText);
        Assert.False(vm.IsMarkdown);

        vm.IsMarkdown = false;
        Assert.Equal(ExportFormat.PlainText, vm.Format);

        vm.IsSrt = true;
        Assert.Equal(ExportFormat.Srt, vm.Format);
        vm.IsDocx = true;
        Assert.Equal(ExportFormat.Docx, vm.Format);
        vm.IsMarkdown = true;
        Assert.Equal(ExportFormat.Markdown, vm.Format);
    }

    [Fact]
    public async Task ThePreviewFont_IsMonoForTheSubtitlesAndPlainText_TheTextFaceForMarkdownAndWord()
    {
        using var vm = await Settled(Create());

        Assert.Equal([false, true, true, false], new[] { ExportFormat.Markdown, ExportFormat.PlainText, ExportFormat.Srt, ExportFormat.Docx }
            .Select(format =>
            {
                vm.Format = format;
                return vm.PreviewIsMono;
            }));
    }

    [Fact]
    public async Task AChangeInARow_RunsOnce_AfterTheDebounce_AndAnOvertakenRunNeverShows()
    {
        using var vm = await Settled(Create(debounce: TimeSpan.FromMilliseconds(80)));
        var before = vm.Preview;

        vm.IncludeSummary = false;
        var overtaken = vm.PreviewTask;
        vm.IncludeActionItems = false;
        vm.Format = ExportFormat.PlainText;
        await vm.PreviewTask;
        await overtaken;

        Assert.NotEqual(before, vm.Preview);
        Assert.StartsWith("Client demo: Q4/roadmap\n", vm.Preview, StringComparison.Ordinal);
        Assert.DoesNotContain("SUMMARY", vm.Preview, StringComparison.Ordinal);
        Assert.DoesNotContain("ACTION ITEMS", vm.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVeryLongTranscript_HasACutPreview_ButTheFileHasAllOfIt()
    {
        var segments = Enumerable.Range(0, 4000).Select(i => Segment(Speaker.Me, i * 5, (i * 5) + 4, "Line number " + i + " of a long meeting.")).ToArray();
        var long_ = new Transcript(Record.Id, segments, "parakeet", "en");
        using var vm = await Settled(Create(transcript: long_));

        Assert.InRange(vm.Preview.Length, 1000, ExportDialogViewModel.MaxPreviewChars + 10);
        Assert.EndsWith("\n…\n", vm.Preview, StringComparison.Ordinal);

        vm.SaveCommand.Execute(null);
        await vm.ActionTask;
        Assert.Contains("Line number 3999 of a long meeting.", _saver.Texts[0].Content, StringComparison.Ordinal);
    }

    // Subtitles -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Subtitles_DisableSummaryAndActionItems_AndLockTheTimestampsOn()
    {
        using var vm = await Settled(Create());
        vm.IncludeSummary = false;
        vm.IncludeTimestamps = false;

        vm.IsSrt = true;

        Assert.True(vm.IsSubtitles);
        Assert.False(vm.SummaryEnabled);
        Assert.False(vm.ActionItemsEnabled);
        Assert.False(vm.TimestampsEnabled);
        Assert.False(vm.IncludeSummary);
        Assert.False(vm.IncludeActionItems);
        Assert.True(vm.IncludeTimestamps);
        Assert.True(vm.IncludeSpeakerNames);
        await vm.PreviewTask;
        Assert.Contains("00:11:52,000 --> 00:12:07,000", vm.Preview, StringComparison.Ordinal);
        Assert.DoesNotContain("Walked the client", vm.Preview, StringComparison.Ordinal);

        // What the user chose before is back when a format without the locks is chosen again.
        vm.IsMarkdown = true;
        Assert.True(vm.SummaryEnabled && vm.ActionItemsEnabled && vm.TimestampsEnabled);
        Assert.False(vm.IncludeSummary);
        Assert.True(vm.IncludeActionItems);
        Assert.False(vm.IncludeTimestamps);
    }

    [Fact]
    public async Task Subtitles_RaisePropertyChangedForTheLockedBoxes()
    {
        using var vm = await Settled(Create());
        var collected = new List<string?>();
        vm.PropertyChanged += (_, e) =>
        {
            lock (collected)
            {
                collected.Add(e.PropertyName);
            }
        };

        vm.IsSrt = true;

        // The preview is set from the thread pool (there is no UI thread here): let it finish before the list is read.
        await vm.PreviewTask;
        List<string?> raised;
        lock (collected)
        {
            raised = [.. collected];
        }

        Assert.Contains(nameof(ExportDialogViewModel.IncludeSummary), raised);
        Assert.Contains(nameof(ExportDialogViewModel.IncludeActionItems), raised);
        Assert.Contains(nameof(ExportDialogViewModel.IncludeTimestamps), raised);
        Assert.Contains(nameof(ExportDialogViewModel.SummaryEnabled), raised);
        Assert.Contains(nameof(ExportDialogViewModel.IsSubtitles), raised);
    }

    // The range -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutAChapter_ThereIsOnlyTheWholeRecording()
    {
        using var vm = await Settled(Create());

        Assert.False(vm.HasChapterRange);
        Assert.Equal("00:00 – 47:12", vm.WholeSpan);
        vm.IsChapterRange = true;
        Assert.True(vm.IsWholeRange);
        Assert.Null(vm.Range);
    }

    [Fact]
    public async Task WithAChapter_TheRangeOptionNamesItAndShowsItsTimes()
    {
        using var vm = await Settled(Create(Chapter));

        Assert.True(vm.HasChapterRange);
        Assert.Equal("Chapter: PDF export", vm.ChapterLabel);
        Assert.Equal("12:15 – 24:30", vm.ChapterSpan);

        vm.IsChapterRange = true;
        await vm.PreviewTask;

        Assert.False(vm.IsWholeRange);
        Assert.Equal(new ExportRange(Chapter.Start, Chapter.End), vm.Range);
        Assert.Contains("Great, what about PDF export?", vm.Preview, StringComparison.Ordinal);
        Assert.Contains("Does search match", vm.Preview, StringComparison.Ordinal);
        Assert.DoesNotContain("quick run", vm.Preview, StringComparison.Ordinal);

        vm.IsWholeRange = true;
        await vm.PreviewTask;
        Assert.Contains("quick run", vm.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheChapterRange_GoesIntoTheSavedFile_WithTheTimesOfTheSession()
    {
        using var vm = await Settled(Create(Chapter));
        vm.IsChapterRange = true;
        vm.IsSrt = true;

        vm.SaveCommand.Execute(null);
        await vm.ActionTask;

        var srt = _saver.Texts.Single().Content;
        Assert.StartsWith("1\n00:12:08,000 --> 00:12:20,000\nOthers: Does search match exact words?\n\n2\n00:12:21,000", srt, StringComparison.Ordinal);
        Assert.Contains("\n3\n00:13:20,000 --> 00:13:32,000\nOthers: Great, what about PDF export?\n\n", srt, StringComparison.Ordinal);
        Assert.DoesNotContain("quick run", srt, StringComparison.Ordinal);
    }

    // Copy and Save ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Copy_PutsTheWholeTextOnTheClipboard_AndSaysSo()
    {
        using var vm = await Settled(Create());
        vm.IsPlainText = true;
        vm.IncludeTimestamps = false;

        Assert.True(vm.CopyCommand.CanExecute(null));
        vm.CopyCommand.Execute(null);
        await vm.ActionTask;

        Assert.Equal(TranscriptExport.Format(ExportFormat.PlainText, Record, Transcript, Summary, ExportLabels.English, new ExportOptions(Timestamps: false), null, TestContext.Current.CancellationToken), _clipboard.Single());
        Assert.Equal("Copied to clipboard", vm.Status);
        Assert.True(vm.HasStatus);

        vm.IncludeSummary = false;
        Assert.Equal(string.Empty, vm.Status);
    }

    [Fact]
    public async Task Copy_IsDisabledForWord_AndWithoutAClipboard()
    {
        using var vm = await Settled(Create());
        var notified = 0;
        vm.CopyCommand.CanExecuteChanged += (_, _) => notified++;

        vm.IsDocx = true;
        Assert.False(vm.CanCopy);
        Assert.False(vm.CopyCommand.CanExecute(null));
        Assert.True(notified > 0);

        vm.IsSrt = true;
        Assert.True(vm.CopyCommand.CanExecute(null));

        using var bare = await Settled(Create(clipboard: false));
        Assert.False(bare.CanCopy);
    }

    [Fact]
    public async Task Copy_ReportsAClipboardThatFails()
    {
        using var vm = await Settled(Create());
        vm.Clipboard = _ => throw new InvalidOperationException("the clipboard is busy");

        vm.CopyCommand.Execute(null);
        await vm.ActionTask;

        Assert.Equal("Couldn’t copy: the clipboard is busy", vm.Status);
        Assert.False(vm.IsBusy);
    }

    [Theory]
    [InlineData(ExportFormat.Markdown, ".md", "Save .md")]
    [InlineData(ExportFormat.PlainText, ".txt", "Save .txt")]
    [InlineData(ExportFormat.Srt, ".srt", "Save .srt")]
    public async Task Save_WritesTheTextThroughTheFileSaver_UnderTheTitleAndTheExtension(ExportFormat format, string extension, string label)
    {
        using var vm = await Settled(Create());
        vm.Format = format;
        vm.IncludeSpeakerNames = false;

        Assert.Equal(label, vm.SaveLabel);
        vm.SaveCommand.Execute(null);
        await vm.ActionTask;

        var saved = Assert.Single(_saver.Texts);
        Assert.Equal("Client demo_ Q4_roadmap" + extension, saved.SuggestedName);
        Assert.Equal(extension, saved.Extension);
        Assert.Equal(TranscriptExport.Format(format, Record, Transcript, Summary, ExportLabels.English, new ExportOptions(SpeakerNames: false), null, TestContext.Current.CancellationToken), saved.Content);
        Assert.Equal("Saved export" + extension, vm.Status);
        Assert.Empty(_saver.Binaries);
    }

    [Fact]
    public async Task Save_AsWord_WritesTheDocxBytesThroughTheBinaryOverload()
    {
        using var vm = await Settled(Create());
        vm.IsDocx = true;
        vm.IncludeActionItems = false;

        Assert.Equal("Save .docx", vm.SaveLabel);
        Assert.Equal("Client demo_ Q4_roadmap.docx", vm.FileName);
        vm.SaveCommand.Execute(null);
        await vm.ActionTask;

        var saved = Assert.Single(_saver.Binaries);
        Assert.Equal("Client demo_ Q4_roadmap.docx", saved.SuggestedName);
        Assert.Equal(".docx", saved.Extension);
        Assert.Equal(
            TranscriptExport.ToDocx(Record, Transcript, Summary, ExportLabels.English, new ExportOptions(ActionItems: false), null),
            saved.Content);
        using var zip = new ZipArchive(new MemoryStream(saved.Content), ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("word/document.xml"));
        Assert.Empty(_saver.Texts);
        Assert.Equal("Saved export.docx", vm.Status);
    }

    [Fact]
    public async Task Save_ACancelledDialog_SaysNothing_AFailureSaysWhy()
    {
        using var vm = await Settled(Create());

        _saver.Cancel = true;
        vm.SaveCommand.Execute(null);
        await vm.ActionTask;
        Assert.Equal(string.Empty, vm.Status);

        _saver.Cancel = false;
        _saver.Failure = new IOException("disk full");
        vm.SaveCommand.Execute(null);
        await vm.ActionTask;
        Assert.Equal("Couldn’t save the file: disk full", vm.Status);
        Assert.False(vm.IsBusy);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheSummary_IsLeftOut_WhenTheRecordingHasNone()
    {
        var noSummary = new ExportDialogViewModel(Record, Transcript, null, ExportLabels.English, _saver, debounce: TimeSpan.Zero);
        await noSummary.PreviewTask;

        Assert.DoesNotContain("## Summary", noSummary.Preview, StringComparison.Ordinal);
        Assert.DoesNotContain("## Action items", noSummary.Preview, StringComparison.Ordinal);
        noSummary.Dispose();
    }

    [Fact]
    public async Task Close_RaisesCloseRequested()
    {
        using var vm = await Settled(Create());
        var closed = 0;
        vm.CloseRequested += () => closed++;

        vm.CloseCommand.Execute(null);

        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task TheTexts_AreInTheLanguageOfTheUi()
    {
        using var ukrainian = UiCulture.Override("uk");
        using var vm = await Settled(Create(Chapter));

        Assert.Equal("Зберегти .md", vm.SaveLabel);
        Assert.Equal("Розділ: PDF export", vm.ChapterLabel);
    }

    // The range text, the empty range and a formatter that fails ------------------------------------------------

    [Fact]
    public async Task FromAnHourOn_BothEndsOfARangeCarryHours()
    {
        var late = new ExportChapterRange("Late", TimeSpan.FromSeconds(3300), TimeSpan.FromSeconds(4000));
        using var vm = await Settled(Create(late));

        Assert.Equal("0:55:00 – 1:06:40", vm.ChapterSpan);
        Assert.Equal("00:00 – 47:12", vm.WholeSpan);
    }

    [Fact]
    public async Task TheWholeRecording_EndsWhereTheLastWordsEnd_WhenTheyRunPastTheLength()
    {
        var segments = new[] { Segment(Speaker.Me, 0, 10, "Short."), Segment(Speaker.Others, 3000, 4000, "Past the stored length.") };
        var past = new Transcript(Record.Id, segments, "parakeet", "en");
        using var vm = await Settled(Create(transcript: past));

        Assert.Equal(TimeSpan.FromSeconds(4000), ExportDialogViewModel.EndOfRecording(Record, past));
        Assert.Equal("0:00:00 – 1:06:40", vm.WholeSpan);
    }

    [Fact]
    public async Task ARangeWithoutSpeech_ShowsAnEmptyStateLine_InEveryFormat()
    {
        var quiet = new ExportChapterRange("Silence", TimeSpan.FromSeconds(2000), TimeSpan.FromSeconds(3000));
        using var vm = await Settled(Create(quiet));
        vm.IsChapterRange = true;

        foreach (var format in new[] { ExportFormat.Markdown, ExportFormat.PlainText, ExportFormat.Srt, ExportFormat.Docx })
        {
            vm.Format = format;
            await vm.PreviewTask;

            Assert.Equal("Nothing was said in this range.", vm.Preview);
        }

        vm.IsWholeRange = true;
        await vm.PreviewTask;
        Assert.Contains("quick run", vm.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEmptyStateLine_IsInUkrainianToo()
    {
        using var ukrainian = UiCulture.Override("uk");
        var quiet = new ExportChapterRange("Тиша", TimeSpan.FromSeconds(2000), TimeSpan.FromSeconds(3000));
        using var vm = await Settled(Create(quiet));

        vm.IsChapterRange = true;
        await vm.PreviewTask;

        Assert.Equal("У цьому діапазоні нічого не сказано.", vm.Preview);
    }

    [Fact]
    public async Task AFormatterThatFails_ShowsAShortErrorLine_AndLeavesNoFaultedTask()
    {
        var broken = new Transcript(Record.Id, [new TranscriptSegment(Speaker.Me, TimeSpan.Zero, TimeSpan.FromSeconds(1), null!, [])], "parakeet", "en");
        using var vm = Create(transcript: broken);

        await vm.PreviewTask;
        Assert.Equal(TaskStatus.RanToCompletion, vm.PreviewTask.Status);
        Assert.Equal("Couldn’t build the preview.", vm.Preview);

        // The next change is formatted again (here: it fails the same way), the dialog stays usable.
        vm.IncludeSummary = false;
        await vm.PreviewTask;
        Assert.Equal(TaskStatus.RanToCompletion, vm.PreviewTask.Status);
        Assert.Equal("Couldn’t build the preview.", vm.Preview);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheErrorLine_IsInUkrainianToo()
    {
        using var ukrainian = UiCulture.Override("uk");
        var broken = new Transcript(Record.Id, [new TranscriptSegment(Speaker.Me, TimeSpan.Zero, TimeSpan.FromSeconds(1), null!, [])], "parakeet", "en");
        using var vm = Create(transcript: broken);

        await vm.PreviewTask;

        Assert.Equal("Не вдалося побудувати попередній перегляд.", vm.Preview);
    }

    [Fact]
    public async Task ASupersededBuild_NeverShows_AndNeverFaults()
    {
        // A long transcript, changed again and again while the first builds still run: only the last state is shown.
        var segments = Enumerable.Range(0, 20_000).Select(i => Segment(Speaker.Me, i * 2, (i * 2) + 1, "Line number " + i)).ToArray();
        var big = new Transcript(Record.Id, segments, "parakeet", "en");
        using var vm = Create(transcript: big);

        vm.Format = ExportFormat.PlainText;
        var first = vm.PreviewTask;
        vm.Format = ExportFormat.Srt;
        var second = vm.PreviewTask;
        vm.IncludeSpeakerNames = false;
        await vm.PreviewTask;
        await first;
        await second;

        Assert.Equal(TaskStatus.RanToCompletion, first.Status);
        Assert.StartsWith("1\n00:00:00,000 --> 00:00:01,000\nLine number 0", vm.Preview, StringComparison.Ordinal);
    }

    /// <summary>An <see cref="IFileSaver"/> that remembers what it was asked to write.</summary>
    private sealed class RecordingFileSaver : IFileSaver
    {
        public List<(string SuggestedName, string Extension, string Content)> Texts { get; } = [];

        public List<(string SuggestedName, string Extension, byte[] Content)> Binaries { get; } = [];

        public bool Cancel { get; set; }

        public Exception? Failure { get; set; }

        public Task<string?> SaveTextAsync(string title, string suggestedFileName, string fileTypeName, string extension, string content)
        {
            if (Failure is { } failure)
            {
                throw failure;
            }

            if (Cancel)
            {
                return Task.FromResult<string?>(null);
            }

            Texts.Add((suggestedFileName, extension, content));
            return Task.FromResult<string?>("export" + extension);
        }

        public Task<string?> SaveBytesAsync(string title, string suggestedFileName, string fileTypeName, string extension, byte[] content)
        {
            if (Failure is { } failure)
            {
                throw failure;
            }

            if (Cancel)
            {
                return Task.FromResult<string?>(null);
            }

            Binaries.Add((suggestedFileName, extension, content));
            return Task.FromResult<string?>("export" + extension);
        }
    }
}
