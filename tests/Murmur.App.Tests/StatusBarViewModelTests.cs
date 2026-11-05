using Murmur.App.Localization;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Activity;

namespace Murmur.App.Tests;

public sealed class StatusBarViewModelTests
{
    private readonly FakeActivityFeed _feed = new();
    private readonly FakeRecorder _recorder = new();

    private StatusBarViewModel Create() => new(_feed, _recorder, action => action());

    private static ActivityItem Rec(string title = "Untitled recording") => new("rec", ActivityKind.Recording, title, null, false);

    private static ActivityItem Asr(string id, double? progress, bool queued = false, string title = "Weekly team sync") =>
        new("asr:" + id, ActivityKind.Transcribing, title, progress, queued);

    private static ActivityItem Model(double? progress, string name = "Gemma 4 26B") =>
        new("model:" + name, ActivityKind.DownloadingModel, name, progress, false);

    [Fact]
    public void NothingRunning_SaysUpToDate_WithAnEmptyPopover()
    {
        var bar = Create();

        Assert.True(bar.IsUpToDate);
        Assert.Equal("Up to date", bar.SummaryText);
        Assert.Empty(bar.SummaryParts);
        Assert.False(bar.IsRecording);
        Assert.True(bar.Activity.IsEmpty);
        Assert.Empty(bar.Activity.Rows);
        Assert.Equal("Nothing running.", bar.Activity.EmptyText);
        Assert.Equal("Activity", bar.Activity.Title);
        Assert.Equal("All on this PC", bar.Activity.Scope);
        Assert.Equal("All local", bar.AllLocalText);
    }

    [Fact]
    public void RunningJobs_AreListedWithPercentages_InTheSummary()
    {
        var bar = Create();

        _feed.Set(Asr("1", 0.64), Model(0.42));

        Assert.False(bar.IsUpToDate);
        Assert.Equal("Transcribing 64% · Downloading 42%", bar.SummaryText);
        Assert.Equal(
            [("Transcribing", "64%"), ("· Downloading", "42%")],
            bar.SummaryParts.Select(p => (p.Label, p.Percent)));
    }

    [Fact]
    public void ASingleJob_HasNoSeparator()
    {
        var bar = Create();

        _feed.Set(Model(0.05));

        Assert.Equal("Downloading 5%", bar.SummaryText);
    }

    [Fact]
    public void AJobWithoutProgress_ShowsOnlyItsWord()
    {
        var bar = Create();

        _feed.Set(new ActivityItem("idx:1", ActivityKind.Indexing, "Standup", null, false), Model(null));

        Assert.Equal("Indexing · Downloading", bar.SummaryText);
        Assert.All(bar.SummaryParts, part => Assert.False(part.HasPercent));
    }

    [Fact]
    public void SeveralJobsOfOneKind_ShareOneEntry_WithTheAverageProgress()
    {
        var bar = Create();

        _feed.Set(Asr("1", 0.2), Asr("2", 0.6));

        Assert.Equal("Transcribing 40%", bar.SummaryText);
    }

    [Fact]
    public void OnlyQueuedWork_IsNotUpToDate_ItCountsTheQueue()
    {
        var bar = Create();

        _feed.Set(Asr("1", null, queued: true), Asr("2", null, queued: true));

        Assert.False(bar.IsUpToDate);
        Assert.Equal("Queued 2", bar.SummaryText);
    }

    [Fact]
    public void QueuedWork_IsNotNamedInTheSummary_WhileSomethingRuns()
    {
        var bar = Create();

        _feed.Set(Asr("1", 0.5), Asr("2", null, queued: true));

        Assert.Equal("Transcribing 50%", bar.SummaryText);
    }

    [Fact]
    public void Recording_ShowsTheLeftPart_NotTheSummary()
    {
        _recorder.Elapsed = TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(48);
        var bar = Create();

        _feed.Set(Rec());

        Assert.True(bar.IsRecording);
        Assert.Equal("12:48", bar.ElapsedText);
        Assert.True(bar.IsUpToDate);
        Assert.Equal("Recording", bar.RecordingText);
    }

