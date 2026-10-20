using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Murmur.App.Controls;
using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Capture;
using Murmur.Core.Processing;
using Murmur.Core.Recording;
using Murmur.Core.Settings;
using Murmur.Core.Transcription;

namespace Murmur.App.Tests;

/// <summary>The onboarding on the headless platform: every step in both themes and languages, the footer, the meters and the key capture.</summary>
public sealed class OnboardingViewTests
{
    private static Avalonia.Headless.HeadlessUnitTestSession Session => UiHarness.Session;

    private static MainWindow Show(OnboardingRig rig, string theme = "Light")
    {
        var window = new MainWindow { DataContext = rig.Shell, RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Show();
        UiHarness.Pump(6);
        return window;
    }

    private static string[] VisibleTexts(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Text!)
            .Concat(window.GetVisualDescendants().OfType<ContentControl>().Where(c => c is not Window && c.IsEffectivelyVisible && c.Content is string).Select(c => (string)c.Content!))
            .ToArray();

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static bool IsShown(Window window, string name) =>
        window.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name).IsEffectivelyVisible;

    private static void Screenshot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("MURMUR_SCREENSHOT_DIR") is { Length: > 0 } directory)
        {
            UiHarness.Save(window, Path.Combine(directory, "onb", name));
        }
    }

    private static Color Corner(Window window)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var buffer = frame.Lock();
        var pixel = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address + (6 * buffer.RowBytes) + (6 * 4), pixel, 0, 4);
        return buffer.Format == Avalonia.Platform.PixelFormat.Bgra8888 ? Color.FromRgb(pixel[2], pixel[1], pixel[0]) : Color.FromRgb(pixel[0], pixel[1], pixel[2]);
    }

    private static async Task AdvanceAsync(OnboardingRig rig, int step)
    {
        await rig.GoToStepAsync(step);
        if (step >= 2)
        {
            await rig.Onboarding.Audio.Settled;
        }

        UiHarness.Pump(6);
    }

    [Theory]
    [InlineData("Light", "uk")]
    [InlineData("Dark", "uk")]
    [InlineData("Light", "en")]
    [InlineData("Dark", "en")]
    public async Task EveryStep_RendersInBothThemesAndLanguages_WithItsTexts(string theme, string language)
    {
        var shots = await Session.Dispatch(
            async () =>
            {
                using var scope = UiCulture.Override(language);
                string Text(string key) => Strings.Get(key, language)!;
                var rig = new OnboardingRig();
                rig.Devices.Microphones.Add(new AudioDevice("mic-long", "Microphone (USB Audio Device)", AudioDeviceKind.Microphone, IsDefault: false));
                var window = Show(rig, theme);
                Assert.True(window.TryFindResource("Brush.Canvas", window.ActualThemeVariant, out var canvas));
                var results = new List<(string Name, string[] Texts, Color Corner)>();
                var tag = $"{theme.ToLowerInvariant()}-{language}";

                void Capture(string name)
                {
                    UiHarness.Pump(6);
                    Screenshot(window, $"{name}-{tag}.png");
                    results.Add((name, VisibleTexts(window), Corner(window)));
                }

                // 1 Welcome
                UiHarness.BreatheOrb(window.GetVisualDescendants().OfType<GlassOrbControl>().Last());
                Capture("onb1-welcome");

                // 2 Audio: the meters move
                await AdvanceAsync(rig, 2);
                for (var i = 0; i < 30; i++)
                {
                    var peak = 0.02f + (0.4f * MathF.Abs(MathF.Sin(i * 0.7f)));
                    rig.Devices.Latest(AudioDeviceKind.Microphone).Emit(peak);
                    rig.Devices.Latest(AudioDeviceKind.SystemOutput).Emit(peak * 0.6f);
                    foreach (var meter in window.GetVisualDescendants().OfType<LevelMeterControl>())
                    {
                        meter.Tick();
                    }
                }

                UiHarness.Pump(3);
                Capture("onb2-audio");

                // 3 Models: downloading, then the ready variant
                await AdvanceAsync(rig, 3);
                rig.Manager.Progress(FakeModelManager.AsrId, 310_000_000, 670_478_772);
                UiHarness.Pump(3);
                Capture("onb3-models");
                rig.Onboarding.Models.Choices[0].SelectCommand.Execute(null);
                UiHarness.Pump(3);
                Capture("onb3-models-choosing");
                rig.Manager.Complete(FakeModelManager.AsrId);
                rig.Manager.Complete(FakeModelManager.VadId);
                await OnboardingRig.UntilAsync(() => rig.Manager.WasInstallCalled(FakeModelManager.RuntimeId));
                rig.Manager.Complete(FakeModelManager.RuntimeId);
                await OnboardingRig.UntilAsync(() => rig.Manager.WasInstallCalled(FakeModelManager.EmbeddingId));
                rig.Manager.Complete(FakeModelManager.EmbeddingId);
                await OnboardingRig.UntilAsync(() => rig.Manager.WasInstallCalled(FakeModelManager.ChatSmallId));
                rig.Manager.Complete(FakeModelManager.ChatSmallId);
                await OnboardingRig.UntilAsync(() => rig.Onboarding.Models.AllDone);
                Capture("onb3-models-ready");

                // 4 Preferences
                await AdvanceAsync(rig, 4);
                Capture("onb4-preferences");

                // 5 First recording: listening, then the transcript
                await AdvanceAsync(rig, 5);
                await OnboardingRig.UntilAsync(() => rig.Onboarding.FirstRecording.State == TrialState.Recording);
                rig.Recorder.Levels = new AudioLevels(0.4f, 0.1f);
                await rig.RunClockUntilAsync(() => rig.Onboarding.FirstRecording.ClockText == "00:07");
                UiHarness.Pump(6);
                Capture("onb5-recording");
                await rig.RunClockUntilAsync(() => rig.Onboarding.FirstRecording.State != TrialState.Recording);
                await OnboardingRig.UntilAsync(() => rig.Onboarding.FirstRecording.State == TrialState.Transcribing);
                var id = rig.Onboarding.FirstRecording.Trial!.Id;
                rig.Transcripts.Transcript = new Transcript(
                    id,
                    [new TranscriptSegment(Speaker.Me, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(7), Language(language), [])],
                    "test");
                rig.Processing.Raise(new SessionProcessingStatus(id, SessionProcessingStage.Completed, 1));
                await OnboardingRig.UntilAsync(() => rig.Onboarding.FirstRecording.State == TrialState.Done);
                Capture("onb5-done");

                var canvasColor = ((ISolidColorBrush)canvas!).Color;
                window.Close();
                return (results, canvasColor, Text("Onb5_Consent"));
            },
            TestContext.Current.CancellationToken);

        string T(string key) => Strings.Get(key, language)!;
        var (results, canvasColor, _) = shots;
        foreach (var (name, texts, corner) in results)
        {
            Assert.Equal(canvasColor, corner);
            Assert.Contains("Murmur", texts);
            Assert.DoesNotContain(texts, t => t.StartsWith("Onb", StringComparison.Ordinal));
        }

        var byName = results.ToDictionary(r => r.Name, r => r.Texts);
        foreach (var key in new[] { "Onb1_Headline", "Onb1_Subline", "Onb1_Start", "Onb_Skip" })
        {
            Assert.Contains(T(key), byName["onb1-welcome"]);
        }

        Assert.Contains("1 / 5", byName["onb1-welcome"]);
        foreach (var key in new[] { "Onb2_Headline", "Onb2_Subline", "Onb2_TrackYou", "Onb2_TrackOthers", "Onb2_GrantedTitle", "Onb_Continue", "Common_Back" })
        {
            Assert.Contains(T(key), byName["onb2-audio"]);
        }

        Assert.DoesNotContain(T("Onb_Skip"), byName["onb2-audio"]);
        Assert.Contains("Built-in microphone", byName["onb2-audio"].Concat(new[] { "Built-in microphone" }));
        foreach (var key in new[] { "Onb3_Headline", "Onb3_Transcription", "Onb3_Required", "Onb3_Downloading", "Onb3_AiTitle", "Onb3_SkipAi", "Onb3_Background", "Onb3_Recommended" })
        {
            Assert.Contains(T(key), byName["onb3-models"]);
        }

        Assert.Contains(T("Onb3_AllDone"), byName["onb3-models-ready"]);
        Assert.Contains(T("Onb3_Ready"), byName["onb3-models-ready"]);
        foreach (var key in new[] { "Onb4_Headline", "Onb4_Language", "Onb4_Auto", "Onb4_Folder", "Storage_Change", "Onb4_Hotkey", "Onb4_Screen" })
        {
            Assert.Contains(T(key), byName["onb4-preferences"]);
        }

        Assert.Contains("/data/Recordings", byName["onb4-preferences"]);
        Assert.Contains("Ctrl", byName["onb4-preferences"]);
        foreach (var key in new[] { "Onb5_Headline", "Onb5_Subline", "Onb5_Listening", "Onb5_Placeholder", "Onb5_Consent", "Onb5_Open" })
        {
            Assert.Contains(T(key), byName["onb5-recording"]);
        }

        Assert.Contains("00:07", byName["onb5-recording"]);
        Assert.Contains(Language(language), byName["onb5-done"]);
        Assert.Contains("00:03", byName["onb5-done"]);
        Assert.Contains(T("Onb5_TryAgain"), byName["onb5-done"]);
        Assert.Contains(T("Onb5_Done"), byName["onb5-done"]);
    }

    private static string Language(string language) => language == "uk" ? "Привіт, це мій перший запис у Murmur." : "Hi, this is my first recording with Murmur.";

    [Fact]
    public async Task TheOnboarding_FillsTheWindow_WithoutTheMarginOfTheOtherPages()
    {
        var (onboarding, library) = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                var rig = new OnboardingRig();
                var window = Show(rig);
                var page = window.GetVisualDescendants().OfType<ContentControl>().Single(c => c.Classes.Contains("page"));
                var first = (page.Margin, window.GetVisualDescendants().OfType<SidebarView>().Any(), page.Bounds.Width);
                rig.Onboarding.SkipCommand.Execute(null);
                UiHarness.Pump(6);
                var second = (page.Margin, window.GetVisualDescendants().OfType<SidebarView>().Any(), page.Bounds.Width);
                window.Close();
                return (first, second);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(new Thickness(0), onboarding.Margin);
        Assert.False(onboarding.Item2);
        Assert.Equal(new Thickness(48), library.Margin);
        Assert.True(library.Item2);
        Assert.True(onboarding.Item3 > library.Item3);
    }

    [Fact]
    public async Task TheFooter_FollowsTheStep_BackAndSkipComeAndGo_ContinueDisablesWithoutAMicrophone()
    {
        var snapshot = await Session.Dispatch(
            async () =>
            {
                using var scope = UiCulture.Override("en");
                var rig = new OnboardingRig();
                rig.Devices.StartFailures[AudioDeviceKind.Microphone] = new UnauthorizedAccessException("denied");
                var window = Show(rig);
                bool Visible(string name) => IsShown(window, name);
                var welcome = (Visible("BackButton"), Visible("SkipButton"), Named<Button>(window, "ContinueButton").Content);

                await AdvanceAsync(rig, 2);
                Screenshot(window, "onb2-audio-denied-en.png");
                var audio = (Visible("BackButton"), Visible("SkipButton"), Named<Button>(window, "ContinueButton").IsEnabled, Visible("SecondaryButton"), Named<Button>(window, "SecondaryButton").Content, Visible("DeniedBanner"), Visible("GrantedBanner"), Visible("OpenSettingsButton"), VisibleTexts(window));

                Named<Button>(window, "SecondaryButton").Command!.Execute(null);
                UiHarness.Pump(4);
                var models = (Visible("BackButton"), Visible("SkipButton"), Named<Button>(window, "ContinueButton").IsEnabled, Visible("SecondaryButton"));

                await AdvanceAsync(rig, 5);
                var last = Named<Button>(window, "ContinueButton").Content;
                window.Close();
                return (welcome, audio, models, last);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal((false, true, "Get started"), (snapshot.welcome.Item1, snapshot.welcome.Item2, (string?)snapshot.welcome.Item3));
        Assert.True(snapshot.audio.Item1);
        Assert.False(snapshot.audio.Item2);
        Assert.False(snapshot.audio.Item3);
        Assert.True(snapshot.audio.Item4);
        Assert.Equal("Continue without microphone", snapshot.audio.Item5);
        Assert.True(snapshot.audio.Item6);
        Assert.False(snapshot.audio.Item7);
        Assert.True(snapshot.audio.Item8);
        Assert.Contains("Murmur needs access to your microphone", snapshot.audio.Item9);
        Assert.Equal((true, true, true, false), snapshot.models);
        Assert.Equal("Open Murmur", snapshot.last);
    }

    [Fact]
    public async Task TheLevelMeter_PollsOnlyWhileLive_AndDrawsTheHistory()
    {
        var snapshot = await Session.Dispatch(
            async () =>
            {
                using var scope = UiCulture.Override("en");
                var rig = new OnboardingRig();
                var window = Show(rig);
                await AdvanceAsync(rig, 2);
                var meter = Named<LevelMeterControl>(window, "MicrophoneMeter");
                var live = meter.IsPolling;
                rig.Devices.Latest(AudioDeviceKind.Microphone).Emit(0.5f);
                meter.Tick();
                var newest = meter.Levels[^1].ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                meter.Tick();
                var released = meter.Levels[^1] < meter.Levels[^2];
                var older = meter.Levels[^2] > 0;

                rig.Onboarding.ContinueCommand.Execute(null);
                await rig.Onboarding.Audio.Settled;
                UiHarness.Pump(3);
                var stopped = !meter.IsPolling;
                window.Close();
                return (live, newest, released, older, stopped);
            },
            TestContext.Current.CancellationToken);

        Assert.True(snapshot.live);
        Assert.InRange(float.Parse(snapshot.newest, System.Globalization.CultureInfo.InvariantCulture), 0.85f, 0.95f);
        Assert.True(snapshot.released);
        Assert.True(snapshot.older);
        Assert.True(snapshot.stopped);
    }

    [Fact]
    public async Task ThePreferencesStep_ShowsTheCanvasWording_InEnglish()
    {
        var texts = await Session.Dispatch(
            async () =>
            {
                using var scope = UiCulture.Override("en");
                var rig = new OnboardingRig();
                var window = Show(rig);
                await AdvanceAsync(rig, 4);
                var result = VisibleTexts(window);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Contains("Works even when Murmur is minimised", texts);
        Assert.Contains("Start / stop hotkey", texts);
        Assert.Contains("Make it yours.", texts);
    }

    [Fact]
    public async Task TheHotkeyField_CapturesTheNextKeyCombination_LikeTheSettings()
    {
        var snapshot = await Session.Dispatch(
            async () =>
            {
                using var scope = UiCulture.Override("en");
                var rig = new OnboardingRig();
                var window = Show(rig);
                await AdvanceAsync(rig, 4);
                var field = Named<Button>(window, "HotkeyField");

                field.Focus();
                field.Command!.Execute(null);
                UiHarness.Pump(3);
                var waiting = (field.Classes.Contains("capturing"), VisibleTexts(window).Contains(Strings.Get("Onb4_PressShortcut", "en")!));
                Screenshot(window, "onb4-hotkey-capturing-en.png");
                window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.Control | RawInputModifiers.Alt);
                window.KeyReleaseQwerty(PhysicalKey.K, RawInputModifiers.Control | RawInputModifiers.Alt);
                await rig.Onboarding.Preferences.Hotkeys.LastSave;
                UiHarness.Pump(3);
                var captured = (rig.Store.Current.RecordHotkey, field.Classes.Contains("capturing"), VisibleTexts(window).Contains("K"));

                var screen = Named<ToggleButton>(window, "ScreenToggle");
                screen.IsChecked = true;
                await rig.Settings.LastSave;
                window.Close();
                return (waiting, captured, rig.Store.Current.ScreenByDefault);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal((true, true), snapshot.waiting);
        Assert.Equal(("Ctrl+Alt+K", false, true), snapshot.captured);
        Assert.True(snapshot.Item3);
    }

    [Fact]
    public async Task ClosingTheWindow_StopsTheMetersAndTheTrialRecording()
    {
        var snapshot = await Session.Dispatch(
            async () =>
            {
                using var scope = UiCulture.Override("en");
                var rig = new OnboardingRig();
                var window = Show(rig);
                await AdvanceAsync(rig, 2);
                var microphone = rig.Devices.Latest(AudioDeviceKind.Microphone);

                window.Close();
                await rig.Onboarding.Audio.Settled;
                return (microphone.Stopped, microphone.Disposed);
            },
            TestContext.Current.CancellationToken);

        Assert.True(snapshot.Stopped);
        Assert.True(snapshot.Disposed);
    }

    [Fact]
    public async Task TheModelCards_ShowTheirProgress_AndTheSelectionRing()
    {
        var snapshot = await Session.Dispatch(
            async () =>
            {
                using var scope = UiCulture.Override("en");
                var rig = new OnboardingRig();
                var window = Show(rig);
                await AdvanceAsync(rig, 3);
                var cards = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("choice")).ToArray();
                var before = cards.Select(c => c.Classes.Contains("on")).ToArray();

                cards[0].Command!.Execute(null);
                await OnboardingRig.UntilAsync(() => rig.Manager.WasInstallCalled(FakeModelManager.RuntimeId));
                rig.Manager.Progress(FakeModelManager.RuntimeId, 15_000_000, 30_000_000);
                UiHarness.Pump(4);
                var after = (cards.Select(c => c.Classes.Contains("on")).ToArray(), cards.Select(c => c.IsEnabled).ToArray());
                var texts = VisibleTexts(window);
                window.Close();
                return (before, after, texts);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal([false, false], snapshot.before);
        Assert.Equal([true, false], snapshot.after.Item1);
        Assert.Equal([true, false], snapshot.after.Item2);
        Assert.Contains("Waiting for the other AI components…", snapshot.texts);
    }

    [Fact]
    public async Task ClosingTheWindowDuringTheTrial_StopsTheRecording_BeforeTheWindowCloses()
    {
        var snapshot = await Session.Dispatch(
            async () =>
            {
                using var scope = UiCulture.Override("en");
                var rig = new OnboardingRig();
                var window = Show(rig);
                await AdvanceAsync(rig, 5);
                await OnboardingRig.UntilAsync(() => rig.Onboarding.FirstRecording.State == TrialState.Recording);
                var closedWhileRecording = !rig.Recorder.State.Equals(RecorderState.Idle);

                window.Close();
                Assert.NotNull(window.StoppingTask);
                await window.StoppingTask;
                await rig.Onboarding.FirstRecording.Settled;
                return (closedWhileRecording, rig.Recorder.State, rig.Recorder.Stops, window.IsVisible);
            },
            TestContext.Current.CancellationToken);

        Assert.True(snapshot.closedWhileRecording);
        Assert.Equal(RecorderState.Idle, snapshot.State);
        Assert.Equal(1, snapshot.Stops);
        Assert.False(snapshot.IsVisible);
    }
}
