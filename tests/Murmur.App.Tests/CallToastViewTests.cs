using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Murmur.App.Controls;
using Murmur.App.Localization;
using Murmur.App.Services;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Calls;
using Murmur.Core.Platform;

namespace Murmur.App.Tests;

/// <summary>The call toast window and the Detect calls row of the Settings on the headless platform.</summary>
public sealed class CallToastViewTests
{
    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private static readonly CallInfo Teams = new("Microsoft Teams", "ms-teams", 100);

    private static T Named<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private sealed class Preference(bool reduce) : IMotionPreference
    {
        public bool ReduceMotion { get; set; } = reduce;

        public event Action? Changed;

        public void Raise() => Changed?.Invoke();
    }

    [Fact]
    public async Task TheWindow_IsABorderlessTopmostToast_ThatDoesNotTakeTheFocus()
    {
        var props = await Session.Dispatch(
            () =>
            {
                var window = new CallToastWindow { DataContext = new CallToastViewModel(Teams, CallToastKind.Offer) };
                window.Show();
                UiHarness.Pump();
                var result = (window.WindowDecorations, window.Topmost, window.ShowInTaskbar, window.ShowActivated, window.CanResize, Named<Border>(window, "Card").Bounds.Width);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(WindowDecorations.None, props.WindowDecorations);
        Assert.True(props.Topmost);
        Assert.False(props.ShowInTaskbar);
        Assert.False(props.ShowActivated);
        Assert.False(props.CanResize);
        Assert.Equal(320, props.Width);
    }

    [Fact]
    public void Placement_PutsTheCard16PxFromTheBottomRightOfTheWorkArea()
    {
        var workArea = new PixelRect(0, 0, 1920, 1040);
        var room = new Thickness(6);

        var position = CallToastWindow.Placement(workArea, 1, new Size(332, 150), room);

        // The card's right edge: 1920 - 16; the window is 6 px wider than the card on that side.
        Assert.Equal(new PixelPoint(1920 - 16 - 326, 1040 - 16 - 144), position);

        var scaled = CallToastWindow.Placement(new PixelRect(100, 50, 2880, 1560), 1.5, new Size(332, 150), room);
        Assert.Equal(new PixelPoint(100 + 2880 - (int)Math.Ceiling(342 * 1.5), 50 + 1560 - (int)Math.Ceiling(160 * 1.5)), scaled);

        var tiny = CallToastWindow.Placement(new PixelRect(0, 0, 200, 100), 1, new Size(332, 150), room);
        Assert.Equal(new PixelPoint(0, 0), tiny);
    }

    [Theory]
    [InlineData("Light", "en")]
    [InlineData("Dark", "en")]
    [InlineData("Light", "uk")]
    [InlineData("Dark", "uk")]
    public async Task TheOffer_ShowsItsTexts_OnCanvasOrSurface(string theme, string language)
    {
        var (fill, expected, texts, offer, auto, check) = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override(language);
                var window = new CallToastWindow
                {
                    DataContext = new CallToastViewModel(Teams, CallToastKind.Offer),
                    RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light,
                };
                window.Show();
                UiHarness.Pump();
                Assert.True(window.TryFindResource(theme == "Dark" ? "Brush.Surface" : "Brush.Canvas", window.ActualThemeVariant, out var brush));
                var result = (
                    ((ISolidColorBrush)Named<Border>(window, "Card").Background!).Color,
                    ((ISolidColorBrush)brush!).Color,
                    window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToArray(),
                    Named<Grid>(window, "OfferButtons").IsVisible,
                    Named<Grid>(window, "AutoButtons").IsVisible,
                    Named<CheckBox>(window, "AlwaysRecordBox").IsVisible);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, fill);
        string Text(string key) => Murmur.App.Resources.Strings.Get(key, language)!;
        Assert.Contains(Text("Calls_DetectedTitle"), texts);
        Assert.Contains(string.Format(System.Globalization.CultureInfo.InvariantCulture, Text("Calls_DetectedBody"), "Microsoft Teams"), texts);
        Assert.Contains(Text("Calls_Record"), texts);
        Assert.Contains(Text("Calls_NotNow"), texts);
        Assert.Contains(Text("Calls_AlwaysRecord"), texts);
        Assert.True(offer);
        Assert.False(auto);
        Assert.True(check);
    }

    [Fact]
    public async Task TheAutoRecordToast_ShowsOpenAndStop_WithoutTheCheckbox()
    {
        var (texts, offer, check) = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                var window = new CallToastWindow { DataContext = new CallToastViewModel(Teams, CallToastKind.AutoRecording) };
                window.Show();
                UiHarness.Pump();
                var result = (
                    window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToArray(),
                    Named<Grid>(window, "OfferButtons").IsVisible,
                    Named<CheckBox>(window, "AlwaysRecordBox").IsVisible);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Contains("Recording Microsoft Teams call", texts);
        Assert.Contains("Open", texts);
        Assert.Contains("Stop", texts);
        Assert.False(offer);
        Assert.False(check);
    }

    [Fact]
    public async Task TheButtons_AndTheCheckbox_ReachTheViewModel()
    {
        var (action, always) = await Session.Dispatch(
            () =>
            {
                var toast = new CallToastViewModel(Teams, CallToastKind.Offer);
                CallToastAction? chosen = null;
                toast.ActionChosen += (_, a) => chosen = a;
                var window = new CallToastWindow { DataContext = toast };
                window.Show();
                UiHarness.Pump();
                Named<CheckBox>(window, "AlwaysRecordBox").IsChecked = true;
                Named<Button>(window, "RecordButton").Command!.Execute(null);
                window.Close();
                return (chosen, toast.AlwaysRecord);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(CallToastAction.Record, action);
        Assert.True(always);
    }

    [Fact]
    public async Task TheHost_ShowsOneToastAtATime_AndTheWindowFollowsReducedMotion()
    {
        var (first, second, reducedAtOpen, reducedLater, closed) = await Session.Dispatch(
            () =>
            {
                var preference = new Preference(reduce: true);
                var host = new CallToastWindowHost(preference);
                host.Show(new CallToastViewModel(Teams, CallToastKind.Offer));
                var firstWindow = host.Window!;
                UiHarness.Pump();
                var reduced = Motion.GetReduceMotion(Named<OrbDisc>(firstWindow, "Orb"));
                host.Show(new CallToastViewModel(Teams, CallToastKind.AutoRecording));
                var secondWindow = host.Window!;
                preference.ReduceMotion = false;
                preference.Raise();
                UiHarness.Pump();
                var later = Motion.GetReduceMotion(secondWindow);
                host.Close();
                return (firstWindow.IsVisible, secondWindow, reduced, later, host.Window is null && !secondWindow.IsVisible);
            },
            TestContext.Current.CancellationToken);

        Assert.False(first);
        Assert.NotNull(second);
        Assert.True(reducedAtOpen);
        Assert.False(reducedLater);
        Assert.True(closed);
    }

    // ---- Settings → Detect calls -------------------------------------------------------------------------------------------

    private sealed class SupportedMonitor : IAudioSessionMonitor
    {
        public bool IsSupported => true;

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public AudioActivitySnapshot Snapshot() => new(false, []);

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheDetectCallsRow_ShowsOnlyWhereTheMonitorIsSupported(bool supported)
    {
        var (visible, texts, toggled, tried) = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                var harness = new UiHarness();
                var store = new FakeSettingsStore();
                var capture = new FakeDisplayCapture();
                var settings = new SettingsViewModel(store, capture, null, null, "/data/Recordings", new FakeFolderLauncher(), a => a(), version: "0.4.0",
                    audioSessions: supported ? new SupportedMonitor() : new NullAudioSessionMonitor());
                var asked = 0;
                settings.SampleCallToastRequested += () => asked++;
                var shell = new ShellViewModel(
                    new LibraryViewModel(harness.Store, harness.Time, null, a => a()),
                    new RecordingViewModel(harness.Recorder, capture, harness.Store, a => a(), store),
                    settings: settings);
                shell.ShowSettings();
                var window = new MainWindow { DataContext = shell, Height = 2800 };
                window.Show();
                UiHarness.Pump(6);
                var row = Named<Border>(window, "DetectCallsRow");
                var rowVisible = row.IsEffectivelyVisible;
                var rowTexts = row.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
                if (rowVisible)
                {
                    Named<Avalonia.Controls.Primitives.ToggleButton>(window, "DetectCallsSwitch").IsChecked = false;
                    Named<Button>(window, "TryCallToastButton").Command!.Execute(null);
                }

                window.Close();
                return (rowVisible, rowTexts, store.Current.DetectCalls, asked);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(supported, visible);
        if (supported)
        {
            Assert.Contains("Detect calls", texts);
            Assert.Contains("Offer to record when a call starts. Detection runs on this PC only.", texts);
            Assert.Contains("Try it", texts);
        }

        Assert.Equal(!supported, toggled);
        Assert.Equal(supported ? 1 : 0, tried);
    }
}
