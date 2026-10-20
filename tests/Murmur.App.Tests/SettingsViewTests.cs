using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Murmur.App.Appearance;
using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.Services;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Models;
using Murmur.Core.Settings;

namespace Murmur.App.Tests;

/// <summary>The Settings screen on the headless platform: rendering, navigation, live updates and key capture.</summary>
public sealed partial class SettingsViewTests
{
    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private sealed class Rig
    {
        public Rig(AppSettings? initial = null)
        {
            Store = new FakeSettingsStore(initial);
            Settings = new SettingsViewModel(Store, Capture, Manager, null, "/data/Recordings", Launcher, action => action(), version: "0.4.0");
            var library = new LibraryViewModel(Harness.Store, Harness.Time, null, action => action());
            var recording = new RecordingViewModel(Harness.Recorder, Capture, Harness.Store, action => action(), Store);
            Shell = new ShellViewModel(
                library,
                recording,
                chatFactory: scope => new ChatViewModel(new FakeAssistant(), new FakeLlm(), Harness.Recorder, scope, "/models"),
                settings: Settings);
        }

        public UiHarness Harness { get; } = new();

        public FakeSettingsStore Store { get; }

        public FakeDisplayCapture Capture { get; } = new();

        public FakeModelManager Manager { get; } = new();

        public FakeFolderLauncher Launcher { get; } = new();

        public SettingsViewModel Settings { get; }

        public ShellViewModel Shell { get; }

        public MainWindow Show(double height = 2800)
        {
            Shell.ShowSettings();
            var window = new MainWindow { DataContext = Shell, Height = height };
            window.Show();
            UiHarness.Pump(6);
            return window;
        }
    }

