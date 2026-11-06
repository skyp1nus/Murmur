using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Murmur.App.Controls;
using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.Services;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

/// <summary>Bookmarks in the recording view (#59): the card, the flags above the waveform and the pills between the transcript lines (English UI).</summary>
public sealed class BookmarkTests : IDisposable
{
    private readonly IDisposable _english = UiCulture.Override("en");
    private readonly SessionFixture _fixture = new();
    private readonly FakeBookmarkStore _store = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HeadlessUnitTestSession Session => UiHarness.Session;

    public void Dispose()
    {
        _english.Dispose();
        _fixture.Dispose();
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private SessionViewModel Create(bool withStore = true, FakeVideoPlayback? video = null, SessionRecord? record = null)
    {
        _fixture.UseShortTranscript();
        return new SessionViewModel(
            record ?? _fixture.Record,
            _fixture.Files,
            _fixture.Transcripts,
            _fixture.Player,
            _fixture.Processing,
            _fixture.Time,
            action => action(),
            videoPlayback: video,
            services: new SessionServices(Bookmarks: withStore ? _store : null));
    }

    private async Task<SessionViewModel> LoadAsync(bool withStore = true)
    {
        var vm = Create(withStore);
        await vm.LoadAsync();
        return vm;
    }

    // The list ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task StoredBookmarks_AreShown_InOrder_WithTheirTimes()
    {
        _store.Put(_fixture.Record.Id, (1205, "Demo date"), (761, "Pricing question"), (3725, ""));

        var vm = await LoadAsync();

        Assert.True(vm.HasBookmarks);
        Assert.Equal(["12:41", "20:05", "1:02:05"], vm.Bookmarks.Select(b => b.TimeText));
        Assert.Equal(["Pricing question", "Demo date", ""], vm.Bookmarks.Select(b => b.Note));
        Assert.Equal([S(761), S(1205), S(3725)], vm.Bookmarks.Select(b => b.At));
    }

    [Fact]
    public async Task ARowWithoutANote_ShowsThePlaceholder()
    {
        _store.Put(_fixture.Record.Id, (10, ""), (20, "Budget"));

        var vm = await LoadAsync();

        Assert.Equal("Add a note", vm.Bookmarks[0].DisplayNote);
        Assert.False(vm.Bookmarks[0].HasNote);
        Assert.Equal("Budget", vm.Bookmarks[1].DisplayNote);
    }

    [Fact]
    public async Task WithoutAStore_TheCardIsHidden_AndThereAreNoBookmarks()
    {
        var vm = await LoadAsync(withStore: false);

        Assert.False(vm.ShowBookmarksCard);
        Assert.False(vm.ShowBookmarksEmpty);
        Assert.False(vm.HasBookmarks);
        Assert.Empty(vm.BookmarkFlags);
        await vm.AddBookmarkAtPlayheadCommand.ExecuteAsync(null);
        Assert.Empty(vm.Bookmarks);
    }

    [Fact]
    public async Task ARecordingWithoutBookmarks_ShowsTheEmptyState()
    {
        var vm = await LoadAsync();

        Assert.True(vm.ShowBookmarksCard);
        Assert.True(vm.ShowBookmarksEmpty);
        Assert.Equal("No bookmarks yet. Press M to add one.", Strings.Session_BookmarksEmpty);

        await vm.AddBookmarkAtPlayheadCommand.ExecuteAsync(null);

        Assert.False(vm.ShowBookmarksEmpty);
    }

    [Fact]
    public async Task BookmarksThatCannotBeRead_AreAsGoodAsNone()
    {
        _store.GetFailure = new IOException("disk");

        var vm = await LoadAsync();

        Assert.False(vm.HasBookmarks);
        Assert.True(vm.ShowBookmarksEmpty);
    }

    [Fact]
    public async Task OnlyThisRecordingsBookmarks_AreShown()
    {
        _store.Put(SessionId.New(), (30, "Somebody else's"));
        _store.Put(_fixture.Record.Id, (40, "Mine"));

        var vm = await LoadAsync();

        Assert.Equal(["Mine"], vm.Bookmarks.Select(b => b.Note));
    }

    // Adding with M -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AddAtThePlayhead_SavesAtThePositionAndShowsItAtOnce()
    {
        var vm = await LoadAsync();
        vm.SeekTo(S(761));

        await vm.AddBookmarkAtPlayheadCommand.ExecuteAsync(null);

        var saved = Assert.Single(_store.All);
        Assert.Equal(S(761), saved.At);
        Assert.Equal(_fixture.Record.Id, saved.Session);
        Assert.Equal("", saved.Note);
        Assert.Equal(["12:41"], vm.Bookmarks.Select(b => b.TimeText));
    }

    [Fact]
    public async Task AddedBookmarks_StayInTimelineOrder()
    {
        var vm = await LoadAsync();
        vm.SeekTo(S(900));
        await vm.AddBookmarkAtPlayheadCommand.ExecuteAsync(null);
        vm.SeekTo(S(100));
        await vm.AddBookmarkAtPlayheadCommand.ExecuteAsync(null);
        vm.SeekTo(S(500));
        await vm.AddBookmarkAtPlayheadCommand.ExecuteAsync(null);

        Assert.Equal([S(100), S(500), S(900)], vm.Bookmarks.Select(b => b.At));
        Assert.Equal(3, vm.BookmarkFlags.Count);
    }

    [Fact]
    public async Task AddingWhilePlaying_UsesTheCurrentPosition()
    {
        var vm = await LoadAsync();
        vm.PlayPauseCommand.Execute(null);
        _fixture.Player.Position = S(75.4);
        vm.Tick();

        await vm.AddBookmarkAtPlayheadCommand.ExecuteAsync(null);

        Assert.Equal(S(75.4), Assert.Single(vm.Bookmarks).At);
    }

    [Fact]
    public async Task ABookmarkThatCannotBeSaved_IsNotShown()
    {
        var vm = await LoadAsync();
        _store.Failure = new IOException("locked");

        await vm.AddBookmarkAtPlayheadCommand.ExecuteAsync(null);

        Assert.Empty(vm.Bookmarks);
        Assert.True(vm.ShowBookmarksEmpty);
    }

    // Notes -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EditingANote_EnterSavesItTrimmed()
    {
        var ids = _store.Put(_fixture.Record.Id, (761, "Pricing"));
        var vm = await LoadAsync();
        var row = vm.Bookmarks[0];

        row.BeginEditCommand.Execute(null);
        Assert.True(row.IsEditing);
        Assert.Equal("Pricing", row.EditText);

        row.EditText = "  Pricing question - follow up  ";
        row.CommitEditCommand.Execute(null);
        await row.LastOperation;

        Assert.False(row.IsEditing);
        Assert.Equal("Pricing question - follow up", row.Note);
        Assert.Equal([(ids[0], "Pricing question - follow up")], _store.Updates);
        Assert.Equal("Pricing question - follow up", _store.All[0].Note);
    }

    [Fact]
    public async Task EditingANote_EscapeDropsIt()
    {
        _store.Put(_fixture.Record.Id, (761, "Pricing"));
        var vm = await LoadAsync();
        var row = vm.Bookmarks[0];

        row.BeginEditCommand.Execute(null);
        row.EditText = "Something else";
        row.CancelEditCommand.Execute(null);

        Assert.False(row.IsEditing);
        Assert.Equal("Pricing", row.Note);
        Assert.Empty(_store.Updates);
    }

    [Fact]
    public async Task ANoteLeftAsItWas_IsNotSavedAgain()
    {
        _store.Put(_fixture.Record.Id, (761, "Pricing"));
        var vm = await LoadAsync();
        var row = vm.Bookmarks[0];

        row.BeginEditCommand.Execute(null);
        row.CommitEditCommand.Execute(null);

        Assert.Empty(_store.Updates);
    }

    [Fact]
    public async Task ANoteCanBeCleared_AndTheRowShowsThePlaceholderAgain()
    {
        _store.Put(_fixture.Record.Id, (761, "Pricing"));
        var vm = await LoadAsync();
        var row = vm.Bookmarks[0];

        row.BeginEditCommand.Execute(null);
        row.EditText = "   ";
        row.CommitEditCommand.Execute(null);
        await row.LastOperation;

        Assert.Equal("", row.Note);
        Assert.Equal("Add a note", row.DisplayNote);
    }

    [Fact]
    public async Task ANoteThatCannotBeSaved_FallsBackToTheStoredOne()
    {
        _store.Put(_fixture.Record.Id, (761, "Pricing"));
        var vm = await LoadAsync();
        var row = vm.Bookmarks[0];
        _store.UpdateFailure = new IOException("locked");

        row.BeginEditCommand.Execute(null);
        row.EditText = "New";
        row.CommitEditCommand.Execute(null);
        await row.LastOperation;

        Assert.Equal("Pricing", row.Note);
        Assert.Equal("Pricing", vm.BookmarkFlags[0].Note);
    }

    [Fact]
    public async Task ANote_ReachesTheFlagTooltipAndThePill()
    {
        _store.Put(_fixture.Record.Id, (12, ""));
        var vm = await LoadAsync();
        var row = vm.Bookmarks[0];

        row.BeginEditCommand.Execute(null);
        row.EditText = "Intro";
        row.CommitEditCommand.Execute(null);
        await row.LastOperation;

        Assert.Equal("Intro", vm.BookmarkFlags[0].Note);
        Assert.Same(row, vm.Segments[0].MarksAfter[0]);
        Assert.Equal("00:12  Intro", row.ToolTip);
    }

    // Deleting ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_RemovesItEverywhere()
    {
        _store.Put(_fixture.Record.Id, (12, "One"), (22, "Two"));
        var vm = await LoadAsync();
        Assert.Equal(2, vm.BookmarkFlags.Count);

        vm.Bookmarks[0].DeleteCommand.Execute(null);
        await Task.Yield();

        Assert.Equal(["Two"], vm.Bookmarks.Select(b => b.Note));
        Assert.Equal(["Two"], vm.BookmarkFlags.Select(f => f.Note));
        Assert.Empty(vm.Segments[0].MarksAfter);
        Assert.Equal(["Two"], vm.Segments[1].MarksAfter.Select(b => b.Note));
        Assert.Equal(["Two"], _store.All.Select(b => b.Note));
    }

    [Fact]
    public async Task DeletingTheLastOne_ShowsTheEmptyStateAgain()
    {
        _store.Put(_fixture.Record.Id, (12, "One"));
        var vm = await LoadAsync();

        vm.Bookmarks[0].DeleteCommand.Execute(null);
        await Task.Yield();

        Assert.False(vm.HasBookmarks);
        Assert.True(vm.ShowBookmarksEmpty);
        Assert.Empty(vm.BookmarkFlags);
    }

    [Fact]
    public async Task ABookmarkThatCannotBeDeleted_StaysEverywhere()
    {
        _store.Put(_fixture.Record.Id, (12, "One"));
        var vm = await LoadAsync();
        _store.DeleteFailure = new IOException("locked");

        vm.Bookmarks[0].DeleteCommand.Execute(null);
        await Task.Yield();

        Assert.Single(vm.Bookmarks);
        Assert.Single(vm.BookmarkFlags);
        Assert.Single(vm.Segments[0].MarksAfter);
    }

    // Seeking -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARow_SeeksTheAudio_AndKeepsItPaused()
    {
        _store.Put(_fixture.Record.Id, (761, "Pricing"));
        var vm = await LoadAsync();

        vm.Bookmarks[0].SeekCommand.Execute(null);

        Assert.Equal([S(761)], _fixture.Player.Seeks);
        Assert.Equal(S(761), vm.Position);
        Assert.Equal(0, _fixture.Player.Plays);
    }

    [Fact]
    public async Task AFlag_SeeksToItsBookmark()
    {
        _store.Put(_fixture.Record.Id, (100, "A"), (761, "B"));
        var vm = await LoadAsync();

        vm.SeekToBookmarkFlag(1);

        Assert.Equal([S(761)], _fixture.Player.Seeks);
        vm.SeekToBookmarkFlag(7);
        vm.SeekToBookmarkFlag(-1);
        Assert.Single(_fixture.Player.Seeks);
    }

    [Fact]
    public async Task APill_SeeksToItsBookmark()
    {
        _store.Put(_fixture.Record.Id, (761, "B"));
        var vm = await LoadAsync();

        vm.Segments[1].MarksAfter[0].SeekCommand.Execute(null);

        Assert.Equal([S(761)], _fixture.Player.Seeks);
    }

    [Fact]
    public async Task ASeek_AlsoMovesTheScreenVideo()
    {
        var segments = new ScreenSegment[] { new(0, TimeSpan.Zero, TimeSpan.FromMinutes(30)), new(1, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30)) };
        foreach (var segment in segments)
        {
            File.WriteAllBytes(_fixture.Files.ScreenSegment(segment.Index), [0]);
        }

        var player = new FakeVideoPlayer(TimeSpan.FromMinutes(30));
        var vm = Create(video: new FakeVideoPlayback(player, true), record: _fixture.Record with { ScreenSegments = segments });
        _store.Put(_fixture.Record.Id, (35 * 60, "Late"));
        await vm.LoadAsync();

        vm.Bookmarks[0].SeekCommand.Execute(null);

        Assert.Equal(S(35 * 60), _fixture.Player.Position);
        Assert.Equal(S(5 * 60), player.Position);
    }

