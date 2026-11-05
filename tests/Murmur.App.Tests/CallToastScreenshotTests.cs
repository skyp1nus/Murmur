using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Murmur.App.Localization;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Calls;
using Murmur.Core.Platform;

namespace Murmur.App.Tests;

/// <summary>Renders the call toast (offer and auto-record) and the Settings "Detect calls" row headlessly into the screenshots directory (#44).</summary>
public sealed class CallToastScreenshotTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly CallInfo Teams = new("Teams", "ms-teams", 100);

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
    [InlineData("Light", CallToastKind.Offer)]
    [InlineData("Dark", CallToastKind.Offer)]
    [InlineData("Light", CallToastKind.AutoRecording)]
    [InlineData("Dark", CallToastKind.AutoRecording)]
    public async Task Toast_WritesPng_AndMatchesTheDesignSizes(string theme, CallToastKind kind)
    {
        using var english = UiCulture.Override("en");
        var path = Path.Combine(RepoPaths.ScreenshotsDirectory, $"call-toast-{kind.ToString().ToLowerInvariant()}-{theme.ToLowerInvariant()}.png");

        var (card, radius, padding) = await UiHarness.Session.Dispatch(
            () =>
            {
                var window = new CallToastWindow
                {
                    DataContext = new CallToastViewModel(Teams, kind),
                    RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light,
                };
                window.Show();
                UiHarness.Pump(6);
                var border = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "Card");
                UiHarness.Save(window, path);
                var result = (border.Bounds.Size, border.CornerRadius, border.Padding);
                window.Close();
                return result;
            },
            Ct);

        Assert.True(new FileInfo(path).Length > 2_000, "Screenshot looks empty.");
        Assert.Equal(320, card.Width);
        Assert.Equal(new CornerRadius(16), radius);
        Assert.Equal(new Thickness(14), padding);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task SettingsDetectCallsRow_WithASupportedMonitor_WritesPng(string theme)
    {
        using var english = UiCulture.Override("en");
        var path = Path.Combine(RepoPaths.ScreenshotsDirectory, $"settings-detect-calls-{theme.ToLowerInvariant()}.png");

        await UiHarness.Session.Dispatch(
            () =>
            {
                var harness = new UiHarness();
                var store = new FakeSettingsStore();
                var capture = new FakeDisplayCapture();
                var settings = new SettingsViewModel(store, capture, null, null, "/data/Recordings", new FakeFolderLauncher(), a => a(), version: "0.4.0",
                    audioSessions: new SupportedMonitor());
                var shell = new ShellViewModel(
                    new LibraryViewModel(harness.Store, harness.Time, null, a => a()),
                    new RecordingViewModel(harness.Recorder, capture, harness.Store, a => a(), store),
                    settings: settings);
                shell.ShowSettings();
                var window = new MainWindow
                {
                    DataContext = shell,
                    Width = 1100,
                    Height = 1800,
                    RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light,
                };
                window.Show();
                UiHarness.Pump(6);
                var row = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "DetectCallsRow");
                Assert.True(row.IsEffectivelyVisible);
                UiHarness.Save(window, path);
                window.Close();
            },
            Ct);

        Assert.True(new FileInfo(path).Length > 10_000, "Screenshot looks empty.");
    }
}
