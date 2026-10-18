using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Capture;
using Murmur.Core.Processing;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.App.Tests;

public sealed class SessionViewModelTests : IDisposable
{
    private readonly SessionFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private async Task<SessionViewModel> LoadShortAsync()
    {
        _fixture.UseShortTranscript();
        var vm = _fixture.Create();
        await vm.LoadAsync();
        return vm;
    }

    // Loading ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Load_ShowsTheTranscript_WithSpeakersAndTimeChips()
    {
        var vm = await LoadShortAsync();

        Assert.Equal(TranscriptStatus.Available, vm.Status);
        Assert.True(vm.ShowTranscriptList);
        Assert.False(vm.ShowStatusCard);
        Assert.Equal(2, vm.Segments.Count);
        Assert.Equal("Я", vm.Segments[0].SpeakerLabel);
        Assert.Equal("Інші", vm.Segments[1].SpeakerLabel);
        Assert.Equal("00:10", vm.Segments[0].TimeText);
        Assert.Equal("00:20", vm.Segments[1].TimeText);
        Assert.Equal("Привіт усім починаємо мітинг", vm.Segments[0].Text);
        Assert.Equal([0, 7, 12, 22], vm.Segments[0].WordStarts);
    }

    [Fact]
    public async Task Load_OpensTheExistingTracksInThePlayer_AndTakesTheDurationFromIt()
    {
        _fixture.Player.Duration = S(125);
        var vm = _fixture.Create();

        await vm.LoadAsync();

        var tracks = Assert.Single(_fixture.Player.Opens);
        Assert.Equal([_fixture.Files.MicrophoneAudio, _fixture.Files.SystemAudio], tracks);
        Assert.True(vm.CanPlay);
        Assert.Equal(S(125), vm.Duration);
        Assert.Equal("00:02:05", vm.DurationText);
        Assert.Null(vm.PlayerError);
    }

    [Fact]
    public async Task Load_ReadsThePeaks()
    {
        _fixture.WritePeaks([1, 50, 255, 7]);
        var vm = _fixture.Create();

        await vm.LoadAsync();

        Assert.Equal([1, 50, 255, 7], vm.Peaks);
    }

    [Fact]
    public async Task Load_WithABrokenPeaksFile_KeepsAnEmptyWaveform()
    {
        File.WriteAllBytes(_fixture.Files.Peaks, [1, 2, 3]);
        var vm = _fixture.Create();

        await vm.LoadAsync();

        Assert.Empty(vm.Peaks);
    }

    [Fact]
    public async Task Load_WithoutAudioFiles_DisablesPlayback()
    {
        using var empty = new SessionFixture(withAudio: false);
        var vm = empty.Create();

        await vm.LoadAsync();

        Assert.False(vm.CanPlay);
        Assert.True(vm.HasPlayerError);
        Assert.Empty(empty.Player.Opens);
        vm.PlayPauseCommand.Execute(null);
        Assert.Equal(0, empty.Player.Plays);
    }

    [Fact]
    public async Task Load_WhenThePlayerCannotOpenTheFiles_ShowsTheReason()
    {
        _fixture.Player.OpenFailure = new InvalidDataException("not a WAV");
        var vm = _fixture.Create();

        await vm.LoadAsync();

        Assert.False(vm.CanPlay);
        Assert.Contains("not a WAV", vm.PlayerError);
    }

    // Current word ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Tick_HighlightsTheWordAtThePlayerPosition_AndMovesItAcrossSegments()
    {
        var vm = await LoadShortAsync();

        _fixture.Player.Position = S(10.5);
        vm.Tick();
        Assert.Equal(1, vm.CurrentWordIndex);
        Assert.Equal(1, vm.Segments[0].ActiveWord);
        Assert.Equal(-1, vm.Segments[1].ActiveWord);
        Assert.Equal(0, vm.CurrentSegmentIndex);

        _fixture.Player.Position = S(20.0);
        vm.Tick();
        Assert.Equal(4, vm.CurrentWordIndex);
        Assert.Equal(-1, vm.Segments[0].ActiveWord);
        Assert.Equal(0, vm.Segments[1].ActiveWord);
        Assert.Equal(1, vm.CurrentSegmentIndex);
    }