    // The flags ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheFlags_StandAtTheirFractionOfTheRecording()
    {
        _store.Put(_fixture.Record.Id, (0, "Start"), (900, "Quarter"), (3600, "End"));

        var vm = await LoadAsync();

        Assert.Equal([0, 0.25, 1], vm.BookmarkFlags.Select(f => f.Fraction));
        Assert.Equal(["00:00", "15:00", "1:00:00"], vm.BookmarkFlags.Select(f => f.TimeText));
    }

    [Fact]
    public void WaveformFlagPositions_UseTheWaveformsMapping()
    {
        Assert.Equal(0, WaveformControl.XOf(0, 400));
        Assert.Equal(100, WaveformControl.XOf(0.25, 400));
        Assert.Equal(400, WaveformControl.XOf(1, 400));
        Assert.Equal(400, WaveformControl.XOf(3, 400));
        Assert.Equal(0, WaveformControl.XOf(-1, 400));
    }

    [Fact]
    public async Task FlagPositions_FollowTheWidth_AndNothingIsRebuiltWhileDrawing()
    {
        var flags = new WaveformFlag[] { new(0.1, "01:00", "A"), new(0.5, "05:00", ""), new(1, "10:00", "C") };

        var (first, again, resized, xsAfterDraw) = await Session.Dispatch(
            () =>
            {
                var control = new BookmarkFlagsControl { Flags = flags, Width = 400, Height = 14 };
                var window = new Window { Content = control, Width = 400, Height = 100 };
                window.Show();
                UiHarness.Pump();
                var xs = control.FlagPositions.ToArray();
                // Drawing again and again (a playhead update redraws the waveform, not the flags) works from the same numbers.
                for (var i = 0; i < 5; i++)
                {
                    control.InvalidateVisual();
                    UiHarness.Pump(1);
                }

                var again = control.FlagPositions.ToArray();
                control.Width = 200;
                UiHarness.Pump();
                var narrow = control.FlagPositions.ToArray();
                window.Close();
                return (xs, again, narrow, again.Length);
            },
            Ct);

        Assert.Equal([40d, 200, 400], first);
        Assert.Equal(first, again);
        Assert.Equal([20d, 100, 200], resized);
        Assert.Equal(3, xsAfterDraw);
    }

