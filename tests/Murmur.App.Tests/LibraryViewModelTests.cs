using Murmur.App.Localization;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

public sealed class LibraryViewModelTests : IDisposable
{
    // The texts asserted below are Ukrainian; English is the default language (D-039).
    private readonly IDisposable _ukrainian = UiCulture.Override("uk");

    public void Dispose() => _ukrainian.Dispose();

    // Sunday 18 Oct 2026, 15:00 UTC.
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 15, 0, 0, TimeSpan.Zero);

    private readonly FakeSessionStore _store = new();
    private readonly TestTime _time = new(Now);

    private LibraryViewModel Create() => new(_store, _time);

    private void SeedThreeSessions()
    {
        _store.Add(Sessions.Make("Standup", Now.AddHours(-2), TimeSpan.FromMinutes(15)));
        _store.Add(Sessions.Make("Design review", Now.AddDays(-3), TimeSpan.FromMinutes(52)));
        _store.Add(Sessions.Make("Retro", Now.AddDays(-20), TimeSpan.FromHours(1) + TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Load_ListsSessionsNewestFirst_WithFormattedFields()
    {
        SeedThreeSessions();
        var vm = Create();

        await vm.LoadAsync();

        Assert.Equal(["Standup", "Design review", "Retro"], vm.Sessions.Select(s => s.Title));
        Assert.Equal("00:15:00", vm.Sessions[0].DurationText);
        Assert.Equal("01:05:00", vm.Sessions[2].DurationText);
        Assert.Equal("Сьогодні, 13:00", vm.Sessions[0].DateText);
        Assert.Equal("15 жовтня, 15:00", vm.Sessions[1].DateText);
        Assert.False(vm.IsLoading);
        Assert.True(vm.HasSessions);
        Assert.False(vm.ShowEmptyState);
    }

    [Fact]
    public async Task Rows_ShowBadgesForRecordedSources()
    {
        _store.Add(Sessions.Make("All three", Now.AddHours(-1), TimeSpan.FromMinutes(1), mic: true, system: true, screen: true));
        _store.Add(Sessions.Make("Mic only", Now.AddHours(-2), TimeSpan.FromMinutes(1), mic: true, system: false));
        var vm = Create();

        await vm.LoadAsync();

        var all = vm.Sessions[0];
        Assert.True(all.HasMicrophone && all.HasSystemAudio && all.HasScreen);
        var mic = vm.Sessions[1];
        Assert.True(mic.HasMicrophone);
        Assert.False(mic.HasSystemAudio);
        Assert.False(mic.HasScreen);
    }

    [Fact]
    public async Task InterruptedAndFailedSessions_GetAStatusBadge()
    {
        _store.Add(Sessions.Make("Crashed", Now.AddHours(-1), TimeSpan.FromMinutes(1), state: SessionState.Interrupted));
        _store.Add(Sessions.Make("Broken", Now.AddHours(-2), TimeSpan.FromMinutes(1), state: SessionState.Failed));
        _store.Add(Sessions.Make("Fine", Now.AddHours(-3), TimeSpan.FromMinutes(1)));
        var vm = Create();

        await vm.LoadAsync();

        Assert.Equal("Перервано", vm.Sessions[0].StatusText);
        Assert.Equal("Помилка", vm.Sessions[1].StatusText);
        Assert.False(vm.Sessions[2].HasStatus);
    }

    [Fact]
    public async Task DateText_UsesYesterdayAndYearWhenNeeded()
    {
        _store.Add(Sessions.Make("Yesterday", Now.AddDays(-1), TimeSpan.FromMinutes(1)));
        _store.Add(Sessions.Make("Last year", new DateTimeOffset(2025, 3, 2, 9, 5, 0, TimeSpan.Zero), TimeSpan.FromMinutes(1)));
        var vm = Create();

        await vm.LoadAsync();

        Assert.Equal("Вчора, 15:00", vm.Sessions[0].DateText);
        Assert.Equal("2 березня 2025, 09:05", vm.Sessions[1].DateText);
    }

    [Fact]
    public async Task EmptyLibrary_ShowsThePhase0EmptyState()
    {
        var vm = Create();

        Assert.False(vm.ShowEmptyState);
        await vm.LoadAsync();

        Assert.True(vm.ShowEmptyState);
        Assert.False(vm.HasSessions);
        Assert.Equal("Ще немає записів", vm.EmptyTitle);
        Assert.Contains("Новий запис", vm.EmptyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TodayFilter_QueriesFromLocalMidnight()
    {
        SeedThreeSessions();
        var vm = Create();
        await vm.LoadAsync();

        vm.SetFilterCommand.Execute(LibraryFilter.Today);
        await vm.LoadTask;

        Assert.Equal(["Standup"], vm.Sessions.Select(s => s.Title));
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero), _store.Queries[^1].From);
        Assert.True(vm.IsTodayFilter);
        Assert.False(vm.IsAllFilter);
    }

    [Fact]
    public async Task WeekFilter_CoversTheLastSevenDays()
    {
        SeedThreeSessions();
        var vm = Create();

        vm.IsWeekFilter = true;
        await vm.LoadTask;

        Assert.Equal(["Standup", "Design review"], vm.Sessions.Select(s => s.Title));
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), _store.Queries[^1].From);
    }

    [Fact]
    public async Task AllFilter_HasNoLowerBound()
    {
        SeedThreeSessions();
        var vm = Create();
        vm.IsWeekFilter = true;
        await vm.LoadTask;

        vm.IsAllFilter = true;
        await vm.LoadTask;

        Assert.Equal(3, vm.Sessions.Count);
        Assert.Null(_store.Queries[^1].From);
    }

    [Fact]
    public async Task FilterWithNoMatches_ShowsAHintInsteadOfTheFirstRunEmptyState()
    {
        _store.Add(Sessions.Make("Old", Now.AddDays(-30), TimeSpan.FromMinutes(1)));
        var vm = Create();

        vm.IsTodayFilter = true;
        await vm.LoadTask;

        Assert.True(vm.ShowEmptyState);
        Assert.Equal("Нічого не знайдено", vm.EmptyTitle);
    }

    [Fact]
    public void UncheckingTheSelectedPill_KeepsItSelected_AndNotifies()
    {
        var vm = Create();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.IsAllFilter = false;

        Assert.Equal(LibraryFilter.All, vm.Filter);
        Assert.Contains(nameof(LibraryViewModel.IsAllFilter), raised);
    }

    [Fact]
    public async Task Search_FiltersByTitleWithoutHittingTheStore()
    {
        SeedThreeSessions();
        var vm = Create();
        await vm.LoadAsync();
        var queries = _store.Queries.Count;

        vm.SearchText = "retro";

        Assert.Equal(["Retro"], vm.Sessions.Select(s => s.Title));
        vm.SearchText = "DESIGN";
        Assert.Equal(["Design review"], vm.Sessions.Select(s => s.Title));
        Assert.Equal(queries, _store.Queries.Count);
    }

    [Fact]
    public async Task LoadFailure_ShowsInlineErrorAndNoRows()
    {
        _store.ListFailure = new IOException("db locked");
        var vm = Create();

        await vm.LoadAsync();

        Assert.Equal("Не вдалося завантажити записи: db locked", vm.Error);
        Assert.True(vm.HasError);
        Assert.Empty(vm.Sessions);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public void NewRecordingCommand_RaisesTheEvent()
    {
        var vm = Create();
        var raised = 0;
        vm.NewRecordingRequested += () => raised++;

        vm.NewRecordingCommand.Execute(null);

        Assert.Equal(1, raised);
    }
}
