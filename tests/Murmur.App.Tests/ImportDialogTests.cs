using Murmur.App.Localization;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Import;

namespace Murmur.App.Tests;

/// <summary>The import dialog's view model (#67): the list, the probes, Recent in Downloads, the options and what Import hands over.</summary>
public sealed class ImportDialogTests : IDisposable
{
    private static readonly DateTimeOffset Now = UiHarness.Now;

    private readonly IDisposable _english = UiCulture.Override("en");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "murmur-import-" + Guid.NewGuid().ToString("N"));
    private readonly FakeImportQueue _queue = new();
    private readonly FakeMediaDecoder _decoder = new();
    private readonly FakeMediaFilePicker _picker = new();
    private readonly TestTime _time = new(Now);

    public ImportDialogTests()
    {
        Directory.CreateDirectory(_root);
        _decoder.Answer("Zoom_Meeting_2026-10-30.mp4", MediaKind.Video, 412_000_000, new TimeSpan(0, 45, 10));
        _decoder.Answer("voice-memo-1029.m4a", MediaKind.Audio, 6_000_000, new TimeSpan(0, 6, 28));
    }

    public void Dispose()
    {
        _english.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string P(string name) => Path.Combine(_root, name);

    private ImportDialogViewModel Create(IDownloadsFolder? downloads = null, bool probe = true, bool picker = true) =>
        new(_queue, probe ? _decoder : null, downloads, picker ? _picker : null, _time, action => action());

    private async Task<ImportDialogViewModel> WithFilesAsync(params string[] names)
    {
        var vm = Create();
        vm.AddFiles(names.Select(P));
        await vm.WhenProbed();
        return vm;
    }

    /// <summary>A file in the Downloads folder of the test, written <paramref name="age"/> before now.</summary>
    private string Download(string name, TimeSpan age, long bytes = 1000)
    {
        var folder = Directory.CreateDirectory(P("Downloads")).FullName;
        var path = Path.Combine(folder, name);
        using (var stream = File.Create(path))
        {
            stream.SetLength(bytes);
        }

        File.SetLastWriteTimeUtc(path, (Now - age).UtcDateTime);
        return path;
    }

    // Adding, removing, duplicates ------------------------------------------------------------------------------

    [Fact]
    public async Task Browse_AddsThePickedFiles_InOrder_AndSkipsDuplicates()
    {
        using var vm = Create();
        _picker.Answer(P("Zoom_Meeting_2026-10-30.mp4"), P("voice-memo-1029.m4a"), P("Zoom_Meeting_2026-10-30.mp4"));
        _picker.Answer(P("voice-memo-1029.m4a"), P("talk.wav"));

        await vm.BrowseCommand.ExecuteAsync(null);
        await vm.BrowseCommand.ExecuteAsync(null);
        await vm.WhenProbed();

        Assert.Equal(["Zoom_Meeting_2026-10-30.mp4", "voice-memo-1029.m4a", "talk.wav"], vm.Files.Select(f => f.Name));
        Assert.Equal(3, vm.ValidCount);
        var call = _picker.Calls[0];
        Assert.Equal("Choose audio or video files", call.Title);
        Assert.Equal("Audio and video", call.FileTypeName);
        Assert.Equal(FakeImportQueue.MockupExtensions, call.Extensions);
    }

    [Fact]
    public async Task ACancelledPicker_AddsNothing_AndAFailingOne_SaysWhy()
    {
        using var vm = Create();

        await vm.BrowseCommand.ExecuteAsync(null);
        Assert.Empty(vm.Files);
        Assert.False(vm.HasFiles);

        _picker.Fail = new InvalidOperationException("no portal");
        await vm.BrowseCommand.ExecuteAsync(null);
        Assert.Equal("Couldn’t open the file dialog: no portal", vm.Error);
        Assert.False(vm.IsClosed);
    }

    [Fact]
    public void WithoutAPicker_BrowseIsOff()
    {
        using var vm = Create(picker: false);

        Assert.False(vm.CanBrowse);
        Assert.False(vm.BrowseCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheSamePath_InAnotherCase_IsTheSameFile_OnWindowsOnly()
    {
        using var vm = Create();

        vm.AddFiles([P("talk.wav"), P("TALK.WAV"), Path.Combine(_root, ".", "talk.wav")]);
        await vm.WhenProbed();

        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2, vm.Files.Count);
        Assert.Equal(Path.GetFullPath(P("talk.wav")), vm.Files[0].Path);
    }

    [Fact]
    public async Task Remove_TakesTheFileOutOfTheList_AndTheCount()
    {
        using var vm = await WithFilesAsync("Zoom_Meeting_2026-10-30.mp4", "voice-memo-1029.m4a");
        Assert.Equal(2, vm.ValidCount);

        vm.Files[0].RemoveCommand.Execute(null);

        Assert.Equal(["voice-memo-1029.m4a"], vm.Files.Select(f => f.Name));
        Assert.Equal(1, vm.ValidCount);
        Assert.Equal("Import 1 file", vm.ImportLabel);

        vm.Files[0].RemoveCommand.Execute(null);
        Assert.False(vm.HasFiles);
        Assert.False(vm.CanImport);
    }

    [Fact]
    public async Task AFileRemovedWhileItIsProbed_DoesNotComeBack()
    {
        _decoder.Hold();
        using var vm = Create();
        vm.AddFiles([P("voice-memo-1029.m4a")]);
        vm.Files[0].RemoveCommand.Execute(null);

        _decoder.Release();
        await vm.WhenProbed();

        Assert.Empty(vm.Files);
        Assert.Equal(0, vm.ValidCount);
    }

    // Probing ---------------------------------------------------------------------------------------------------

    /// <remarks>
    /// The body runs on a dedicated (non-pool) thread that stays blocked until the probes are done: a pool caller could be handed a
    /// later probe once it awaits, and a finished thread's id could be reused. Every probe must run on a pool thread, never the caller's.
    /// </remarks>
    [Fact]
    public async Task EachFile_IsProbedOffTheCallersThread_AndShowsKindSizeAndDuration()
    {
        _decoder.Hold();
        using var vm = Create();

        var caller = await Task.Factory.StartNew(
            () =>
            {
                Assert.False(Thread.CurrentThread.IsThreadPoolThread);
                vm.AddFiles([P("Zoom_Meeting_2026-10-30.mp4"), P("voice-memo-1029.m4a")]);

                // AddFiles returned while the probes wait: the rows say "…" and nothing counts yet.
                Assert.All(vm.Files, f => Assert.True(f.IsProbing));
                Assert.All(vm.Files, f => Assert.Equal("…", f.Detail));
                Assert.Equal(0, vm.ValidCount);
                Assert.False(vm.ImportCommand.CanExecute(null));

                _decoder.Release();
                vm.WhenProbed().GetAwaiter().GetResult();
                return Environment.CurrentManagedThreadId;
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Assert.Equal(2, _decoder.Threads.Count);
        Assert.All(_decoder.Threads, thread =>
        {
            Assert.True(thread.IsPool);
            Assert.NotEqual(caller, thread.Id);
        });
        Assert.Equal(["Video · 412 MB · 45:10", "Audio · 6 MB · 06:28"], vm.Files.Select(f => f.Detail));
        Assert.True(vm.Files[0].IsVideo);
        Assert.True(vm.Files[1].IsAudio);
        Assert.Equal(2, vm.ValidCount);
        Assert.True(vm.ImportCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnUnsupportedExtension_IsNotProbed_AndDoesNotCount()
    {
        using var vm = await WithFilesAsync("Zoom_Meeting_2026-10-30.mp4", "notes.pdf");

        var notes = vm.Files[1];
        Assert.Equal(ImportFileState.Unsupported, notes.State);
        Assert.Equal("Not supported — pick an audio or video file", notes.Detail);
        Assert.True(notes.IsDimmed);
        Assert.False(notes.IsValid);
        Assert.DoesNotContain(_decoder.Probed, p => p.EndsWith("notes.pdf", StringComparison.Ordinal));
        Assert.Equal(1, vm.ValidCount);
        Assert.Equal("1 file to import", vm.FilesTitle);
    }

    [Fact]
    public async Task WhatTheProbeRejects_SaysWhy_AndDoesNotCount()
    {
        _decoder.Throw("codec.mkv", new NotSupportedException("no decoder"));
        _decoder.Throw("broken.wav", new InvalidDataException("no data chunk"));
        _decoder.Throw("gone.mp3", new FileNotFoundException("gone"));
        _decoder.Throw("locked.flac", new UnauthorizedAccessException("Access denied."));

        using var vm = await WithFilesAsync("codec.mkv", "broken.wav", "gone.mp3", "locked.flac", "voice-memo-1029.m4a");

        Assert.Equal(
            [
                "Not supported — pick an audio or video file",
                "Can’t read this file — it may be damaged",
                "File not found",
                "Couldn’t open the file: Access denied.",
                "Audio · 6 MB · 06:28",
            ],
            vm.Files.Select(f => f.Detail));
        Assert.Equal([true, true, true, true, false], vm.Files.Select(f => f.IsDimmed));
        Assert.Equal(1, vm.ValidCount);
        Assert.Equal("Import 1 file", vm.ImportLabel);
    }

    [Fact]
    public async Task WithoutAProbe_TheSizeIsShown_AndEverySupportedFileCounts()
    {
        var path = P("memo.wav");
        await File.WriteAllBytesAsync(path, new byte[2_500_000], TestContext.Current.CancellationToken);
        using var vm = Create(probe: false);

        vm.AddFiles([path, P("notes.pdf")]);
        await vm.WhenProbed();

        Assert.Equal("3 MB", vm.Files[0].Detail);
        Assert.True(vm.Files[0].IsValid);
        Assert.False(vm.Files[0].IsAudio || vm.Files[0].IsVideo);
        Assert.Equal(1, vm.ValidCount);
    }

    [Fact]
    public async Task AProbeThatFinishesAfterTheDialogClosed_IsDropped()
    {
        _decoder.Hold();
        var vm = Create();
        vm.AddFiles([P("voice-memo-1029.m4a")]);

        vm.Dispose();
        _decoder.Release();
        await vm.WhenProbed();

        Assert.True(vm.Files[0].IsProbing);
        Assert.Equal(0, vm.ValidCount);
    }

    // The button and the counts ---------------------------------------------------------------------------------

    [Fact]
    public async Task TheButtonAndTheTitle_CountTheValidFiles()
    {
        using var vm = Create();
        Assert.Equal("0 files to import", vm.FilesTitle);
        Assert.Equal("Import 0 files", vm.ImportLabel);
        Assert.False(vm.CanImport);
        Assert.False(vm.ImportCommand.CanExecute(null));

        vm.AddFiles([P("voice-memo-1029.m4a")]);
        await vm.WhenProbed();
        Assert.Equal("1 file to import", vm.FilesTitle);
        Assert.Equal("Import 1 file", vm.ImportLabel);
        Assert.True(vm.CanImport);

        vm.AddFiles([P("Zoom_Meeting_2026-10-30.mp4"), P("notes.pdf")]);
        await vm.WhenProbed();
        Assert.Equal("2 files to import", vm.FilesTitle);
        Assert.Equal("Import 2 files", vm.ImportLabel);
    }

    [Theory]
    [InlineData(0, "0 файлів до імпорту", "Імпортувати 0 файлів")]
    [InlineData(1, "1 файл до імпорту", "Імпортувати 1 файл")]
    [InlineData(2, "2 файли до імпорту", "Імпортувати 2 файли")]
    [InlineData(4, "4 файли до імпорту", "Імпортувати 4 файли")]
    [InlineData(5, "5 файлів до імпорту", "Імпортувати 5 файлів")]
    [InlineData(11, "11 файлів до імпорту", "Імпортувати 11 файлів")]
    [InlineData(12, "12 файлів до імпорту", "Імпортувати 12 файлів")]
    [InlineData(21, "21 файл до імпорту", "Імпортувати 21 файл")]
    [InlineData(22, "22 файли до імпорту", "Імпортувати 22 файли")]
    [InlineData(111, "111 файлів до імпорту", "Імпортувати 111 файлів")]
    public void TheCounts_UseTheUkrainianPluralForms(int count, string title, string button)
    {
        using var ukrainian = UiCulture.Override("uk");

        Assert.Equal(title, ImportDialogViewModel.Plural(count, Resources.Strings.Import_CountOne, Resources.Strings.Import_CountFew, Resources.Strings.Import_CountMany));
        Assert.Equal(button, ImportDialogViewModel.Plural(count, Resources.Strings.Import_ButtonOne, Resources.Strings.Import_ButtonFew, Resources.Strings.Import_ButtonMany));
    }

    [Theory]
    [InlineData(0, "0 files to import")]
    [InlineData(1, "1 file to import")]
    [InlineData(2, "2 files to import")]
    [InlineData(21, "21 files to import")]
    public void TheCounts_UseTheEnglishPluralForms(int count, string title)
    {
        Assert.Equal(title, ImportDialogViewModel.Plural(count, Resources.Strings.Import_CountOne, Resources.Strings.Import_CountFew, Resources.Strings.Import_CountMany));
    }

    // The options and Import ------------------------------------------------------------------------------------

    [Fact]
    public void TheOptions_StartAtAutoDetect_AndCopyToLibrary_AndListTheParakeetLanguages()
    {
        using var vm = Create();

        Assert.Null(vm.SelectedLanguage.Code);
        Assert.Equal("Auto-detect", vm.SelectedLanguage.Name);
        Assert.Equal(26, vm.Languages.Count);
        Assert.Equal(SpokenLanguages.All.Select(l => l.NativeName), vm.Languages.Skip(1).Select(l => l.Name));
        Assert.Equal(OriginalFileMode.CopyToLibrary, vm.OriginalFile);
        Assert.True(vm.IsCopyToLibrary);
        Assert.False(vm.IsLeaveInPlace);

        vm.IsLeaveInPlace = true;
        Assert.Equal(OriginalFileMode.LeaveInPlace, vm.OriginalFile);
        Assert.False(vm.IsCopyToLibrary);
        vm.IsLeaveInPlace = false;
        Assert.Equal(OriginalFileMode.LeaveInPlace, vm.OriginalFile);
    }

    [Fact]
    public async Task Import_HandsTheValidFilesWithTheOptions_ToTheQueue_AndCloses()
    {
        using var vm = await WithFilesAsync("Zoom_Meeting_2026-10-30.mp4", "notes.pdf", "voice-memo-1029.m4a");
        var closed = 0;
        vm.CloseRequested += () => closed++;
        vm.SelectedLanguage = vm.Languages.Single(l => l.Code == "de");
        vm.IsLeaveInPlace = true;

        await vm.ImportCommand.ExecuteAsync(null);

        var options = new ImportOptions("de", OriginalFileMode.LeaveInPlace);
        Assert.Equal(
            [new ImportRequest(P("Zoom_Meeting_2026-10-30.mp4"), options), new ImportRequest(P("voice-memo-1029.m4a"), options)],
            _queue.Requests);
        Assert.Equal(1, _queue.Calls);
        Assert.Equal(1, closed);
        Assert.True(vm.IsClosed);
    }

    [Fact]
    public async Task Import_WithTheDefaults_AsksForAutoDetect_AndACopy()
    {
        using var vm = await WithFilesAsync("voice-memo-1029.m4a");

        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Equal([new ImportRequest(P("voice-memo-1029.m4a"), new ImportOptions(null, OriginalFileMode.CopyToLibrary))], _queue.Requests);
    }

    [Fact]
    public async Task AFailingImport_KeepsTheDialogOpen_WithTheError()
    {
        using var vm = await WithFilesAsync("voice-memo-1029.m4a");
        var closed = 0;
        vm.CloseRequested += () => closed++;
        _queue.Fail = new IOException("disk full");

        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Equal(0, closed);
        Assert.False(vm.IsClosed);
        Assert.True(vm.HasError);
        Assert.Equal("Couldn’t start the import: disk full", vm.Error);
        Assert.False(vm.IsImporting);
        Assert.True(vm.ImportCommand.CanExecute(null));

        _queue.Fail = null;
        await vm.ImportCommand.ExecuteAsync(null);
        Assert.Null(vm.Error);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task Closing_ImportsNothing()
    {
        using var vm = await WithFilesAsync("voice-memo-1029.m4a");
        var closed = 0;
        vm.CloseRequested += () => closed++;

        vm.CloseCommand.Execute(null);
        vm.CloseCommand.Execute(null);

        Assert.Equal(1, closed);
        Assert.True(vm.IsClosed);
        Assert.Equal(0, _queue.Calls);
        Assert.Empty(_queue.Requests);
    }

    [Fact]
    public async Task WhileTheFilesAreHandedOver_CancelAndTheCross_AreOff_AndCloseDoesNothing()
    {
        using var vm = await WithFilesAsync("voice-memo-1029.m4a");
        var closed = 0;
        vm.CloseRequested += () => closed++;
        _queue.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var import = vm.ImportCommand.ExecuteAsync(null);

        Assert.True(vm.IsImporting);
        Assert.False(vm.CanClose);
        Assert.False(vm.CloseCommand.CanExecute(null));
        vm.CloseCommand.Execute(null); // Esc and the scrim do not ask CanExecute
        Assert.False(vm.IsClosed);

        _queue.Gate.SetResult();
        await import;

        Assert.Equal(1, closed);
        Assert.True(vm.IsClosed);
        Assert.Single(_queue.Requests);
    }

    [Fact]
    public async Task Dispose_ReleasesTheTokenAndTheProbeGate_OnceTheProbesHaveStopped_AndLeavesTheQueue()
    {
        _decoder.Hold();
        var vm = Create();
        vm.AddFiles([P("voice-memo-1029.m4a")]);
        Assert.Equal(1, _queue.Subscribers);

        vm.Dispose();
        await vm.WhenReleased.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await vm.WhenProbed().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(0, _queue.Subscribers);
        Assert.True(vm.Files[0].IsProbing); // the cancelled probe left its row alone
        vm.AddFiles([P("talk.wav")]);
        Assert.Single(vm.Files);
        vm.Dispose();
    }

    // Recent in Downloads ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RecentInDownloads_ListsTheNewestMedia_OfTheLast30Days_AtMostFive()
    {
        Download("podcast-ep42.mp3", TimeSpan.FromDays(8), 58_000_000);
        Download("today.wav", TimeSpan.FromHours(2));
        Download("yesterday.m4a", TimeSpan.FromDays(1));
        Download("monday.mp4", TimeSpan.FromDays(3));
        Download("older.ogg", TimeSpan.FromDays(20));
        Download("oldest.flac", TimeSpan.FromDays(25));
        Download("too-old.wav", TimeSpan.FromDays(31));
        Download("notes.pdf", TimeSpan.FromHours(1));
        using var vm = Create(new FakeDownloadsFolder(P("Downloads")));

        await vm.RecentTask;

        Assert.True(vm.HasRecent);
        Assert.Equal(["today.wav", "yesterday.m4a", "monday.mp4", "podcast-ep42.mp3", "older.ogg"], vm.Recent.Select(r => r.Name));
        Assert.Equal("58 MB · Last week", vm.Recent[3].Detail);
        Assert.Equal("1 KB · Today", vm.Recent[0].Detail);
        Assert.Equal("1 KB · Yesterday", vm.Recent[1].Detail);
        Assert.Equal("1 KB · This week", vm.Recent[2].Detail);
        Assert.Equal("1 KB · Sep 28", vm.Recent[4].Detail);
        Assert.True(vm.Recent[2].IsVideo);
        Assert.False(vm.Recent[0].IsVideo);
    }

    [Fact]
    public async Task Add_PutsADownloadInTheList_AndTheRowShowsItAsAdded_UntilItIsRemoved()
    {
        var podcast = Download("podcast-ep42.mp3", TimeSpan.FromDays(8), 58_000_000);
        using var vm = Create(new FakeDownloadsFolder(P("Downloads")));
        await vm.RecentTask;
        var row = Assert.Single(vm.Recent);
        Assert.Equal("Add", row.AddLabel);
        Assert.True(row.AddCommand.CanExecute(null));

        row.AddCommand.Execute(null);
        await vm.WhenProbed();

        Assert.Equal([podcast], vm.Files.Select(f => f.Path));
        Assert.True(row.IsAdded);
        Assert.Equal("Added", row.AddLabel);
        Assert.False(row.AddCommand.CanExecute(null));

        // The same file picked again is not added twice.
        _picker.Answer(podcast);
        await vm.BrowseCommand.ExecuteAsync(null);
        Assert.Single(vm.Files);

        vm.Files[0].RemoveCommand.Execute(null);
        Assert.False(row.IsAdded);
        Assert.True(row.AddCommand.CanExecute(null));
    }

    [Fact]
    public async Task ADownloadThatIsAlreadyInTheList_ShowsAsAdded()
    {
        var podcast = Download("podcast-ep42.mp3", TimeSpan.FromDays(8));
        using var vm = Create(new FakeDownloadsFolder(P("Downloads")));
        await vm.RecentTask;

        vm.AddFiles([podcast]);

        Assert.True(vm.Recent[0].IsAdded);
    }

    [Fact]
    public async Task ADownloadThatAlreadyWaitsInTheQueue_ShowsAsAdded_AndFollowsTheQueue()
    {
        var podcast = Download("podcast-ep42.mp3", TimeSpan.FromDays(8));
        var memo = Download("memo.m4a", TimeSpan.FromDays(2));
        var talk = Download("talk.wav", TimeSpan.FromDays(1));
        _queue.Earlier.Add(new ImportItem(1, podcast, ImportState.Queued, 0, null, null));
        _queue.Earlier.Add(new ImportItem(2, memo, ImportState.Done, 1, null, null));
        _queue.Earlier.Add(new ImportItem(3, talk, ImportState.Failed, 0, null, "damaged"));
        using var vm = Create(new FakeDownloadsFolder(P("Downloads")));
        await vm.RecentTask;

        bool Added(string path) => vm.Recent.Single(r => r.Path == path).IsAdded;
        Assert.True(Added(podcast));
        Assert.False(vm.Recent.Single(r => r.Path == podcast).AddCommand.CanExecute(null));
        Assert.False(Added(memo));
        Assert.False(Added(talk));
        Assert.Empty(vm.Files);

        // The podcast finished, the talk is retried: the rows follow the queue's Changed (raised off the UI thread).
        _queue.Earlier[0] = _queue.Earlier[0] with { State = ImportState.Done };
        _queue.Earlier[2] = _queue.Earlier[2] with { State = ImportState.Decoding };
        await _queue.RaiseChanged();

        Assert.False(Added(podcast));
        Assert.False(Added(memo));
        Assert.True(Added(talk));
    }

    [Fact]
    public async Task EachRefresh_TakesOneSnapshotOfTheQueue_WhateverTheNumberOfDownloadRows()
    {
        foreach (var name in new[] { "a.mp3", "b.mp3", "c.mp3", "d.mp3", "e.mp3" })
        {
            Download(name, TimeSpan.FromDays(1));
        }

        using var vm = Create(new FakeDownloadsFolder(P("Downloads")));
        await vm.RecentTask;
        Assert.Equal(5, vm.Recent.Count);

        var before = _queue.ItemsReads;
        await _queue.RaiseChanged();

        Assert.Equal(before + 1, _queue.ItemsReads);
    }

    [Fact]
    public async Task AnError_Clears_AfterASuccessfulPick_OrAdd()
    {
        var podcast = Download("podcast-ep42.mp3", TimeSpan.FromDays(8));
        using var vm = Create(new FakeDownloadsFolder(P("Downloads")));
        await vm.RecentTask;

        _picker.Fail = new InvalidOperationException("no portal");
        await vm.BrowseCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);

        _picker.Fail = null;
        _picker.Answer(P("voice-memo-1029.m4a"));
        await vm.BrowseCommand.ExecuteAsync(null);
        Assert.Null(vm.Error);
        Assert.False(vm.HasError);
        await vm.WhenProbed();

        _queue.Fail = new IOException("disk full");
        await vm.ImportCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);

        vm.Recent.Single(r => r.Path == podcast).AddCommand.Execute(null);
        Assert.Null(vm.Error);
    }

    [Fact]
    public async Task RecentInDownloads_IsHidden_WithoutAFolder_OrWithoutRecentMedia()
    {
        using (var none = Create())
        {
            await none.RecentTask;
            Assert.False(none.HasRecent);
        }

        using (var missing = Create(new FakeDownloadsFolder(null)))
        {
            await missing.RecentTask;
            Assert.False(missing.HasRecent);
        }

        Download("notes.pdf", TimeSpan.FromHours(1));
        Download("too-old.wav", TimeSpan.FromDays(40));
        using var empty = Create(new FakeDownloadsFolder(P("Downloads")));
        await empty.RecentTask;
        Assert.False(empty.HasRecent);
        Assert.Empty(empty.Recent);
    }

    // The drop zone ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheFormatsLine_FollowsTheMockupsOrder_WithWhatTheQueueTakes()
    {
        using (var all = Create())
        {
            Assert.Equal("MP3 · WAV · M4A · FLAC · OGG · MP4 · MKV · MOV · WEBM", all.FormatsText);
        }

        var queue = new FakeImportQueue(".wma", ".MP4", ".wav", ".aac");
        using var some = new ImportDialogViewModel(queue, null, null, null, _time, action => action());
        Assert.Equal("WAV · MP4 · AAC · WMA", some.FormatsText);
        Assert.Equal([".wav", ".mp4", ".aac", ".wma"], some.Extensions);
    }

    [Theory]
    [InlineData(0, "Today")]
    [InlineData(1, "Yesterday")]
    [InlineData(2, "This week")]
    [InlineData(6, "This week")]
    [InlineData(7, "Last week")]
    [InlineData(13, "Last week")]
    [InlineData(14, "Oct 4")]
    public void TheAgeOfADownload_UsesTheLibrarysWords(int days, string expected)
    {
        Assert.Equal(expected, ImportDialogViewModel.Age(Now - TimeSpan.FromDays(days), _time));
    }

    [Theory]
    [InlineData(412_000_000, "412 MB")]
    [InlineData(6_000_000, "6 MB")]
    [InlineData(999_499_999, "999 MB")]
    [InlineData(1_500_000_000, "1.50 GB")]
    [InlineData(800_000, "800 KB")]
    public void TheSize_IsInMegabytes_UpToAGigabyte(long bytes, string expected)
    {
        Assert.Equal(expected, ImportDialogViewModel.Size(bytes));
    }
}
