using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

public sealed class LibraryProcessingTests
{
    private static readonly DateTimeOffset Now = UiHarness.Now;

    private readonly FakeSessionStore _store = new();
    private readonly FakeProcessing _processing = new();
    private readonly TestTime _time = new(Now);

    private LibraryViewModel Create() => new(_store, _time, _processing, action => action());

    private SessionRecord Add(string title, SessionState state)
    {
        var record = Sessions.Make(title, Now.AddHours(-1), TimeSpan.FromMinutes(10), state: state);
        _store.Add(record);
        return record;
    }

    [Fact]
    public async Task Rows_ShowTheStoredStateOfFinishedWork()
    {
        Add("Ready", SessionState.Ready);
        Add("Failed", SessionState.Failed);
        Add("Plain", SessionState.Recorded);
        var vm = Create();

        await vm.LoadAsync();

        var badges = vm.Sessions.ToDictionary(s => s.Title, s => s.StatusText);
        Assert.Equal("Готово", badges["Ready"]);
        Assert.Equal("Помилка", badges["Failed"]);
        Assert.Null(badges["Plain"]);
    }

    [Fact]
    public async Task Rows_ShowTheLiveProgress_WhenTheListLoads()
    {
        var record = Add("Running", SessionState.Transcribing);
        _processing.Set(new SessionProcessingStatus(record.Id, SessionProcessingStage.Transcribing, 0.42));
        var vm = Create();

        await vm.LoadAsync();

        Assert.Equal("Транскрибується 42 %", vm.Sessions[0].StatusText);
        Assert.True(vm.Sessions[0].HasStatus);
    }

    [Fact]
    public async Task Rows_FollowTheProgressEvents_ThroughToReady()
    {
        var record = Add("Running", SessionState.Recorded);
        var vm = Create();
        await vm.LoadAsync();
        var row = vm.Sessions[0];
        var changes = new List<string?>();
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionItemViewModel.StatusText))
            {
                changes.Add(row.StatusText);
            }
        };

        _processing.Raise(new SessionProcessingStatus(record.Id, SessionProcessingStage.Queued, 0));
        _processing.Raise(new SessionProcessingStatus(record.Id, SessionProcessingStage.Transcribing, 0.425));
        _processing.Raise(new SessionProcessingStatus(record.Id, SessionProcessingStage.Transcribing, 0.429));
        _processing.Raise(new SessionProcessingStatus(record.Id, SessionProcessingStage.Saving, 0.98));
        _processing.Raise(new SessionProcessingStatus(record.Id, SessionProcessingStage.Completed, 1));

        // Equal text raises no notification (42 % twice).
        Assert.Equal(["У черзі", "Транскрибується 42 %", "Транскрибується 98 %", "Готово"], changes);
    }

    [Fact]
    public async Task Rows_ShowAFailureAndAMissingModel()
    {
        var failed = Add("A", SessionState.Recorded);
        var missing = Add("B", SessionState.Recorded);
        var vm = Create();
        await vm.LoadAsync();

        _processing.Raise(new SessionProcessingStatus(failed.Id, SessionProcessingStage.Failed, 0, "boom"));
        _processing.Raise(new SessionProcessingStatus(missing.Id, SessionProcessingStage.ModelMissing, 0, "no model"));

        var badges = vm.Sessions.ToDictionary(s => s.Id, s => s.StatusText);
        Assert.Equal("Помилка", badges[failed.Id]);
        Assert.Equal("Без транскрипту", badges[missing.Id]);
    }

    [Fact]
    public async Task EventsForSessionsOutsideTheList_AreIgnored()
    {
        Add("A", SessionState.Recorded);
        var vm = Create();
        await vm.LoadAsync();

        _processing.Raise(new SessionProcessingStatus(SessionId.New(), SessionProcessingStage.Transcribing, 0.5));

        Assert.Null(vm.Sessions[0].StatusText);
    }

    [Fact]
    public async Task ClickingARow_OpensTheSession_AndClearsTheSelection()
    {
        var record = Add("Open me", SessionState.Ready);
        var vm = Create();
        await vm.LoadAsync();
        var opened = new List<SessionRecord>();
        vm.SessionOpened += opened.Add;

        vm.SelectedSession = vm.Sessions[0];
        vm.SelectedSession = vm.Sessions[0];

        Assert.Equal([record, record], opened);
        Assert.Null(vm.SelectedSession);
    }

    [Fact]
    public async Task WithoutAPipeline_RowsStillShowTheStoredState()
    {
        Add("Ready", SessionState.Ready);
        var vm = new LibraryViewModel(_store, _time);

        await vm.LoadAsync();

        Assert.Equal("Готово", vm.Sessions[0].StatusText);
    }
}
