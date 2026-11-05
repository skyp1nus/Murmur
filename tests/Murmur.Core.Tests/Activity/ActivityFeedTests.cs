using Murmur.Core.Activity;
using Murmur.Core.Jobs;
using Murmur.Core.Models;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Activity;

public sealed class ActivityFeedTests : IAsyncDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TempDirectory _dir = new();
    private readonly InMemorySessionStore _sessions;
    private readonly FakeRecorder _recorder = new();
    private readonly FakeModels _models = new();
    private readonly List<ActivityFeed> _feeds = [];

    public ActivityFeedTests()
    {
        _sessions = new InMemorySessionStore(_dir.File("sessions"));
    }

    public ValueTask DisposeAsync()
    {
        foreach (var feed in _feeds)
        {
            feed.Dispose();
        }

        _dir.Dispose();
        return ValueTask.CompletedTask;
    }

    private ActivityFeed Create(bool withSessions = true, bool withModels = true, SessionProcessor? processor = null)
    {
        var feed = new ActivityFeed(_recorder, withSessions ? _sessions : null, processor, withModels ? _models : null, Interval);
        _feeds.Add(feed);
        return feed;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the feed.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private SessionRecord Seed(string title)
    {
        var record = new SessionRecord { Id = SessionId.New(), Title = title, StartedAt = DateTimeOffset.UtcNow, State = SessionState.Recorded };
        _sessions.Seed(record);
        return record;
    }

    [Fact]
    public void Idle_HasNoItems()
    {
        var feed = Create();

        Assert.Empty(feed.Current);
    }

    [Fact]
    public async Task Recording_AppearsWhileRecordingOrPaused_AndDisappearsAfterwards()
    {
        var feed = Create();
        var session = Seed("Weekly sync");

        _recorder.BeginRecording(session);
        await WaitUntilAsync(() => feed.Current.Count == 1);
        var item = Assert.Single(feed.Current);
        Assert.Equal(new ActivityItem("rec", ActivityKind.Recording, "Weekly sync", null, false), item);

        _recorder.Pause();
        await Task.Delay(Interval * 4, TestContext.Current.CancellationToken);
        Assert.Equal(item, Assert.Single(feed.Current));

        _recorder.Resume();
        _recorder.EndRecording();
        await WaitUntilAsync(() => feed.Current.Count == 0);
    }

    [Fact]
    public async Task Recording_AlreadyRunningWhenTheFeedIsBuilt_IsThere()
    {
        _recorder.BeginRecording(Seed("Already"));

        var feed = Create();

        Assert.Equal("Already", Assert.Single(feed.Current).Title);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Transcribing_ShowsProgress_ThenGoesWhenCompleted()
    {
        var feed = Create();
        var session = Seed("Standup");

        feed.OnProcessing(new SessionProcessingStatus(session.Id, SessionProcessingStage.Transcribing, 0.64));
        await WaitUntilAsync(() => feed.Current.Count == 1);
        await WaitUntilAsync(() => feed.Current[0].Title == "Standup");
        var item = Assert.Single(feed.Current);
        Assert.Equal(ActivityKind.Transcribing, item.Kind);
        Assert.Equal("asr:" + session.Id, item.Key);
        Assert.Equal(0.64, item.Progress);
        Assert.False(item.Queued);

        feed.OnProcessing(new SessionProcessingStatus(session.Id, SessionProcessingStage.Transcribing, 0.9));
        await WaitUntilAsync(() => feed.Current[0].Progress == 0.9);

        feed.OnProcessing(new SessionProcessingStatus(session.Id, SessionProcessingStage.Completed, 1));
        await WaitUntilAsync(() => feed.Current.Count == 0);
    }

    [Fact]
    public async Task QueuedSession_IsQueuedWithoutProgress_AndRunningComesFirst()
    {
        var feed = Create();
        var waiting = Seed("Waiting");
        var running = Seed("Running");

        feed.OnProcessing(new SessionProcessingStatus(waiting.Id, SessionProcessingStage.Queued, 0));
        feed.OnProcessing(new SessionProcessingStatus(running.Id, SessionProcessingStage.Transcribing, 0.2));
        await WaitUntilAsync(() => feed.Current.Count == 2);

        Assert.Equal(("Running", false), (feed.Current[0].Title, feed.Current[0].Queued));
        Assert.Equal(new ActivityItem("asr:" + waiting.Id, ActivityKind.Transcribing, feed.Current[1].Title, null, true), feed.Current[1]);
    }

    [Theory]
    [InlineData(SessionProcessingStage.Failed)]
    [InlineData(SessionProcessingStage.ModelMissing)]
    [InlineData(SessionProcessingStage.Completed)]
    public async Task FinishedStages_AreNotActivity(SessionProcessingStage stage)
    {
        var feed = Create();
        var session = Seed("Done");
        feed.OnProcessing(new SessionProcessingStatus(session.Id, SessionProcessingStage.Transcribing, 0.5));
        await WaitUntilAsync(() => feed.Current.Count == 1);

        feed.OnProcessing(new SessionProcessingStatus(session.Id, stage, 0));

        await WaitUntilAsync(() => feed.Current.Count == 0);
    }

    [Fact]
    public async Task Indexing_IsAnItemWithoutProgress()
    {
        var feed = Create();
        var session = Seed("Indexed");

        feed.OnProcessing(new SessionProcessingStatus(session.Id, SessionProcessingStage.Indexing, 1));
        await WaitUntilAsync(() => feed.Current.Count == 1);

        var item = feed.Current[0];
        Assert.Equal(("idx:" + session.Id, ActivityKind.Indexing, null, false), (item.Key, item.Kind, item.Progress, item.Queued));
    }

    [Fact]
    public async Task Title_IsLoadedFromTheStore_WithoutOneTheRowHasNone()
    {
        var feed = Create(withSessions: false);
        var session = Seed("Hidden");

        feed.OnProcessing(new SessionProcessingStatus(session.Id, SessionProcessingStage.Transcribing, 0.1));
        await WaitUntilAsync(() => feed.Current.Count == 1);

        Assert.Equal(string.Empty, feed.Current[0].Title);
    }

    [Fact]
    public async Task ModelDownload_ShowsBytesAsProgress_ThenGoes()
    {
        var feed = Create();
        var model = new ModelInfo("gemma", ModelKind.Chat, "Gemma 4 26B", 1000, false);

        _models.Raise(new ModelStatus(model, ModelInstallState.Downloading, 420, 1000, null));
        await WaitUntilAsync(() => feed.Current.Count == 1);
        Assert.Equal(new ActivityItem("model:gemma", ActivityKind.DownloadingModel, "Gemma 4 26B", 0.42, false), feed.Current[0]);

        _models.Raise(new ModelStatus(model, ModelInstallState.Downloading, 900, 1000, null));
        await WaitUntilAsync(() => feed.Current[0].Progress == 0.9);

        _models.Raise(new ModelStatus(model, ModelInstallState.Installed, 1000, 1000, null));
        await WaitUntilAsync(() => feed.Current.Count == 0);
    }

    [Fact]
    public async Task ModelDownload_WithUnknownTotal_IsIndeterminate()
    {
        var feed = Create();
        var model = new ModelInfo("m", ModelKind.Asr, "Parakeet", 1000, true);

        _models.Raise(new ModelStatus(model, ModelInstallState.Downloading, 10, null, null));
        await WaitUntilAsync(() => feed.Current.Count == 1);

        Assert.Null(feed.Current[0].Progress);
    }

    [Fact]
    public void ModelDownload_AlreadyRunningWhenTheFeedIsBuilt_IsThere()
    {
        var model = new ModelInfo("gemma", ModelKind.Chat, "Gemma", 1000, false);
        _models.Catalog = [model];
        _models.Statuses["gemma"] = new ModelStatus(model, ModelInstallState.Downloading, 100, 1000, null);

        var feed = Create();

        Assert.Equal(0.1, Assert.Single(feed.Current).Progress);
    }

    [Fact]
    public async Task EverythingAtOnce_RecordingFirst_QueuedLast()
    {
        var feed = Create();
        var a = Seed("A");
        _recorder.BeginRecording(Seed("Live"));
        feed.OnProcessing(new SessionProcessingStatus(a.Id, SessionProcessingStage.Queued, 0));
        _models.Raise(new ModelStatus(new ModelInfo("m", ModelKind.Chat, "Gemma", 10, false), ModelInstallState.Downloading, 5, 10, null));
        await WaitUntilAsync(() => feed.Current.Count == 3);

        Assert.Equal([ActivityKind.Recording, ActivityKind.DownloadingModel, ActivityKind.Transcribing], feed.Current.Select(i => i.Kind));
    }

    [Fact]
    public async Task ManyEvents_RaiseChangedAtMostOncePerInterval()
    {
        var feed = Create();
        var model = new ModelInfo("m", ModelKind.Chat, "Gemma", 100_000, false);
        var raised = 0;
        feed.Changed += () => Interlocked.Increment(ref raised);

        var started = DateTime.UtcNow;
        for (var i = 1; i <= 5_000; i++)
        {
            _models.Raise(new ModelStatus(model, ModelInstallState.Downloading, i, 100_000, null));
        }

        var elapsed = DateTime.UtcNow - started;
        await Task.Delay(Interval * 6, TestContext.Current.CancellationToken);

        var budget = (int)(elapsed / Interval) + 2;
        Assert.InRange(Volatile.Read(ref raised), 1, budget);
        Assert.Equal(1.0 * 5_000 / 100_000, feed.Current[0].Progress);
    }

    [Fact]
    public async Task UnchangedSnapshot_RaisesNothing()
    {
        var feed = Create();
        var model = new ModelInfo("m", ModelKind.Chat, "Gemma", 100, false);
        _models.Raise(new ModelStatus(model, ModelInstallState.Downloading, 5, 100, null));
        await WaitUntilAsync(() => feed.Current.Count == 1);
        var raised = 0;
        feed.Changed += () => Interlocked.Increment(ref raised);

        _models.Raise(new ModelStatus(model, ModelInstallState.Downloading, 5, 100, null));
        await Task.Delay(Interval * 5, TestContext.Current.CancellationToken);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Events_AreAllocationFreeInSteadyState()
    {
        var feed = Create();
        var model = new ModelInfo("m", ModelKind.Chat, "Gemma", 100_000, false);
        var status = new ModelStatus(model, ModelInstallState.Downloading, 5, 100_000, null);
        var processing = new SessionProcessingStatus(SessionId.New(), SessionProcessingStage.Transcribing, 0.3);
        feed.OnModelStatus(status);
        feed.OnProcessing(processing);

        var allocated = Allocations.SteadyState(() =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                feed.OnModelStatus(status);
                feed.OnProcessing(processing);
            }
        });

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void RecorderStateChanges_AreAllocationFreeInSteadyState()
    {
        var feed = Create();
        var session = Seed("Live");
        _recorder.BeginRecording(session);

        var allocated = Allocations.SteadyState(() =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                _recorder.Pause();
                _recorder.Resume();
            }
        });

        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task Dispose_UnsubscribesAndStopsRaising()
    {
        var feed = Create();
        var raised = 0;
        feed.Changed += () => Interlocked.Increment(ref raised);

        feed.Dispose();
        feed.Dispose();
        _recorder.BeginRecording(Seed("After"));
        await Task.Delay(Interval * 4, TestContext.Current.CancellationToken);

        Assert.Equal(0, raised);
        Assert.Empty(feed.Current);
    }

    [Fact]
    public async Task RealProcessor_TranscribingShowsUpAndGoesAway()
    {
        var asr = new FakeAsr { Gate = new SemaphoreSlim(0) };
        asr.Script("mic.wav", (0.5, 1.5, "hello"));
        await using var queue = new JobQueue();
        using var processor = new SessionProcessor(_recorder, _sessions, new InMemoryTranscriptStore(), asr, queue);
        var feed = Create(processor: processor);

        var created = await _sessions.CreateSessionAsync("Real meeting", DateTimeOffset.UtcNow, CancellationToken.None);
        TestWav.Square(_sessions.GetFiles(created.Id).MicrophoneAudio, TimeSpan.FromSeconds(2), 0.5f);
        var session = created with { State = SessionState.Recording, HasMicrophone = true, Duration = TimeSpan.FromSeconds(2) };
        _sessions.Seed(session);
        _recorder.BeginRecording(session);
        _sessions.Seed(session with { State = SessionState.Recorded });
        _recorder.EndRecording();

        await WaitUntilAsync(() => feed.Current.Any(i => i.Kind == ActivityKind.Transcribing && i.Title == "Real meeting"));

        asr.Gate.Release(10);
        await WaitUntilAsync(() => feed.Current.Count == 0);
    }

    [Fact]
    public async Task RealProcessor_ASessionDeletedWhileQueued_LosesItsRow()
    {
        var asr = new FakeAsr { Gate = new SemaphoreSlim(0) };
        asr.Script("mic.wav", (0.5, 1.5, "hello"));
        await using var queue = new JobQueue();
        using var processor = new SessionProcessor(_recorder, _sessions, new InMemoryTranscriptStore(), asr, queue);
        var feed = Create(processor: processor);

        var running = await RecordedSessionAsync("Running one");
        var waiting = await RecordedSessionAsync("Waiting one");
        Assert.True(await processor.EnqueueAsync(running.Id, TestContext.Current.CancellationToken));
        await WaitUntilAsync(() => feed.Current.Any(i => i.Title == "Running one"));
        Assert.True(await processor.EnqueueAsync(waiting.Id, TestContext.Current.CancellationToken));
        await WaitUntilAsync(() => feed.Current.Any(i => i.Key == "asr:" + waiting.Id && i.Queued));

        await _sessions.DeleteSessionAsync(waiting.Id, CancellationToken.None);
        asr.Gate.Release(10);

        await WaitUntilAsync(() => feed.Current.Count == 0);
    }

    [Fact]
    public async Task StatusCleared_DropsTheRow()
    {
        var feed = Create();
        var session = Seed("Gone");
        feed.OnProcessing(new SessionProcessingStatus(session.Id, SessionProcessingStage.Queued, 0));
        await WaitUntilAsync(() => feed.Current.Count == 1);

        feed.OnStatusCleared(session.Id);

        await WaitUntilAsync(() => feed.Current.Count == 0);
    }

    [Fact]
    public async Task ARecreatedRow_ReadsTheTitleAgain_SoARenameShows()
    {
        var feed = Create();
        var session = Seed("Before");
        feed.OnProcessing(new SessionProcessingStatus(session.Id, SessionProcessingStage.Transcribing, 0.1));
        await WaitUntilAsync(() => feed.Current.Count == 1 && feed.Current[0].Title == "Before");
        feed.OnProcessing(new SessionProcessingStatus(session.Id, SessionProcessingStage.Completed, 1));
        await WaitUntilAsync(() => feed.Current.Count == 0);

        _sessions.Seed(session with { Title = "After" });
        feed.OnProcessing(new SessionProcessingStatus(session.Id, SessionProcessingStage.Transcribing, 0.2));

        await WaitUntilAsync(() => feed.Current.Count == 1 && feed.Current[0].Title == "After");
    }

    [Fact]
    public async Task ProgressChangesBelowOnePercent_RaiseNothing()
    {
        var feed = Create();
        var model = new ModelInfo("m", ModelKind.Chat, "Gemma", 100_000, false);
        _models.Raise(new ModelStatus(model, ModelInstallState.Downloading, 5_000, 100_000, null));
        await WaitUntilAsync(() => feed.Current.Count == 1);
        var raised = 0;
        feed.Changed += () => Interlocked.Increment(ref raised);

        _models.Raise(new ModelStatus(model, ModelInstallState.Downloading, 5_100, 100_000, null));
        await Task.Delay(Interval * 5, TestContext.Current.CancellationToken);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void TheDefaultInterval_IsAtMostTwoHertz()
    {
        Assert.True(ActivityFeed.DefaultInterval >= TimeSpan.FromMilliseconds(500));
    }

    private async Task<SessionRecord> RecordedSessionAsync(string title)
    {
        var created = await _sessions.CreateSessionAsync(title, DateTimeOffset.UtcNow, CancellationToken.None);
        TestWav.Square(_sessions.GetFiles(created.Id).MicrophoneAudio, TimeSpan.FromSeconds(2), 0.5f);
        var session = created with { State = SessionState.Recorded, HasMicrophone = true, Duration = TimeSpan.FromSeconds(2) };
        _sessions.Seed(session);
        return session;
    }

    private sealed class FakeModels : IModelManager
    {
        public string ModelsDirectory => "/models";

        public IReadOnlyList<ModelInfo> Catalog { get; set; } = [];

        public string RecommendedChatModelId => string.Empty;

        public long AvailableFreeSpaceBytes => long.MaxValue;

        public Dictionary<string, ModelStatus> Statuses { get; } = [];

        public event Action<ModelStatus>? StatusChanged;

        public ModelStatus GetStatus(string id) => Statuses[id];

        public Task InstallAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveAsync(string id, CancellationToken cancellationToken) => Task.CompletedTask;

        public void Raise(ModelStatus status) => StatusChanged?.Invoke(status);
    }
}
