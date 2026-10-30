using Avalonia.Controls;
using Avalonia.VisualTree;
using Murmur.App.Controls;
using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.Services;
using Murmur.App.Tests.Design;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.Core.Capture;
using Murmur.Core.Intelligence;
using Murmur.Core.Models;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;
using Murmur.Core.Settings;

namespace Murmur.App.Tests;

/// <summary>
/// Phase 4.1 end-to-end checks that cross feature boundaries: the Ukrainian UI on every screen, one chat held by two views,
/// a chat whose recording disappears, pause with reduced motion, settings changes during a download, the speed chip on poor players.
/// </summary>
public sealed class Phase41CrossFeatureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Parity: no board is left ungated ------------------------------------------------------------------------

    [Fact]
    public void EveryBoardOfTheParityTable_HasAllThreeGates_AndTheMainScreensTheSidebarGate()
    {
        foreach (var board in DesignBoards.All)
        {
            Assert.True(board.MaxShapeMismatchPercent is > 0, board.Name + ": no shape gate");
            Assert.True(board.MinTextMatchedPercent is >= 90, board.Name + ": no text gate");
            Assert.NotNull(board.MaxTextGeometryErrors);
            if (board.HasSidebar)
            {
                Assert.NotNull(board.MaxSidebarShapePercent);
            }
        }
    }

    // ---- Ask AI: the tab and the page can hold the same saved thread -----------------------------------------------

    /// <summary>
    /// The page of Ask AI survives a visit to a recording, and the recording's Ask AI tab continues the latest chat of that recording, which
    /// can be the very thread the page shows. After the tab appended an exchange, the page (shown again) reads the thread anew and its next
    /// exchange is stored after the tab's: nothing is refused, nothing is lost.
    /// </summary>
    [Fact]
    public async Task ThePageAndTheTab_HoldingOneThread_StoreEveryExchange_WhicheverViewAsksLater()
    {
        using var english = UiCulture.Override("en");
        using var rig = new DesignRig();
        await rig.Library.LoadAsync();
        var thread = await rig.ChatStore.CreateThreadAsync("Decisions", rig.ClientDemo.Id, Ct);
        await rig.ChatStore.AppendMessageAsync(new ChatMessage(thread.Id, 0, TurnRole.User, "What was decided?", [], UiHarness.Now), Ct);
        await rig.ChatStore.AppendMessageAsync(new ChatMessage(thread.Id, 1, TurnRole.Assistant, "PDF export moved.", [], UiHarness.Now), Ct);

        // The page shows the thread (from the sidebar's Chats list).
        rig.Shell.ShowLibraryChat();
        var page = rig.Shell.LibraryChat!;
        await page.OpenThreadAsync(thread);
        Assert.Equal(2, page.Chat.Messages.Count);

        // The user visits the recording; its tab continues the same thread and the user asks there.
        rig.Shell.OpenSession(rig.ClientDemo);
        await rig.Shell.SessionLoadTask;
        var tab = rig.Shell.Session!.Chat;
        await DesignRig.UntilAsync(() => tab.Messages.Count == 2, "the tab continues the thread");
        Assert.Equal(thread.Id, tab.Thread!.Id);
        tab.Question = "Who owns the follow-up?";
        tab.AskCommand.Execute(null);
        rig.Assistant.Answer(["Anna does."]);
        await tab.AskTask;
        Assert.Equal(4, rig.ChatStore.MessagesOf(thread.Id).Count);
        Assert.All(tab.Messages, m => Assert.Null(m.SaveNote));

        // Back on the page (the same object) the thread is read again, and the user asks another question.
        rig.Shell.ShowLibraryChat();
        Assert.Same(page, rig.Shell.LibraryChat);
        await DesignRig.UntilAsync(() => page.Chat.Messages.Count == 4, "the page shows what the tab added");
        page.Chat.Question = "And the deadline?";
        page.Chat.AskCommand.Execute(null);
        rig.Assistant.Answer(["Friday."]);
        await page.Chat.AskTask;

        Assert.All(page.Chat.Messages, m => Assert.Null(m.SaveNote));
        var stored = rig.ChatStore.MessagesOf(thread.Id);
        Assert.Equal(["What was decided?", "PDF export moved.", "Who owns the follow-up?", "Anna does.", "And the deadline?", "Friday."], stored.Select(m => m.Text));
        Assert.Equal(6, page.Chat.Messages.Count);
    }

    /// <summary>
    /// Even before the page has read the thread again (the user asks at once), the exchange is stored after what the other view appended.
    /// </summary>
    [Fact]
    public async Task ThePage_AskingAtOnceAfterTheTabAppended_StoresItsExchangeAfterTheTabs()
    {
        using var english = UiCulture.Override("en");
        using var rig = new DesignRig();
        await rig.Library.LoadAsync();
        var thread = await rig.ChatStore.CreateThreadAsync("Decisions", rig.ClientDemo.Id, Ct);
        await rig.ChatStore.AppendMessageAsync(new ChatMessage(thread.Id, 0, TurnRole.User, "What was decided?", [], UiHarness.Now), Ct);
        await rig.ChatStore.AppendMessageAsync(new ChatMessage(thread.Id, 1, TurnRole.Assistant, "PDF export moved.", [], UiHarness.Now), Ct);
        rig.Shell.ShowLibraryChat();
        var page = rig.Shell.LibraryChat!;
        await page.OpenThreadAsync(thread);

        // Another writer (the tab) appends behind the page's back; the page asks without having reloaded.
        await rig.ChatStore.AppendMessageAsync(new ChatMessage(thread.Id, 2, TurnRole.User, "Who owns the follow-up?", [], UiHarness.Now), Ct);
        await rig.ChatStore.AppendMessageAsync(new ChatMessage(thread.Id, 3, TurnRole.Assistant, "Anna does.", [], UiHarness.Now), Ct);
        page.Chat.Question = "And the deadline?";
        page.Chat.AskCommand.Execute(null);
        rig.Assistant.Answer(["Friday."]);
        await page.Chat.AskTask;

        Assert.All(page.Chat.Messages, m => Assert.Null(m.SaveNote));
        Assert.Equal(["What was decided?", "PDF export moved.", "Who owns the follow-up?", "Anna does.", "And the deadline?", "Friday."], rig.ChatStore.MessagesOf(thread.Id).Select(m => m.Text));
    }

    /// <summary>
    /// The other order: the page's answer is still streaming when the user opens the recording, so the tab reads the thread before the
    /// page has saved. The page then saves, and the tab's next exchange follows it instead of being refused.
    /// </summary>
    [Fact]
    public async Task TheTab_OpenedWhileThePageStillAnswers_StoresItsExchangeAfterThePages()
    {
        using var english = UiCulture.Override("en");
        using var rig = new DesignRig();
        await rig.Library.LoadAsync();
        var thread = await rig.ChatStore.CreateThreadAsync("Decisions", rig.ClientDemo.Id, Ct);
        await rig.ChatStore.AppendMessageAsync(new ChatMessage(thread.Id, 0, TurnRole.User, "What was decided?", [], UiHarness.Now), Ct);
        await rig.ChatStore.AppendMessageAsync(new ChatMessage(thread.Id, 1, TurnRole.Assistant, "PDF export moved.", [], UiHarness.Now), Ct);
        rig.Shell.ShowLibraryChat();
        var page = rig.Shell.LibraryChat!;
        await page.OpenThreadAsync(thread);

        page.Chat.Question = "And the deadline?";
        page.Chat.AskCommand.Execute(null);
        rig.Assistant.Push("Fri");
        await DesignRig.UntilAsync(() => page.Chat.IsGenerating && page.Chat.Messages[^1].Text.Length > 0, "the page's answer streams");

        rig.Shell.OpenSession(rig.ClientDemo);
        await rig.Shell.SessionLoadTask;
        var tab = rig.Shell.Session!.Chat;
        await DesignRig.UntilAsync(() => tab.Messages.Count == 2, "the tab continues the thread as it was stored");

        rig.Assistant.Answer(["day."]);
        await page.Chat.AskTask;
        Assert.Equal(4, rig.ChatStore.MessagesOf(thread.Id).Count);

        tab.Question = "Who owns the follow-up?";
        tab.AskCommand.Execute(null);
        rig.Assistant.Answer(["Anna does."]);
        await tab.AskTask;

        Assert.All(tab.Messages, m => Assert.Null(m.SaveNote));
        Assert.Equal(["What was decided?", "PDF export moved.", "And the deadline?", "Friday.", "Who owns the follow-up?", "Anna does."], rig.ChatStore.MessagesOf(thread.Id).Select(m => m.Text));
    }

    // ---- Chat: the recording of a scoped chat is deleted while an answer is being generated ------------------------

    [Fact]
    public async Task AScopedChat_WhoseRecordingIsDeletedWhileTheAnswerStreams_KeepsTheAnswerOnScreen_AndSaysItIsNoLongerSaved()
    {
        using var english = UiCulture.Override("en");
        using var rig = new DesignRig();
        await rig.Library.LoadAsync();

        rig.Shell.ShowNewChat(rig.ClientDemo, scoped: true);
        var chat = rig.Shell.LibraryChat!.Chat;
        Assert.Equal(rig.ClientDemo.Id, chat.Scope);

        // One saved exchange, then a second one is on its way when the recording disappears.
        chat.Question = "First?";
        chat.AskCommand.Execute(null);
        rig.Assistant.Answer(["One."]);
        await chat.AskTask;
        var thread = chat.Thread!;

        chat.Question = "Second?";
        chat.AskCommand.Execute(null);
        rig.Assistant.Push("Tw");
        await DesignRig.UntilAsync(() => chat.Messages.Count == 4 && chat.Messages[^1].Text.Length > 0, "the answer streams");
        Assert.True(chat.IsGenerating);

        rig.ChatStore.DeleteSession(rig.ClientDemo.Id); // the cascade of the real store
        await rig.SessionStore.DeleteSessionAsync(rig.ClientDemo.Id, Ct);
        rig.Assistant.Answer(["o."]);
        await chat.AskTask;

        Assert.False(chat.IsGenerating);
        Assert.Equal("Two.", chat.Messages[^1].Text);
        Assert.Equal(Strings.Chat_NoLongerSaved, chat.Messages[^1].SaveNote);
        Assert.Null(chat.Thread);
        Assert.Empty(rig.ChatStore.MessagesOf(thread.Id));
    }

    // ---- Recording: pause and resume with reduced motion --------------------------------------------------------

    [Fact]
    public async Task PauseAndResume_WithReducedMotion_KeepTheOrbStill_WhileTheTimerAndTheStateFollow()
    {
        using var english = UiCulture.Override("en");
        var (runningStates, frameDelta, states) = await UiHarness.Session.Dispatch(
            async () =>
            {
                var harness = new UiHarness();
                var shell = harness.CreateShell();
                var window = UiHarness.Show(shell);
                Motion.SetReduceMotion(window, true);
                shell.ShowRecording();
                UiHarness.Pump(6);
                var orb = window.GetVisualDescendants().OfType<GlassOrbControl>().Single();
                var start = orb.AppliedFrames;
                var running = new List<bool> { orb.IsFrameLoopRunning };
                var recorderStates = new List<RecorderState>();

                await shell.Recording.StartStopAsync();
                UiHarness.Pump(6);
                running.Add(orb.IsFrameLoopRunning);
                recorderStates.Add(shell.Recording.State);

                await shell.Recording.PauseResumeCommand.ExecuteAsync(null);
                UiHarness.Pump(6);
                running.Add(orb.IsFrameLoopRunning);
                recorderStates.Add(shell.Recording.State);

                await shell.Recording.PauseResumeCommand.ExecuteAsync(null);
                UiHarness.Pump(6);
                running.Add(orb.IsFrameLoopRunning);
                recorderStates.Add(shell.Recording.State);

                var frames = orb.AppliedFrames - start;
                await shell.Recording.StartStopAsync();
                window.Close();
                return (running.ToArray(), frames, recorderStates.ToArray());
            },
            Ct);

        Assert.All(runningStates, running => Assert.False(running));
        Assert.Equal([RecorderState.Recording, RecorderState.Paused, RecorderState.Recording], states);
        Assert.True(frameDelta <= 1, $"the orb applied {frameDelta} frames while motion was reduced");
    }

    // ---- Settings: language and theme chosen while a runtime downloads ---------------------------------------------

    [Fact]
    public async Task SwitchingLanguageAndTheme_WhileARuntimeDownloads_DoesNotDisturbTheDownload()
    {
        using var english = UiCulture.Override("en");
        var store = new FakeSettingsStore();
        var manager = new FakeModelManager();
        manager.AddUnlisted(new ModelInfo(FakeModelManager.VulkanRuntimeId, ModelKind.LlmRuntime, "llama.cpp (Vulkan server)", 32_000_000, Required: false));
        var settings = new SettingsViewModel(
            store,
            new FakeDisplayCapture(),
            manager,
            null,
            "/data/Recordings",
            new FakeFolderLauncher(),
            action => action(),
            accelerationProbe: new FakeAccelerationProbe(AiAcceleration.Cpu, AiAcceleration.Vulkan),
            accelerationStatus: new FakeAccelerationStatus(),
            accelerationRuntimeIds: FakeModelManager.AccelerationRuntimeIds);
        var card = settings.Acceleration!;

        card.Options.Single(o => o.Value == AiAcceleration.Vulkan).ChooseCommand.Execute(null);
        var operation = card.LastOperation;
        manager.Progress(FakeModelManager.VulkanRuntimeId, 8_000_000, 32_000_000);
        Assert.True(card.IsDownloading);

        var appearance = settings.Appearance;
        appearance.IsUkrainian = true;
        await appearance.LastSave;
        appearance.IsDark = true;
        await appearance.LastSave;

        Assert.Equal("uk", store.Current.Language);
        Assert.Equal(ThemePreference.Dark, store.Current.Theme);
        Assert.Equal(AiAcceleration.Vulkan, store.Current.Acceleration);
        Assert.True(card.IsDownloading);
        Assert.Equal(25, card.Percent);
        Assert.True(appearance.NeedsRestart);

        manager.Progress(FakeModelManager.VulkanRuntimeId, 16_000_000, 32_000_000);
        Assert.Equal(50, card.Percent);
        manager.Complete(FakeModelManager.VulkanRuntimeId);
        await operation;

        Assert.False(card.IsDownloading);
        Assert.False(card.HasError);
        Assert.Equal("uk", store.Current.Language);
        Assert.Equal(AiAcceleration.Vulkan, store.Current.Acceleration);
    }

    // ---- Onboarding again from Preferences, while recording ----------------------------------------------------------

    [Fact]
    public async Task RunningTheOnboardingAgain_WhileARecordingRuns_ShowsNothing_ButTheFlagIsReset()
    {
        using var english = UiCulture.Override("en");
        using var rig = new DesignRig();
        await rig.Library.LoadAsync();
        await rig.Recording.StartStopAsync();
        Assert.Equal(RecorderState.Recording, rig.Recording.State);

        rig.Settings.Appearance.RestartOnboardingCommand.Execute(null);
        await rig.Settings.Appearance.LastSave;

        // The recording is never interrupted by the introduction; the reset is kept for the next start.
        Assert.False(rig.Shell.IsOnboarding);
        Assert.False(rig.Store.Current.OnboardingCompleted);

        // Settings says so: not "opens now" but "the next time Murmur starts".
        Assert.True(rig.Settings.Appearance.OnboardingDeferred);
        Assert.False(rig.Settings.Appearance.OnboardingOpensNow);
        await rig.Recording.StartStopAsync();
    }

    [Fact]
    public async Task RunningTheOnboardingAgain_WhileIdle_OpensItNow_AndDefersNothing()
    {
        using var english = UiCulture.Override("en");
        using var rig = new DesignRig();
        await rig.Library.LoadAsync();

        rig.Settings.Appearance.RestartOnboardingCommand.Execute(null);
        await rig.Settings.Appearance.LastSave;

        Assert.False(rig.Settings.Appearance.OnboardingDeferred);
        Assert.True(rig.Settings.Appearance.OnboardingOpensNow);
    }

    // ---- Speed: players without the optional capabilities ------------------------------------------------------------

    /// <summary>An <see cref="IAudioPlayer"/> that is nothing more: no mute, no speed.</summary>
    private sealed class PlainAudio(FakeAudioPlayer inner) : IAudioPlayer
    {
        public PlaybackState State => inner.State;

        public TimeSpan Duration => inner.Duration;

        public TimeSpan Position => inner.Position;

        public event Action<PlaybackState>? StateChanged
        {
            add => inner.StateChanged += value;
            remove => inner.StateChanged -= value;
        }

        public ValueTask OpenAsync(IReadOnlyList<string> trackPaths, CancellationToken cancellationToken) => inner.OpenAsync(trackPaths, cancellationToken);

        public void Play() => inner.Play();

        public void Pause() => inner.Pause();

        public void Seek(TimeSpan position) => inner.Seek(position);

        public void Dispose() => inner.Dispose();
    }

    /// <summary>An <see cref="IVideoPlayer"/> that is nothing more: no playback rate.</summary>
    private sealed class PlainVideo(FakeVideoPlayer inner) : IVideoPlayer
    {
        public bool IsOpen => inner.IsOpen;

        public TimeSpan Duration => inner.Duration;

        public VideoSize Size => inner.Size;

        public TimeSpan Position => inner.Position;

        public bool IsPlaying => inner.IsPlaying;

        public event Action<string>? Failed
        {
            add => inner.Failed += value;
            remove => inner.Failed -= value;
        }

        public ValueTask OpenAsync(string filePath, CancellationToken cancellationToken) => inner.OpenAsync(filePath, cancellationToken);

        public void Close() => inner.Close();

        public void Play() => inner.Play();

        public void Pause() => inner.Pause();

        public void Seek(TimeSpan position) => inner.Seek(position);

        public bool TryCopyFrame(Span<byte> destination, int width, int height, int stride) => inner.TryCopyFrame(destination, width, height, stride);

        public void Dispose() => inner.Dispose();
    }

    private sealed class PlainVideoPlayback(FakeVideoPlayer video) : IVideoPlayback
    {
        public bool IsSupported => true;

        public IVideoPlayer CreateVideoPlayer() => new PlainVideo(video);
    }

    private static async Task<(SessionViewModel Vm, FakeAudioPlayer Audio, FakeVideoPlayer Video, SessionFixture Fixture)> LoadSessionAsync(bool plainAudio, bool plainVideo)
    {
        var fixture = new SessionFixture();
        fixture.UseShortTranscript();
        var segments = new[] { new ScreenSegment(0, TimeSpan.Zero, TimeSpan.FromSeconds(10)) };
        File.WriteAllBytes(fixture.Files.ScreenSegment(0), [0]);
        var video = new FakeVideoPlayer(TimeSpan.FromSeconds(10));
        IVideoPlayback playback = plainVideo ? new PlainVideoPlayback(video) : new FakeVideoPlayback(video);
        IAudioPlayer player = plainAudio ? new PlainAudio(fixture.Player) : fixture.Player;
        var vm = new SessionViewModel(fixture.Record with { ScreenSegments = segments }, fixture.Files, fixture.Transcripts, player, fixture.Processing, fixture.Time, action => action(), videoPlayback: playback);
        await vm.LoadAsync();
        return (vm, fixture.Player, video, fixture);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task TheSpeedChip_WithAPlayerThatCannotChangeTheSpeed_IsDisabled_AndTheVideoRateIsNeverTouched(bool plainAudio, bool plainVideo)
    {
        using var english = UiCulture.Override("en");
        var (vm, audio, video, fixture) = await LoadSessionAsync(plainAudio, plainVideo);
        using (fixture)
        {
            Assert.False(vm.CanChangeSpeed);
            Assert.False(vm.CycleSpeedCommand.CanExecute(null));
            Assert.Equal("1×", vm.SpeedText);

            // Execute does not consult CanExecute: the chip must stay put even so.
            vm.CycleSpeedCommand.Execute(null);
            Assert.Equal("1×", vm.SpeedText);
            Assert.Equal(1, vm.Speed);
            Assert.Equal(1, audio.Speed);
            Assert.All(video.Rates, rate => Assert.Equal(1, rate)); // the neutral rate at most
            vm.PlayFrom(TimeSpan.FromSeconds(2));
            vm.SeekTo(TimeSpan.FromSeconds(4));
            vm.Dispose();
        }
    }

    [Fact]
    public async Task TheSpeedChip_WithAnAudioPlayerThatCanButAVideoThatCannotFollow_StillCyclesTheAudio_WithoutAVideoRate()
    {
        using var english = UiCulture.Override("en");
        var (vm, audio, video, fixture) = await LoadSessionAsync(plainAudio: false, plainVideo: true);
        using (fixture)
        {
            Assert.True(vm.CanChangeSpeed);
            vm.CycleSpeedCommand.Execute(null);
            vm.CycleSpeedCommand.Execute(null);

            Assert.Equal("1.5×", vm.SpeedText);
            Assert.Equal(1.5, audio.Speed);
            Assert.Empty(video.Rates);
        }
    }

    [Fact]
    public async Task TheSpeedChip_WithAnAudioPlayerThatCanAndAVideoThatCanFollow_HandsTheSameRateToBoth()
    {
        using var english = UiCulture.Override("en");
        var (vm, audio, video, fixture) = await LoadSessionAsync(plainAudio: false, plainVideo: false);
        using (fixture)
        {
            vm.CycleSpeedCommand.Execute(null);
            vm.CycleSpeedCommand.Execute(null);
            vm.CycleSpeedCommand.Execute(null);

            Assert.Equal(2, audio.Speed);
            Assert.Equal(2, video.Rate);
            Assert.Equal("2×", vm.SpeedText);
        }
    }
}
