using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Murmur.App.Localization;
using Murmur.App.Services;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Capture;
using Murmur.Core.Intelligence;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

/// <summary>The chapter strip: the chapters in the view model, the current one following the playhead, seeking, and their generation with the summary (English UI).</summary>
public sealed class ChapterTests : IDisposable
{
    private readonly IDisposable _english = UiCulture.Override("en");
    private readonly SessionFixture _fixture = new();
    private readonly FakeSummarizer _summarizer = new();
    private readonly FakeSummaryStore _summaries = new();
    private readonly FakeChapterGenerator _generator = new();
    private readonly FakeChapterStore _chapters = new();
    private readonly FakeRecorder _recorder = new();
    private readonly FakeLlm _llm = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HeadlessUnitTestSession Session => UiHarness.Session;

    public void Dispose()
    {
        _english.Dispose();
        _fixture.Dispose();
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private SessionViewModel Create(bool withGenerator = true, FakeVideoPlayback? video = null, SessionRecord? record = null)
    {
        _fixture.UseShortTranscript();
        _llm.Status = LlmStatus.Unloaded;
        var chat = new ChatViewModel(new FakeAssistant(), _llm, _recorder, _fixture.Record.Id, "/models", action => action());
        return new SessionViewModel(
            record ?? _fixture.Record,
            _fixture.Files,
            _fixture.Transcripts,
            _fixture.Player,
            _fixture.Processing,
            _fixture.Time,
            action => action(),
            chat,
            video,
            new SessionServices(_summarizer, _summaries, ChapterGenerator: withGenerator ? _generator : null, Chapters: _chapters));
    }

    private async Task<SessionViewModel> LoadAsync(bool withGenerator = true)
    {
        var vm = Create(withGenerator);
        await vm.LoadAsync();
        return vm;
    }

    // The strip -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARecordingWithoutChapters_HasNone_AndHidesTheStrip()
    {
        var vm = await LoadAsync();

        Assert.Empty(vm.Chapters);
        Assert.False(vm.HasChapters);
        Assert.Null(vm.CurrentChapter);
    }

    [Fact]
    public async Task StoredChapters_AreShown_InOrder_WithTheirStartTimes()
    {
        _chapters.Put(_fixture.Record.Id, [FakeChapterGenerator.Sample[2], FakeChapterGenerator.Sample[0], FakeChapterGenerator.Sample[1], new Chapter("Late", new TimeSpan(1, 2, 5))]);

        var vm = await LoadAsync();

        Assert.True(vm.HasChapters);
        Assert.Equal(["Intro", "Roadmap & search", "PDF export", "Late"], vm.Chapters.Select(c => c.Title));
        Assert.Equal(["00:00", "04:10", "12:15", "1:02:05"], vm.Chapters.Select(c => c.StartText));
        Assert.Equal(vm.Chapters[0], vm.CurrentChapter);
    }

    [Fact]
    public async Task ChaptersThatCannotBeRead_AreAsGoodAsNone()
    {
        _chapters.GetFailure = new IOException("disk");

        var vm = await LoadAsync();

        Assert.False(vm.HasChapters);
    }

    [Fact]
    public async Task TheCurrentChapter_FollowsThePlayhead()
    {
        _chapters.Put(_fixture.Record.Id, FakeChapterGenerator.Sample);
        var vm = await LoadAsync();
        vm.PlayPauseCommand.Execute(null);

        (double Seconds, int Index)[] steps = [(0, 0), (249.9, 0), (250, 1), (600, 1), (735, 2), (1469, 2), (1470, 3), (3599, 3), (30, 0)];
        foreach (var (seconds, index) in steps)
        {
            _fixture.Player.Position = S(seconds);
            vm.Tick();

            Assert.Same(vm.Chapters[index], vm.CurrentChapter);
            Assert.Equal(vm.Chapters.Select((_, i) => i == index), vm.Chapters.Select(c => c.IsCurrent));
        }
    }

    [Fact]
    public async Task Ticks_InsideOneChapter_RaiseNoChapterNotifications()
    {
        _chapters.Put(_fixture.Record.Id, FakeChapterGenerator.Sample);
        var vm = await LoadAsync();
        vm.PlayPauseCommand.Execute(null);
        _fixture.Player.Position = S(300);
        vm.Tick();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.Chapters[1].PropertyChanged += (_, e) => raised.Add("row:" + e.PropertyName);

        for (var seconds = 301; seconds < 700; seconds += 7)
        {
            _fixture.Player.Position = S(seconds);
            vm.Tick();
        }

        Assert.DoesNotContain(raised, name => name is nameof(SessionViewModel.CurrentChapter) or nameof(SessionViewModel.Chapters) or "row:IsCurrent");
    }

    [Fact]
    public async Task FindingTheCurrentChapter_AllocatesNothing_WhileTheChapterStaysTheSame()
    {
        _chapters.Put(_fixture.Record.Id, FakeChapterGenerator.Sample);
        var vm = await LoadAsync();
        vm.UpdateCurrentChapter(S(300));

        // Warm up, then count: a binary search over the rows that touches nothing while the chapter is the same.
        vm.UpdateCurrentChapter(S(301));
        var allocated = Murmur.Core.Tests.Fakes.Allocations.SteadyState(() =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                vm.UpdateCurrentChapter(S(300 + (i % 400)));
            }
        });