    [Fact]
    public void Recording_TheTimerFollowsTheRecorder_OnRefresh_AndExcludesPauses()
    {
        var bar = Create();
        _feed.Set(Rec());
        Assert.Equal("00:00", bar.ElapsedText);

        _recorder.Elapsed = TimeSpan.FromSeconds(75);
        bar.Refresh();
        Assert.Equal("01:15", bar.ElapsedText);

        // While paused the recorder's Elapsed stands still, so the bar does too.
        bar.Refresh();
        Assert.Equal("01:15", bar.ElapsedText);

        _recorder.Elapsed = TimeSpan.FromHours(1) + TimeSpan.FromSeconds(5);
        bar.Refresh();
        Assert.Equal("1:00:05", bar.ElapsedText);
    }

    [Fact]
    public void Refresh_WithoutARecording_ReadsNothing()
    {
        var bar = Create();
        _recorder.Elapsed = TimeSpan.FromMinutes(5);

        bar.Refresh();

        Assert.Equal("00:00", bar.ElapsedText);
    }

    [Fact]
    public void Recording_ClearsWhenTheItemGoes()
    {
        var bar = Create();
        _feed.Set(Rec());
        Assert.True(bar.IsRecording);

        _feed.Set();

        Assert.False(bar.IsRecording);
        Assert.True(bar.Activity.IsEmpty);
    }

    [Fact]
    public void PopoverRows_FollowTheFeed_WithKindTitleProgressAndQueued()
    {
        var bar = Create();

        _feed.Set(Rec(), Asr("1", 0.64), Model(0.42), new ActivityItem("idx:1", ActivityKind.Indexing, "Weekly team sync", null, true));

        var rows = bar.Activity.Rows;
        Assert.Equal(4, rows.Count);
        Assert.False(bar.Activity.IsEmpty);

        Assert.Equal(("Recording", " · Untitled recording", true), (rows[0].KindText, rows[0].TitleSuffix, rows[0].IsRecording));
        Assert.Equal(("Transcribing", " · Weekly team sync", true, "64%", 64.0), (rows[1].KindText, rows[1].TitleSuffix, rows[1].HasProgress, rows[1].PercentText, rows[1].Percent));
        Assert.True(rows[1].ShowTranscriptIcon);
        Assert.Equal(("Downloading", "42%", true), (rows[2].KindText, rows[2].PercentText, rows[2].ShowDownloadIcon));
        Assert.Equal(("Indexing", true, false, true, "Queued"), (rows[3].KindText, rows[3].IsQueued, rows[3].HasProgress, rows[3].ShowClockIcon, rows[3].QueuedText));
    }

