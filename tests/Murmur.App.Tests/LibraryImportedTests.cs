using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Import;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.App.Tests;

/// <summary>
/// Imported files in the Library (#68): the row states (queued, decoding, transcribing, ready, failed), the live updates from the import
/// queue, the "Imported" filter chip and its combinations, the meta line of the recording details, the strings, and headless renders.
/// </summary>
public sealed class LibraryImportedTests : IDisposable
{
    private static readonly DateTimeOffset Now = UiHarness.Now;

    private readonly IDisposable _english = UiCulture.Override("en");
    private readonly FakeSessionStore _store = new();
    private readonly FakeProcessing _processing = new();
    private readonly FakeImportQueue _queue = new();
    private readonly FakeTranscriptStore _transcripts = new();
    private readonly TestTime _time = new(Now);
    private readonly List<Action> _posted = [];

    public void Dispose() => _english.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private LibraryViewModel Create(bool imports = true, bool inline = true) => new(
        _store,
        _time,
        _processing,
        inline ? action => action() : _posted.Add,
        transcripts: _transcripts,
        imports: imports ? _queue : null);

    private SessionRecord AddImported(
        string title,
        string file,
        SessionState state = SessionState.Ready,
        double hoursAgo = 1,
        TimeSpan? duration = null)
    {
        var record = Sessions.Make(title, Now.AddHours(-hoursAgo), duration ?? TimeSpan.FromMinutes(45), mic: false, state: state) with
        {
            Source = SessionSource.Imported,
            OriginalPath = file,
        };
        _store.Add(record);
        return record;
    }

    private SessionRecord AddRecorded(string title, double hoursAgo = 2, SessionState state = SessionState.Ready)
    {
        var record = Sessions.Make(title, Now.AddHours(-hoursAgo), TimeSpan.FromMinutes(30), state: state);
        _store.Add(record);
        return record;
    }

    private void Speak(SessionRecord record, string? language)
    {
        _transcripts.PerSession[record.Id] = new Transcript(
            record.Id,
            [TranscriptFactory.Segment(Speaker.Others, 1, "Hello", "everyone")],
            "fake",
            language);
    }

    private static SessionItemViewModel Row(LibraryViewModel vm, string title) => vm.Sessions.Single(s => s.Title == title);

    // Row states --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AQueuedImport_ShowsARowWithTheFileIcon_AndQueued_BeforeItHasASession()
    {
        _queue.Earlier.Add(new ImportItem(7, @"C:\Users\me\Downloads\voice-memo-1029.m4a", ImportState.Queued, 0, null, null));
        var vm = Create();

        await vm.LoadAsync();

        var row = Assert.Single(vm.Sessions);
        Assert.Equal("voice-memo-1029", row.Title);
        Assert.True(row.IsFileIcon);
        Assert.False(row.IsScreenIcon || row.IsMicrophoneIcon || row.IsSystemAudioIcon);
        Assert.True(row.ShowQueued);
        Assert.Equal("Queued", row.QueuedText);
        Assert.False(row.ShowMeta);
        Assert.False(row.ShowProgress);
        Assert.Equal(string.Empty, row.ClockText);
        Assert.Contains(row, vm.Entries);
        Assert.False(row.CanOpen);
        Assert.False(row.OpenCommand.CanExecute(null));
    }

