using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Murmur.App.Appearance;
using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Settings;
using Murmur.Storage;

namespace Murmur.App.Tests;

/// <summary>
/// The start-up path with a real <see cref="JsonSettingsStore"/> on a temporary folder: language and theme come from
/// settings.json before the first window is shown, later saves re-theme an open window, a corrupt file falls back to defaults.
/// </summary>
public sealed partial class SettingsStartupTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "murmur-startup-" + Guid.NewGuid().ToString("N"));

    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private string SettingsFile => Path.Combine(_folder, "settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void WriteSettings(string json)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsFile, json);
    }

    private static Color Corner(Window window)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var buffer = frame.Lock();
        var pixel = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address + (6 * buffer.RowBytes) + (6 * 4), pixel, 0, 4);
        return buffer.Format == Avalonia.Platform.PixelFormat.Bgra8888
            ? Color.FromRgb(pixel[2], pixel[1], pixel[0])
            : Color.FromRgb(pixel[0], pixel[1], pixel[2]);
    }

    private static Color CanvasOf(Window window, ThemeVariant variant)
    {
        Assert.True(window.TryFindResource("Brush.Canvas", variant, out var value));
        return ((ISolidColorBrush)value!).Color;
    }

    private static string[] VisibleTexts(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Text!).ToArray();

    /// <summary>What <c>App.Initialize</c> does with the store, replayed on the running headless application.</summary>
    private static StartUpScope StartUp(JsonSettingsStore store) => new(store);

    /// <summary>The settings-driven start-up with the language scoped to the test flow, never the process-wide value.</summary>
    private sealed class StartUpScope : IDisposable
    {
        private readonly IDisposable _language;
        private readonly ThemeApplier _theme;

        public StartUpScope(JsonSettingsStore store)
        {
            _language = UiCulture.Override(store.Current.Language);
            _theme = new ThemeApplier(Application.Current!, store);
        }

        public void Dispose()
        {
            _theme.Dispose();
            _language.Dispose();
        }
    }

    [Theory]
    [InlineData("""{ "theme": "Dark", "language": "en" }""", "Dark", "en")]
    [InlineData("""{ "theme": "Light", "language": "uk" }""", "Light", "uk")]
    public async Task SettingsFile_SetsLanguageAndTheme_BeforeTheFirstWindowRenders(string json, string theme, string language)
    {
        WriteSettings(json);
        var store = new JsonSettingsStore(SettingsFile);
        var harness = new UiHarness();
        var shell = harness.CreateShell();
        await shell.Library.LoadAsync();

        var (variant, actual, corner, canvas, texts, culture) = await Session.Dispatch(
            () =>
            {
                var app = Application.Current!;
                var before = app.RequestedThemeVariant;
                try
                {
                    using var applier = StartUp(store);
                    var window = new MainWindow { DataContext = shell };
                    window.Show();
                    UiHarness.Pump(6);
                    var result = (app.RequestedThemeVariant, window.ActualThemeVariant, Corner(window), CanvasOf(window, window.ActualThemeVariant), VisibleTexts(window), UiCulture.Language);
                    window.Close();
                    return result;
                }
                finally
                {
                    app.RequestedThemeVariant = before;
                }
            },
            TestContext.Current.CancellationToken);

        var expected = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        Assert.Equal(expected, variant);
        Assert.Equal(expected, actual);
        Assert.Equal(canvas, corner);
        Assert.Equal(language, culture);
        Assert.Contains(language == "en" ? "No recordings yet" : Uk("Library_EmptyTitle"), texts);
    }

    private static string Uk(string key) =>
        System.Xml.Linq.XDocument.Load(Path.Combine(RepoPaths.ResourcesDirectory, "Strings.resx")).Root!.Elements("data")
            .Single(e => (string)e.Attribute("name")! == key).Element("value")!.Value;

    [Fact]
    public async Task SavingTheThemeThroughTheStore_ReThemesAnOpenWindow_LightDarkSystem()
    {
        WriteSettings("""{ "theme": "Light", "language": "en" }""");
        var store = new JsonSettingsStore(SettingsFile);
        var harness = new UiHarness();
        var shell = harness.CreateShell();
        await shell.Library.LoadAsync();

        var (light, dark, system, lightAgain) = await Session.Dispatch(
            async () =>
            {
                var app = Application.Current!;
                var before = app.RequestedThemeVariant;
                try
                {
                    using var applier = StartUp(store);
                    var window = new MainWindow { DataContext = shell };
                    window.Show();
                    UiHarness.Pump(6);

                    async Task<(ThemeVariant Actual, Color Corner, Color Canvas)> SwitchTo(ThemePreference theme)
                    {
                        await store.SaveAsync(store.Current with { Theme = theme }, CancellationToken.None);
                        UiHarness.Pump(6);
                        return (window.ActualThemeVariant, Corner(window), CanvasOf(window, window.ActualThemeVariant));
                    }

                    var first = (Actual: window.ActualThemeVariant, Corner: Corner(window), Canvas: CanvasOf(window, window.ActualThemeVariant));
                    var toDark = await SwitchTo(ThemePreference.Dark);
                    var toSystem = await SwitchTo(ThemePreference.System);
                    var toLight = await SwitchTo(ThemePreference.Light);
                    window.Close();
                    return (first, toDark, toSystem, toLight);
                }
                finally
                {
                    app.RequestedThemeVariant = before;
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ThemeVariant.Light, light.Actual);
        Assert.Equal(ThemeVariant.Dark, dark.Actual);
        Assert.NotEqual(light.Corner, dark.Corner);
        Assert.Equal(dark.Canvas, dark.Corner);
        Assert.Equal(light.Canvas, light.Corner);
        // System follows the headless OS (light); whatever it is, the window must match its own variant's canvas.
        Assert.Equal(system.Canvas, system.Corner);
        Assert.Equal(light.Canvas, lightAgain.Corner);
        Assert.Contains("\"theme\": \"Light\"", File.ReadAllText(SettingsFile), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    public async Task CorruptSettingsFile_GivesDefaults_KeepsABadCopy_AndTheWindowStillRenders(string content)
    {
        WriteSettings(content);
        var store = new JsonSettingsStore(SettingsFile);
        var harness = new UiHarness();
        var shell = harness.CreateShell();
        await shell.Library.LoadAsync();

        var (variant, culture, texts) = await Session.Dispatch(
            () =>
            {
                var app = Application.Current!;
                var before = app.RequestedThemeVariant;
                try
                {
                    using var applier = StartUp(store);
                    var window = new MainWindow { DataContext = shell };
                    window.Show();
                    UiHarness.Pump(6);
                    var result = (app.RequestedThemeVariant, UiCulture.Language, VisibleTexts(window));
                    window.Close();
                    return result;
                }
                finally
                {
                    app.RequestedThemeVariant = before;
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(new AppSettings(), store.Current);
        Assert.Equal(ThemeVariant.Default, variant);
        Assert.Equal("uk", culture);
        Assert.Contains(Uk("Library_EmptyTitle"), texts);
        if (content.Length > 0)
        {
            Assert.True(File.Exists(SettingsFile + ".bad"));
            Assert.Equal(content, File.ReadAllText(SettingsFile + ".bad"));
            Assert.False(File.Exists(SettingsFile));
        }
    }

    // English completeness --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Library")]
    [InlineData("LibraryChat")]
    [InlineData("Recording")]
    [InlineData("SessionTranscript")]
    [InlineData("SessionAsk")]
    public async Task EnglishScreens_ShowNoKeyNamesAndNoUkrainianUiTexts(string screen)
    {
        // View models format dates and badges when they are built, so they are built in the English UI culture too.
        using var englishFlow = UiCulture.Override("en");
        var harness = new UiHarness();
        harness.Store.Add(Sessions.Make("Weekly standup", UiHarness.Now.AddHours(-1), TimeSpan.FromMinutes(23), screen: true));
        harness.Store.Add(Sessions.Make("Interrupted call", UiHarness.Now.AddDays(-9), TimeSpan.FromMinutes(8), state: Murmur.Core.Sessions.SessionState.Interrupted));
        SessionFixture? fixture = null;
        ShellViewModel shell;
        if (screen.StartsWith("Session", StringComparison.Ordinal))
        {
            fixture = new SessionFixture(duration: TimeSpan.FromMinutes(23));
            fixture.UseShortTranscript();
            harness.Store.Add(fixture.Record);
            var f = fixture;
            shell = harness.CreateShell(
                record => new SessionViewModel(record, f.Files, f.Transcripts, f.Player, f.Processing, f.Time, action => action(), new ChatViewModel(new FakeAssistant(), new FakeLlm(), harness.Recorder, record.Id, "/models")),
                f.Processing);
            await shell.Library.LoadAsync();
            shell.Library.SelectedSession = shell.Library.Sessions.First(s => s.Id == f.Record.Id);
            await shell.SessionLoadTask;
            shell.Session!.IsAskTab = screen == "SessionAsk";
        }
        else
        {
            var library = new LibraryViewModel(harness.Store, harness.Time, null, action => action());
            var recording = new RecordingViewModel(harness.Recorder, harness.Capture, harness.Store, action => action());
            shell = new ShellViewModel(library, recording, chatFactory: scope => new ChatViewModel(new FakeAssistant(), new FakeLlm(), harness.Recorder, scope, "/models"));
            await library.LoadAsync();
            if (screen == "LibraryChat")
            {
                shell.ShowLibraryChat();
            }
            else if (screen == "Recording")
            {
                shell.ShowRecording();
            }
        }

        using var _ = fixture;
        var (texts, content) = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                var window = new MainWindow { DataContext = shell };
                window.Show();
                UiHarness.Pump(6);
                var all = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty)
                    .Concat(window.GetVisualDescendants().OfType<ContentControl>().Where(c => c.IsEffectivelyVisible && c is not Window).Select(c => c.Content as string ?? string.Empty))
                    .Concat(window.GetVisualDescendants().OfType<TextBox>().Select(t => t.PlaceholderText ?? string.Empty))
                    .ToArray();
                window.Close();
                return (all, all);
            },
            TestContext.Current.CancellationToken);

        var keys = UkResx().Keys.ToHashSet(StringComparer.Ordinal);
        var ukUiTexts = UkResx().Where(p => EnResx()[p.Key] != p.Value && p.Value.Length > 2 && !p.Value.Contains("{0}", StringComparison.Ordinal)).Select(p => p.Value).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(content, t => keys.Contains(t) || KeyShaped().IsMatch(t));
        Assert.DoesNotContain(content, t => ukUiTexts.Contains(t));
        if (screen is "Library" or "LibraryChat" or "Recording")
        {
            // No Ukrainian data on these screens (titles are Latin), so any Cyrillic is a leaked UI text.
            Assert.DoesNotContain(content, t => Cyrillic().IsMatch(t));
        }

        Assert.NotEmpty(texts);
    }

    private static Dictionary<string, string> UkResx() => Read("Strings.resx");

    private static Dictionary<string, string> EnResx() => Read("Strings.en.resx");

    private static Dictionary<string, string> Read(string name) =>
        System.Xml.Linq.XDocument.Load(Path.Combine(RepoPaths.ResourcesDirectory, name)).Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => e.Element("value")!.Value, StringComparer.Ordinal);

    [GeneratedRegex("[Ѐ-ӿ]")]
    private static partial Regex Cyrillic();

    [GeneratedRegex(@"^[A-Z][A-Za-z0-9]*_[A-Za-z0-9_]+$")]
    private static partial Regex KeyShaped();
}
