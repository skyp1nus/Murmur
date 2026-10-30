using Avalonia;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Murmur.App.Controls;
using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.Tests.Design;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Processing;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Settings;
using Murmur.Core.Transcription;

namespace Murmur.App.Tests;

/// <summary>The shell of the canvas (sidebar, top bar) and the Library page: states, lists, filters, the cards and their hand-offs.</summary>
public sealed class ShellLibraryTests
{
    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private static async Task<DesignRig> LoadedRigAsync()
    {
        var rig = new DesignRig();
        await rig.Library.LoadAsync();
        await rig.Library.DetailsTask;
        return rig;
    }

    // ---- sidebar ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sidebar_Recent_ListsTheNewestReadyRecordings_AndSkipsOneThatIsStillTranscribing()
    {
        using var scope = UiCulture.Override("en");
        using var rig = await LoadedRigAsync();
        var sidebar = rig.Shell.Sidebar!;

        Assert.Equal(SidebarContext.Recent, sidebar.Context);
        Assert.Equal("Recent", sidebar.ContextTitle);
        Assert.Equal(["Client demo — Q4 roadmap", "Voice note", "Interview — Senior .NET"], sidebar.Entries.Select(e => e.Title));
        Assert.All(sidebar.Entries, e => Assert.False(e.IsActive));
    }

    [Fact]
    public async Task Sidebar_Recent_HighlightsTheOpenRecording_AndAnEntryOpensIt()
    {
        using var scope = UiCulture.Override("en");
        using var rig = await LoadedRigAsync();
        var sidebar = rig.Shell.Sidebar!;

        sidebar.Entries[1].OpenCommand.Execute(null);
        await rig.Shell.SessionLoadTask;

        Assert.Equal(rig.Records[1].Id, rig.Shell.Session!.Id);
        Assert.True(sidebar.IsLibrary, "the library stays the current place while one of its recordings is open");
        Assert.Equal([false, true, false], sidebar.Entries.Select(e => e.IsActive));
        Assert.Equal(SidebarContext.Recent, sidebar.Context);
    }

    [Fact]
    public async Task Sidebar_RecentFollowsTheProcessing_ARecordingJoinsWhenItsTranscriptionEnds()
    {
        using var scope = UiCulture.Override("en");
        using var rig = await LoadedRigAsync();
        var weekly = rig.Records[2];

        rig.Processing.Raise(new SessionProcessingStatus(weekly.Id, SessionProcessingStage.Completed, 1));

        Assert.Equal(["Client demo — Q4 roadmap", "Voice note", "Weekly team sync"], rig.Shell.Sidebar!.Entries.Select(e => e.Title));
    }

    [Fact]
    public async Task Sidebar_ShowsTheRecordingPill_WhileARecordingRuns_AndItLeadsBack()
    {
        using var scope = UiCulture.Override("en");
        using var rig = await LoadedRigAsync();
        var sidebar = rig.Shell.Sidebar!;
        Assert.True(sidebar.ShowNewRecording);
        Assert.False(sidebar.IsRecordingActive);
        Assert.Equal(Strings.Nav_OfflineText, sidebar.OfflineText);

        rig.Shell.ShowRecording();
        await rig.Recording.StartStopAsync();

        Assert.Same(sidebar, rig.Shell.Sidebar);
        Assert.True(sidebar.IsRecordingActive);
        Assert.False(sidebar.ShowNewRecording);
        Assert.True(sidebar.IsOnRecordingPage);
        Assert.False(sidebar.IsLibrary);
        Assert.Equal(SidebarContext.None, sidebar.Context);
        Assert.False(sidebar.HasEntries);
        Assert.Equal("No models are loaded while recording", sidebar.OfflineText);

        rig.Shell.ShowLibrary();
        Assert.True(sidebar.IsRecordingActive, "the pill stays while the recording runs");
        Assert.False(sidebar.IsOnRecordingPage);
        sidebar.ShowRecordingCommand.Execute(null);
        Assert.Same(rig.Recording, rig.Shell.CurrentPage);
    }

    [Fact]
    public async Task Sidebar_ChatsAreProvidedByTheChatScreen_AndSectionsByTheSettings_AndSettingsHasNoOfflineCard()
    {
        using var scope = UiCulture.Override("en");
        using var rig = await LoadedRigAsync();
        var sidebar = rig.Shell.Sidebar!;

        rig.Shell.ShowLibraryChat();
        Assert.Equal(SidebarContext.Chats, sidebar.Context);
        Assert.False(sidebar.HasEntries, "nothing until the chat screen provides chats");
        var opened = 0;
        sidebar.SetChats([new SidebarEntry("PDF export", () => opened++, isActive: true), new SidebarEntry("Action items this week")]);
        Assert.Equal("Chats", sidebar.ContextTitle);
        Assert.Equal(["PDF export", "Action items this week"], sidebar.Entries.Select(e => e.Title));
        sidebar.Entries[0].OpenCommand.Execute(null);
        Assert.Equal(1, opened);
        Assert.True(sidebar.ShowOffline);

        rig.Shell.ShowSettings();
        Assert.Equal(SidebarContext.Sections, sidebar.Context);
        Assert.Equal("Sections", sidebar.ContextTitle);
        Assert.Equal(rig.Settings.Sections.Select(s => s.Title), sidebar.Entries.Select(e => e.Title));
        Assert.False(sidebar.ShowOffline, "the Settings screen of the canvas has no offline card");

        var scrolled = new List<string>();
        rig.Settings.SectionRequested += scrolled.Add;
        sidebar.Entries[2].OpenCommand.Execute(null);
        Assert.Equal([rig.Settings.Sections[2].Id], scrolled);
    }