    private static string[] VisibleTexts(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Text!)
            .Concat(window.GetVisualDescendants().OfType<ContentControl>().Where(c => c is not Window && c.IsEffectivelyVisible && c.Content is string).Select(c => (string)c.Content!))
            .ToArray();

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static Color Corner(Window window)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var buffer = frame.Lock();
        var pixel = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address + (6 * buffer.RowBytes) + ((buffer.Size.Width - 8) * 4), pixel, 0, 4);
        return buffer.Format == Avalonia.Platform.PixelFormat.Bgra8888
            ? Color.FromRgb(pixel[2], pixel[1], pixel[0])
            : Color.FromRgb(pixel[0], pixel[1], pixel[2]);
    }

    /// <summary>Screenshots go where the architect asked for them (set <c>MURMUR_SCREENSHOT_DIR</c>); they are not tracked files.</summary>
    private static void Screenshot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("MURMUR_SCREENSHOT_DIR") is { Length: > 0 } directory)
        {
            UiHarness.Save(window, Path.Combine(directory, name));
        }
    }

    [Fact]
    public async Task Settings_ShowsTheCanvasWording_InEnglish()
    {
        var texts = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                var rig = new Rig();
                var window = rig.Show();
                var result = VisibleTexts(window);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Contains("Show in Explorer", texts);
        Assert.Contains("Work even when the window is minimised.", texts);
        Assert.Contains("Audio and video of new recordings are saved in this folder.", texts);
        Assert.Contains("Start / stop recording", texts);
        Assert.Contains("Open Murmur", texts);
        Assert.Contains("Free, no accounts, no cloud.", texts);
    }

    [Theory]
    [InlineData("Light", "uk")]
    [InlineData("Dark", "uk")]
    [InlineData("Light", "en")]
    [InlineData("Dark", "en")]
    public async Task Settings_RendersInBothThemesAndLanguages_WithEverySection(string theme, string language)
    {
        var (variant, canvas, corner, texts, icons) = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override(language);
                var rig = new Rig();
                rig.Manager.SetInstalled(FakeModelManager.AsrId);
                rig.Manager.SetInstalled(FakeModelManager.VadId);
                var window = rig.Show();
                window.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
                UiHarness.Pump(6);
                Screenshot(window, $"settings-{theme.ToLowerInvariant()}-{language}.png");
                Assert.True(window.TryFindResource("Brush.Canvas", window.ActualThemeVariant, out var canvasBrush));
                var result = (window.ActualThemeVariant, ((ISolidColorBrush)canvasBrush!).Color, Corner(window), VisibleTexts(window), window.GetVisualDescendants().OfType<Avalonia.Controls.Control>().Count());
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light, variant);
        Assert.Equal(canvas, corner);
        Assert.True(icons > 100);
        string Text(string key) => Strings.Get(key, language)!;
        foreach (var key in new[]
        {
            "Settings_Title", "Settings_ModelsTitle", "Settings_DevicesTitle", "Settings_StorageTitle", "Settings_HotkeysTitle",
            "Settings_AppearanceTitle", "Settings_AboutTitle", "Models_AiNote", "Hotkeys_Record", "Hotkeys_ToggleScreen", "Hotkeys_ShowApp",
            "Storage_Change", "Storage_Open", "Appearance_ThemeLight", "Appearance_ThemeDark", "Appearance_ThemeSystem",
            "Appearance_OnboardingRestart", "Nav_Library", "Nav_Settings", "Devices_ScreenByDefault",
        })
        {
            Assert.Contains(Text(key), texts);
        }

        Assert.Contains("Ctrl Shift R", texts);
        Assert.Contains("Parakeet TDT 0.6B v3 (int8)", texts);
        Assert.Contains("/data/Recordings", texts);
        Assert.Contains("0.4.0", texts);
        Assert.Contains(texts, t => t.Contains("NVIDIA", StringComparison.Ordinal) && t.Contains("CC-BY-4.0", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("Gemma 4", StringComparison.Ordinal) && t.Contains("Google", StringComparison.Ordinal));
        Assert.Equal(2, texts.Count(t => t == Text("Models_Installed")));

        if (language == "en")
        {
            // Only the language names keep their own spelling in the English UI.
            var cyrillic = texts.Where(t => Cyrillic().IsMatch(t)).Distinct().ToArray();
            Assert.Equal([Strings.Get("Appearance_LanguageUk", "en")], cyrillic);
        }

        var keyShaped = KeyShaped();
        Assert.DoesNotContain(texts, t => keyShaped.IsMatch(t));
    }

    [GeneratedRegex("[Ѐ-ӿ]")]
    private static partial Regex Cyrillic();

    [GeneratedRegex(@"^[A-Z][A-Za-z0-9]*_[A-Za-z0-9_]+$")]
    private static partial Regex KeyShaped();

    [Fact]
    public async Task ModelRows_ReactToManagerEvents_InstallProgressInstalled_CancelAndFailure()
    {
        var snapshot = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                var rig = new Rig();
                var window = rig.Show();
                var vad = rig.Settings.Models!.Rows.Single(r => r.Id == FakeModelManager.VadId);
                var bars = () => window.GetVisualDescendants().OfType<ProgressBar>().Where(b => b.IsEffectivelyVisible).ToArray();
                var buttons = () => window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b is not ToggleButton)
                    .Select(b => b.Content as string ?? b.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text).ToArray();

                var idle = (Bars: bars().Length, Download: buttons().Count(b => b == "Download"));

                vad.InstallCommand.Execute(null);
                rig.Manager.Progress(FakeModelManager.VadId, 160_000, 640_000);
                UiHarness.Pump(4);
                var downloading = (Bars: bars().Select(b => b.Value).ToArray(), Cancel: buttons().Count(b => b == "Cancel"), Texts: VisibleTexts(window));
                Screenshot(window, "settings-models-downloading-en.png");

                vad.CancelCommand.Execute(null);
                UiHarness.Pump(4);
                var cancelled = (Bars: bars().Length, Download: buttons().Count(b => b == "Download"));

                vad.InstallCommand.Execute(null);
                rig.Manager.Fail(FakeModelManager.VadId, "HTTP 500");
                UiHarness.Pump(4);
                var failed = (Texts: VisibleTexts(window), Retry: buttons().Count(b => b == "Retry"));

                vad.InstallCommand.Execute(null);
                rig.Manager.Complete(FakeModelManager.VadId);
                UiHarness.Pump(4);
                var installed = (Texts: VisibleTexts(window), Remove: buttons().Count(b => b == "Remove"), Bars: bars().Length);
                window.Close();
                return (idle, downloading, cancelled, failed, installed);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, snapshot.idle.Bars);
        Assert.Equal(6, snapshot.idle.Download);
        Assert.Equal([25.0], snapshot.downloading.Bars);
        Assert.Equal(1, snapshot.downloading.Cancel);
        Assert.Contains("25%", snapshot.downloading.Texts);
        Assert.Contains("160 KB of 640 KB", snapshot.downloading.Texts);
        Assert.Equal(0, snapshot.cancelled.Bars);
        Assert.Equal(6, snapshot.cancelled.Download);
        Assert.Contains(Strings.Get("Models_InstallFailed", "en"), snapshot.failed.Texts);
        Assert.Equal(1, snapshot.failed.Retry);
        Assert.Equal(0, snapshot.installed.Bars);
        Assert.Equal(1, snapshot.installed.Remove);
        Assert.Contains("Installed", snapshot.installed.Texts);
        Assert.DoesNotContain(Strings.Get("Models_InstallFailed", "en"), snapshot.installed.Texts);
    }

    [Fact]
    public async Task Sidebar_ShowsOnTheTopLevelPages_WithTheCurrentPageMarked_AndLeadsToSettingsAndBack()
    {
        var snapshot = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("uk");
                var rig = new Rig();
                var window = new MainWindow { DataContext = rig.Shell };
                window.Show();
                UiHarness.Pump(4);

                Button NavButton(string text) => window.GetVisualDescendants().OfType<Button>()
                    .Single(b => b.Classes.Contains("nav") && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text));
                bool On(string text) => NavButton(text).Classes.Contains("on");
                int Sidebars() => window.GetVisualDescendants().OfType<SidebarView>().Count();
                int Sections() => window.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("sub") && b.IsEffectivelyVisible);

                var library = (Sidebars: Sidebars(), LibraryOn: On(Strings.Nav_Library), SettingsOn: On(Strings.Nav_Settings), Sections: Sections());

                NavButton(Strings.Nav_Settings).Command!.Execute(null);
                UiHarness.Pump(4);
                var settings = (Page: rig.Shell.CurrentPage, Views: window.GetVisualDescendants().OfType<SettingsView>().Count(), LibraryOn: On(Strings.Nav_Library), SettingsOn: On(Strings.Nav_Settings), Sections: Sections());

                NavButton(Strings.Nav_Library).Command!.Execute(null);
                UiHarness.Pump(4);
                var back = (Page: rig.Shell.CurrentPage, SettingsOn: On(Strings.Nav_Settings), Sections: Sections());

                NavButton(Strings.Common_AskAi).Command!.Execute(null);
                UiHarness.Pump(4);
                var chat = (Page: rig.Shell.CurrentPage, ChatOn: On(Strings.Common_AskAi));

                window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("side") && b.IsEffectivelyVisible && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == Strings.Library_NewRecording))
                    .Command!.Execute(null);
                UiHarness.Pump(4);
                var recording = (Page: rig.Shell.CurrentPage, Sidebars: Sidebars());

                window.Close();
                return (library, settings, back, chat, recording, rig.Shell);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, snapshot.library.Sidebars);
        Assert.True(snapshot.library.LibraryOn);
        Assert.False(snapshot.library.SettingsOn);
        Assert.Equal(0, snapshot.library.Sections);

        Assert.IsType<SettingsViewModel>(snapshot.settings.Page);
        Assert.Equal(1, snapshot.settings.Views);
        Assert.False(snapshot.settings.LibraryOn);
        Assert.True(snapshot.settings.SettingsOn);
        Assert.Equal(6, snapshot.settings.Sections);

        Assert.IsType<LibraryViewModel>(snapshot.back.Page);
        Assert.False(snapshot.back.SettingsOn);
        Assert.Equal(0, snapshot.back.Sections);

        Assert.IsType<LibraryChatViewModel>(snapshot.chat.Page);
        Assert.True(snapshot.chat.ChatOn);

        Assert.IsType<RecordingViewModel>(snapshot.recording.Page);
        Assert.Equal(1, snapshot.recording.Sidebars);
    }

    [Fact]
    public async Task WithoutASettingsScreen_TheWindowHasNoSidebar()
    {
        var harness = new UiHarness();
        var shell = harness.CreateShell();

        var sidebars = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var count = window.GetVisualDescendants().OfType<SidebarView>().Count();
                window.Close();
                return count;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, sidebars);
        shell.ShowSettings();
        Assert.IsType<LibraryViewModel>(shell.CurrentPage);
    }

    [Fact]
    public async Task SectionLinks_ScrollTheSectionIntoView()
    {
        var (before, after) = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("uk");
                var rig = new Rig();
                var window = rig.Show(height: 780);
                var scroller = Named<ScrollViewer>(window, "Scroller");
                var start = scroller.Offset.Y;
                var about = rig.Settings.Sections.Single(s => s.Id == SettingsViewModel.AboutSectionId);

                var link = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("sub") && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == about.Title));
                link.Command!.Execute(null);
                UiHarness.Pump(6);
                var result = (start, scroller.Offset.Y);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, before);
        Assert.True(after > 200, $"The about section was not scrolled into view (offset {after}).");
    }

    [Fact]
    public async Task Devices_ThePickersShowTheSavedChoice_AndPickingSavesIt()
    {
        var (labels, selected, saved) = await Session.Dispatch(
            async () =>
            {
                using var scope = UiCulture.Override("en");
                var rig = new Rig(new AppSettings { MicrophoneDeviceId = "mic-usb" });
                var window = rig.Show();
                var microphone = Named<ComboBox>(window, "MicrophoneBox");
                var display = Named<ComboBox>(window, "DisplayBox");
                var names = microphone.Items.OfType<ChoiceItem>().Select(i => i.Name).ToArray();
                var shown = ((ChoiceItem)microphone.SelectedItem!).Id;

                display.SelectedItem = display.Items.OfType<ChoiceItem>().Single(i => i.Id == "display-2");
                Named<ToggleButton>(window, "ScreenByDefaultToggle").IsChecked = true;
                await rig.Settings.LastSave;
                UiHarness.Pump(3);
                var result = (names, shown, rig.Store.Current);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(["System default", "Built-in microphone", "USB headset"], labels);
        Assert.Equal("mic-usb", selected);
        Assert.Equal("display-2", saved.ScreenDisplayId);
        Assert.True(saved.ScreenByDefault);
        Assert.Equal("mic-usb", saved.MicrophoneDeviceId);
    }

    [Fact]
    public async Task Storage_ChangeUsesTheFolderPickerOfTheView_AndOpenCallsTheLauncher()
    {
        // Open creates the folder before launching it, so it must be one the test may create (CI runners cannot write /mnt).
        var picked = Path.Combine(Path.GetTempPath(), "murmur-settings-" + Guid.NewGuid().ToString("N"), "meetings");
        var (pickerWired, opened, shownPath) = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                var rig = new Rig();
                var window = rig.Show();
                var wired = rig.Settings.Storage.FolderPicker is TopLevelFolderPicker;
                rig.Settings.Storage.FolderPicker = new FakeFolderPicker { Result = picked };
                Named<Button>(window, "ChangeFolderButton").Command!.Execute(null);
                UiHarness.Pump(4);
                Named<Button>(window, "OpenFolderButton").Command!.Execute(null);
                var result = (wired, rig.Launcher.Opened.ToArray(), Named<TextBlock>(window, "RecordingsPath").Text);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        try
        {
            Assert.True(pickerWired);
            Assert.Equal([picked], opened);
            Assert.Equal(picked, shownPath);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(picked)!, recursive: true);
        }
    }

    [Fact]
    public async Task Hotkeys_TheNextKeyCombinationAfterAClickIsCaptured_EscCancels_BackspaceClears()
    {
        var snapshot = await Session.Dispatch(
            async () =>
            {
                using var scope = UiCulture.Override("en");
                var rig = new Rig();
                var window = rig.Show();
                var caps = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("keycap")).ToArray();

                caps[0].Focus();
                caps[0].Command!.Execute(null);
                UiHarness.Pump(3);
                var waiting = (caps[0].Content, caps[0].Classes.Contains("capturing"));
                Screenshot(window, "settings-hotkey-capturing-en.png");
                window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.Control | RawInputModifiers.Alt);
                window.KeyReleaseQwerty(PhysicalKey.K, RawInputModifiers.Control | RawInputModifiers.Alt);
                await rig.Settings.Hotkeys.LastSave;
                UiHarness.Pump(3);
                var captured = (caps[0].Content, rig.Store.Current.RecordHotkey, caps[0].Classes.Contains("capturing"));

                caps[1].Focus();
                caps[1].Command!.Execute(null);
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                UiHarness.Pump(3);
                var escaped = (caps[1].Content, rig.Store.Current.ToggleScreenHotkey);

                caps[2].Focus();
                caps[2].Command!.Execute(null);
                window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
                await rig.Settings.Hotkeys.LastSave;
                UiHarness.Pump(3);
                var cleared = (caps[2].Content, rig.Store.Current.ShowAppHotkey);

                // A duplicate is refused and says so on screen.
                caps[1].Focus();
                caps[1].Command!.Execute(null);
                window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.Control | RawInputModifiers.Alt);
                UiHarness.Pump(3);
                var duplicate = (VisibleTexts(window), rig.Store.Current.ToggleScreenHotkey);
                window.Close();
                return (waiting, captured, escaped, cleared, duplicate);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(("Press a new shortcut…", true), snapshot.waiting);
        Assert.Equal(("Ctrl Alt K", "Ctrl+Alt+K", false), snapshot.captured);
        Assert.Equal(("Ctrl Shift S", "Ctrl+Shift+S"), snapshot.escaped);
        Assert.Equal(("Not set", (string?)null), snapshot.cleared);
        Assert.Contains("Already used by “Start / stop recording”.", snapshot.duplicate.Item1);
        Assert.Equal("Ctrl+Shift+S", snapshot.duplicate.Item2);
    }

    [Fact]
    public async Task Theme_ChangedOnTheScreen_IsAppliedAtOnce_ThroughTheThemeApplier()
    {
        var (light, dark, system, saved) = await Session.Dispatch(
            async () =>
            {
                using var scope = UiCulture.Override("en");
                var app = Application.Current!;
                var before = app.RequestedThemeVariant;
                try
                {
                    var rig = new Rig(new AppSettings { Theme = ThemePreference.Light });
                    using var applier = new ThemeApplier(app, rig.Store);
                    var window = rig.Show(height: 780);
                    UiHarness.Pump(4);
                    var first = (app.RequestedThemeVariant, window.ActualThemeVariant, Corner(window));

                    Named<ToggleButton>(window, "ThemeDark").IsChecked = true;
                    await rig.Settings.LastSave;
                    UiHarness.Pump(6);
                    var second = (app.RequestedThemeVariant, window.ActualThemeVariant, Corner(window));

                    Named<ToggleButton>(window, "ThemeSystem").IsChecked = true;
                    await rig.Settings.LastSave;
                    UiHarness.Pump(6);
                    var third = (app.RequestedThemeVariant, window.ActualThemeVariant, Named<ToggleButton>(window, "ThemeSystem").IsChecked);
                    var theme = rig.Store.Current.Theme;
                    window.Close();
                    return (first, second, third, theme);
                }
                finally
                {
                    app.RequestedThemeVariant = before;
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ThemeVariant.Light, light.RequestedThemeVariant);
        Assert.Equal(ThemeVariant.Dark, dark.RequestedThemeVariant);
        Assert.Equal(ThemeVariant.Dark, dark.ActualThemeVariant);
        Assert.NotEqual(light.Item3, dark.Item3);
        Assert.Equal(ThemeVariant.Default, system.RequestedThemeVariant);
        Assert.True(system.Item3);
        Assert.Equal(ThemePreference.System, saved);
    }

    [Fact]
    public async Task Language_ChosenOnTheScreen_IsSaved_ShowsTheRestartNote_AndKeepsTheRunningLanguage()
    {
        var snapshot = await Session.Dispatch(
            async () =>
            {
                using var scope = UiCulture.Override("en");
                var rig = new Rig();
                var window = rig.Show();
                var noteBefore = VisibleTexts(window).Contains(Strings.Appearance_RestartNote);

                Named<ToggleButton>(window, "LanguageUk").IsChecked = true;
                await rig.Settings.LastSave;
                UiHarness.Pump(4);
                var result = (noteBefore, VisibleTexts(window).Contains(Strings.Appearance_RestartNote), rig.Store.Current.Language, UiCulture.Language, Named<ToggleButton>(window, "LanguageEn").IsChecked);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.False(snapshot.noteBefore);
        Assert.True(snapshot.Item2);
        Assert.Equal("uk", snapshot.Item3);
        Assert.Equal("en", snapshot.Item4);
        Assert.False(snapshot.Item5);
    }

    [Fact]
    public async Task Onboarding_RestartButton_ClearsTheCompletedFlag_AndSaysSo()
    {
        var snapshot = await Session.Dispatch(
            async () =>
            {
                using var scope = UiCulture.Override("en");
                var rig = new Rig(new AppSettings { OnboardingCompleted = true });
                var window = rig.Show();
                var before = VisibleTexts(window).Contains(Strings.Get("Appearance_OnboardingDone", "en")!);

                Named<Button>(window, "RestartOnboardingButton").Command!.Execute(null);
                await rig.Settings.LastSave;
                UiHarness.Pump(4);
                var result = (before, VisibleTexts(window).Contains(Strings.Get("Appearance_OnboardingDone", "en")!), rig.Store.Current.OnboardingCompleted);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.False(snapshot.before);
        Assert.True(snapshot.Item2);
        Assert.False(snapshot.Item3);
    }

    [Fact]
    public async Task NarrowWindow_StacksTheSectionTitleAboveItsContent()
    {
        var (wide, narrow) = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                var rig = new Rig();
                var window = rig.Show(height: 2800);
                var panel = Named<Controls.ResponsivePairPanel>(window, "DevicesSection");
                var children = panel.Children;
                var side = (children[0].Bounds.Y == children[1].Bounds.Y, children[1].Bounds.X);
                window.Width = 900;
                UiHarness.Pump(6);
                var stacked = (children[0].Bounds.Y == children[1].Bounds.Y, children[1].Bounds.Y > children[0].Bounds.Y, children[1].Bounds.Width);
                Screenshot(window, "settings-narrow-en.png");
                window.Close();
                return (side, stacked);
            },
            TestContext.Current.CancellationToken);

        Assert.True(wide.Item1);
        Assert.True(wide.Item2 > 200);
        Assert.False(narrow.Item1);
        Assert.True(narrow.Item2);
        Assert.True(narrow.Item3 > 400);
    }
}