    [Fact]
    public async Task Tick_BeforeTheFirstWordAndInALongPause_HighlightsNothing()
    {
        var vm = await LoadShortAsync();

        _fixture.Player.Position = S(2);
        vm.Tick();
        Assert.Equal(-1, vm.CurrentWordIndex);
        Assert.Equal(-1, vm.CurrentSegmentIndex);

        _fixture.Player.Position = S(10.0);
        vm.Tick();
        Assert.Equal(0, vm.CurrentWordIndex);

        _fixture.Player.Position = S(16);
        vm.Tick();
        Assert.Equal(-1, vm.CurrentWordIndex);
        Assert.All(vm.Segments, segment => Assert.Equal(-1, segment.ActiveWord));
        Assert.Equal(0, vm.CurrentSegmentIndex);
    }

    [Fact]
    public async Task Tick_UpdatesTheClockTexts()
    {
        var vm = await LoadShortAsync();

        _fixture.Player.Position = S(75);
        vm.Tick();

        Assert.Equal("00:01:15", vm.PositionText);
        Assert.Equal(75.0 / 3600, vm.PositionFraction, 6);
    }

    [Fact]
    public async Task OnlyTheOldAndTheNewRow_AreNotifiedWhenTheHighlightMoves()
    {
        _fixture.UseTranscript(TranscriptFactory.Make(_fixture.Record.Id, 1_200));
        var vm = _fixture.Create();
        await vm.LoadAsync();
        var notified = new HashSet<int>();
        foreach (var row in vm.Segments)
        {
            var index = row.Index;
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SegmentViewModel.ActiveWord))
                {
                    notified.Add(index);
                }
            };
        }

        _fixture.Player.Position = vm.Segments[10].Start;
        vm.Tick();
        notified.Clear();
        _fixture.Player.Position = vm.Segments[11].Start;
        vm.Tick();

        Assert.Equal([10, 11], notified.Order());
    }

    // Seeking ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ClickingAWord_SeeksToItsStart_AndPlays()
    {
        var vm = await LoadShortAsync();

        vm.Segments[1].SeekWordCommand.Execute(2);

        Assert.Equal([S(21.0)], _fixture.Player.Seeks);
        Assert.Equal(1, _fixture.Player.Plays);
        Assert.True(vm.IsPlaying);
        Assert.Equal(S(21.0), vm.Position);
        Assert.Equal(6, vm.CurrentWordIndex);
    }

    [Fact]
    public async Task ClickingTheTimeChip_SeeksToTheSegmentStart_AndPlays()
    {
        var vm = await LoadShortAsync();

        vm.Segments[0].SeekCommand.Execute(null);

        Assert.Equal([S(10)], _fixture.Player.Seeks);
        Assert.True(vm.IsPlaying);
    }

    [Fact]
    public async Task ClickingASegmentWithoutWordTimings_SeeksToTheSegmentStart()
    {
        _fixture.UseTranscript(new Transcript(_fixture.Record.Id, [new TranscriptSegment(Speaker.Me, S(42), S(50), "Текст без таймінгів", [])], "fake"));
        var vm = _fixture.Create();
        await vm.LoadAsync();

        Assert.Equal("Текст без таймінгів", vm.Segments[0].Text);
        Assert.Empty(vm.Segments[0].WordStarts);
        vm.Segments[0].SeekWordCommand.Execute(0);

        Assert.Equal([S(42)], _fixture.Player.Seeks);
    }

    [Fact]
    public async Task Seek_IsClampedToTheSessionLength()
    {
        var vm = await LoadShortAsync();

        vm.SeekTo(S(-5));
        vm.SeekTo(S(99_999));
        vm.SeekToFraction(0.5);

        Assert.Equal([TimeSpan.Zero, TimeSpan.FromHours(1), TimeSpan.FromMinutes(30)], _fixture.Player.Seeks);
    }

    [Fact]
    public async Task Scrubbing_MovesThePlayheadButSeeksOnlyWhenTheDragEnds()
    {
        var vm = await LoadShortAsync();

        vm.BeginScrub();
        vm.ScrubTo(0.25);
        vm.ScrubTo(0.5);
        _fixture.Player.Position = S(1);
        vm.Tick();

        Assert.Empty(_fixture.Player.Seeks);
        Assert.Equal(0.5, vm.PositionFraction, 6);

        vm.EndScrub(0.5);

        Assert.Equal([TimeSpan.FromMinutes(30)], _fixture.Player.Seeks);
    }

    // Play / pause ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlayPause_TogglesThePlayer_AndTheLabel()
    {
        var vm = await LoadShortAsync();
        Assert.Equal("Відтворити", vm.PlayPauseLabel);

        vm.PlayPauseCommand.Execute(null);
        Assert.True(vm.IsPlaying);
        Assert.Equal("Пауза", vm.PlayPauseLabel);

        vm.PlayPauseCommand.Execute(null);
        Assert.False(vm.IsPlaying);
        Assert.Equal((1, 1), (_fixture.Player.Plays, _fixture.Player.Pauses));
    }

    [Fact]
    public async Task WhenThePlayerEnds_PlaybackStopsAtTheEnd()
    {
        var vm = await LoadShortAsync();
        vm.PlayPauseCommand.Execute(null);

        _fixture.Player.End();

        Assert.False(vm.IsPlaying);
        Assert.Equal(vm.Duration, vm.Position);
        Assert.Equal(1.0, vm.PositionFraction);
    }

    [Fact]
    public async Task PlayerEvents_AreMarshalledThroughThePostCallback()
    {
        var posted = new List<Action>();
        _fixture.UseShortTranscript();
        var vm = new SessionViewModel(_fixture.Record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, posted.Add);
        await vm.LoadAsync();

        _fixture.Player.Play();

        Assert.False(vm.IsPlaying);
        Assert.Single(posted);
        posted[0]();
        Assert.True(vm.IsPlaying);
    }

    // Auto-scroll -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AutoScroll_FollowsTheCurrentSegment()
    {
        var vm = await LoadShortAsync();
        var requested = new List<int>();
        vm.ScrollToSegmentRequested += requested.Add;

        _fixture.Player.Position = S(10);
        vm.Tick();
        _fixture.Player.Position = S(10.5);
        vm.Tick();
        _fixture.Player.Position = S(20);
        vm.Tick();

        Assert.Equal([0, 1], requested);
    }

    [Fact]
    public async Task AutoScroll_IsSuspendedForThreeSecondsAfterTheUserScrolled()
    {
        var vm = await LoadShortAsync();
        var requested = new List<int>();
        vm.ScrollToSegmentRequested += requested.Add;
        _fixture.Player.Position = S(10);
        vm.Tick();
        requested.Clear();

        vm.NotifyUserScrolled();
        Assert.True(vm.AutoScrollSuspended);
        _fixture.Time.Advance(S(2.9));
        _fixture.Player.Position = S(20);
        vm.Tick();
        Assert.Empty(requested);
        Assert.True(vm.AutoScrollSuspended);

        _fixture.Time.Advance(S(0.2));
        Assert.False(vm.AutoScrollSuspended);
    }

    [Fact]
    public async Task AutoScroll_DeliversTheSuppressedRequest_OnTheFirstTickAfterTheHold()
    {
        _fixture.UseTranscript(TranscriptFactory.Make(_fixture.Record.Id, 120));
        var vm = _fixture.Create();
        await vm.LoadAsync();
        var requested = new List<int>();
        vm.ScrollToSegmentRequested += requested.Add;
        _fixture.Player.Position = vm.Segments[1].Start;
        vm.Tick();
        requested.Clear();

        vm.NotifyUserScrolled();
        _fixture.Player.Position = vm.Segments[2].Start;
        vm.Tick();
        Assert.Empty(requested);

        _fixture.Time.Advance(S(3));
        vm.Tick();
        Assert.Equal([2], requested);

        // Delivered once: further ticks within the same segment stay quiet.
        vm.Tick();
        Assert.Equal([2], requested);
    }

    [Fact]
    public async Task AutoScroll_AfterTheHold_ScrollsToTheLatestSegment_WhenSeveralWereSkipped()
    {
        _fixture.UseTranscript(TranscriptFactory.Make(_fixture.Record.Id, 120));
        var vm = _fixture.Create();
        await vm.LoadAsync();
        var requested = new List<int>();
        vm.ScrollToSegmentRequested += requested.Add;
        _fixture.Player.Position = vm.Segments[1].Start;
        vm.Tick();
        requested.Clear();

        vm.NotifyUserScrolled();
        _fixture.Player.Position = vm.Segments[2].Start;
        vm.Tick();
        _fixture.Player.Position = vm.Segments[3].Start;
        vm.Tick();
        _fixture.Time.Advance(S(3));
        vm.Tick();

        Assert.Equal([3], requested);
    }

    [Fact]
    public async Task ASeekByTheUser_ClearsTheScrollHold_AndBringsTheSegmentIntoView()
    {
        var vm = await LoadShortAsync();
        var requested = new List<int>();
        vm.ScrollToSegmentRequested += requested.Add;
        vm.NotifyUserScrolled();

        vm.Segments[1].SeekWordCommand.Execute(0);

        Assert.False(vm.AutoScrollSuspended);
        Assert.Equal([1], requested);
    }

    // Tabs ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Tabs_SwitchBetweenTranscriptAndThePhase3Placeholders()
    {
        var vm = await LoadShortAsync();
        Assert.True(vm.IsTranscriptTab);
        Assert.True(vm.ShowTranscriptList);

        vm.IsSummaryTab = true;
        Assert.True(vm.ShowSummary);
        Assert.False(vm.ShowTranscriptList);
        Assert.False(vm.ShowStatusCard);
        Assert.False(vm.IsTranscriptTab);

        vm.IsAskTab = true;
        Assert.True(vm.ShowAsk);
        Assert.False(vm.ShowSummary);

        vm.IsAskTab = false; // a toggle un-checking itself must not leave no tab selected
        Assert.Equal(SessionTab.Ask, vm.SelectedTab);

        vm.IsTranscriptTab = true;
        Assert.True(vm.ShowTranscriptList);
    }

    // Status states ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task NoTranscript_AndTheAsrIsNotInstalled_ExplainsHowToInstallParakeet()
    {
        _fixture.Processing.IsAsrInstalled = false;
        var vm = _fixture.Create();

        await vm.LoadAsync();

        Assert.Equal(TranscriptStatus.AsrMissing, vm.Status);
        Assert.True(vm.ShowStatusCard);
        Assert.Equal("Модель Parakeet не встановлена", vm.StatusTitle);
        Assert.Contains("scripts/fetch-models.ps1", vm.StatusText);
        Assert.Contains("/models", vm.StatusText);
        Assert.True(vm.CanRetry);
    }

    [Fact]
    public async Task NoTranscript_WhileTheJobRuns_ShowsTheProgress()
    {
        var fixture = new SessionFixture(SessionState.Transcribing);
        fixture.Processing.Set(new SessionProcessingStatus(fixture.Record.Id, SessionProcessingStage.Transcribing, 0.425));
        var vm = fixture.Create();

        await vm.LoadAsync();

        Assert.Equal(TranscriptStatus.Processing, vm.Status);
        Assert.True(vm.ShowProgress);
        Assert.Equal(42, vm.ProgressValue);
        Assert.Equal("42 %", vm.ProgressText);
        Assert.Equal("Транскрибуємо мовлення", vm.StatusTitle);
        fixture.Dispose();
    }

    [Fact]
    public async Task Progress_FollowsThePipelineEvents()
    {
        _fixture.Processing.Set(new SessionProcessingStatus(_fixture.Record.Id, SessionProcessingStage.Queued, 0));
        var vm = _fixture.Create();
        await vm.LoadAsync();
        Assert.Equal(TranscriptStatus.Processing, vm.Status);

        _fixture.Processing.Raise(new SessionProcessingStatus(_fixture.Record.Id, SessionProcessingStage.Transcribing, 0.07));
        Assert.Equal("7 %", vm.ProgressText);
        _fixture.Processing.Raise(new SessionProcessingStatus(_fixture.Record.Id, SessionProcessingStage.Transcribing, 0.9));
        Assert.Equal("90 %", vm.ProgressText);
        Assert.Equal(90, vm.ProgressValue);
    }

    [Fact]
    public async Task EventsOfOtherSessions_AreIgnored()
    {
        var vm = _fixture.Create();
        await vm.LoadAsync();
        var before = vm.Status;

        _fixture.Processing.Raise(new SessionProcessingStatus(SessionId.New(), SessionProcessingStage.Failed, 0, "boom"));

        Assert.Equal(before, vm.Status);
    }

    [Fact]
    public async Task WhenTheJobCompletes_TheTranscriptAppearsWithoutReopening()
    {
        _fixture.Processing.Set(new SessionProcessingStatus(_fixture.Record.Id, SessionProcessingStage.Transcribing, 0.5));
        var vm = _fixture.Create();
        await vm.LoadAsync();
        Assert.Equal(TranscriptStatus.Processing, vm.Status);

        _fixture.UseShortTranscript();
        _fixture.WritePeaks([9, 9]);
        _fixture.Processing.Raise(new SessionProcessingStatus(_fixture.Record.Id, SessionProcessingStage.Completed, 1));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(TranscriptStatus.Available, vm.Status);
        Assert.Equal(2, vm.Segments.Count);
        Assert.Equal([9, 9], vm.Peaks);
    }

    [Fact]
    public async Task Failure_ShowsTheError_AndRetryRequeuesTheSession()
    {
        var fixture = new SessionFixture(SessionState.Failed);
        fixture.Processing.Set(new SessionProcessingStatus(fixture.Record.Id, SessionProcessingStage.Failed, 0, "Воркер аварійно завершився"));
        var vm = fixture.Create();
        await vm.LoadAsync();

        Assert.Equal(TranscriptStatus.Failed, vm.Status);
        Assert.Equal("Воркер аварійно завершився", vm.StatusText);
        Assert.True(vm.CanRetry);
        Assert.Equal("Спробувати ще", vm.RetryLabel);

        await vm.RetryCommand.ExecuteAsync(null);

        Assert.Equal([fixture.Record.Id], fixture.Processing.Enqueued);
        Assert.Equal(TranscriptStatus.Processing, vm.Status);
        Assert.Equal("У черзі на транскрибування", vm.StatusTitle);
        fixture.Dispose();
    }

    [Fact]
    public async Task Failure_WithoutALiveStatus_UsesTheStoredError()
    {
        var fixture = new SessionFixture(SessionState.Failed);
        var failed = fixture.Record with { Error = "disk full" };
        var vm = new SessionViewModel(failed, fixture.Files, fixture.Transcripts, fixture.Player, fixture.Processing, fixture.Time, a => a());

        await vm.LoadAsync();

        Assert.Equal(TranscriptStatus.Failed, vm.Status);
        Assert.Equal("disk full", vm.StatusText);
        fixture.Dispose();
    }

    [Fact]
    public async Task Retry_WhenTheQueueRejects_ShowsTheError()
    {
        _fixture.Processing.IsAsrInstalled = true;
        _fixture.Processing.EnqueueFailure = new IOException("db locked");
        var vm = _fixture.Create();
        await vm.LoadAsync();

        await vm.RetryCommand.ExecuteAsync(null);

        Assert.Equal(TranscriptStatus.Failed, vm.Status);
        Assert.Contains("db locked", vm.StatusText);
    }

    [Fact]
    public async Task NeverTranscribedSession_OffersToStartTheTranscription()
    {
        var fixture = new SessionFixture(SessionState.Interrupted);
        var vm = fixture.Create();

        await vm.LoadAsync();

        Assert.Equal(TranscriptStatus.NotTranscribed, vm.Status);
        Assert.Equal("Транскрибувати", vm.RetryLabel);
        await vm.RetryCommand.ExecuteAsync(null);
        Assert.Single(fixture.Processing.Enqueued);
        fixture.Dispose();
    }

    [Fact]
    public async Task ModelMissingEvent_SwitchesToTheInstallCard()
    {
        _fixture.Processing.Set(new SessionProcessingStatus(_fixture.Record.Id, SessionProcessingStage.Queued, 0));
        var vm = _fixture.Create();
        await vm.LoadAsync();

        _fixture.Processing.Raise(new SessionProcessingStatus(_fixture.Record.Id, SessionProcessingStage.ModelMissing, 0, SessionProcessor.AsrNotInstalledMessage));

        Assert.Equal(TranscriptStatus.AsrMissing, vm.Status);
    }

    [Fact]
    public async Task EmptyTranscript_SaysThereIsNoSpeech()
    {
        _fixture.UseTranscript(new Transcript(_fixture.Record.Id, [], "fake"));
        var vm = _fixture.Create();

        await vm.LoadAsync();

        Assert.Equal(TranscriptStatus.Empty, vm.Status);
        Assert.Equal("Мовлення не знайдено", vm.StatusTitle);
    }

    [Fact]
    public async Task UnreadableTranscript_ShowsTheErrorInsteadOfThrowing()
    {
        _fixture.Transcripts.ReadFailure = new IOException("database is locked");
        var vm = _fixture.Create();

        await vm.LoadAsync();

        Assert.Equal(TranscriptStatus.Failed, vm.Status);
        Assert.Contains("database is locked", vm.StatusText);
    }

    // Lifetime --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Dispose_ReleasesThePlayer_AndStopsFollowingEvents()
    {
        _fixture.Processing.Set(new SessionProcessingStatus(_fixture.Record.Id, SessionProcessingStage.Queued, 0));
        var vm = _fixture.Create();
        await vm.LoadAsync();

        vm.Dispose();
        vm.Dispose();

        Assert.True(_fixture.Player.Disposed);
        _fixture.Processing.Raise(new SessionProcessingStatus(_fixture.Record.Id, SessionProcessingStage.Failed, 0, "late"));
        _fixture.Player.Play();
        Assert.Equal(TranscriptStatus.Processing, vm.Status);
        Assert.False(vm.IsPlaying);
    }

    [Fact]
    public async Task Back_RaisesBackRequested()
    {
        var vm = await LoadShortAsync();
        var raised = 0;
        vm.BackRequested += () => raised++;

        vm.BackCommand.Execute(null);

        Assert.Equal(1, raised);
    }

    // Performance -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task BuildingTheViewModel_ForATenThousandWordTranscript_TakesUnder200Ms()
    {
        // Warm up the code paths on a small transcript so JIT time is not measured.
        _fixture.UseTranscript(TranscriptFactory.Make(_fixture.Record.Id, 100));
        await _fixture.Create().LoadAsync();

        var transcript = TranscriptFactory.Make(_fixture.Record.Id, 10_000);
        _fixture.UseTranscript(transcript);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var vm = _fixture.Create();
        await vm.LoadAsync();
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);

        Assert.Equal(10_000, transcript.Segments.Sum(s => s.Words.Count));
        Assert.Equal(TranscriptStatus.Available, vm.Status);
        Assert.True(elapsed < TimeSpan.FromMilliseconds(200), $"Building took {elapsed.TotalMilliseconds:F1} ms.");
    }

    [Fact]
    public async Task EveryPlayerTick_OverATenThousandWordTranscript_StaysCheap()
    {
        _fixture.UseTranscript(TranscriptFactory.Make(_fixture.Record.Id, 10_000));
        var vm = _fixture.Create();
        await vm.LoadAsync();
        vm.Tick();

        // One hour of 30 Hz ticks, about 108 000 of them.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < 108_000; i++)
        {
            _fixture.Player.Position = TimeSpan.FromMilliseconds(i * 33.3);
            vm.Tick();
        }

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"108 000 ticks took {elapsed.TotalMilliseconds:F0} ms.");
    }
}