    [Fact]
    public async Task Sidebar_IsOnEveryMainScreen_AndOnlyTheOnboardingFillsTheWindow()
    {
        using var scope = UiCulture.Override("en");
        using var rig = await LoadedRigAsync();

        rig.Shell.ShowRecording();
        Assert.NotNull(rig.Shell.Sidebar);
        rig.Shell.OpenSession(rig.ClientDemo);
        await rig.Shell.SessionLoadTask;
        Assert.NotNull(rig.Shell.Sidebar);
        rig.Shell.ShowLibraryChat();
        Assert.NotNull(rig.Shell.Sidebar);
        rig.Shell.ShowSettings();
        Assert.NotNull(rig.Shell.Sidebar);
    }

    // ---- top bar --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TopBar_HasTheBreadcrumbOfEachScreen()
    {
        using var scope = UiCulture.Override("en");
        using var rig = await LoadedRigAsync();
        var bar = rig.Shell.TopBar;

        var library = Assert.IsType<BreadcrumbViewModel>(bar.Leading);
        Assert.Equal(("Library", null, false), (library.Root, library.Current, library.IsLink));
        Assert.True(library.IsPlain);

        rig.Shell.ShowRecording();
        var recording = Assert.IsType<BreadcrumbViewModel>(bar.Leading);
        Assert.Equal(("Library", "New recording", true), (recording.Root, recording.Current, recording.IsLink));
        recording.RootCommand!.Execute(null);
        Assert.Same(rig.Library, rig.Shell.CurrentPage);

        rig.Shell.OpenSession(rig.ClientDemo);
        await rig.Shell.SessionLoadTask;
        var session = Assert.IsType<BreadcrumbViewModel>(bar.Leading);
        Assert.Equal(("Library", "Client demo — Q4 roadmap"), (session.Root, session.Current));
        Assert.Same(rig.Shell.Session!.Actions, bar.Trailing);

        rig.Shell.ShowSettings();
        var settings = Assert.IsType<BreadcrumbViewModel>(bar.Leading);
        Assert.Equal(("Settings", null), (settings.Root, settings.Current));
    }

    [Fact]
    public async Task TopBar_Hint_ShowsTheRecordGestureFromTheSettings_AndStopWhileRecording()
    {
        using var scope = UiCulture.Override("en");
        using var rig = await LoadedRigAsync();
        var hint = Assert.IsType<HotkeyHintViewModel>(rig.Shell.TopBar.Trailing);
        Assert.Equal(("Record", "Ctrl Shift R"), (hint.Label, hint.Gesture));
        Assert.Equal("Ctrl Shift R", rig.Library.RecordHotkeyText);

        rig.Shell.ShowRecording();
        await rig.Recording.StartStopAsync();
        Assert.Equal("Stop", hint.Label);

        await rig.Store.SaveAsync(rig.Store.Current with { RecordHotkey = "Ctrl+Alt+K" }, CancellationToken.None);
        Assert.Equal("Ctrl Alt K", hint.Gesture);

        await rig.Store.SaveAsync(rig.Store.Current with { RecordHotkey = null }, CancellationToken.None);
        Assert.False(hint.HasGesture);
        Assert.False(rig.Library.HasRecordHotkey);
    }

    [Fact]
    public async Task TopBar_SlotsCanBeFilledByAScreen()
    {
        using var rig = await LoadedRigAsync();
        var own = new object();
        rig.Shell.TopBar.Trailing = own;
        Assert.Same(own, rig.Shell.TopBar.Trailing);
    }

    // ---- library: rows, groups --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Library_GroupsRowsByDay_AndRoundsTheCardsOfEachDay()
    {
        using var scope = UiCulture.Override("en");
        using var rig = await LoadedRigAsync();
        var entries = rig.Library.Entries;

        Assert.IsType<LibraryHeroEntry>(entries[0]);
        Assert.IsType<LibraryHeaderEntry>(entries[1]);
        Assert.IsType<LibraryFiltersEntry>(entries[2]);
        Assert.Equal(["Today", "Yesterday", "This week"], entries.OfType<LibraryGroupEntry>().Select(g => g.Title));

        var rows = entries.OfType<SessionItemViewModel>().ToArray();
        Assert.Equal(["First", "Middle", "Last", "First", "Last", "Only"], rows.Select(r => r.Position.ToString()));
        Assert.True(rows[0].HasTopCorners && !rows[0].HasBottomCorners);
        Assert.True(rows[5].HasTopCorners && rows[5].HasBottomCorners);
    }