    [Fact]
    public async Task AClickOnAFlag_RaisesItsIndex_AClickElsewhereDoesNot()
    {
        var flags = new WaveformFlag[] { new(0.1, "01:00", "A"), new(0.5, "05:00", "") };

        var (hits, clicked) = await Session.Dispatch(
            () =>
            {
                var control = new BookmarkFlagsControl { Flags = flags, Width = 400, Height = 14 };
                var window = new Window { Content = control, Width = 400, Height = 100 };
                window.Show();
                UiHarness.Pump();
                var raised = new List<int>();
                control.FlagActivated += raised.Add;
                var hits = new[] { control.HitTest(40), control.HitTest(45), control.HitTest(200), control.HitTest(120), control.HitTest(0) };
                var origin = control.TranslatePoint(new Point(0, 7), window)!.Value;
                window.MouseDown(origin + new Point(200, 0), MouseButton.Left);
                window.MouseUp(origin + new Point(200, 0), MouseButton.Left);
                window.MouseDown(origin + new Point(300, 0), MouseButton.Left);
                window.MouseUp(origin + new Point(300, 0), MouseButton.Left);
                window.Close();
                return (hits, raised.ToArray());
            },
            Ct);

        Assert.Equal([0, 0, 1, -1, -1], hits);
        Assert.Equal([1], clicked);
    }

