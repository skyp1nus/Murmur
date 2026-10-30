using Murmur.App.Localization;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Capture;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

public sealed class LibrarySearchTests : IDisposable
{
    // The texts asserted below are Ukrainian; English is the default language (D-039).
    private readonly IDisposable _ukrainian = UiCulture.Override("uk");

    private static readonly TimeSpan Debounce = LibraryViewModel.SearchDebounce;

    private readonly FakeSessionStore _store = new();
    private readonly TestTime _time = new(UiHarness.Now);
    private readonly FakeAssistant _assistant = new();
    private readonly FakeSearchIndex _keyword = new();
    private readonly SessionFixture _fixture = new();

    public void Dispose()
    {
        _ukrainian.Dispose();
        _fixture.Dispose();
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private LibraryViewModel Create(bool assistant = true, bool keyword = true) =>
        new(_store, _time, post: action => action(), keywordSearch: keyword ? _keyword : null, assistant: assistant ? _assistant : null);

    private SessionRecord Add(string title, double daysAgo = 0)
    {
        var record = Sessions.Make(title, UiHarness.Now.AddDays(-daysAgo).AddHours(-1), TimeSpan.FromHours(1), state: SessionState.Ready);
        _store.Add(record);
        return record;
    }

    // Debounce ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Typing_RunsTheSearch300msAfterTheLastKeystroke_WithTheLatestText()
    {
        var standup = Add("Стендап");
        _assistant.SearchResult = [Hits.Make(standup.Id, 754, "переносимо реліз на пʼятницю")];
        var vm = Create();
        await vm.LoadAsync();

        vm.SearchText = "р";
        _time.Advance(TimeSpan.FromMilliseconds(200));
        vm.SearchText = "ре";
        _time.Advance(TimeSpan.FromMilliseconds(200));
        vm.SearchText = "реліз";
        _time.Advance(Debounce - TimeSpan.FromMilliseconds(1));

        Assert.Empty(_assistant.Searches);
        Assert.True(vm.ShowSearchResults);
        Assert.False(vm.ShowSessionList);
        Assert.True(vm.IsSearching);
        Assert.Equal("Шукаю…", vm.SearchStatusText);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await vm.SearchTask;

        var search = Assert.Single(_assistant.Searches);
        Assert.Equal(("реліз", (SessionId?)null, LibraryViewModel.SearchLimit), search);
        Assert.False(vm.IsSearching);
        Assert.Null(vm.SearchStatusText);
        var row = Assert.Single(vm.SearchResults);
        Assert.Equal("Стендап", row.Title);
        Assert.Equal("12:34", row.TimeText);
        Assert.True(row.HasTime);
        Assert.Equal("переносимо реліз на пʼятницю", row.Snippet);
        Assert.True(vm.HasSearchResults);
        Assert.Empty(_keyword.Queries);
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public async Task ClearingTheBox_CancelsThePendingSearch_AndShowsTheSessionsAgain()
    {
        Add("Стендап");
        var vm = Create();
        await vm.LoadAsync();

        vm.SearchText = "реліз";
        vm.SearchText = string.Empty;
        _time.Advance(Debounce * 2);
        await vm.SearchTask;

        Assert.Empty(_assistant.Searches);
        Assert.False(vm.ShowSearchResults);
        Assert.False(vm.IsSearching);
        Assert.True(vm.ShowSessionList);
        Assert.Empty(vm.SearchResults);
        Assert.Equal(0, _time.ActiveTimers);
    }

    // Fallback ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhenTheAssistantFails_TheKeywordIndexAnswers()
    {
        var standup = Add("Стендап");
        _assistant.SearchFailure = new InvalidOperationException("embedding server crashed");
        _keyword.KeywordResult = [Hits.Make(standup.Id, 65, "бюджет на квартал", Core.Sessions.SearchSource.Keyword)];
        var vm = Create();
        await vm.LoadAsync();

        vm.SearchText = "бюджет";
        _time.Advance(Debounce);
        await vm.SearchTask;

        Assert.Single(_assistant.Searches);
        Assert.Equal(["бюджет"], _keyword.Queries);
        var row = Assert.Single(vm.SearchResults);
        Assert.Equal("01:05", row.TimeText);
        Assert.Null(vm.SearchError);
    }

    [Fact]
    public async Task WithoutAnAssistant_TheKeywordIndexIsUsed()
    {
        var standup = Add("Стендап");
        _keyword.KeywordResult = [Hits.Make(standup.Id, 3725, "дедлайн у четвер", Core.Sessions.SearchSource.Keyword)];
        var vm = Create(assistant: false);
        await vm.LoadAsync();

        vm.SearchText = "дедлайн";
        _time.Advance(Debounce);
        await vm.SearchTask;

        Assert.Equal(["дедлайн"], _keyword.Queries);
        Assert.Equal("1:02:05", Assert.Single(vm.SearchResults).TimeText);
    }

    [Fact]
    public async Task WithoutAnyTranscriptSearch_ThePillFiltersTitlesAsBefore()
    {
        Add("Стендап");
        Add("Ретро");
        var vm = Create(assistant: false, keyword: false);
        await vm.LoadAsync();

        vm.SearchText = "ретро";

        Assert.False(vm.HasTranscriptSearch);
        Assert.False(vm.ShowSearchResults);
        Assert.Equal(["Ретро"], vm.Sessions.Select(s => s.Title));
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public async Task AFailingSearch_ShowsTheError()
    {
        Add("Стендап");
        _assistant.SearchFailure = new InvalidOperationException("index is locked");
        var vm = Create(keyword: false);
        await vm.LoadAsync();

        vm.SearchText = "бюджет";
        _time.Advance(Debounce);
        await vm.SearchTask;

        Assert.Equal("Пошук не вдався: index is locked", vm.SearchError);
        Assert.Equal(vm.SearchError, vm.SearchStatusText);
        Assert.False(vm.ShowNoSearchResults);
    }

    // Results -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Results_ListTitleMatchesFirst_RespectTheDateFilter_AndDropDeletedSessions()
    {
        var today = Add("Бюджет на квартал");
        var old = Add("Ретро", daysAgo: 30);
        _assistant.SearchResult =
        [
            Hits.Make(old.Id, 30, "бюджет ретро"),
            Hits.Make(today.Id, 90, "обговорили бюджет"),
            Hits.Make(SessionId.New(), 10, "видалений запис"),
        ];
        var vm = Create();
        vm.Filter = LibraryFilter.Week;
        await vm.LoadTask;

        vm.SearchText = "бюджет";
        _time.Advance(Debounce);
        await vm.SearchTask;

        Assert.Equal(
            [("Бюджет на квартал", false), ("Бюджет на квартал", true)],
            vm.SearchResults.Select(r => (r.Title, r.HasTime)));
        Assert.Equal("Сьогодні, 14:00", vm.SearchResults[0].Snippet);
        Assert.Equal("01:30", vm.SearchResults[1].TimeText);
    }

    [Fact]
    public async Task NoHits_ShowsNothingFound()
    {
        Add("Стендап");
        var vm = Create();
        await vm.LoadAsync();

        vm.SearchText = "космос";
        Assert.False(vm.ShowNoSearchResults);
        _time.Advance(Debounce);
        await vm.SearchTask;

        Assert.True(vm.ShowNoSearchResults);
        Assert.False(vm.HasSearchResults);
        Assert.False(vm.ShowEmptyState);
    }

    [Fact]
    public void LongSnippets_AreCutAtAWord()
    {
        var text = string.Join(' ', Enumerable.Repeat("слово", 60));

        var snippet = SearchResultViewModel.Shorten("  " + text.Replace(" ", " \n ", StringComparison.Ordinal));

        Assert.True(snippet.Length <= SearchResultViewModel.MaxSnippetLength + 1);
        Assert.EndsWith("слово…", snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("  ", snippet, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', snippet);
    }

    // Navigation --------------------------------------------------------------------------------------------------

    private ShellViewModel CreateShell(LibraryViewModel library) =>
        new(
            library,
            new RecordingViewModel(new FakeRecorder(), new FakeCapture(), _store, action => action()),
            sessionFactory: record => new SessionViewModel(record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action()),
            chatFactory: scope => new ChatViewModel(_assistant, new FakeLlm(), null, scope, post: action => action()));

    [Fact]
    public async Task ClickingAResult_OpensTheSessionAtThatTime_Paused()
    {
        _store.Add(_fixture.Record);
        _fixture.UseShortTranscript();
        _assistant.SearchResult = [Hits.Make(_fixture.Record.Id, 20, "Добрий день колеги")];
        var library = Create();
        var shell = CreateShell(library);
        await library.LoadAsync();
        library.SearchText = "колеги";
        _time.Advance(Debounce);
        await library.SearchTask;

        library.SelectedSearchResult = library.SearchResults.Single(r => r.HasTime);
        await shell.SessionLoadTask;

        Assert.Null(library.SelectedSearchResult);
        var session = Assert.IsType<SessionViewModel>(shell.CurrentPage);
        Assert.Equal(_fixture.Record.Id, session.Id);
        Assert.Equal([S(20)], _fixture.Player.Seeks);
        Assert.Equal(0, _fixture.Player.Plays);
        Assert.Equal(S(20), session.Position);
        Assert.Equal(1, session.CurrentSegmentIndex);
    }

    [Fact]
    public async Task ATitleMatch_OpensTheSessionFromTheStart()
    {
        _store.Add(_fixture.Record);
        var library = Create();
        var shell = CreateShell(library);
        await library.LoadAsync();
        library.SearchText = "стендап";
        _time.Advance(Debounce);
        await library.SearchTask;

        library.SelectedSearchResult = Assert.Single(library.SearchResults);
        await shell.SessionLoadTask;

        Assert.IsType<SessionViewModel>(shell.CurrentPage);
        Assert.Empty(_fixture.Player.Seeks);
    }

    [Fact]
    public async Task AskAll_OpensTheLibraryWideChat_AndKeepsItsHistory()
    {
        var library = Create();
        var shell = CreateShell(library);
        Assert.True(library.CanAskAll);

        library.AskAllCommand.Execute(null);

        var page = Assert.IsType<LibraryChatViewModel>(shell.CurrentPage);
        Assert.Null(page.Chat.Scope);
        page.Chat.Question = "Що вирішили?";
        page.Chat.AskCommand.Execute(null);
        _assistant.Finish();
        await page.Chat.AskTask;

        page.BackCommand.Execute(null);
        Assert.Same(library, shell.CurrentPage);
        library.AskAllCommand.Execute(null);

        Assert.Same(page, shell.CurrentPage);
        Assert.Equal(2, page.Chat.Messages.Count);
        Assert.Null(Assert.Single(_assistant.Asks).Scope);
    }

    [Fact]
    public async Task AChipInTheLibraryChat_OpensTheCitedSessionAtThatTime_AndPlays_ThenBackReturnsToTheChat()
    {
        var other = Add("Інший мітинг");
        _store.Add(_fixture.Record);
        _fixture.UseShortTranscript();
        var library = Create();
        var shell = CreateShell(library);
        await library.LoadAsync();
        shell.ShowLibraryChat();
        var chat = shell.LibraryChat!.Chat;
        chat.Question = "Хто привітав колег?";
        chat.AskCommand.Execute(null);
        _assistant.Answer(["Інші [00:20], див. також [00:05]."], Hits.Cite(_fixture.Record.Id, 20), Hits.Cite(other.Id, 5));
        await chat.AskTask;

        chat.Messages[1].Citations[0].ActivateCommand.Execute(null);
        await shell.NavigationTask;
        await shell.SessionLoadTask;

        var session = Assert.IsType<SessionViewModel>(shell.CurrentPage);
        Assert.Equal(_fixture.Record.Id, session.Id);
        Assert.Equal([S(20)], _fixture.Player.Seeks);
        Assert.Equal(1, _fixture.Player.Plays);
        Assert.Equal(PlaybackState.Playing, _fixture.Player.State);

        // The breadcrumb leads back to where the chip was clicked: the chat, not the library.
        var crumb = Assert.IsType<BreadcrumbViewModel>(shell.TopBar.Leading);
        Assert.Equal((Murmur.App.Resources.Strings.Common_AskAi, true), (crumb.Root, crumb.IsLink));
        crumb.RootCommand!.Execute(null);

        Assert.Same(shell.LibraryChat, shell.CurrentPage);
        Assert.True(_fixture.Player.Disposed);
        // On the chat page itself the top bar holds the scope pills (no breadcrumb).
        Assert.Same(shell.LibraryChat!.ScopePills, shell.TopBar.Leading);
    }

    [Fact]
    public async Task AChipForADeletedSession_StaysOnTheChat()
    {
        var library = Create();
        var shell = CreateShell(library);
        await library.LoadAsync();
        shell.ShowLibraryChat();

        await shell.OpenSessionAtAsync(SessionId.New(), S(10), play: true);

        Assert.IsType<LibraryChatViewModel>(shell.CurrentPage);
    }
}