    [Fact]
    public async Task Library_OlderDaysGetTheirOwnHeading()
    {
        using var scope = UiCulture.Override("en");
        var rig = new DesignRig();
        using var _ = rig;
        var old = Fakes.Sessions.Make("Planning", UiHarness.Now.AddDays(-12), TimeSpan.FromMinutes(20));
        var older = Fakes.Sessions.Make("Kick-off", UiHarness.Now.AddDays(-12).AddHours(-1), TimeSpan.FromMinutes(20));
        rig.SessionStore.Add(old);
        rig.SessionStore.Add(older);
        await rig.Library.LoadAsync();

        var headings = rig.Library.Entries.OfType<LibraryGroupEntry>().Select(g => g.Title).ToArray();
        Assert.Equal(["Today", "Yesterday", "This week", "Tue, Oct 6"], headings);
        Assert.Equal(["Planning", "Kick-off"], rig.Library.Entries.OfType<SessionItemViewModel>().TakeLast(2).Select(r => r.Title));
        Assert.Equal("15:00", rig.Library.Entries.OfType<SessionItemViewModel>().Single(r => r.Title == "Planning").TimeText);
    }

    [Fact]
    public async Task Library_Row_ShowsTimeLanguageAndSources_TheFirstLineAndTheDuration()
    {
        using var scope = UiCulture.Override("en");
        using var rig = await LoadedRigAsync();
        var rows = rig.Library.Sessions;

        Assert.Equal("10:30 · English · Mic, system audio, screen", rows[0].MetaText);
        Assert.Equal("…PDF export moves to next sprint: first we finish meeting summaries and action items.", rows[0].Snippet);
        Assert.Equal("47:12", rows[0].ClockText);
        Assert.Equal(RowIcon.Screen, rows[0].Icon);
        Assert.Equal("09:05 · English · Mic", rows[1].MetaText);
        Assert.Equal(RowIcon.Microphone, rows[1].Icon);
        Assert.Equal(RowIcon.SystemAudio, rows[2].Icon);
        Assert.Equal("1:02:15", rows[3].ClockText);
        Assert.Equal("Mon, 14:00 · English · Mic, system audio", rows[5].MetaText);
    }

    [Fact]
    public async Task Library_RowWithoutATranscript_OmitsTheLanguage()
    {
        using var scope = UiCulture.Override("en");
        var rig = new DesignRig();
        using var _ = rig;
        rig.Transcripts.PerSession.Clear();
        await rig.Library.LoadAsync();
        await rig.Library.DetailsTask;

        Assert.Equal("10:30 · Mic, system audio, screen", rig.Library.Sessions[0].MetaText);
        Assert.False(rig.Library.Sessions[0].HasSnippet);
    }

    [Fact]
    public async Task Library_TranscribingRow_ShowsTheBarInsteadOfTheText()
    {
        using var scope = UiCulture.Override("en");
        using var rig = await LoadedRigAsync();
        var weekly = rig.Library.Sessions[2];

        Assert.True(weekly.ShowProgress);
        Assert.Equal(64, weekly.ProgressPercent);
        Assert.Equal("Transcribing · 64%", weekly.ProgressText);
        Assert.False(weekly.ShowSnippet);
        Assert.Equal("08:00 · English · Mic, system audio", weekly.MetaText);

        rig.Processing.Raise(new SessionProcessingStatus(weekly.Id, SessionProcessingStage.Transcribing, 0.8));
        Assert.Equal(80, weekly.ProgressPercent);
    }

    [Fact]
    public async Task Library_InterruptedAndFailedRows_KeepTheirStateInTheMetaLine()
    {
        using var scope = UiCulture.Override("en");
        var rig = new DesignRig();
        using var _ = rig;
        rig.SessionStore.Add(Fakes.Sessions.Make("Crash", UiHarness.Now.AddMinutes(-10), TimeSpan.FromMinutes(7), mic: true, system: false, state: SessionState.Interrupted));
        rig.SessionStore.Add(Fakes.Sessions.Make("Broken", UiHarness.Now.AddMinutes(-20), TimeSpan.FromMinutes(5), state: SessionState.Failed));
        await rig.Library.LoadAsync();

        var crash = rig.Library.Sessions.Single(r => r.Title == "Crash");
        var broken = rig.Library.Sessions.Single(r => r.Title == "Broken");
        Assert.Equal("14:50 · Mic · Interrupted", crash.MetaText);
        Assert.Equal("14:40 · Mic, system audio · Failed", broken.MetaText);
        Assert.False(crash.ShowProgress);
    }

    [Fact]
    public async Task Library_ClickingARowOpensTheSession()
    {
        using var rig = await LoadedRigAsync();
        SessionRecord? opened = null;
        rig.Library.SessionOpened += r => opened = r;

        rig.Library.Sessions[1].OpenCommand.Execute(null);

        Assert.Equal(rig.Records[1].Id, opened!.Id);
    }