    [Fact]
    public async Task TheFlagTooltip_HasTheTimeInGeistMono_AndTheNote()
    {
        var flags = new WaveformFlag[] { new(0.1, "12:41", "Pricing question"), new(0.5, "20:05", "") };

        var (first, second, none) = await Session.Dispatch(
            () =>
            {
                var control = new BookmarkFlagsControl { Flags = flags, Width = 400, Height = 14 };
                var window = new Window { Content = control, Width = 400, Height = 100 };
                window.Show();
                UiHarness.Pump();
                var tip = (StackPanel)control.TipFor(0)!;
                var bare = (StackPanel)control.TipFor(1)!;
                var result = (
                    tip.Children.OfType<TextBlock>().Select(t => t.Text).ToArray(),
                    bare.Children.OfType<TextBlock>().Select(t => t.Text).ToArray(),
                    control.TipFor(5));
                Assert.Contains("Geist Mono", ((TextBlock)tip.Children[0]).FontFamily.Name);
                window.Close();
                return result;
            },
            Ct);

        Assert.Equal(["12:41", "Pricing question"], first);
        Assert.Equal(["20:05"], second);
        Assert.Null(none);
    }

    // The pills ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APill_SitsAfterTheLineItsMomentIsIn()
    {
        // Lines start at 10 s and 20 s.
        _store.Put(_fixture.Record.Id, (3, "Before"), (10, "AtFirst"), (15, "InFirst"), (19.9, "EndOfFirst"), (20, "AtSecond"), (3000, "Late"));

        var vm = await LoadAsync();

        Assert.Equal(["Before"], vm.Segments[0].MarksBefore.Select(b => b.Note));
        Assert.Equal(["AtFirst", "InFirst", "EndOfFirst"], vm.Segments[0].MarksAfter.Select(b => b.Note));
        Assert.Empty(vm.Segments[1].MarksBefore);
        Assert.Equal(["AtSecond", "Late"], vm.Segments[1].MarksAfter.Select(b => b.Note));
        Assert.True(vm.Segments[0].HasMarksBefore);
        Assert.False(vm.Segments[1].HasMarksBefore);
    }