    [Fact]
    public void PopoverRows_AreUpdatedInPlace_ByKey()
    {
        var bar = Create();
        _feed.Set(Asr("1", 0.1), Model(0.2));
        var first = bar.Activity.Rows[0];
        var second = bar.Activity.Rows[1];
        var changes = 0;
        bar.Activity.Rows.CollectionChanged += (_, _) => changes++;

        _feed.Set(Asr("1", 0.5), Model(0.9));

        Assert.Same(first, bar.Activity.Rows[0]);
        Assert.Same(second, bar.Activity.Rows[1]);
        Assert.Equal("50%", first.PercentText);
        Assert.Equal("90%", second.PercentText);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void PopoverRows_KeepTheirObjects_WhenOneAboveFinishes()
    {
        var bar = Create();
        _feed.Set(Asr("1", 0.1), Model(0.2));
        var model = bar.Activity.Rows[1];

        _feed.Set(Model(0.3));

        Assert.Same(model, Assert.Single(bar.Activity.Rows));
        Assert.Equal("30%", model.PercentText);
    }

    [Fact]
    public void AQueuedRow_BecomesRunning_InPlace()
    {
        var bar = Create();
        _feed.Set(Asr("1", null, queued: true));
        var row = bar.Activity.Rows[0];
        Assert.True(row.IsQueued);
        Assert.True(row.ShowClockIcon);

        _feed.Set(Asr("1", 0.3));

        Assert.Same(row, bar.Activity.Rows[0]);
        Assert.False(row.IsQueued);
        Assert.True(row.HasProgress);
        Assert.True(row.ShowTranscriptIcon);
        Assert.False(row.ShowClockIcon);
    }

    [Fact]
    public void ARowWithoutATitle_ShowsTheKindOnly()
    {
        var bar = Create();

        _feed.Set(Asr("1", 0.2, title: string.Empty));

        Assert.Equal(string.Empty, bar.Activity.Rows[0].TitleSuffix);
    }

    [Fact]
    public void TheRecordingRow_ShowsTheElapsedTime()
    {
        var bar = Create();
        _feed.Set(Rec(), Model(0.5));

        _recorder.Elapsed = TimeSpan.FromSeconds(90);
        bar.Refresh();

        Assert.Equal("01:30", bar.Activity.Rows[0].ElapsedText);
        Assert.Equal(string.Empty, bar.Activity.Rows[1].ElapsedText);
    }

    [Fact]
    public void Texts_AreUkrainianInUkrainian()
    {
        using var _ = UiCulture.Override("uk");
        var bar = Create();
        Assert.Equal("Усе актуально", bar.SummaryText);
        Assert.Equal("Нічого не виконується.", bar.Activity.EmptyText);
        Assert.Equal("Усе локально", bar.AllLocalText);

        _feed.Set(Asr("1", 0.64), Model(0.42));

        Assert.Equal("Транскрибується 64% · Завантажується 42%", bar.SummaryText);
    }

    [Fact]
    public void ShowRecording_RaisesTheRequest()
    {
        var bar = Create();
        var requested = 0;
        bar.RecordingRequested += () => requested++;

        bar.ShowRecordingCommand.Execute(null);

        Assert.Equal(1, requested);
    }

    [Fact]
    public void Dispose_StopsFollowingTheFeed()
    {
        var bar = Create();

        bar.Dispose();
        bar.Dispose();
        _feed.Set(Model(0.5));

        Assert.True(bar.IsUpToDate);
        Assert.False(_feed.HasSubscribers);
    }

    [Fact]
    public void FeedEvents_AreMarshalledToTheUiThread()
    {
        var posted = new List<Action>();
        var bar = new StatusBarViewModel(_feed, _recorder, posted.Add);

        _feed.Set(Model(0.5));

        Assert.True(bar.IsUpToDate); // nothing applied until the UI thread runs the posted action
        var action = Assert.Single(posted);
        action();
        Assert.Equal("Downloading 50%", bar.SummaryText);
    }

    [Fact]
    public void TheBar_HasNothingToConfigure()
    {
        // Facts only: the view models expose no setting, budget, toggle or cancel command.
        var commands = typeof(StatusBarViewModel).GetProperties().Where(p => p.PropertyType.Name.Contains("Command", StringComparison.Ordinal)).Select(p => p.Name);
        Assert.Equal(["ShowRecordingCommand"], commands);
        Assert.DoesNotContain(typeof(ActivityPopoverViewModel).GetProperties(), p => p.PropertyType.Name.Contains("Command", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(ActivityRowViewModel).GetProperties(), p => p.PropertyType.Name.Contains("Command", StringComparison.Ordinal));
    }

    [Fact]
    public void TheShell_HasAStatusBar_OnlyWithAFeed()
    {
        var time = new TestTime(UiHarness.Now);
        var without = ShellViewModel.Create(new AppServices(_recorder, new FakeDisplayCapture(), new FakeSessionStore(), time));
        var with = ShellViewModel.Create(new AppServices(_recorder, new FakeDisplayCapture(), new FakeSessionStore(), time, Activity: _feed));

        Assert.Null(without.StatusBar);
        Assert.NotNull(with.StatusBar);
        Assert.True(_feed.HasSubscribers);

        with.OnWindowClosed();
        Assert.False(_feed.HasSubscribers);
    }

    [Fact]
    public void RecordingRequested_OpensTheRecordingScreen()
    {
        var shell = ShellViewModel.Create(new AppServices(_recorder, new FakeDisplayCapture(), new FakeSessionStore(), new TestTime(UiHarness.Now), Settings: new FakeSettingsStore(new Murmur.Core.Settings.AppSettings { OnboardingCompleted = true }), Activity: _feed));
        Assert.Same(shell.Library, shell.CurrentPage);

        shell.StatusBar!.ShowRecordingCommand.Execute(null);

        Assert.Same(shell.Recording, shell.CurrentPage);
    }
}
