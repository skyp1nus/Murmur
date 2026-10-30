using Murmur.App.Localization;
using Murmur.App.Services;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Intelligence;
using Murmur.Core.Processing;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.App.Tests;

/// <summary>The recording view beyond the player: header, filters, speed, search, summary, export and the menus of the top bar (English UI).</summary>
public sealed class SessionFeaturesTests : IDisposable
{
    private readonly IDisposable _english = UiCulture.Override("en");
    private readonly SessionFixture _fixture = new();
    private readonly FakeSummarizer _summarizer = new();
    private readonly FakeSummaryStore _summaries = new();
    private readonly FakeSessionStore _store = new();
    private readonly FakeFolderLauncher _folders = new();
    private readonly FakeRecorder _recorder = new();
    private readonly FakeLlm _llm = new();
    private readonly FakeFileSaver _saver = new();

    public SessionFeaturesTests()
    {
        _store.Add(_fixture.Record);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _english.Dispose();
        _fixture.Dispose();
    }

    private async Task<SessionViewModel> LoadAsync(bool aiInstalled = true, bool withSummarizer = true)
    {
        _fixture.UseShortTranscript();
        _llm.Status = aiInstalled ? LlmStatus.Unloaded : LlmStatus.NotInstalled;
        var chat = new ChatViewModel(new FakeAssistant(), _llm, _recorder, _fixture.Record.Id, "/models", action => action());
        var vm = new SessionViewModel(
            _fixture.Record,
            _fixture.Files,
            _fixture.Transcripts,
            _fixture.Player,
            _fixture.Processing,
            _fixture.Time,
            action => action(),
            chat,
            null,
            new SessionServices(withSummarizer ? _summarizer : null, _summaries, _store, _folders)) { FileSaver = _saver };
        await vm.LoadAsync();
        return vm;
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    // Waveform --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARecordingWithoutPeaks_ComputesThemFromTheAudioOnce_AndKeepsThemInTheFolder()
    {
        _fixture.UseShortTranscript();
        var samples = new float[16_000 * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = i < 16_000 ? 0.5f : 0.1f;
        }

        using (var writer = new Murmur.Core.Audio.WavPcm16Writer(_fixture.Files.MicrophoneAudio))
        {
            writer.Append(samples);
            writer.UpdateHeader();
        }

        using (var writer = new Murmur.Core.Audio.WavPcm16Writer(_fixture.Files.SystemAudio))
        {
            writer.Append(new float[16_000]);
            writer.UpdateHeader();
        }

        Assert.False(File.Exists(_fixture.Files.Peaks));
        var vm = _fixture.Create();

        await vm.LoadAsync();

        Assert.Equal(100, vm.Peaks.Length);
        Assert.True(vm.Peaks[10] > vm.Peaks[60]);
        Assert.True(File.Exists(_fixture.Files.Peaks));
        Assert.Equal(vm.Peaks, Murmur.Core.Audio.PeaksFile.Read(_fixture.Files.Peaks));
    }

    [Fact]
    public async Task AudioThatIsNotAWav_LeavesTheWaveformEmpty_WithoutAnError()
    {
        var vm = await LoadAsync();

        Assert.Empty(vm.Peaks);
        Assert.False(vm.HasPlayerError);
    }

    // Header ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheHeader_HasTheDate_TheLength_TheLanguageAndTheEngine_SeparatedByDots()
    {
        _fixture.Transcripts.Transcript = new Transcript(_fixture.Record.Id, [TranscriptFactory.Segment(Speaker.Me, 1, "hello")], SessionProcessor.Engine, "en");
        var vm = new SessionViewModel(_fixture.Record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action());
        Assert.Equal(["Today, 14:00", "·", "1:00:00"], vm.Meta.Select(p => p.Text));

        await vm.LoadAsync();

        Assert.Equal(["Today, 14:00", "·", "1:00:00", "·", "English", "·", "Parakeet TDT v3"], vm.Meta.Select(p => p.Text));
        Assert.Equal([false, false, true, false, false, false, false], vm.Meta.Select(p => p.IsMono));
        Assert.Equal(["·", "·", "·"], vm.Meta.Where(p => p.IsSeparator).Select(p => p.Text));
    }

    [Theory]
    [InlineData("parakeet-tdt-0.6b-v3-int8", "Parakeet TDT v3")]
    [InlineData("Parakeet-TDT", "Parakeet TDT")]
    [InlineData("whisper-small", "whisper-small")]
    [InlineData("  ", null)]
    public void EngineNames_AreTheProductNames(string engine, string? expected) => Assert.Equal(expected, EngineNames.Of(engine));

    [Theory]
    [InlineData("gemma-4-E4B-it-Q4_K_M", "Gemma 4 E4B")]
    [InlineData("google_gemma-4-26B-A4B-it-Q4_K_M", "Gemma 4 26B A4B")]
    [InlineData("llama", "llama")]
    public void ModelNames_AreTheProductNames(string id, string expected) => Assert.Equal(expected, ModelNames.Of(id));

    // Filters and speed -----------------------------------------------------------------------------------------

    [Fact]
    public async Task TheFilters_MuteTheirTrackInThePlayer_AndDimTheLinesOfThatSpeaker()
    {
        var vm = await LoadAsync();
        Assert.True(vm.CanMuteTracks);
        Assert.Contains((0, false), _fixture.Player.MuteCalls);

        vm.YouOn = false;

        Assert.Contains((0, true), _fixture.Player.MuteCalls);
        Assert.True(vm.Segments[0].IsDimmed);
        Assert.False(vm.Segments[1].IsDimmed);

        vm.YouOn = true;
        vm.OthersOn = false;

        Assert.Contains((1, true), _fixture.Player.MuteCalls);
        Assert.False(vm.Segments[0].IsDimmed);
        Assert.True(vm.Segments[1].IsDimmed);
    }

    [Fact]
    public async Task TheFilters_AreOffForAPlayerThatCannotMute()
    {
        _fixture.Player.CanMuteTracks = false;
        var vm = await LoadAsync();

        vm.YouOn = false;

        Assert.False(vm.CanMuteTracks);
        Assert.DoesNotContain(_fixture.Player.MuteCalls, c => c.Muted);
        Assert.True(vm.Segments[0].IsDimmed);
    }

    [Fact]
    public async Task TheSpeedChip_CyclesThroughTheFiveSpeeds_AndSetsThePlayer()
    {
        var vm = await LoadAsync();
        var seen = new List<string> { vm.SpeedText };
        var speeds = new List<double> { _fixture.Player.Speed };

        for (var i = 0; i < 5; i++)
        {
            vm.CycleSpeedCommand.Execute(null);
            seen.Add(vm.SpeedText);
            speeds.Add(_fixture.Player.Speed);
        }

        Assert.Equal(["1×", "1.25×", "1.5×", "2×", "0.75×", "1×"], seen);
        Assert.Equal([1, 1.25, 1.5, 2, 0.75, 1], speeds);
    }

    [Fact]
    public async Task TheSpeedChip_IsDisabled_WhenThePlayerCannotChangeTheSpeed()
    {
        _fixture.Player.CanChangeSpeed = false;
        var vm = await LoadAsync();

        Assert.False(vm.CanChangeSpeed);
        Assert.False(vm.CycleSpeedCommand.CanExecute(null));
    }

    // Search ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Search_MarksTheHits_AndEnterJumpsToTheNextRowAndWraps()
    {
        _fixture.Transcripts.Transcript = new Transcript(
            _fixture.Record.Id,
            [
                TranscriptFactory.Segment(Speaker.Me, 10, "alpha", "beta"),
                TranscriptFactory.Segment(Speaker.Others, 20, "gamma", "Beta"),
                TranscriptFactory.Segment(Speaker.Me, 30, "delta"),
                TranscriptFactory.Segment(Speaker.Others, 40, "betamax"),
            ],
            "fake");
        var vm = new SessionViewModel(_fixture.Record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action());
        await vm.LoadAsync();
        var scrolls = new List<int>();
        vm.ScrollToSegmentRequested += scrolls.Add;

        vm.SearchText = " beta ";

        Assert.Equal([0, 1, 3], vm.SearchHits);
        Assert.All(vm.SearchHits, i => Assert.Equal("beta", vm.Segments[i].HighlightTerm));
        Assert.Null(vm.Segments[2].HighlightTerm is { } ? null : vm.Segments[2].HighlightTerm);

        vm.NextHitCommand.Execute(null);
        Assert.Equal(S(10), _fixture.Player.Position);
        vm.NextHitCommand.Execute(null);
        Assert.Equal(S(20), _fixture.Player.Position);
        vm.NextHitCommand.Execute(null);
        Assert.Equal(S(40), _fixture.Player.Position);
        vm.NextHitCommand.Execute(null);
        Assert.Equal(S(10), _fixture.Player.Position);
        vm.PreviousHitCommand.Execute(null);
        Assert.Equal(S(40), _fixture.Player.Position);
        Assert.Contains(3, scrolls);
    }

    [Fact]
    public async Task Search_WithoutHitsOrText_HasNothingToJumpTo()
    {
        var vm = await LoadAsync();

        vm.SearchText = "zzz";
        Assert.Empty(vm.SearchHits);
        Assert.False(vm.NextHitCommand.CanExecute(null));

        vm.SearchText = "";
        Assert.Null(vm.HighlightTerm);
        Assert.All(vm.Segments, s => Assert.Null(s.HighlightTerm));
    }

    [Fact]
    public async Task TheCurrentRow_IsTheFilledOne()
    {
        var vm = await LoadAsync();

        vm.SeekTo(S(21));

        Assert.False(vm.Segments[0].IsCurrent);
        Assert.True(vm.Segments[1].IsCurrent);
    }

    [Fact]
    public async Task TheSpokenWord_IsShownOnlyWhilePlaying()
    {
        var vm = await LoadAsync();
        vm.SeekTo(S(11));
        Assert.Equal(-1, vm.Segments[0].VisibleWord);

        vm.PlayPauseCommand.Execute(null);
        _fixture.Player.Position = S(11);
        vm.Tick();

        Assert.Equal(vm.Segments[0].ActiveWord, vm.Segments[0].VisibleWord);
        Assert.True(vm.Segments[0].VisibleWord >= 0);
    }

    // Summary ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutASummary_TheTabOffersToSummarize()
    {
        var vm = await LoadAsync();

        Assert.Equal(SummaryStatus.None, vm.SummaryState);
        Assert.True(vm.ShowSummaryEmpty);
        Assert.True(vm.CanSummarize);
        Assert.Null(vm.SummaryNotice);
    }

    [Fact]
    public async Task AStoredSummary_IsShown_WithItsChipsAndFooter()
    {
        _summaries.Put(FakeSummarizer.Sample(_fixture.Record.Id));

        var vm = await LoadAsync();

        Assert.Equal(SummaryStatus.Ready, vm.SummaryState);
        Assert.Contains("Walked the client", vm.SummaryOverview);
        Assert.Equal(2, vm.KeyPoints.Count);
        Assert.Equal("12:52", vm.KeyPoints[0].TimeText);
        Assert.Equal("Generated locally · Gemma 4 E4B", vm.SummaryFooter);
        Assert.True(vm.HasKeyPoints && vm.HasActionItems);

        vm.KeyPoints[0].PlayCommand.Execute(null);
        Assert.Equal(S(772), _fixture.Player.Position);
        Assert.True(vm.IsPlaying);
    }

    [Fact]
    public async Task AfterANewTranscript_TheStaleSummaryIsGone()
    {
        _summaries.Put(FakeSummarizer.Sample(_fixture.Record.Id));
        var vm = await LoadAsync();
        Assert.Equal(SummaryStatus.Ready, vm.SummaryState);

        // Re-transcribing stored a new transcript, and the store dropped the summary of the old one.
        await _summaries.DeleteSummaryAsync(_fixture.Record.Id, Ct);
        _fixture.Processing.Raise(new SessionProcessingStatus(_fixture.Record.Id, SessionProcessingStage.Completed, 1));
        for (var i = 0; i < 200 && vm.SummaryState != SummaryStatus.None; i++)
        {
            await Task.Delay(5, Ct);
        }

        Assert.Equal(SummaryStatus.None, vm.SummaryState);
        Assert.Empty(vm.KeyPoints);
        Assert.Equal(string.Empty, vm.SummaryOverview);
    }

    [Fact]
    public async Task Summarize_ShowsProgress_ThenTheSummary_AndSavesIt()
    {
        var vm = await LoadAsync();
        _summarizer.Gate = new TaskCompletionSource();
        _summarizer.ProgressSteps = [0.25, 0.5];

        vm.SummarizeCommand.Execute(null);
        for (var i = 0; i < 200 && vm.SummaryProgress < 50; i++)
        {
            await Task.Delay(5, Ct);
        }

        Assert.Equal(SummaryStatus.Working, vm.SummaryState);
        Assert.True(vm.ShowSummaryWorking);
        Assert.Equal(50, vm.SummaryProgress);
        Assert.Equal("50%", vm.SummaryProgressText);
        Assert.False(vm.CanSummarize);

        _summarizer.Gate.SetResult();
        await vm.SummaryTask;

        Assert.Equal(SummaryStatus.Ready, vm.SummaryState);
        Assert.Single(_summaries.Saved);
        Assert.Equal(_fixture.Record.Id, _summarizer.Sessions.Single());
        Assert.True(vm.CanSummarize);
    }

    [Fact]
    public async Task Regenerate_ReplacesTheStoredSummary()
    {
        _summaries.Put(FakeSummarizer.Sample(_fixture.Record.Id));
        var vm = await LoadAsync();
        _summarizer.Result = FakeSummarizer.Sample(_fixture.Record.Id) with { Overview = "A second version." };

        vm.SummarizeCommand.Execute(null);
        await vm.SummaryTask;

        Assert.Equal("A second version.", vm.SummaryOverview);
        Assert.Equal("A second version.", _summaries.Saved.Single().Overview);
    }

    [Fact]
    public async Task Stopping_GoesBackToWhatTheTabShowedBefore()
    {
        _summaries.Put(FakeSummarizer.Sample(_fixture.Record.Id));
        var vm = await LoadAsync();
        _summarizer.Gate = new TaskCompletionSource();

        vm.SummarizeCommand.Execute(null);
        await Task.Delay(20, Ct);
        vm.CancelSummaryRunCommand.Execute(null);
        await vm.SummaryTask;

        Assert.True(_summarizer.Cancelled);
        Assert.Equal(SummaryStatus.Ready, vm.SummaryState);
        Assert.Empty(_summaries.Saved);

        var fresh = await LoadAsync();
        Assert.Equal(SummaryStatus.Ready, fresh.SummaryState);
    }

    [Fact]
    public async Task StoppingTheFirstSummary_BringsBackTheEmptyState()
    {
        var vm = await LoadAsync();
        _summarizer.Gate = new TaskCompletionSource();

        vm.SummarizeCommand.Execute(null);
        await Task.Delay(20, Ct);
        vm.CancelSummaryRunCommand.Execute(null);
        await vm.SummaryTask;

        Assert.Equal(SummaryStatus.None, vm.SummaryState);
    }

    [Fact]
    public async Task StopPressedWhileTheResultIsOnItsWay_DoesNotLeaveTheTabWorking()
    {
        var vm = await LoadAsync();
        _summarizer.BeforeReturn = () => vm.CancelSummaryRunCommand.Execute(null);

        vm.SummarizeCommand.Execute(null);
        await vm.SummaryTask;

        Assert.Equal(SummaryStatus.None, vm.SummaryState);
        Assert.False(vm.ShowSummaryWorking);
        Assert.Empty(_summaries.Saved);
        Assert.True(vm.CanSummarize);
    }

    [Fact]
    public async Task AFailedSummary_ShowsWhy_AndCanBeTriedAgain()
    {
        var vm = await LoadAsync();
        _summarizer.Failure = new InvalidDataException("The model returned no summary.");

        vm.SummarizeCommand.Execute(null);
        await vm.SummaryTask;

        Assert.Equal(SummaryStatus.Failed, vm.SummaryState);
        Assert.Equal("Couldn’t summarize the recording. Try again in a moment.", vm.SummaryError);
        Assert.DoesNotContain("model returned", vm.SummaryError);
        Assert.True(vm.CanSummarize);

        _summarizer.Failure = null;
        vm.SummarizeCommand.Execute(null);
        await vm.SummaryTask;
        Assert.Equal(SummaryStatus.Ready, vm.SummaryState);
    }

    [Fact]
    public async Task WithoutTheModels_TheTabExplainsAndDoesNotRun()
    {
        var vm = await LoadAsync(aiInstalled: false);

        Assert.False(vm.CanSummarize);
        Assert.True(vm.SummaryNeedsModels);
        Assert.Contains("scripts/fetch-models.ps1 -Ai", vm.SummaryNotice);
        Assert.False(vm.SummarizeCommand.CanExecute(null));
        Assert.Equal(0, _summarizer.Calls);

        var unwired = await LoadAsync(withSummarizer: false);
        Assert.False(unwired.CanSummarize);
        Assert.True(unwired.SummaryNeedsModels);
    }

    [Fact]
    public async Task WhileRecording_TheTabSaysTheModelsAreNotLoaded_AndResumesAfterwards()
    {
        var vm = await LoadAsync();
        await _recorder.StartAsync(new RecordingOptions(), CancellationToken.None);

        vm.SelectedTab = SessionTab.Summary;

        Assert.False(vm.CanSummarize);
        Assert.Contains("Models are not loaded while a recording is running", vm.SummaryNotice);

        await _recorder.StopAsync(CancellationToken.None);
        vm.SelectedTab = SessionTab.Transcript;
        vm.SelectedTab = SessionTab.Summary;
        Assert.True(vm.CanSummarize);
        Assert.Null(vm.SummaryNotice);
    }

    [Fact]
    public async Task WithoutATranscript_ThereIsNothingToSummarize()
    {
        _fixture.Transcripts.Transcript = null;
        var vm = new SessionViewModel(
            _fixture.Record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action(),
            new ChatViewModel(new FakeAssistant(), _llm, _recorder, _fixture.Record.Id, "/models", action => action()), null,
            new SessionServices(_summarizer, _summaries, _store, _folders));
        await vm.LoadAsync();

        Assert.False(vm.CanSummarize);
        Assert.Contains("transcribe the recording first", vm.SummaryNotice);
    }

    [Fact]
    public async Task ClosingTheView_StopsTheRunningSummary()
    {
        var vm = await LoadAsync();
        _summarizer.Gate = new TaskCompletionSource();
        vm.SummarizeCommand.Execute(null);
        await Task.Delay(20, Ct);

        vm.Dispose();
        await vm.SummaryTask;

        Assert.True(_summarizer.Cancelled);
        Assert.Empty(_summaries.Saved);
    }

    // Export ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Export_WritesTheFormatTheUserChose_WithTheTitleAsTheFileName()
    {
        var vm = await LoadAsync();
        Assert.True(vm.CanExport);

        vm.ExportSrtCommand.Execute(null);
        await vm.ExportTask;

        var saved = _saver.Saved.Single();
        Assert.Equal(".srt", saved.Extension);
        Assert.StartsWith("Щотижневий стендап команди.srt", saved.SuggestedName);
        Assert.StartsWith("1\n00:00:10,000 --> ", saved.Content);
        Assert.Contains("You: Привіт усім починаємо мітинг", saved.Content);
        Assert.Equal("Saved export.srt", vm.ActionMessage);
    }

    [Fact]
    public async Task ExportToMarkdown_IncludesTheSummary_WhenThereIsOne_OnlyThen()
    {
        var withoutSummary = await LoadAsync();
        withoutSummary.ExportMarkdownCommand.Execute(null);
        await withoutSummary.ExportTask;
        Assert.DoesNotContain("## Summary", _saver.Saved[0].Content);

        _summaries.Put(FakeSummarizer.Sample(_fixture.Record.Id));
        var withSummary = await LoadAsync();
        withSummary.ExportMarkdownCommand.Execute(null);
        await withSummary.ExportTask;
        Assert.Contains("## Summary", _saver.Saved[1].Content);
        Assert.Contains("- [ ] You — send the demo invite (`13:24`)", _saver.Saved[1].Content);

        withSummary.ExportTextCommand.Execute(null);
        await withSummary.ExportTask;
        Assert.DoesNotContain("Summary", _saver.Saved[2].Content);
    }

    [Fact]
    public async Task Export_CancelledDialog_SaysNothing_AFailureSaysWhy()
    {
        var vm = await LoadAsync();
        _saver.Cancel = true;
        vm.ExportWebVttCommand.Execute(null);
        await vm.ExportTask;
        Assert.Null(vm.ActionMessage);

        _saver.Cancel = false;
        _saver.Failure = new IOException("disk full");
        vm.ExportWebVttCommand.Execute(null);
        await vm.ExportTask;
        Assert.Equal("Couldn’t save the file: disk full", vm.ActionMessage);
    }

    [Fact]
    public async Task Export_NeedsATranscript()
    {
        _fixture.Transcripts.Transcript = null;
        var vm = _fixture.Create();
        vm.FileSaver = _saver;
        await vm.LoadAsync();

        Assert.False(vm.CanExport);
        Assert.False(vm.ExportMarkdownCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("Client demo — Q4 roadmap", "Client demo — Q4 roadmap")]
    [InlineData("a/b:c*d?", "a_b_c_d_")]
    [InlineData("  ..  ", "recording")]
    public void FileNames_AreSafeForWindows(string title, string expected) => Assert.Equal(expected, FileNames.Safe(title));

    // The menus of the top bar ---------------------------------------------------------------------------------

    [Fact]
    public async Task Rename_ChangesTheTitle_ThroughTheStore_AndIgnoresABlankOne()
    {
        var vm = await LoadAsync();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await vm.RenameAsync("   ");
        Assert.Empty(_store.Renames);

        await vm.RenameAsync("  Planning  ");

        Assert.Equal("Planning", vm.Title);
        Assert.Equal([(_fixture.Record.Id, "Planning")], _store.Renames);
        Assert.Contains(nameof(SessionViewModel.Title), changed);
    }

    [Fact]
    public async Task Rename_Failure_KeepsTheTitle_AndSaysWhy()
    {
        var vm = await LoadAsync();
        var before = vm.Title;
        _store.RenameFailure = new IOException("locked");

        await vm.RenameAsync("Other");

        Assert.Equal(before, vm.Title);
        Assert.Equal("Couldn’t rename the recording: locked", vm.ActionMessage);
    }

    [Fact]
    public async Task Delete_ReleasesTheMedia_ThenDeletes_ThenSaysSo()
    {
        var vm = await LoadAsync();
        var deleted = 0;
        vm.Deleted += () => deleted++;

        await vm.DeleteAsync();

        Assert.True(_fixture.Player.Disposed);
        Assert.Equal([_fixture.Record.Id], _store.Deleted);
        Assert.Equal(1, deleted);
        Assert.False(vm.CanPlay);
    }

    [Fact]
    public async Task Delete_IsNotOffered_WhileTheRecordingIsBeingTranscribed()
    {
        _fixture.Processing.Raise(new SessionProcessingStatus(_fixture.Record.Id, SessionProcessingStage.Transcribing, 0.3));
        var vm = _fixture.Create();
        await vm.LoadAsync();
        vm.Actions.BeginDeleteCommand.Execute(null);

        // The processing status of the pipeline shows the progress card instead of the list.
        Assert.Equal(TranscriptStatus.Processing, vm.Status);
        Assert.False(vm.CanDelete);
        await vm.DeleteAsync();
        Assert.Empty(_store.Deleted);
    }

    [Fact]
    public async Task Delete_WhenTheStoreFails_BringsThePlaybackBack_AndSaysWhy()
    {
        _store.DeleteFailure = new IOException("mic.wav is in use");
        var created = 0;
        var second = new FakeAudioPlayer { Duration = _fixture.Record.Duration };
        _fixture.UseShortTranscript();
        var vm = new SessionViewModel(
            _fixture.Record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action(),
            null, null, new SessionServices(_summarizer, _summaries, _store, _folders, () =>
            {
                created++;
                return second;
            })) { FileSaver = _saver };
        await vm.LoadAsync();
        vm.SeekTo(S(21));
        var deleted = 0;
        vm.Deleted += () => deleted++;

        await vm.DeleteAsync();

        Assert.True(_fixture.Player.Disposed);
        Assert.Equal(1, created);
        Assert.Single(second.Opens);
        Assert.True(vm.CanPlay);
        Assert.Equal(S(21), second.Position);
        Assert.Equal(0, deleted);
        Assert.Equal("Couldn’t delete the recording: mic.wav is in use", vm.ActionMessage);
    }

    [Fact]
    public async Task Export_AnyFailureOfTheDialogIsReported_NotJustIoErrors()
    {
        var vm = await LoadAsync();
        _saver.Failure = new NotSupportedException("Stream does not support SetLength.");

        vm.ExportMarkdownCommand.Execute(null);
        await vm.ExportTask;

        Assert.Equal("Couldn’t save the file: Stream does not support SetLength.", vm.ActionMessage);
    }

    [Fact]
    public async Task ShowInFolder_OpensTheFolderOfTheRecording_AndReportsAFailure()
    {
        var vm = await LoadAsync();

        vm.ShowInFolderCommand.Execute(null);
        Assert.Equal([_fixture.Directory], _folders.Opened);
        Assert.Null(vm.ActionMessage);

        _folders.Failure = "no file manager";
        vm.ShowInFolderCommand.Execute(null);
        Assert.Equal("Couldn’t open the folder: no file manager", vm.ActionMessage);
    }

    [Fact]
    public async Task TheMenu_StartsAtTheList_AsksBeforeDeleting_AndRenamesWithTheBox()
    {
        var vm = await LoadAsync();
        var actions = vm.Actions;
        Assert.True(actions.IsMenu);

        actions.BeginDeleteCommand.Execute(null);
        Assert.True(actions.IsConfirmDelete);
        actions.BackCommand.Execute(null);
        Assert.True(actions.IsMenu);
        Assert.Empty(_store.Deleted);

        actions.BeginRenameCommand.Execute(null);
        Assert.True(actions.IsRename);
        Assert.Equal(vm.Title, actions.RenameText);
        actions.RenameText = "Retro";
        actions.ApplyRenameCommand.Execute(null);
        await vm.EditTask;
        Assert.Equal("Retro", vm.Title);
        Assert.True(actions.IsMenu);

        actions.BeginDeleteCommand.Execute(null);
        actions.ConfirmDeleteCommand.Execute(null);
        await vm.EditTask;
        Assert.Equal([_fixture.Record.Id], _store.Deleted);

        actions.BeginRenameCommand.Execute(null);
        actions.ResetMenu();
        Assert.True(actions.IsMenu);
    }

    [Fact]
    public async Task Retranscribe_IsOfferedForATranscribedRecording_WhenTheModelIsInstalled()
    {
        var vm = await LoadAsync();
        Assert.True(vm.CanRetranscribe);

        vm.RetryCommand.Execute(null);
        await Task.Delay(20, Ct);
        Assert.Equal([_fixture.Record.Id], _fixture.Processing.Enqueued);

        _fixture.Processing.IsAsrInstalled = false;
        vm.RefreshExport();
        Assert.False(vm.CanRetranscribe);
    }

    private sealed class FakeFileSaver : IFileSaver
    {
        public List<(string SuggestedName, string Extension, string Content)> Saved { get; } = [];

        public bool Cancel { get; set; }

        public Exception? Failure { get; set; }

        public Task<string?> SaveTextAsync(string title, string suggestedFileName, string fileTypeName, string extension, string content)
        {
            if (Failure is { } failure)
            {
                throw failure;
            }

            if (Cancel)
            {
                return Task.FromResult<string?>(null);
            }

            Saved.Add((suggestedFileName, extension, content));
            return Task.FromResult<string?>("export" + extension);
        }
    }
}