        Assert.Equal(0, allocated);
        Assert.Equal("Roadmap & search", vm.CurrentChapter?.Title);
    }

    [Fact]
    public async Task TheCurrentChapter_FollowsPlaybackAtOtherSpeeds()
    {
        _chapters.Put(_fixture.Record.Id, FakeChapterGenerator.Sample);
        var vm = await LoadAsync();
        vm.PlayPauseCommand.Execute(null);
        vm.CycleSpeedCommand.Execute(null);
        vm.CycleSpeedCommand.Execute(null);
        vm.CycleSpeedCommand.Execute(null);
        Assert.Equal(2, vm.Speed);

        // The player reports media time, so the chapter follows the position whatever the rate.
        _fixture.Player.Position = S(740);
        vm.Tick();

        Assert.Equal("PDF export", vm.CurrentChapter?.Title);
    }

    [Fact]
    public async Task ScrubbingTheWaveform_MovesTheCurrentChapter()
    {
        _chapters.Put(_fixture.Record.Id, FakeChapterGenerator.Sample);
        var vm = await LoadAsync();

        vm.BeginScrub();
        vm.ScrubTo(0.5); // 30 min

        Assert.Equal("Next steps & pricing", vm.CurrentChapter?.Title);
    }

    [Fact]
    public async Task ASegment_SeeksThePlayerToTheStartOfItsChapter()
    {
        _chapters.Put(_fixture.Record.Id, FakeChapterGenerator.Sample);
        var vm = await LoadAsync();

        vm.Chapters[2].SeekCommand.Execute(null);

        Assert.Equal([S(735)], _fixture.Player.Seeks);
        Assert.Equal(S(735), vm.Position);
        Assert.Equal("PDF export", vm.CurrentChapter?.Title);
        Assert.True(vm.Chapters[2].IsCurrent);
        Assert.False(vm.Chapters[0].IsCurrent);
    }

    [Fact]
    public async Task ASegment_AlsoSeeksTheScreenVideo()
    {
        var segments = new ScreenSegment[] { new(0, TimeSpan.Zero, TimeSpan.FromMinutes(30)), new(1, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30)) };
        foreach (var segment in segments)
        {
            File.WriteAllBytes(_fixture.Files.ScreenSegment(segment.Index), [0]);
        }

        var player = new FakeVideoPlayer(TimeSpan.FromMinutes(30));
        var vm = Create(video: new FakeVideoPlayback(player, true), record: _fixture.Record with { ScreenSegments = segments });
        _chapters.Put(_fixture.Record.Id, [new Chapter("One", TimeSpan.Zero), new Chapter("Two", S(35 * 60))]);
        await vm.LoadAsync();

        vm.Chapters[1].SeekCommand.Execute(null);

        // The chapter is 5 minutes into the second screen segment.
        Assert.Equal(S(35 * 60), _fixture.Player.Position);
        Assert.Equal(S(5 * 60), player.Position);
    }

    [Fact]
    public async Task ASegmentOfAPausedPlayer_KeepsItPaused()
    {
        _chapters.Put(_fixture.Record.Id, FakeChapterGenerator.Sample);
        var vm = await LoadAsync();

        vm.Chapters[1].SeekCommand.Execute(null);

        Assert.Equal(0, _fixture.Player.Plays);
        Assert.False(vm.IsPlaying);
    }

    [Fact]
    public async Task EachSegment_HasATooltipWithItsTitle()
    {
        _chapters.Put(_fixture.Record.Id, FakeChapterGenerator.Sample);

        var vm = await LoadAsync();

        Assert.Equal("Go to chapter \"Roadmap & search\"", vm.Chapters[1].ToolTip);
    }

    // Generation with the summary -------------------------------------------------------------------------------

    [Fact]
    public async Task Summarize_AlsoMakesTheChapters_StoresThem_AndShowsTheStrip()
    {
        var vm = await LoadAsync();

        vm.SummarizeCommand.Execute(null);
        await vm.SummaryTask;

        Assert.Equal(1, _generator.Calls);
        Assert.Equal(SummaryStatus.Ready, vm.SummaryState);
        var saved = Assert.Single(_chapters.Saved);
        Assert.Equal(_fixture.Record.Id, saved.Session);
        Assert.Equal(FakeChapterGenerator.Sample, saved.Chapters);
        Assert.Equal(["Intro", "Roadmap & search", "PDF export", "Next steps & pricing"], vm.Chapters.Select(c => c.Title));
        Assert.True(vm.HasChapters);
    }

    [Fact]
    public async Task Regenerate_ReplacesTheStoredChapters()
    {
        _chapters.Put(_fixture.Record.Id, [new Chapter("Old", TimeSpan.Zero)]);
        _summaries.Put(FakeSummarizer.Sample(_fixture.Record.Id));
        var vm = await LoadAsync();
        Assert.Equal(["Old"], vm.Chapters.Select(c => c.Title));
        _generator.Result = [new("New one", TimeSpan.Zero), new("New two", S(600))];

        vm.SummarizeCommand.Execute(null);
        await vm.SummaryTask;

        Assert.Equal(["New one", "New two"], vm.Chapters.Select(c => c.Title));
        Assert.Equal(["New one", "New two"], _chapters.Saved.Single().Chapters.Select(c => c.Title));
    }

    [Fact]
    public async Task ARegenerationThatFindsNoChapters_ClearsThem()
    {
        _chapters.Put(_fixture.Record.Id, FakeChapterGenerator.Sample);
        var vm = await LoadAsync();
        _generator.Result = [];

        vm.SummarizeCommand.Execute(null);
        await vm.SummaryTask;

        Assert.False(vm.HasChapters);
        Assert.Empty(_chapters.Saved.Single().Chapters);
    }

    [Fact]
    public async Task ARecordingShorterThanTheMinimum_StillAsksTheGenerator_ButGivesTheWholeBarToTheSummary()
    {
        _chapters.Put(_fixture.Record.Id, FakeChapterGenerator.Sample);
        _summaries.Put(FakeSummarizer.Sample(_fixture.Record.Id));
        var vm = await LoadAsync();
        _generator.MinimumDuration = TimeSpan.FromHours(2);
        _generator.Result = [];
        _summarizer.ProgressSteps = [0.5];
        _summarizer.Gate = new TaskCompletionSource();

        vm.SummarizeCommand.Execute(null);
        for (var i = 0; i < 200 && vm.SummaryProgress < 50; i++)
        {
            await Task.Delay(5, Ct);
        }

        // The generator answers [] for a short recording by itself; the summary is all of the bar.
        Assert.Equal(50, vm.SummaryProgress, 6);
        _summarizer.Gate.SetResult();
        await vm.SummaryTask;

        Assert.Equal(1, _generator.Calls);
        Assert.Equal(SummaryStatus.Ready, vm.SummaryState);
        Assert.False(vm.HasChapters);
    }

    [Fact]
    public async Task WithAnUnknownDuration_TheChaptersKeepTheirShareOfTheBar()
    {
        var record = _fixture.Record with { Duration = TimeSpan.Zero };
        _fixture.Player.Duration = TimeSpan.Zero;
        var vm = Create(record: record);
        await vm.LoadAsync();
        _summarizer.ProgressSteps = [1];
        _generator.Gate = new TaskCompletionSource();

        vm.SummarizeCommand.Execute(null);
        for (var i = 0; i < 200 && vm.SummaryProgress < 70; i++)
        {
            await Task.Delay(5, Ct);
        }

        Assert.Equal(70, vm.SummaryProgress, 6);
        _generator.Gate.SetResult();
        await vm.SummaryTask;
        Assert.True(vm.HasChapters);
    }

    [Fact]
    public async Task WithoutAGenerator_TheSummaryWorksAsBefore()
    {
        var vm = await LoadAsync(withGenerator: false);

        vm.SummarizeCommand.Execute(null);
        await vm.SummaryTask;

        Assert.Equal(SummaryStatus.Ready, vm.SummaryState);
        Assert.Empty(_chapters.Saved);
    }

    [Fact]
    public async Task AFailingGenerator_KeepsTheSummary_AndTheEarlierChapters()
    {
        _chapters.Put(_fixture.Record.Id, [new Chapter("Old", TimeSpan.Zero)]);
        var vm = await LoadAsync();
        _generator.Failure = new InvalidDataException("no chapters");

        vm.SummarizeCommand.Execute(null);
        await vm.SummaryTask;

        Assert.Equal(SummaryStatus.Ready, vm.SummaryState);
        Assert.Single(_summaries.Saved);
        Assert.Empty(_chapters.Saved);
        Assert.Equal(["Old"], vm.Chapters.Select(c => c.Title));
    }

    [Fact]
    public async Task ChaptersThatCannotBeSaved_AreStillShown()
    {
        var vm = await LoadAsync();
        _chapters.SaveFailure = new IOException("disk");

        vm.SummarizeCommand.Execute(null);
        await vm.SummaryTask;

        Assert.Equal(SummaryStatus.Ready, vm.SummaryState);
        Assert.True(vm.HasChapters);
    }

    [Fact]
    public async Task ProgressCoversTheSummaryAndTheChapters_NeverGoingBack()
    {
        var vm = await LoadAsync();
        _summarizer.ProgressSteps = [0.5, 1];
        _generator.ProgressSteps = [0.5];
        _generator.Gate = new TaskCompletionSource();
        var seen = new List<double>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionViewModel.SummaryProgress))
            {
                seen.Add(vm.SummaryProgress);
            }
        };

        vm.SummarizeCommand.Execute(null);
        for (var i = 0; i < 200 && vm.SummaryProgress < 80; i++)
        {
            await Task.Delay(5, Ct);
        }

        // The summary alone fills 70 % of the bar; the chapters, half done, bring it to 85 %.
        Assert.Equal(85, vm.SummaryProgress, 6);
        Assert.Equal(SummaryStatus.Working, vm.SummaryState);

        _generator.Gate.SetResult();
        await vm.SummaryTask;

        Assert.Equal(seen.Order().ToList(), seen.Where(p => p > 0).ToList());
        Assert.Equal(SummaryStatus.Ready, vm.SummaryState);
    }

    [Fact]
    public async Task TheSummary_IsSavedAsSoonAsItIsWritten_BeforeTheChaptersAreDone()
    {
        var vm = await LoadAsync();
        _generator.Gate = new TaskCompletionSource();

        vm.SummarizeCommand.Execute(null);
        for (var i = 0; i < 200 && _generator.Calls == 0; i++)
        {
            await Task.Delay(5, Ct);
        }

        Assert.Single(_summaries.Saved);
        Assert.Empty(_chapters.Saved);
        Assert.Equal(SummaryStatus.Working, vm.SummaryState);

        _generator.Gate.SetResult();
        await vm.SummaryTask;

        Assert.Single(_summaries.Saved);
        Assert.Single(_chapters.Saved);
        Assert.Equal(SummaryStatus.Ready, vm.SummaryState);
    }

    [Fact]
    public async Task Stopping_WhileTheChaptersAreMade_KeepsTheNewSummary_AndTheOldChapters()
    {
        _chapters.Put(_fixture.Record.Id, [new Chapter("Old", TimeSpan.Zero)]);
        _summaries.Put(FakeSummarizer.Sample(_fixture.Record.Id));
        var vm = await LoadAsync();
        _summarizer.Result = FakeSummarizer.Sample(_fixture.Record.Id) with { Overview = "The new summary." };
        _generator.Gate = new TaskCompletionSource();

        vm.SummarizeCommand.Execute(null);
        for (var i = 0; i < 200 && _generator.Calls == 0; i++)
        {
            await Task.Delay(5, Ct);
        }

        vm.CancelSummaryRunCommand.Execute(null);
        await vm.SummaryTask;

        Assert.Equal(SummaryStatus.Ready, vm.SummaryState);
        Assert.Equal("The new summary.", vm.SummaryOverview);
        Assert.Equal("The new summary.", _summaries.Saved.Single().Overview);
        Assert.Empty(_chapters.Saved);
        Assert.Equal(["Old"], vm.Chapters.Select(c => c.Title));
    }

    [Fact]
    public async Task ACancellationThatIsNotStop_FromTheModelHost_FailsOnlyTheChapters()
    {
        _chapters.Put(_fixture.Record.Id, [new Chapter("Old", TimeSpan.Zero)]);
        var vm = await LoadAsync();
        _generator.Failure = new OperationCanceledException("the model was unloaded while loading");

        vm.SummarizeCommand.Execute(null);
        await vm.SummaryTask;

        Assert.Equal(SummaryStatus.Ready, vm.SummaryState);
        Assert.Single(_summaries.Saved);
        Assert.Contains("Walked the client", vm.SummaryOverview);
        Assert.Empty(_chapters.Saved);
        Assert.Equal(["Old"], vm.Chapters.Select(c => c.Title));
    }

    [Fact]
    public async Task AfterANewTranscript_TheChaptersAreReloaded()
    {
        _chapters.Put(_fixture.Record.Id, FakeChapterGenerator.Sample);
        var vm = await LoadAsync();
        Assert.True(vm.HasChapters);

        // Re-transcribing stored a new transcript, and the store dropped the chapters of the old one.
        _chapters.Put(_fixture.Record.Id, []);
        _fixture.Processing.Raise(new Murmur.Core.Processing.SessionProcessingStatus(_fixture.Record.Id, Murmur.Core.Processing.SessionProcessingStage.Completed, 1));
        for (var i = 0; i < 200 && vm.HasChapters; i++)
        {
            await Task.Delay(5, Ct);
        }

        Assert.False(vm.HasChapters);
        Assert.Null(vm.CurrentChapter);
    }

    // The view --------------------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheStrip_ShowsOneEqualSegmentPerChapter_EightPixelsApart_AndMarksTheCurrentOne()
    {
        _chapters.Put(_fixture.Record.Id, FakeChapterGenerator.Sample);
        var harness = new UiHarness();
        harness.Store.Add(_fixture.Record);
        var shell = harness.CreateShell(_ => Create(), _fixture.Processing);
        await shell.Library.LoadAsync();
        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;

        var (visible, buttons, current, height, gaps, equalWidths) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var strip = window.GetVisualDescendants().OfType<ChapterStripView>().Single();
                var segments = strip.GetVisualDescendants().OfType<Button>().ToArray();
                var bounds = segments.Select(b => b.TranslatePoint(new Point(0, 0), window)!.Value).ToArray();
                var result = (
                    strip.IsVisible,
                    segments.Length,
                    segments.Select(b => b.Classes.Contains("current")).ToArray(),
                    segments[0].Bounds.Height,
                    Enumerable.Range(1, segments.Length - 1).Select(i => bounds[i].X - (bounds[i - 1].X + segments[i - 1].Bounds.Width)).ToArray(),
                    segments.Select(b => Math.Round(b.Bounds.Width)).Distinct().Count() <= 2);
                window.Close();
                return result;
            },
            Ct);

        Assert.True(visible);
        Assert.Equal(4, buttons);
        Assert.Equal([true, false, false, false], current);
        Assert.Equal(40, height);
        Assert.All(gaps, gap => Assert.Equal(8, gap, 1));
        Assert.True(equalWidths);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheStrip_IsHidden_WhenTheRecordingHasNoChapters()
    {
        var harness = new UiHarness();
        harness.Store.Add(_fixture.Record);
        var shell = harness.CreateShell(_ => Create(), _fixture.Processing);
        await shell.Library.LoadAsync();
        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;

        var visible = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var result = window.GetVisualDescendants().OfType<ChapterStripView>().Single().IsVisible;
                window.Close();
                return result;
            },
            Ct);

        Assert.False(visible);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ClickingASegment_SeeksThePlayer()
    {
        _chapters.Put(_fixture.Record.Id, FakeChapterGenerator.Sample);
        var harness = new UiHarness();
        harness.Store.Add(_fixture.Record);
        var shell = harness.CreateShell(_ => Create(), _fixture.Processing);
        await shell.Library.LoadAsync();
        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;

        var seeks = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var segment = window.GetVisualDescendants().OfType<ChapterStripView>().Single().GetVisualDescendants().OfType<Button>().ElementAt(1);
                var inWindow = segment.TranslatePoint(new Point(segment.Bounds.Width / 2, segment.Bounds.Height / 2), window)!.Value;
                window.MouseDown(inWindow, Avalonia.Input.MouseButton.Left);
                window.MouseUp(inWindow, Avalonia.Input.MouseButton.Left);
                UiHarness.Pump();
                var result = _fixture.Player.Seeks.ToArray();
                window.Close();
                return result;
            },
            Ct);

        Assert.Equal([S(250)], seeks);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [Trait("Category", "Slow")]
    public async Task Session_WithChapters_WritesPng(string theme)
    {
        _chapters.Put(_fixture.Record.Id, [.. FakeChapterGenerator.Sample, new Chapter("A chapter with a title that is much too long to fit its segment", S(2400))]);
        var harness = new UiHarness();
        harness.Store.Add(_fixture.Record);
        var shell = harness.CreateShell(_ => Create(), _fixture.Processing);
        await shell.Library.LoadAsync();
        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;
        _fixture.Player.Position = S(800);
        var path = System.IO.Path.Combine(RepoPaths.ScreenshotsDirectory, "chapters-session-" + theme.ToLowerInvariant() + ".png");

        await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                window.RequestedThemeVariant = theme == "Dark" ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
                shell.Session!.Tick();
                UiHarness.Pump(6);
                UiHarness.Save(window, path);
                window.Close();
            },
            Ct);

        Assert.True(new FileInfo(path).Length > 10_000, "Screenshot looks empty.");
        Assert.Equal("PDF export", shell.Session!.CurrentChapter?.Title);
    }

    private static List<Chapter> Twelve() =>
        Enumerable.Range(0, 12).Select(i => new Chapter(i % 2 == 0 ? "A rather long chapter title number " + i : "Short " + i, i < 11 ? S(i * 300) : new TimeSpan(100, 2, 5))).ToList();

    [Theory]
    [InlineData(900, 600, false, false)]
    [InlineData(500, 600, true, true)]
    [Trait("Category", "Slow")]
    public async Task TwelveChapters_InASmallWindow_NeverOverflowTheirSegments(int width, int height, bool strippedDown, bool timeHidden)
    {
        _chapters.Put(_fixture.Record.Id, Twelve());
        var harness = new UiHarness();
        harness.Store.Add(_fixture.Record);
        var shell = harness.CreateShell(_ => Create(), _fixture.Processing);
        await shell.Library.LoadAsync();
        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;

        var (count, overflows, timesVisible, narrowest) = await Session.Dispatch(
            () =>
            {
                // 900 x 600 is the smallest window of the app. A strip narrower than that (the segments below 56 px) is hosted on its own.
                Window window = strippedDown
                    ? new Window { Width = width, Height = height, Content = new ChapterStripView { DataContext = shell.Session } }
                    : UiHarness.Show(shell);
                window.Width = width;
                window.Height = height;
                if (strippedDown)
                {
                    window.Show();
                }

                UiHarness.Pump(6);
                var strip = window.GetVisualDescendants().OfType<ChapterStripView>().Single();
                var segments = strip.GetVisualDescendants().OfType<Button>().ToArray();
                var padding = segments[0].Padding;
                var over = new List<string>();
                var visibleTimes = 0;
                foreach (var segment in segments)
                {
                    foreach (var text in segment.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible))
                    {
                        if (text.Classes.Contains("chapterTime"))
                        {
                            visibleTimes++;
                        }

                        // A text block is measured no wider than the room it is given, so overflow cannot be seen in its size: it is the missing
                        // ellipsis that lets a long text run past its segment (the clip then cuts it mid-letter).
                        if (text.TextTrimming == Avalonia.Media.TextTrimming.None || text.Bounds.Width > segment.Bounds.Width - padding.Left - padding.Right + 0.5)
                        {
                            over.Add(text.Text + " " + text.Bounds.Width + " in " + segment.Bounds.Width);
                        }
                    }
                }

                var result = (segments.Length, over, visibleTimes, segments.Min(b => b.Bounds.Width));
                window.Close();
                return result;
            },
            Ct);

        Assert.Equal(12, count);
        Assert.Empty(overflows);
        Assert.True(timeHidden == (timesVisible == 0), $"times visible {timesVisible}, narrowest {narrowest}");
        Assert.Equal(!timeHidden, narrowest >= ChapterStripView.CompactWidth);
    }

    [Fact]
    public async Task WhileRecording_NeitherTheSummaryNorTheChaptersAreMade()
    {
        var vm = await LoadAsync();
        await _recorder.StartAsync(new RecordingOptions(), CancellationToken.None);

        Assert.False(vm.CanSummarize);
        vm.SummarizeCommand.Execute(null);
        await vm.SummaryTask;

        Assert.Equal(0, _generator.Calls);
        Assert.Empty(_chapters.Saved);
        Assert.False(vm.HasChapters);
    }

    [Fact]
    public async Task WithoutTheSummarizer_NoChaptersAreMade()
    {
        _fixture.UseShortTranscript();
        var chat = new ChatViewModel(new FakeAssistant(), _llm, _recorder, _fixture.Record.Id, "/models", action => action());
        var vm = new SessionViewModel(_fixture.Record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action(), chat, null,
            new SessionServices(null, _summaries, ChapterGenerator: _generator, Chapters: _chapters));
        await vm.LoadAsync();

        Assert.False(vm.CanSummarize);
        vm.SummarizeCommand.Execute(null);

        Assert.Equal(0, _generator.Calls);
    }

    [Theory]
    [InlineData("Light", 900, 600)]
    [InlineData("Dark", 900, 600)]
    [Trait("Category", "Slow")]
    public async Task Session_WithChapters_AtTheSmallestWindow_WritesPng(string theme, int width, int height)
    {
        _chapters.Put(_fixture.Record.Id, [.. FakeChapterGenerator.Sample, new Chapter("A chapter with a title that is much too long to fit its segment", S(2400))]);
        var harness = new UiHarness();
        harness.Store.Add(_fixture.Record);
        var shell = harness.CreateShell(_ => Create(), _fixture.Processing);
        await shell.Library.LoadAsync();
        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;
        _fixture.Player.Position = S(800);
        var path = System.IO.Path.Combine(RepoPaths.ScreenshotsDirectory, "chapters-session-" + theme.ToLowerInvariant() + "-" + width + "x" + height + ".png");

        await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                window.Width = width;
                window.Height = height;
                window.RequestedThemeVariant = theme == "Dark" ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
                shell.Session!.Tick();
                UiHarness.Pump(6);
                UiHarness.Save(window, path);
                window.Close();
            },
            Ct);

        Assert.True(new FileInfo(path).Length > 10_000, "Screenshot looks empty.");
    }
}