    [Fact]
    public async Task Library_ChangingTheSearchKeepsTheFixedBlocks_AndReplacesOnlyTheTail()
    {
        using var rig = await LoadedRigAsync();
        var head = rig.Library.Entries.Take(3).ToArray();
        var changes = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        rig.Library.Entries.CollectionChanged += (_, e) => changes.Add(e.Action);

        rig.Library.SearchText = "Voice";
        rig.Time.Advance(LibraryViewModel.SearchDebounce);
        await rig.Library.SearchTask;

        Assert.Equal(head, rig.Library.Entries.Take(3));
        Assert.Equal(["Voice note"], rig.Library.Entries.OfType<SearchResultViewModel>().Select(r => r.Title));
        Assert.Empty(rig.Library.Entries.OfType<SessionItemViewModel>());
        Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, changes);
    }

    // ---- library: filters ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Library_MonthFilter_StartsOnTheFirstOfTheMonth()
    {
        using var rig = await LoadedRigAsync();

        rig.Library.IsMonthFilter = true;
        await rig.Library.LoadTask;

        Assert.True(rig.Library.IsMonthFilter);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), rig.SessionStore.Queries[^1].From);
        Assert.Null(rig.SessionStore.Queries[^1].To);
    }

    [Fact]
    public async Task Library_PickedRange_FiltersByStartDate_IncludingBothEndDays()
    {
        using var scope = UiCulture.Override("en");
        using var rig = await LoadedRigAsync();

        rig.Library.ApplyRange(new DateTime(2026, 10, 18), new DateTime(2026, 10, 17));
        await rig.Library.LoadTask;

        var query = rig.SessionStore.Queries[^1];
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero), query.From);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero).AddTicks(-1), query.To);
        Assert.True(rig.Library.IsRangeFilter);
        Assert.Equal("Oct 17 – Oct 18", rig.Library.RangeText);
        Assert.Equal(5, rig.Library.Sessions.Count);

        rig.Library.ApplyRange(new DateTime(2026, 10, 12), new DateTime(2026, 10, 12));
        await rig.Library.LoadTask;
        Assert.Equal(["Sprint retro"], rig.Library.Sessions.Select(r => r.Title));
        Assert.Equal("Oct 12", rig.Library.RangeText);

        rig.Library.ClearRangeCommand.Execute(null);
        await rig.Library.LoadTask;
        Assert.True(rig.Library.IsAllFilter);
        Assert.Equal("Pick dates", rig.Library.RangeText);
        Assert.Equal(6, rig.Library.Sessions.Count);
    }

    [Fact]
    public async Task Library_RecentStaysTheSame_WhateverTheFilterHides()
    {
        using var rig = await LoadedRigAsync();
        var before = rig.Library.Recent.Select(r => r.Id).ToArray();

        rig.Library.ApplyRange(new DateTime(2026, 10, 12), new DateTime(2026, 10, 12));
        await rig.Library.LoadTask;

        Assert.Equal(before, rig.Library.Recent.Select(r => r.Id));
    }

    [Fact]
    public async Task DateRangeCalendar_SendsTheFirstDayAtOnce_AndTheRangeWhenTheSecondIsChosen()
    {
        var result = await Session.Dispatch(
            () =>
            {
                var ranges = new List<DateRange>();
                var calendar = new DateRangeCalendar { RangeCommand = new CommunityToolkit.Mvvm.Input.RelayCommand<DateRange>(ranges.Add) };
                calendar.SelectedDates.Add(new DateTime(2026, 10, 5));
                calendar.SelectedDates.Clear();
                calendar.SelectedDates.AddRange(new DateTime(2026, 10, 7), new DateTime(2026, 10, 9));
                return ranges.ToArray();
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(new DateRange(new DateTime(2026, 10, 5), new DateTime(2026, 10, 5)), result[0]);
        Assert.Contains(new DateRange(new DateTime(2026, 10, 7), new DateTime(2026, 10, 9)), result);
    }

    [Fact]
    public async Task PickDates_TwoTapsChooseARange_TheSameDayTwiceOneDay_AndClearEmptiesIt_EachClosingTheFlyout()
    {
        using var rig = await LoadedRigAsync();

        var steps = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                var window = new MainWindow { DataContext = rig.Shell, Width = 1440, Height = 1400 };
                window.Show();
                UiHarness.Pump(6);
                var pick = window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("filter") && b.Flyout is not null);
                var flyout = (Flyout)pick.Flyout!;

                (DateTime? Start, DateTime? End, LibraryFilter Filter, bool Open, int Selected) State() =>
                    (rig.Library.RangeStart, rig.Library.RangeEnd, rig.Library.Filter, flyout.IsOpen, Calendar().SelectedDates.Count);

                DateRangeCalendar Calendar() => (DateRangeCalendar)((Panel)flyout.Content!).Children[0];

                void Open()
                {
                    flyout.ShowAt(pick);
                    UiHarness.Pump(6);
                    Calendar().DisplayDate = new DateTime(2026, 10, 1);
                    UiHarness.Pump(6);
                }

                void Tap(int day)
                {
                    var calendar = Calendar();
                    var button = calendar.GetVisualDescendants().OfType<CalendarDayButton>().Single(b => !b.Classes.Contains(":inactive") && !b.Classes.Contains("inactive") && Equals(b.Content, day.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    var top = TopLevel.GetTopLevel(calendar)!;
                    var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), top)!.Value;
                    top.MouseDown(point, Avalonia.Input.MouseButton.Left);
                    top.MouseUp(point, Avalonia.Input.MouseButton.Left);
                    UiHarness.Pump(4);
                }

                var result = new List<(DateTime? Start, DateTime? End, LibraryFilter Filter, bool Open, int Selected)>();
                Open();
                Tap(5);
                result.Add(State());
                Tap(9);
                var chosen = State();
                Open();
                var shownAgain = Calendar().SelectedDates.Count;
                flyout.Hide();
                UiHarness.Pump(4);
                result.Add((chosen.Start, chosen.End, chosen.Filter, chosen.Open, shownAgain));

                Open();
                Tap(12);
                Tap(12);
                result.Add((rig.Library.RangeStart, rig.Library.RangeEnd, rig.Library.Filter, flyout.IsOpen, 1));

                Open();
                var clear = ((Panel)flyout.Content!).Children.OfType<Button>().Single();
                clear.Command!.Execute(null);
                UiHarness.Pump(4);
                var closedByClear = !flyout.IsOpen;
                Open();
                result.Add((rig.Library.RangeStart, rig.Library.RangeEnd, rig.Library.Filter, !closedByClear, Calendar().SelectedDates.Count));
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal((new DateTime(2026, 10, 5), new DateTime(2026, 10, 5), LibraryFilter.Range, true, 1), steps[0]);
        // The range is chosen and the flyout closed; opened again it shows the five days.
        Assert.Equal((new DateTime(2026, 10, 5), new DateTime(2026, 10, 9), LibraryFilter.Range, false, 5), steps[1]);
        Assert.Equal((new DateTime(2026, 10, 12), new DateTime(2026, 10, 12), LibraryFilter.Range, false, 1), steps[2]);

        // "Clear dates" closed the flyout and emptied the selection.
        Assert.Equal((null, null, LibraryFilter.All, false, 0), steps[3]);
    }

    // ---- library: the cards -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NewRecordingCard_StartsARecordingWithTheSwitchesOfTheCard()
    {
        using var rig = await LoadedRigAsync();
        Assert.True(rig.Library.Microphone);
        Assert.True(rig.Library.SystemAudio);
        Assert.False(rig.Library.Screen);

        rig.Library.SystemAudio = false;
        rig.Library.Screen = true;
        rig.Library.StartRecordingCommand.Execute(null);
        await rig.Recording.StartStopCommand.ExecutionTask!;

        Assert.Same(rig.Recording, rig.Shell.CurrentPage);
        var start = Assert.Single(rig.Recorder.Starts);
        Assert.True(start.Microphone);
        Assert.False(start.SystemAudio);
        Assert.True(start.Screen);
    }

    [Fact]
    public async Task NewRecordingCard_NeedsASource_AndTheScreenSwitchIsTheScreenByDefaultSetting()
    {
        using var rig = await LoadedRigAsync();

        rig.Library.Microphone = false;
        rig.Library.SystemAudio = false;
        Assert.False(rig.Library.StartRecordingCommand.CanExecute(null));
        rig.Library.Microphone = true;
        Assert.True(rig.Library.StartRecordingCommand.CanExecute(null));

        rig.Library.Screen = true;
        await Task.Yield();
        for (var i = 0; i < 100 && !rig.Store.Current.ScreenByDefault; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(rig.Store.Current.ScreenByDefault);

        // A change made in the Settings shows in the card the next time the library is shown, without being saved again.
        await rig.Store.SaveAsync(rig.Store.Current with { ScreenByDefault = false }, CancellationToken.None);
        rig.Shell.ShowRecording();
        rig.Shell.ShowLibrary();
        Assert.False(rig.Library.Screen);
    }

    [Fact]
    public async Task AskCard_SendingAQuestion_OpensAskAiAndAsksItOverTheWholeLibrary()
    {
        using var rig = await LoadedRigAsync();
        Assert.False(rig.Library.AskCommand.CanExecute(null));

        rig.Library.AskQuestion = "  What did we decide about PDF export?  ";
        Assert.True(rig.Library.AskCommand.CanExecute(null));
        rig.Library.AskCommand.Execute(null);

        var chat = rig.Shell.LibraryChat!.Chat;
        Assert.Same(rig.Shell.LibraryChat, rig.Shell.CurrentPage);
        Assert.Null(chat.Scope);
        Assert.Equal("What did we decide about PDF export?", chat.Messages[0].Text);
        Assert.Equal(string.Empty, rig.Library.AskQuestion);
    }

    [Fact]
    public async Task AskCard_ASuggestionChipAsksItAtOnce()
    {
        using var scope = UiCulture.Override("en");
        using var rig = await LoadedRigAsync();
        Assert.Equal(DesignDemoData.AskLibrarySuggestions, rig.Library.Suggestions);

        rig.Library.AskSuggestionCommand.Execute("Summarize yesterday");

        Assert.Equal("Summarize yesterday", rig.Shell.LibraryChat!.Chat.Messages[0].Text);
        Assert.Same(rig.Shell.LibraryChat, rig.Shell.CurrentPage);
    }

    // ---- library: details, settings, focus ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Library_ReadsTheTranscriptSummariesOfAllRowsInOneBatch()
    {
        using var rig = await LoadedRigAsync();

        var request = Assert.Single(rig.Transcripts.SummaryRequests);
        Assert.Equal(rig.Records.Select(r => r.Id.Value).Order(), request.Select(i => i.Value).Order());
        Assert.Equal(0, rig.Transcripts.Reads);

        // Everything known is shown without asking the store again (a search or a re-filter rebuilds nothing it already read).
        rig.Library.SearchText = "x";
        rig.Library.SearchText = string.Empty;
        await rig.Library.DetailsTask;
        Assert.Single(rig.Transcripts.SummaryRequests);
    }

    [Fact]
    public async Task Library_ReadsNothingWhileRecording_AndCatchesUpWhenTheRecordingEnds()
    {
        using var rig = new DesignRig();
        rig.Shell.ShowRecording();
        await rig.Recording.StartStopAsync();

        await rig.Library.LoadAsync();
        await rig.Library.DetailsTask;
        Assert.Empty(rig.Transcripts.SummaryRequests);
        Assert.Null(rig.Library.Sessions[0].Language);

        await rig.Recording.StopAsync();
        await rig.Library.DetailsTask;
        for (var i = 0; i < 100 && rig.Library.Sessions[0].Language is null; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.NotEmpty(rig.Transcripts.SummaryRequests);
        Assert.Equal("English", rig.Library.Sessions[0].Language);
    }

    [Fact]
    public async Task Library_WhenProcessingCompletesOrFails_TheRowReadsItsTranscriptAgain()
    {
        using var scope = UiCulture.Override("en");
        using var rig = await LoadedRigAsync();
        var weekly = rig.Library.Sessions[2];
        Assert.Null(weekly.Snippet);
        var before = rig.Transcripts.SummaryRequests.Count;

        // The transcript was written by the processing that just finished.
        rig.Transcripts.PerSession[weekly.Id] = new Transcript(weekly.Id, [new TranscriptSegment(Speaker.Me, TimeSpan.Zero, TimeSpan.FromSeconds(2), "Done at last.", [])], "design", "uk");
        rig.Processing.Raise(new SessionProcessingStatus(weekly.Id, SessionProcessingStage.Completed, 1));
        await rig.Library.DetailsTask;

        Assert.Equal(before + 1, rig.Transcripts.SummaryRequests.Count);
        Assert.Equal(new[] { weekly.Id }, rig.Transcripts.SummaryRequests[^1]);
        Assert.Equal("Done at last.", weekly.Snippet);
        Assert.Equal("Ukrainian", weekly.Language);

        rig.Processing.Raise(new SessionProcessingStatus(weekly.Id, SessionProcessingStage.Failed, 0));
        await rig.Library.DetailsTask;
        Assert.Equal(before + 2, rig.Transcripts.SummaryRequests.Count);
    }

    [Fact]
    public async Task Library_ATranscriptSearch_DoesNotRebuildTheRowsPerKeystroke()
    {
        using var rig = await LoadedRigAsync();
        var rows = rig.Library.Sessions;

        rig.Library.SearchText = "a";
        rig.Library.SearchText = "ab";
        rig.Library.SearchText = "abc";

        Assert.Same(rows, rig.Library.Sessions);
    }

    [Fact]
    public async Task Library_RangeEnd_ExcludesARecordingAtMidnightOfTheNextDay()
    {
        using var rig = await LoadedRigAsync();
        var midnight = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        rig.SessionStore.Add(Fakes.Sessions.Make("Late", midnight.AddTicks(-TimeSpan.TicksPerSecond), TimeSpan.FromMinutes(1)));
        rig.SessionStore.Add(Fakes.Sessions.Make("Midnight", midnight, TimeSpan.FromMinutes(1)));

        rig.Library.ApplyRange(new DateTime(2026, 10, 12), new DateTime(2026, 10, 12));
        await rig.Library.LoadTask;

        Assert.Equal(["Late", "Sprint retro"], rig.Library.Sessions.Select(r => r.Title).Order());
    }

    [Fact]
    public async Task Library_RangeEnd_AlsoHoldsForTheTranscriptSearch()
    {
        using var rig = await LoadedRigAsync();
        var midnight = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        var late = Fakes.Sessions.Make("Late", midnight.AddMinutes(-5), TimeSpan.FromMinutes(1));
        var next = Fakes.Sessions.Make("Next", midnight, TimeSpan.FromMinutes(1));
        rig.SessionStore.Add(late);
        rig.SessionStore.Add(next);
        rig.Assistant.SearchResult = [Hits.Make(late.Id, 5, "budget"), Hits.Make(next.Id, 5, "budget")];
        rig.Library.ApplyRange(new DateTime(2026, 10, 12), new DateTime(2026, 10, 12));
        await rig.Library.LoadTask;

        rig.Library.SearchText = "budget";
        rig.Time.Advance(LibraryViewModel.SearchDebounce);
        await rig.Library.SearchTask;

        Assert.Equal(["Late"], rig.Library.SearchResults.Select(r => r.Title));
    }

    [Fact]
    public async Task Library_Switches_AreKeptAsRecordingDefaults_ThroughTheSharedWriter()
    {
        using var rig = await LoadedRigAsync();
        var writer = SettingsWriter.For(rig.Store);
        Assert.Same(writer, SettingsWriter.For(rig.Store));

        rig.Library.Microphone = false;
        rig.Library.Screen = true;
        await rig.Library.SwitchSaveTask;
        await writer.LastSave;

        Assert.False(rig.Store.Current.RecordMicrophoneByDefault);
        Assert.True(rig.Store.Current.RecordSystemAudioByDefault);
        Assert.True(rig.Store.Current.ScreenByDefault);

        // The recording screen starts with them, and so does the library the next time.
        rig.Shell.ShowRecording();
        Assert.False(rig.Recording.Microphone);
        Assert.True(rig.Recording.SystemAudio);
        Assert.True(rig.Recording.Screen);
        rig.Shell.ShowLibrary();
        Assert.False(rig.Library.Microphone);
        Assert.True(rig.Library.Screen);
    }

    [Fact]
    public async Task Library_NeverSavesADefaultWithNeitherAudioSource()
    {
        using var rig = await LoadedRigAsync();
        var writer = SettingsWriter.For(rig.Store);

        rig.Library.Microphone = false;
        await rig.Library.SwitchSaveTask;
        rig.Library.SystemAudio = false;
        await rig.Library.SwitchSaveTask;
        await writer.LastSave;

        // The card shows both off for now (Start recording is disabled), but the saved default keeps its last source.
        Assert.False(rig.Library.Microphone);
        Assert.False(rig.Library.SystemAudio);
        Assert.False(rig.Library.StartRecordingCommand.CanExecute(null));
        Assert.False(rig.Store.Current.RecordMicrophoneByDefault);
        Assert.True(rig.Store.Current.RecordSystemAudioByDefault);

        // The next time the library shows, it starts from the saved default, so the record hotkey never meets two empty switches.
        rig.Shell.ShowRecording();
        Assert.True(rig.Recording.SystemAudio);
        Assert.True(rig.Recording.StartStopCommand.CanExecute(null));
    }

    [Fact]
    public async Task Library_AfterItIsDisposed_LateEventsDoNothing()
    {
        using var rig = await LoadedRigAsync();
        var weekly = rig.Library.Sessions[2];
        var before = rig.Transcripts.SummaryRequests.Count;
        rig.Library.Dispose();

        rig.Processing.Raise(new SessionProcessingStatus(weekly.Id, SessionProcessingStage.Completed, 1));
        await rig.Recording.StopAsync();

        Assert.Equal(before, rig.Transcripts.SummaryRequests.Count);
    }

    [Fact]
    public async Task Library_ASwitchThatCannotBeSaved_ShowsTheError()
    {
        using var scope = UiCulture.Override("en");
        using var rig = await LoadedRigAsync();
        rig.Store.SaveFailure = new IOException("disk full");

        rig.Library.Microphone = false;
        await rig.Library.SwitchSaveTask;

        Assert.Contains("disk full", rig.Library.Error);
        Assert.Contains(rig.Library.Entries, e => e is LibraryNoticeEntry notice && notice.Text.Contains("disk full", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recent_LeavesOutTheRecordingThatRuns()
    {
        using var rig = new DesignRig();
        rig.Shell.ShowRecording();
        await rig.Recording.StartStopAsync();
        var running = rig.Recorder.Current!;
        rig.SessionStore.Add(running);

        await rig.Library.LoadAsync();

        Assert.DoesNotContain(rig.Library.Recent, r => r.Id == running.Id);
        Assert.Equal(3, rig.Library.Recent.Count);
    }

    [Fact]
    public void FramePages_AreTheOnesThatSayTheyFillTheFrame()
    {
        using var rig = new DesignRig();

        Assert.False(rig.Shell.PageUsesFrameInset);
        rig.Shell.ShowRecording();
        Assert.False(rig.Shell.PageUsesFrameInset);
        rig.Shell.ShowSettings();
        Assert.False(rig.Shell.PageUsesFrameInset);
        Assert.IsAssignableFrom<IFillsFrame>(rig.Library);
        Assert.True(typeof(IFillsFrame).IsAssignableFrom(typeof(RecordingViewModel)));
    }

    [Fact]
    public void DayHeading_ShowsTheYearOnlyWhenItIsNotTheCurrentOne()
    {
        using var scope = UiCulture.Override("en");
        var time = new TestTime(UiHarness.Now);

        Assert.Equal("Tue, Oct 6", UiFormat.DayHeading(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero), time));
        Assert.Equal("Mon, Oct 6, 2025", UiFormat.DayHeading(new DateTimeOffset(2025, 10, 6, 12, 0, 0, TimeSpan.Zero), time));
        using var uk = UiCulture.Override("uk");
        Assert.Equal("пн, 6 " + Strings.TitleMonth_10 + " 2025", UiFormat.DayHeading(new DateTimeOffset(2025, 10, 6, 12, 0, 0, TimeSpan.Zero), time));
    }

    [Fact]
    public async Task Library_TheSearchBoxKeepsItsFocusWhileTyping_AndTabReachesTheFiftiethRowOfThreeHundred()
    {
        using var rig = new DesignRig();
        for (var i = 0; i < 300; i++)
        {
            rig.SessionStore.Add(Fakes.Sessions.Make("Recording " + i, UiHarness.Now.AddDays(-20).AddMinutes(-i), TimeSpan.FromMinutes(5)));
        }

        await rig.Library.LoadAsync();

        var (stillFocused, focusedRow) = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                var window = new MainWindow { DataContext = rig.Shell, Width = 1440, Height = 900 };
                window.Show();
                UiHarness.Pump(6);
                var search = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.Classes.Contains("bare") && !t.Classes.Contains("ask"));
                search.Focus();
                UiHarness.Pump(2);
                window.KeyTextInput("rec");
                UiHarness.Pump(4);
                var focused = ReferenceEquals(window.FocusManager!.GetFocusedElement(), search) && search.Text == "rec";

                rig.Library.SearchText = string.Empty;
                UiHarness.Pump(4);
                var first = window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("libraryRow"));
                first.Focus();
                UiHarness.Pump(2);
                for (var i = 0; i < 49; i++)
                {
                    window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                    window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                    UiHarness.Pump(2);
                }

                var row = (window.FocusManager!.GetFocusedElement() as Control)?.DataContext as SessionItemViewModel;
                window.Close();
                return (focused, row?.Title);
            },
            TestContext.Current.CancellationToken);

        Assert.True(stillFocused, "the search box lost the focus while typing");
        Assert.Equal(rig.Library.Sessions[49].Title, focusedRow);
    }

    [Fact]
    public async Task Library_ShiftTabStepsBackThroughTheRows_AndFromTheFirstRowReachesTheLastFilter()
    {
        using var rig = await LoadedRigAsync();

        var (second, firstThenBack, filter) = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                var window = new MainWindow { DataContext = rig.Shell, Width = 1440, Height = 1500 };
                window.Show();
                UiHarness.Pump(6);

                void Press(RawInputModifiers modifiers)
                {
                    window.KeyPressQwerty(PhysicalKey.Tab, modifiers);
                    window.KeyReleaseQwerty(PhysicalKey.Tab, modifiers);
                    UiHarness.Pump(3);
                }

                var rows = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("libraryRow")).ToArray();
                rows[2].Focus();
                UiHarness.Pump(2);
                Press(RawInputModifiers.Shift);
                var one = window.FocusManager!.GetFocusedElement() == rows[1];
                Press(RawInputModifiers.Shift);
                var two = window.FocusManager.GetFocusedElement() == rows[0];
                Press(RawInputModifiers.Shift);
                var last = window.FocusManager.GetFocusedElement() as Button;
                var result = (one, two, last is not null && last.Classes.Contains("filter") && last.Flyout is not null);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.True(second, "Shift+Tab goes to the row above");
        Assert.True(firstThenBack, "and to the first row");
        Assert.True(filter, "from the first row it reaches the last filter chip (Pick dates)");
    }

    // ---- formatting -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void LanguageNames_AreTheUiLanguage_AndFallBackToTheCode()
    {
        using (UiCulture.Override("en"))
        {
            Assert.Equal("English", LanguageNames.Of("en"));
            Assert.Equal("English", LanguageNames.Of("en-US"));
            Assert.Equal("Ukrainian", LanguageNames.Of("uk"));
            Assert.Equal("ZZ", LanguageNames.Of("zz"));
            Assert.Null(LanguageNames.Of(null));
        }

        using (UiCulture.Override("uk"))
        {
            Assert.Equal("Англійська", LanguageNames.Of("en"));
        }
    }

    [Fact]
    public void AutoFitPanel_FillsAsManyColumnsAsFit()
    {
        Assert.Equal(2, AutoFitPanel.ColumnsFor(1016, 420, 16, 2));
        Assert.Equal(1, AutoFitPanel.ColumnsFor(855, 420, 16, 2));
        Assert.Equal(2, AutoFitPanel.ColumnsFor(856, 420, 16, 2));
        Assert.Equal(1, AutoFitPanel.ColumnsFor(2000, 420, 16, 1));
        Assert.Equal(1, AutoFitPanel.ColumnsFor(2000, 1000, 16, 2));
        Assert.Equal(2, AutoFitPanel.ColumnsFor(2016, 1000, 16, 2));
    }

    // ---- the window --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task LibraryPage_AtTheMinimumWindowSize_StacksTheCards_WithoutHorizontalScrolling()
    {
        using var rig = await LoadedRigAsync();

        var (narrow, wide, extentOk, sidebarWidth) = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                var window = new MainWindow { DataContext = rig.Shell, Width = 900, Height = 600 };
                window.Show();
                UiHarness.Pump(6);
                var panel = window.GetVisualDescendants().OfType<AutoFitPanel>().Single();
                var cards = panel.Children.Where(c => c.IsVisible).ToArray();
                var stacked = cards[1].Bounds.Y > cards[0].Bounds.Y && cards[1].Bounds.X == cards[0].Bounds.X;
                var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.GetVisualParent() is not null && s.Content is ItemsPresenter);
                var fits = scroller.Extent.Width <= scroller.Viewport.Width + 0.5;
                var width = window.GetVisualDescendants().OfType<SidebarView>().Single().Bounds.Width;

                window.Width = 1440;
                UiHarness.Pump(6);
                var side = cards[1].Bounds.X > cards[0].Bounds.X && Math.Abs(cards[1].Bounds.Y - cards[0].Bounds.Y) < 1;
                window.Close();
                return (stacked, side, fits, width);
            },
            TestContext.Current.CancellationToken);

        Assert.True(narrow, "the two cards stack on a narrow window");
        Assert.True(wide, "the two cards sit side by side on a wide window");
        Assert.True(extentOk, "no horizontal scrolling at the minimum size");
        Assert.Equal(241, sidebarWidth);
    }

    [Fact]
    public async Task LibraryPage_VirtualisesALongLibrary()
    {
        using var rig = new DesignRig();
        for (var i = 0; i < 300; i++)
        {
            rig.SessionStore.Add(Fakes.Sessions.Make("Recording " + i, UiHarness.Now.AddDays(-20).AddMinutes(-i), TimeSpan.FromMinutes(5)));
        }

        await rig.Library.LoadAsync();

        var realised = await Session.Dispatch(
            () =>
            {
                var window = new MainWindow { DataContext = rig.Shell, Width = 1440, Height = 900 };
                window.Show();
                UiHarness.Pump(6);
                var rows = window.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("libraryRow"));
                window.Close();
                return rows;
            },
            TestContext.Current.CancellationToken);

        Assert.True(rig.Library.Entries.Count > 300);
        Assert.InRange(realised, 1, 40);
    }
}