    [Fact]
    public async Task APillIsTheSameRowAsTheCards()
    {
        _store.Put(_fixture.Record.Id, (15, "InFirst"));

        var vm = await LoadAsync();

        Assert.Same(vm.Bookmarks[0], vm.Segments[0].MarksAfter[0]);
    }

    [Fact]
    public async Task AddingABookmark_TouchesOnlyTheRowsItChanges()
    {
        _store.Put(_fixture.Record.Id, (15, "InFirst"));
        var vm = await LoadAsync();
        List<string> changed = [];
        vm.Segments[0].PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        vm.Segments[1].PropertyChanged += (_, e) => changed.Add("second:" + e.PropertyName);

        vm.SeekTo(S(30));
        await vm.AddBookmarkAtPlayheadCommand.ExecuteAsync(null);

        Assert.Contains("second:MarksAfter", changed);
        Assert.DoesNotContain("MarksAfter", changed);
        Assert.Same(vm.Bookmarks[0], vm.Segments[0].MarksAfter[0]);
    }

    [Fact]
    public async Task APillDoesNotMoveFollowAlong()
    {
        _store.Put(_fixture.Record.Id, (12, "One"));
        var vm = await LoadAsync();
        var scrolls = new List<int>();
        vm.ScrollToSegmentRequested += scrolls.Add;
        vm.PlayPauseCommand.Execute(null);

        _fixture.Player.Position = S(21);
        vm.Tick();

        Assert.Equal([1], scrolls);
        Assert.Equal(1, vm.CurrentSegmentIndex);
    }

    [Fact]
    public async Task ABookmarkInARecordingWithoutTranscript_IsStillInTheCard()
    {
        _store.Put(_fixture.Record.Id, (12, "One"));
        var vm = Create();
        _fixture.Transcripts.Transcript = null;

        await vm.LoadAsync();

        Assert.Single(vm.Bookmarks);
        Assert.Empty(vm.Segments);
    }

    // The window --------------------------------------------------------------------------------------------------

    private async Task<(UiHarness Harness, ShellViewModel Shell)> OpenAsync()
    {
        var harness = new UiHarness();
        harness.Store.Add(_fixture.Record);
        _fixture.UseShortTranscript();
        var shell = harness.CreateShell(
            record => new SessionViewModel(record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action(), services: new SessionServices(Bookmarks: _store)),
            _fixture.Processing);
        await shell.Library.LoadAsync();
        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;
        return (harness, shell);
    }