    [Fact]
    public async Task AStandInRow_IsDatedLikeTheSession_FromTheFilesTime()
    {
        var file = Path.Combine(Path.GetTempPath(), "murmur-standin-" + Guid.NewGuid().ToString("N") + ".mp3");
        await File.WriteAllBytesAsync(file, [0], Ct);
        try
        {
            File.SetCreationTimeUtc(file, Now.UtcDateTime.AddDays(-20));
            File.SetLastWriteTimeUtc(file, Now.UtcDateTime.AddDays(-2));
            _queue.Earlier.Add(new ImportItem(1, file, ImportState.Queued, 0, null, null));
            var vm = Create();

            await vm.LoadAsync();

            var created = vm.Sessions[0].Record.StartedAt;
            // Where the file system keeps no creation time the write time is used; either way it is not "now".
            Assert.True(created < Now.AddDays(-1), created.ToString("O"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task AStandInRow_OfAFileThatIsGone_FallsBackToNow()
    {
        _queue.Earlier.Add(new ImportItem(1, "/no/such/file.mp3", ImportState.Queued, 0, null, null));
        var vm = Create();

        await vm.LoadAsync();

        Assert.Equal(Now.UtcDateTime, vm.Sessions[0].Record.StartedAt.UtcDateTime);
    }

    [Fact]
    public async Task ATranscribingImport_ShowsOnlyTheTitleAndTheBar()
    {
        var record = AddImported("Zoom Meeting", "/in/z.mp4", SessionState.Transcribing);
        _processing.Set(new SessionProcessingStatus(record.Id, SessionProcessingStage.Transcribing, 0.2));
        var vm = Create();

        await vm.LoadAsync();

        var row = vm.Sessions[0];
        Assert.False(row.ShowMeta);
        Assert.True(row.ShowProgress);
        _processing.Raise(new SessionProcessingStatus(record.Id, SessionProcessingStage.Completed, 1));
        Assert.True(row.ShowMeta);
    }

    /// <summary>Review round 1: the queue sets the session while the list is read; the stand-in row must not vanish until the session is in the list.</summary>
    [Fact]
    public async Task ASessionCreatedWhileTheListLoads_KeepsItsRow_UntilTheNextChange()
    {
        _queue.Earlier.Add(new ImportItem(1, "/in/Interview.mp3", ImportState.Decoding, 0, null, null));
        var vm = Create();
        var gate = new TaskCompletionSource();
        _store.ListGate = gate.Task;

        var load = vm.LoadAsync();

        // The queue creates the session; it raises no Changed for that.
        var record = AddImported("Interview", "/in/Interview.mp3", SessionState.Recording);
        _queue.Earlier[0] = _queue.Earlier[0] with { Session = record.Id };
        gate.SetResult();
        await load;

        var row = Assert.Single(vm.Sessions);
        Assert.Equal("Interview", row.Title);
        Assert.False(row.CanOpen, "still the stand-in: the list read did not see the session");

        // The next Changed (the queue saved the entry) shows the real session.
        await _queue.RaiseChanged();
        await vm.LoadTask;
        var real = Assert.Single(vm.Sessions);
        Assert.Equal(record.Id, real.Id);
        Assert.True(real.CanOpen);
    }

    [Fact]
    public async Task TheChip_GoesAway_WhenTheLastImportedSessionIsGone_UnderADateFilter()
    {
        var record = AddImported("Old import", "/in/b.mp3", hoursAgo: 24 * 20);
        AddRecorded("Today recording", hoursAgo: 2);
        var vm = Create();
        await vm.LoadAsync();
        vm.SetFilterCommand.Execute(LibraryFilter.Today);
        await vm.LoadTask;
        Assert.True(vm.ShowImportedFilter);
        vm.ToggleImportedCommand.Execute(null);

        await _store.DeleteSessionAsync(record.Id, Ct);
        vm.SetFilterCommand.Execute(LibraryFilter.Week);
        await vm.LoadTask;

        Assert.False(vm.ShowImportedFilter);
        Assert.False(vm.IsImportedFilter);
    }

    [Fact]
    public async Task AQueuedImport_IsNotOpened_BecauseItHasNoSessionYet()
    {
        _queue.Earlier.Add(new ImportItem(7, "/music/a.mp3", ImportState.Queued, 0, null, null));
        var vm = Create();
        var opened = 0;
        vm.SessionOpened += _ => opened++;
        await vm.LoadAsync();

        vm.Sessions[0].OpenCommand.Execute(null);

        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task ADecodingImport_StaysQueued_WithoutABar()
    {
        var record = AddImported("Zoom Meeting", "/in/Zoom_Meeting.mp4", SessionState.Recording);
        _queue.Earlier.Add(new ImportItem(1, "/in/Zoom_Meeting.mp4", ImportState.Decoding, 0.38, record.Id, null));
        var vm = Create();

        await vm.LoadAsync();

        var row = Row(vm, "Zoom Meeting");
        Assert.True(row.ShowQueued);
        Assert.False(row.ShowProgress);
        Assert.Equal("Queued", row.StatusText);
        Assert.True(row.IsFileIcon);
    }

    [Fact]
    public async Task AnImportedSession_StillRecording_WithoutAQueueItem_IsQueued()
    {
        AddImported("Half made", "/in/half.wav", SessionState.Recording);
        var vm = Create();

        await vm.LoadAsync();

        Assert.True(vm.Sessions[0].ShowQueued);
    }

    [Fact]
    public async Task ATranscribingImport_ShowsTheBar_AndTheProgressText()
    {
        var record = AddImported("Zoom Meeting", "/in/Zoom_Meeting.mp4", SessionState.Transcribing);
        _queue.Earlier.Add(new ImportItem(1, "/in/Zoom_Meeting.mp4", ImportState.Done, 1, record.Id, null));
        _processing.Set(new SessionProcessingStatus(record.Id, SessionProcessingStage.Transcribing, 0.386));
        var vm = Create();

        await vm.LoadAsync();

        var row = Row(vm, "Zoom Meeting");
        Assert.True(row.ShowProgress);
        Assert.False(row.ShowQueued);
        Assert.Equal(38, row.ProgressPercent);
        Assert.Equal("Transcribing · 38%", row.ProgressText);
        Assert.True(row.IsFileIcon);
    }

    [Fact]
    public async Task AReadyImport_ShowsImported_Language_AndTheFileName()
    {
        var record = AddImported("Zoom Meeting", @"C:\Users\me\Downloads\Zoom_Meeting_2026-10-30.mp4");
        Speak(record, "en");
        var vm = Create();

        await vm.LoadAsync();
        await vm.DetailsTask;

        var row = Row(vm, "Zoom Meeting");
        Assert.Equal("Imported · English · Zoom_Meeting_2026-10-30.mp4", row.MetaText);
        Assert.Equal("Zoom_Meeting_2026-10-30.mp4", row.OriginalName);
        Assert.True(row.ShowMeta);
        Assert.False(row.ShowQueued);
        Assert.Equal("45:00", row.ClockText);
    }

    [Fact]
    public async Task AReadyImport_WithoutALanguage_LeavesItOut()
    {
        AddImported("Memo", "/in/memo.m4a");
        var vm = Create();

        await vm.LoadAsync();
        await vm.DetailsTask;

        Assert.Equal("Imported · memo.m4a", vm.Sessions[0].MetaText);
    }

    [Fact]
    public async Task AFailedImportedSession_ClosesTheLineWithTheState()
    {
        AddImported("Broken", "/in/broken.wav", SessionState.Failed);
        var vm = Create();

        await vm.LoadAsync();

        Assert.Equal("Imported · broken.wav · Failed", vm.Sessions[0].MetaText);
        Assert.False(vm.Sessions[0].ShowQueued);
    }

    [Fact]
    public async Task AFailedImport_WithoutASession_ShowsARowThatSaysFailed()
    {
        _queue.Earlier.Add(new ImportItem(3, "/in/notes.txt", ImportState.Failed, 0, null, ImportQueue.UnsupportedCode));
        var vm = Create();

        await vm.LoadAsync();

        var row = Assert.Single(vm.Sessions);
        Assert.Equal("Imported · notes.txt · Failed", row.MetaText);
        Assert.Equal("Failed", row.StatusText);
        Assert.False(row.ShowQueued);
        Assert.True(row.IsFileIcon);
    }

    [Fact]
    public async Task ACancelledImport_HasNoRow()
    {
        _queue.Earlier.Add(new ImportItem(3, "/in/a.mp3", ImportState.Cancelled, 0, null, null));
        var vm = Create();

        await vm.LoadAsync();

        Assert.Empty(vm.Sessions);
    }

    [Fact]
    public async Task ARecordedSession_IsUnchanged()
    {
        AddRecorded("Standup");
        var vm = Create();

        await vm.LoadAsync();

        var row = Row(vm, "Standup");
        Assert.False(row.IsFileIcon);
        Assert.False(row.IsImported);
        Assert.True(row.ShowMeta);
        Assert.DoesNotContain("Imported", row.MetaText);
    }

    // Live updates ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheLibrary_FollowsTheQueue_FromQueuedToReady_OffTheUiThread()
    {
        var vm = Create(inline: false);
        await vm.LoadAsync();
        Assert.Empty(vm.Sessions);

        // Queued: no session yet.
        _queue.Earlier.Add(new ImportItem(1, "/in/Interview_1.mp3", ImportState.Queued, 0, null, null));
        await _queue.RaiseChanged();
        Assert.Empty(vm.Sessions);
        Assert.Single(_posted);
        RunPosted();
        await vm.LoadTask;
        Assert.Equal("Interview 1", Assert.Single(vm.Sessions).Title);
        Assert.True(vm.Sessions[0].ShowQueued);

        // Decoding: the session exists (recording), the placeholder is gone.
        var record = AddImported("Interview 1", "/in/Interview_1.mp3", SessionState.Recording);
        _queue.Earlier[0] = _queue.Earlier[0] with { State = ImportState.Decoding, Session = record.Id };
        await _queue.RaiseChanged();
        RunPosted();
        await vm.LoadTask;
        var decoding = Assert.Single(vm.Sessions);
        Assert.Equal(record.Id, decoding.Id);
        Assert.True(decoding.ShowQueued);

        // Done: the usual processing follows.
        _store.Replace(record with { State = SessionState.Transcribing });
        _queue.Earlier[0] = _queue.Earlier[0] with { State = ImportState.Done, Progress = 1 };
        await _queue.RaiseChanged();
        RunPosted();
        await vm.LoadTask;
        _processing.Raise(new SessionProcessingStatus(record.Id, SessionProcessingStage.Transcribing, 0.38));
        RunPosted();
        var row = Assert.Single(vm.Sessions);
        Assert.True(row.ShowProgress);
        Assert.Equal("Transcribing · 38%", row.ProgressText);

        _store.Replace(record with { State = SessionState.Ready });
        _processing.Raise(new SessionProcessingStatus(record.Id, SessionProcessingStage.Completed, 1));
        RunPosted();
        Assert.False(Assert.Single(vm.Sessions).ShowProgress);
    }

    [Fact]
    public async Task AProgressOnlyChange_DoesNotReloadTheList()
    {
        _queue.Earlier.Add(new ImportItem(1, "/in/a.mp3", ImportState.Queued, 0, null, null));
        var vm = Create();
        await vm.LoadAsync();
        var loads = _store.Queries.Count;

        _queue.Earlier[0] = _queue.Earlier[0] with { Progress = 0.5 };
        await _queue.RaiseChanged();

        Assert.Equal(loads, _store.Queries.Count);
    }

    [Fact]
    public async Task Dispose_StopsFollowingTheQueue()
    {
        var vm = Create();
        await vm.LoadAsync();

        vm.Dispose();

        Assert.Equal(0, _queue.Subscribers);
    }

    private void RunPosted()
    {
        var batch = _posted.ToArray();
        _posted.Clear();
        foreach (var action in batch)
        {
            action();
        }
    }

    // The filter chip ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheChip_IsHidden_WithoutImportedSessions()
    {
        AddRecorded("Standup");
        var vm = Create();

        await vm.LoadAsync();

        Assert.False(vm.ShowImportedFilter);
        Assert.False(vm.IsImportedFilter);
    }

    [Fact]
    public async Task TheChip_AppearsWithAnImportedSession_AndWithAQueuedImport()
    {
        AddRecorded("Standup");
        var vm = Create();
        await vm.LoadAsync();
        Assert.False(vm.ShowImportedFilter);

        _queue.Earlier.Add(new ImportItem(1, "/in/a.mp3", ImportState.Queued, 0, null, null));
        await _queue.RaiseChanged();
        await vm.LoadTask;

        Assert.True(vm.ShowImportedFilter);
    }

    [Fact]
    public async Task TheChip_IsHidden_WithoutAnImportQueue_EvenWithImportedSessionsAbsent()
    {
        AddRecorded("Standup");
        var vm = Create(imports: false);

        await vm.LoadAsync();

        Assert.False(vm.ShowImportedFilter);
    }

    [Fact]
    public async Task TheFilter_ListsOnlyImportedSessions_AndTurnsOff()
    {
        AddRecorded("Standup");
        AddImported("Memo", "/in/memo.m4a");
        AddImported("Zoom", "/in/zoom.mp4", hoursAgo: 3);
        var vm = Create();
        await vm.LoadAsync();
        Assert.Equal(3, vm.Sessions.Count);

        vm.ToggleImportedCommand.Execute(null);

        Assert.True(vm.IsImportedFilter);
        Assert.Equal(["Memo", "Zoom"], vm.Sessions.Select(s => s.Title));

        vm.ToggleImportedCommand.Execute(null);

        Assert.Equal(3, vm.Sessions.Count);
    }

    [Fact]
    public async Task TheFilter_CombinesWithADateChip()
    {
        AddImported("Today import", "/in/a.mp3", hoursAgo: 1);
        AddRecorded("Today recording", hoursAgo: 2);
        AddImported("Old import", "/in/b.mp3", hoursAgo: 24 * 20);
        var vm = Create();
        await vm.LoadAsync();
        vm.ToggleImportedCommand.Execute(null);
        Assert.Equal(2, vm.Sessions.Count);

        vm.SetFilterCommand.Execute(LibraryFilter.Today);
        await vm.LoadTask;

        Assert.Equal(["Today import"], vm.Sessions.Select(s => s.Title));
        Assert.True(vm.IsTodayFilter);
        Assert.True(vm.IsImportedFilter);
        Assert.True(vm.ShowImportedFilter);
    }

    [Fact]
    public async Task TheChip_StaysShown_UnderADateFilter_ThatHidesTheImportedOnes()
    {
        AddImported("Old import", "/in/b.mp3", hoursAgo: 24 * 20);
        AddRecorded("Today recording", hoursAgo: 2);
        var vm = Create();
        await vm.LoadAsync();
        Assert.True(vm.ShowImportedFilter);

        vm.SetFilterCommand.Execute(LibraryFilter.Today);
        await vm.LoadTask;

        Assert.True(vm.ShowImportedFilter);
    }

    [Fact]
    public async Task TheFilter_CombinesWithTheTitleSearch()
    {
        AddImported("Quarterly review import", "/in/a.mp3");
        AddImported("Memo", "/in/memo.m4a");
        AddRecorded("Quarterly review recording");
        var vm = Create();
        await vm.LoadAsync();
        vm.ToggleImportedCommand.Execute(null);

        vm.SearchText = "quarterly";

        Assert.Equal(["Quarterly review import"], vm.Sessions.Select(s => s.Title));
    }

    [Fact]
    public async Task TheFilter_RestrictsTheTranscriptSearchResults()
    {
        var imported = AddImported("Memo", "/in/memo.m4a");
        var recorded = AddRecorded("Standup");
        var index = new FakeSearchIndex
        {
            KeywordResult =
            [
                new SearchHit(new TextChunk(imported.Id, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9), "budget talk"), 1, SearchSource.Keyword),
                new SearchHit(new TextChunk(recorded.Id, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(9), "budget again"), 0.9, SearchSource.Keyword),
            ],
        };
        var vm = new LibraryViewModel(_store, _time, _processing, action => action(), keywordSearch: index, imports: _queue);
        await vm.LoadAsync();
        vm.ToggleImportedCommand.Execute(null);

        vm.SearchText = "budget";
        _time.Advance(LibraryViewModel.SearchDebounce);
        await vm.SearchTask;

        Assert.Equal([imported.Id], vm.SearchResults.Select(r => r.Record.Id));
    }

    [Fact]
    public async Task TheFilter_ShowsTheFilteredEmptyState_WhenNothingMatches()
    {
        AddImported("Memo", "/in/memo.m4a");
        var vm = Create();
        await vm.LoadAsync();
        vm.ToggleImportedCommand.Execute(null);

        vm.SearchText = "nothing like this";

        Assert.Empty(vm.Sessions);
        Assert.True(vm.IsFilteredNow);
        Assert.Contains(vm.Entries, e => e is LibraryEmptyEntry);
    }

    // The details -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheDetails_NameTheImport_AndTheOriginalFile()
    {
        var record = Sessions.Make("Zoom Meeting", Now.AddHours(-1), TimeSpan.FromMinutes(45), mic: false) with
        {
            Source = SessionSource.Imported,
            OriginalPath = @"C:\Users\me\Downloads\Zoom_Meeting_2026-10-30.mp4",
        };
        using var fixture = new SessionFixture(record: record);
        fixture.UseTranscript(new Transcript(record.Id, [TranscriptFactory.Segment(Speaker.Others, 1, "Hi")], "parakeet-tdt-0.6b-v3-int8", "en"));
        var vm = fixture.Create();

        await vm.LoadAsync();

        var texts = vm.Meta.Select(p => p.Text).ToList();
        Assert.Equal(["Imported", "·", "Zoom_Meeting_2026-10-30.mp4"], texts[^3..]);
        Assert.Contains("English", texts);
        Assert.Equal("45:00", texts[2]);
    }

    [Fact]
    public async Task TheDetails_OfAnImport_LoadTheTranscript_LikeARecording()
    {
        var record = Sessions.Make("Standup", Now.AddHours(-1), TimeSpan.FromMinutes(14), mic: false) with
        {
            Source = SessionSource.Imported,
            OriginalPath = "/in/standup.wav",
        };
        using var fixture = new SessionFixture(record: record);
        fixture.UseTranscript(new Transcript(record.Id, [TranscriptFactory.Segment(Speaker.Others, 1, "Hi")], "parakeet-tdt-0.6b-v3-int8", "en"));
        var vm = fixture.Create();

        await vm.LoadAsync();

        Assert.Equal(TranscriptStatus.Available, vm.Status);
        Assert.True(vm.ShowTranscriptList);
        Assert.NotEmpty(vm.Segments);
    }

    [Fact]
    public async Task TheDetails_OfARecording_DoNotMentionImports()
    {
        using var fixture = new SessionFixture();
        var vm = fixture.Create();

        await vm.LoadAsync();

        Assert.DoesNotContain(vm.Meta, p => p.Text == "Imported");
    }

    // Strings -----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheStrings_AreTranslated()
    {
        Assert.Equal("Imported", Strings.Library_FilterImported);
        Assert.Equal("Imported", Strings.Library_ImportedMeta);
        using var _ = UiCulture.Override("uk");
        Assert.Equal("Імпортовані", Strings.Library_FilterImported);
        Assert.Equal("Імпортовано", Strings.Library_ImportedMeta);
    }

    [Fact]
    public async Task TheRow_IsUkrainian_InUk()
    {
        AddImported("Memo", "/in/memo.m4a");
        using var uk = UiCulture.Override("uk");
        var vm = Create();

        await vm.LoadAsync();

        Assert.Equal("Імпортовано · memo.m4a", vm.Sessions[0].MetaText);
    }

    [Theory]
    [InlineData("/a/b/c.mp3", "c.mp3")]
    [InlineData(@"C:\a\b\c.mp3", "c.mp3")]
    [InlineData("c.mp3", "c.mp3")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void FileNameOf_TakesTheLastSegment_OfEitherSeparator(string? path, string expected) =>
        Assert.Equal(expected, SessionItemViewModel.FileNameOf(path));

    // Renders -----------------------------------------------------------------------------------------------------

    /// <summary>The mockup's three rows: Zoom Meeting transcribing at 38 %, voice-memo-1029 queued, and a ready import.</summary>
    private async Task<ShellViewModel> MockupShellAsync()
    {
        var zoom = AddImported("Zoom Meeting 2026-10-30", "/in/Zoom_Meeting_2026-10-30.mp4", SessionState.Transcribing, hoursAgo: 1, duration: new TimeSpan(0, 45, 10));
        var ready = AddImported("standup 2026-10-28", "/in/standup-2026-10-28.wav", hoursAgo: 3, duration: new TimeSpan(0, 14, 2));
        Speak(ready, "en");
        AddRecorded("Weekly product sync", hoursAgo: 5);
        _queue.Earlier.Add(new ImportItem(1, "/in/Zoom_Meeting_2026-10-30.mp4", ImportState.Done, 1, zoom.Id, null));
        _queue.Earlier.Add(new ImportItem(2, "/in/voice-memo-1029.m4a", ImportState.Queued, 0, null, null));
        _processing.Set(new SessionProcessingStatus(zoom.Id, SessionProcessingStage.Transcribing, 0.38));
        var library = new LibraryViewModel(_store, _time, _processing, action => action(), transcripts: _transcripts, imports: _queue);
        var recording = new RecordingViewModel(new FakeRecorder(), new FakeCapture(), _store, action => action());
        var shell = new ShellViewModel(library, recording);
        await library.LoadAsync();
        await library.DetailsTask;
        return shell;
    }

    [Theory]
    [InlineData("Light", 1100, 720)]
    [InlineData("Dark", 1100, 720)]
    [InlineData("Light", 900, 600)]
    [InlineData("Dark", 900, 600)]
    [Trait("Category", "Slow")]
    public async Task TheLibrary_WithImportedRows_WritesPng(string theme, int width, int height)
    {
        var shell = await MockupShellAsync();
        var path = Path.Combine(RepoPaths.ScreenshotsDirectory, $"library-imported-{theme.ToLowerInvariant()}-{width}x{height}.png");

        var (tiles, chip, queuedSize, filtered) = await Session.Dispatch(
            () =>
            {
                var window = new MainWindow
                {
                    DataContext = shell,
                    Width = width,
                    Height = height,
                    RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light,
                };
                window.Show();
                UiHarness.Pump();
                var library = window.GetVisualDescendants().OfType<LibraryView>().Single();
                var scroll = library.GetVisualDescendants().OfType<ScrollViewer>().First();
                scroll.Offset = new Vector(0, scroll.Extent.Height);
                UiHarness.Pump(3);
                var rowTiles = library.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("rowTile") && b.IsEffectivelyVisible).ToList();
                var chipButton = library.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => b.Name == "ImportedFilter");
                var queued = library.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == "Queued" && t.IsEffectivelyVisible);
                UiHarness.Save(window, path);
                var result = (
                    rowTiles.Count(t => t.Bounds.Width == 32 && t.Bounds.Height == 32),
                    chipButton is { IsEffectivelyVisible: true, Bounds.Height: 28 },
                    queued?.FontSize ?? 0,
                    shell.Library.Sessions.Count);
                window.Close();
                return result;
            },
            Ct);

        Assert.True(tiles >= 3, "the imported rows keep the 32 px tile");
        Assert.True(chip, "the Imported chip is 28 px high and shown");
        Assert.Equal(11, queuedSize);
        Assert.Equal(3 + 1, filtered);
        Assert.True(new FileInfo(path).Length > 10_000, "Screenshot looks empty.");
    }
}