    [Fact]
    public async Task TheWindow_ShowsTheCardTheFlagsAndThePills()
    {
        _store.Put(_fixture.Record.Id, (12, "Intro"), (761, "Pricing question"));
        var (_, shell) = await OpenAsync();

        var (card, rows, flags, pills, empty) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var result = (
                    window.GetVisualDescendants().OfType<BookmarksCardView>().Single().IsEffectivelyVisible,
                    window.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("bookmarkRow")),
                    window.GetVisualDescendants().OfType<BookmarkFlagsControl>().Single().IsEffectivelyVisible,
                    window.GetVisualDescendants().OfType<BookmarkPillView>().Count(),
                    window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Classes.Contains("bookmarksEmpty") && t.IsEffectivelyVisible));
                window.Close();
                return result;
            },
            Ct);

        Assert.True(card);
        Assert.Equal(2, rows);
        Assert.True(flags);
        Assert.Equal(2, pills);
        Assert.False(empty);
    }

    [Fact]
    public async Task TheWindow_ShowsTheEmptyState_AndNoFlags()
    {
        var (_, shell) = await OpenAsync();

        var (card, empty, flags, pills) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var result = (
                    window.GetVisualDescendants().OfType<BookmarksCardView>().Single().IsEffectivelyVisible,
                    window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("bookmarksEmpty")).IsEffectivelyVisible,
                    window.GetVisualDescendants().OfType<BookmarkFlagsControl>().Single().IsEffectivelyVisible,
                    window.GetVisualDescendants().OfType<BookmarkPillView>().Count());
                window.Close();
                return result;
            },
            Ct);

        Assert.True(card);
        Assert.True(empty);
        Assert.False(flags);
        Assert.Equal(0, pills);
    }

    [Fact]
    public async Task AClickOnAPill_SeeksThere()
    {
        _store.Put(_fixture.Record.Id, (761, "Pricing question"));
        var (_, shell) = await OpenAsync();

        await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var pill = window.GetVisualDescendants().OfType<BookmarkPillView>().Single();
                var centre = pill.TranslatePoint(new Point(pill.Bounds.Width / 2, pill.Bounds.Height / 2), window)!.Value;
                window.MouseDown(centre, MouseButton.Left);
                window.MouseUp(centre, MouseButton.Left);
                UiHarness.Pump();
                window.Close();
            },
            Ct);

        Assert.Equal([S(761)], _fixture.Player.Seeks);
    }

    [Fact]
    public async Task AClickOnACardRow_SeeksThere()
    {
        _store.Put(_fixture.Record.Id, (761, "Pricing question"));
        var (_, shell) = await OpenAsync();

        await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("bookmarkSeek"));
                var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseDown(centre, MouseButton.Left);
                window.MouseUp(centre, MouseButton.Left);
                UiHarness.Pump();
                window.Close();
            },
            Ct);

        Assert.Equal([S(761)], _fixture.Player.Seeks);
    }

    [Fact]
    public async Task AMarkWithinHalfASecondOfAnother_IsTheSameMoment()
    {
        var vm = await LoadAsync();
        vm.SeekTo(S(100));
        await vm.AddBookmarkAtPlayheadCommand.ExecuteAsync(null);
        await vm.AddBookmarkAtPlayheadCommand.ExecuteAsync(null);
        vm.SeekTo(S(100.4));
        await vm.AddBookmarkAtPlayheadCommand.ExecuteAsync(null);

        Assert.Equal([S(100)], vm.Bookmarks.Select(b => b.At));
        Assert.Single(_store.All);

        vm.SeekTo(S(100.6));
        await vm.AddBookmarkAtPlayheadCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Bookmarks.Count);
    }

    [Fact]
    public async Task KeyRepeat_BeforeTheStoreAnswers_AddsOnlyOne()
    {
        var vm = await LoadAsync();
        vm.SeekTo(S(30));

        var first = vm.AddBookmarkAtPlayheadCommand.ExecuteAsync(null);
        var second = vm.AddBookmarkAtPlayheadCommand.ExecuteAsync(null);
        var third = vm.AddBookmarkAtPlayheadCommand.ExecuteAsync(null);
        await Task.WhenAll(first, second, third);

        Assert.Single(_store.All);
        Assert.Single(vm.Bookmarks);
    }

    [Fact]
    public async Task TheEmptyState_IsNotShownBeforeTheLoadHasFinished()
    {
        var vm = Create();

        Assert.True(vm.ShowBookmarksCard);
        Assert.False(vm.ShowBookmarksEmpty);

        await vm.LoadAsync();

        Assert.True(vm.ShowBookmarksEmpty);
    }

    [Fact]
    public async Task TheRowActions_AreNotHitTestableUntilTheRowIsHovered()
    {
        _store.Put(_fixture.Record.Id, (761, "Pricing question"));
        var (_, shell) = await OpenAsync();

        var (idle, hovered) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var row = window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("bookmarkRow"));
                var actions = row.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Classes.Contains("bookmarkActions"));
                var before = (actions.Opacity, actions.IsHitTestVisible);
                var origin = row.TranslatePoint(new Avalonia.Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
                window.MouseMove(origin, RawInputModifiers.None);
                UiHarness.Pump();
                var after = (actions.Opacity, actions.IsHitTestVisible);
                window.Close();
                return (before, after);
            },
            Ct);

        Assert.Equal((0d, false), idle);
        Assert.Equal((1d, true), hovered);
    }

    [Fact]
    public async Task TheMKey_AddsABookmarkAtThePlayhead()
    {
        var (_, shell) = await OpenAsync();
        shell.Session!.SeekTo(S(42));

        await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.M, RawInputModifiers.None);
                window.Close();
            },
            Ct);
        await Task.Delay(50, Ct);
        await Session.Dispatch(() => UiHarness.Pump(), Ct);

        Assert.Equal([S(42)], _store.All.Select(b => b.At));
        Assert.Equal(["00:42"], shell.Session.Bookmarks.Select(b => b.TimeText));
    }

    [Fact]
    public async Task TheMKey_IsIgnoredWhileATextBoxHasTheFocus()
    {
        var (_, shell) = await OpenAsync();

        await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var search = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SearchBox");
                search.Focus();
                UiHarness.Pump();
                window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.M, RawInputModifiers.None);
                window.Close();
            },
            Ct);
        await Task.Delay(50, Ct);

        Assert.Empty(_store.All);
    }

    [Fact]
    public async Task TheMKey_WithAModifierIsNotABookmark()
    {
        var (_, shell) = await OpenAsync();

        await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.Control);
                window.KeyReleaseQwerty(PhysicalKey.M, RawInputModifiers.Control);
                window.Close();
            },
            Ct);
        await Task.Delay(50, Ct);

        Assert.Empty(_store.All);
    }

    [Fact]
    public async Task TheNoteBox_TakesTheFocus_EnterSaves_AndEscapeDrops()
    {
        var ids = _store.Put(_fixture.Record.Id, (761, "Pricing"));
        var (_, shell) = await OpenAsync();
        var row = shell.Session!.Bookmarks[0];

        await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                row.BeginEditCommand.Execute(null);
                UiHarness.Pump(6);
                var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Classes.Contains("bookmarkEditor") && t.IsEffectivelyVisible);
                Assert.True(box.IsFocused);

                box.Text = "Changed";
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                UiHarness.Pump();
                Assert.False(row.IsEditing);
                Assert.Equal("Pricing", row.Note);

                row.BeginEditCommand.Execute(null);
                UiHarness.Pump(6);
                box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Classes.Contains("bookmarkEditor") && t.IsEffectivelyVisible);
                box.Text = "Budget";
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                UiHarness.Pump();
                window.Close();
            },
            Ct);
        await row.LastOperation;

        Assert.Equal("Budget", row.Note);
        Assert.Equal([(ids[0], "Budget")], _store.Updates);
    }

    // Strings -----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheStrings_AreInEnglishAndUkrainian_WithTheSamePlaceholders()
    {
        string[] keys = ["Session_Bookmarks", "Session_BookmarksHint", "Session_BookmarkPlaceholder", "Session_BookmarksEmpty", "Session_BookmarkTip", "Session_BookmarkName", "Session_BookmarkEdit", "Session_BookmarkDelete", "Session_BookmarkNoteBox"];
        static string Of(string language, string key) => Strings.Get(key, language) ?? throw new InvalidOperationException(key);

        foreach (var key in keys)
        {
            var en = Of("en", key);
            var uk = Of("uk", key);
            Assert.NotEqual(en, uk);
            Assert.Equal(en.Contains("{0}"), uk.Contains("{0}"));
        }

        Assert.Equal("Закладок ще немає. Натисніть M, щоб додати.", Of("uk", "Session_BookmarksEmpty"));
    }
}
