using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Murmur.App.Appearance;
using Murmur.App.Controls;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Settings;

namespace Murmur.App.Tests;

/// <summary>Light and dark themes: resources per variant, runtime switching, the settings-driven <see cref="ThemeApplier"/>.</summary>
public sealed class ThemeTests
{
    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private static ThemeVariant Variant(string name) => name == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    /// <summary>The colours of one theme straight from docs/design/tokens.json (<c>color</c> or <c>colorDark</c>).</summary>
    private static Dictionary<string, Color> Palette(string theme)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RepoPaths.TokensJson));
        var section = doc.RootElement.GetProperty(theme == "Dark" ? "colorDark" : "color");
        var palette = new Dictionary<string, Color>(StringComparer.Ordinal);
        foreach (var property in section.EnumerateObject())
        {
            palette[property.Name] = Color.Parse(property.Value.GetString()!);
        }

        return palette;
    }

    private static string Pascal(string name) => char.ToUpperInvariant(name[0]) + name[1..];

    private static Color ResolveColor(Application app, string key, ThemeVariant variant)
    {
        Assert.True(app.TryGetResource(key, variant, out var value), $"{key} is missing in {variant}.");
        return value switch
        {
            Color color => color,
            ISolidColorBrush brush => brush.Color,
            _ => throw new InvalidOperationException($"{key} in {variant} is a {value?.GetType().Name ?? "null"}, not a colour or a solid brush."),
        };
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task EveryColourToken_ResolvesToItsThemeValue_AsColourAndBrush(string theme)
    {
        var palette = Palette(theme);

        var resolved = await Session.Dispatch(
            () =>
            {
                var app = Application.Current!;
                return palette.Keys.ToDictionary(
                    key => key,
                    key => (Color: ResolveColor(app, "Color." + Pascal(key), Variant(theme)), Brush: ResolveColor(app, "Brush." + Pascal(key), Variant(theme)), Hairline: ResolveColor(app, "Brush.Hairline", Variant(theme))),
                    StringComparer.Ordinal);
            },
            TestContext.Current.CancellationToken);

        foreach (var (key, expected) in palette)
        {
            Assert.Equal(expected, resolved[key].Color);
            Assert.Equal(expected, resolved[key].Brush);
            Assert.Equal(palette["stone"], resolved[key].Hairline);
        }
    }

    [Fact]
    public async Task DarkPalette_DiffersFromLight_ForTheSurfaces()
    {
        var (lightCanvas, darkCanvas, lightInk, darkInk) = await Session.Dispatch(
            () =>
            {
                var app = Application.Current!;
                return (ResolveColor(app, "Brush.Canvas", ThemeVariant.Light), ResolveColor(app, "Brush.Canvas", ThemeVariant.Dark),
                    ResolveColor(app, "Brush.Ink", ThemeVariant.Light), ResolveColor(app, "Brush.Ink", ThemeVariant.Dark));
            },
            TestContext.Current.CancellationToken);

        Assert.NotEqual(lightCanvas, darkCanvas);
        Assert.NotEqual(lightInk, darkInk);
        Assert.Equal(Color.Parse("#141312"), darkCanvas);
        Assert.Equal(Color.Parse("#f5f3f1"), darkInk);
    }

    [Fact]
    public async Task ThemeIndependentTokens_ResolveInBothThemes()
    {
        var keys = new[] { "Font.Body", "Font.Mono", "FontSize.Body", "Radius.Card", "Radius.Pill", "Space.16", "Hairline.Thickness", "Shadow.Subtle", "Inset.Button" };

        var missing = await Session.Dispatch(
            () => keys.SelectMany(k => new[] { ThemeVariant.Light, ThemeVariant.Dark }.Where(v => !Application.Current!.TryGetResource(k, v, out _)).Select(v => $"{k} ({v})")).ToArray(),
            TestContext.Current.CancellationToken);

        Assert.Empty(missing);
    }

    [Fact]
    public async Task ControlThemeOverrides_ResolveInBothThemes_WithTheSameKeys()
    {
        // Every key that Controls.axaml overrides per theme exists in both variants and points at that variant's palette.
        var keys = ControlsThemeKeys("Light");
        Assert.Equal(keys.Order(StringComparer.Ordinal), ControlsThemeKeys("Dark").Order(StringComparer.Ordinal));
        Assert.Contains("TextControlBorderBrush", keys);
        Assert.Contains("Brush.FillHover", keys);

        var (light, dark) = await Session.Dispatch(
            () => (ResolveColor(Application.Current!, "TextControlBorderBrush", ThemeVariant.Light), ResolveColor(Application.Current!, "TextControlBorderBrush", ThemeVariant.Dark)),
            TestContext.Current.CancellationToken);

        Assert.Equal(Palette("Light")["stone"], light);
        Assert.Equal(Palette("Dark")["stone"], dark);

        var missing = await Session.Dispatch(
            () => keys.SelectMany(k => new[] { ThemeVariant.Light, ThemeVariant.Dark }.Where(v => !Application.Current!.TryGetResource(k, v, out _)).Select(v => $"{k} ({v})")).ToArray(),
            TestContext.Current.CancellationToken);
        Assert.Empty(missing);
    }

    [Fact]
    public async Task DarkHoverFills_FollowTheDarkMockups()
    {
        // Dark mockups: ghost hover = Stone (not Surface), primary hover = InkHover, chip hover/pressed = ButtonBorder.
        var dark = Palette("Dark");
        var light = Palette("Light");

        var fills = await Session.Dispatch(
            () => (
                ResolveColor(Application.Current!, "Brush.FillHover", ThemeVariant.Light),
                ResolveColor(Application.Current!, "Brush.FillHover", ThemeVariant.Dark),
                ResolveColor(Application.Current!, "Brush.FillPressed", ThemeVariant.Light),
                ResolveColor(Application.Current!, "Brush.FillPressed", ThemeVariant.Dark)),
            TestContext.Current.CancellationToken);

        Assert.Equal(light["surface"], fills.Item1);
        Assert.Equal(dark["stone"], fills.Item2);
        Assert.Equal(light["stone"], fills.Item3);
        Assert.Equal(dark["buttonBorder"], fills.Item4);
    }

    [Fact]
    public async Task EveryDynamicResourceKeyUsedByTheApp_ResolvesInBothThemes()
    {
        var keys = Directory.EnumerateFiles(RepoPaths.AppDirectory, "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(
                System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(f), "<!--.*?-->", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline),
                @"DynamicResource\s+([A-Za-z0-9_.]+)").Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Contains("Brush.Canvas", keys);

        var missing = await Session.Dispatch(
            () => keys.SelectMany(k => new[] { ThemeVariant.Light, ThemeVariant.Dark }.Where(v => !Application.Current!.TryGetResource(k, v, out _)).Select(v => $"{k} ({v})")).ToArray(),
            TestContext.Current.CancellationToken);

        Assert.Empty(missing);
    }

    private static string[] ControlsThemeKeys(string variant)
    {
        var doc = System.Xml.Linq.XDocument.Load(Path.Combine(RepoPaths.AppDirectory, "Styles", "Controls.axaml"));
        var x = System.Xml.Linq.XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var themeDictionaries = doc.Descendants().Single(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries");
        var dictionary = themeDictionaries.Elements().Single(e => (string?)e.Attribute(x + "Key") == variant);
        return dictionary.Elements().Select(e => (string)e.Attribute(x + "Key")!).ToArray();
    }

    // Rendering the screens in both themes ---------------------------------------------------------------------------

    public static TheoryData<string, string> ScreensByTheme()
    {
        var data = new TheoryData<string, string>();
        foreach (var screen in new[] { "Library", "LibraryEmpty", "Recording", "SessionTranscript", "SessionAsk", "LibraryChat" })
        {
            data.Add(screen, "Light");
            data.Add(screen, "Dark");
        }

        return data;
    }

    private static async Task<(ShellViewModel Shell, SessionFixture? Fixture)> BuildAsync(string screen)
    {
        var harness = new UiHarness();
        if (screen == "Library")
        {
            harness.SeedSessions();
        }

        switch (screen)
        {
            case "SessionTranscript":
            case "SessionAsk":
                var fixture = new SessionFixture(duration: TimeSpan.FromMinutes(23));
                fixture.UseShortTranscript();
                fixture.WritePeaks(SessionScreenshotTests.FakePeaks(23 * 60));
                harness.Store.Add(fixture.Record);
                var assistant = new FakeAssistant();
                var shell = harness.CreateShell(
                    record => new SessionViewModel(
                        record, fixture.Files, fixture.Transcripts, fixture.Player, fixture.Processing, fixture.Time,
                        action => action(), new ChatViewModel(assistant, new FakeLlm(), harness.Recorder, record.Id, "/models")),
                    fixture.Processing);
                await shell.Library.LoadAsync();
                shell.Library.SelectedSession = shell.Library.Sessions[0];
                await shell.SessionLoadTask;
                if (screen == "SessionAsk")
                {
                    shell.Session!.IsAskTab = true;
                }

                return (shell, fixture);

            case "LibraryChat":
                var library = new LibraryViewModel(harness.Store, harness.Time, null, action => action());
                var recording = new RecordingViewModel(harness.Recorder, harness.Capture, harness.Store, action => action());
                var chatShell = new ShellViewModel(library, recording, chatFactory: scope => new ChatViewModel(new FakeAssistant(), new FakeLlm(), harness.Recorder, scope, "/models"));
                await library.LoadAsync();
                chatShell.ShowLibraryChat();
                return (chatShell, null);

            case "Recording":
                var recordingShell = harness.CreateShell();
                recordingShell.ShowRecording();
                return (recordingShell, null);

            default:
                var libraryShell = harness.CreateShell();
                await libraryShell.Library.LoadAsync();
                return (libraryShell, null);
        }
    }

    /// <summary>
    /// Every brush that the app's own styles put on a control comes from the palette of the theme in effect: a lookup that
    /// failed (or froze the other theme) would leave Fluent's own colour, which is not in the palette.
    /// </summary>
    private static string[] PaletteViolations(Window window, string theme)
    {
        var allowed = Palette(theme).Values.ToHashSet();
        var violations = new List<string>();

        void Check(Visual visual, string property, IBrush? brush)
        {
            if (brush is null || (brush is ISolidColorBrush { Color.A: 0 }))
            {
                return;
            }

            if (brush is not ISolidColorBrush solid || !allowed.Contains(solid.Color))
            {
                violations.Add($"{visual.GetType().Name}.{property} = {(brush is ISolidColorBrush s ? s.Color.ToString() : brush.GetType().Name)}");
            }
        }

        foreach (var visual in window.GetVisualDescendants().OfType<Visual>().Where(v => v.IsEffectivelyVisible))
        {
            switch (visual)
            {
                case TextBlock text:
                    Check(text, nameof(TextBlock.Foreground), text.Foreground);
                    break;
                case Border border when border.Classes.Count > 0:
                    Check(border, nameof(Border.Background), border.Background);
                    Check(border, nameof(Border.BorderBrush), border.BorderBrush);
                    break;
                case Button or ToggleButton or TextBox or ComboBox:
                    var control = (TemplatedControl)visual;
                    Check(control, nameof(TemplatedControl.Background), control.Background);
                    Check(control, nameof(TemplatedControl.Foreground), control.Foreground);
                    Check(control, nameof(TemplatedControl.BorderBrush), control.BorderBrush);
                    break;
            }
        }

        return [.. violations.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Palette names of the secondary text colours a screen must show (views set them explicitly with DynamicResource).</summary>
    private static string[] ExpectedTextColours(string screen) => screen switch
    {
        "Library" => ["smoke", "graphite"],
        "LibraryEmpty" => ["graphite"],
        "Recording" or "SessionTranscript" => ["smoke"],
        "LibraryChat" => ["smoke"],
        _ => [],
    };

    private static string[] MissingTextColours(Window window, string screen, string theme)
    {
        var palette = Palette(theme);
        var used = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && t.Classes.Count > 0 && t.Foreground is ISolidColorBrush)
            .Select(t => ((ISolidColorBrush)t.Foreground!).Color)
            .ToHashSet();
        return ExpectedTextColours(screen).Where(name => !used.Contains(palette[name])).ToArray();
    }

    [Theory]
    [MemberData(nameof(ScreensByTheme))]
    public async Task Screens_RenderWithThePaletteOfTheTheme(string screen, string theme)
    {
        var (shell, fixture) = await BuildAsync(screen);
        using var _ = fixture;
        var canvas = Palette(theme)["canvas"];

        var (corner, violations, texts, missingInk) = await Session.Dispatch(
            () =>
            {
                var window = new MainWindow { DataContext = shell, RequestedThemeVariant = Variant(theme) };
                window.Show();
                UiHarness.Pump(6);
                var result = (Corner(window), PaletteViolations(window, theme), window.GetVisualDescendants().OfType<TextBlock>().Count(t => t.IsEffectivelyVisible), MissingTextColours(window, screen, theme));
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(canvas, corner);
        Assert.Empty(violations);
        Assert.Empty(missingInk);
        Assert.True(texts > 0, "The screen shows no text.");
    }

    /// <summary>The colour of a pixel a few DIPs inside the window corner (the page margin): the window background.</summary>
    private static Color Corner(Window window)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var buffer = frame.Lock();
        var offset = (6 * buffer.RowBytes) + (6 * 4);
        var pixel = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address + offset, pixel, 0, 4);
        return buffer.Format == Avalonia.Platform.PixelFormat.Bgra8888
            ? Color.FromRgb(pixel[2], pixel[1], pixel[0])
            : Color.FromRgb(pixel[0], pixel[1], pixel[2]);
    }

    [Theory]
    [InlineData("Library", "Light", "Dark")]
    [InlineData("Library", "Dark", "Light")]
    [InlineData("SessionTranscript", "Light", "Dark")]
    [InlineData("SessionAsk", "Dark", "Light")]
    [InlineData("Recording", "Light", "Dark")]
    public async Task SwitchingTheThemeAtRuntime_ReResolvesEverythingWithoutRebuildingTheViews(string screen, string from, string to)
    {
        var (shell, fixture) = await BuildAsync(screen);
        using var _ = fixture;

        var (before, after, afterViolations, sameView, missingInk) = await Session.Dispatch(
            () =>
            {
                var window = new MainWindow { DataContext = shell, RequestedThemeVariant = Variant(from) };
                window.Show();
                UiHarness.Pump(6);
                var content = window.GetVisualDescendants().OfType<UserControl>().First();
                var first = Corner(window);

                window.RequestedThemeVariant = Variant(to);
                UiHarness.Pump(6);
                var second = Corner(window);
                var result = (first, second, PaletteViolations(window, to), ReferenceEquals(content, window.GetVisualDescendants().OfType<UserControl>().First()), MissingTextColours(window, screen, to));
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(Palette(from)["canvas"], before);
        Assert.Equal(Palette(to)["canvas"], after);
        Assert.Empty(afterViolations);
        Assert.Empty(missingInk);
        Assert.True(sameView);
    }

    [Fact]
    public async Task Waveform_AndTranscriptText_FollowTheThemeBrushes()
    {
        var (shell, fixture) = await BuildAsync("SessionTranscript");
        using var _ = fixture;

        var (light, dark) = await Session.Dispatch(
            () =>
            {
                var window = new MainWindow { DataContext = shell, RequestedThemeVariant = ThemeVariant.Light };
                window.Show();
                UiHarness.Pump(6);
                var wave = window.GetVisualDescendants().OfType<WaveformControl>().Single();
                var text = window.GetVisualDescendants().OfType<TranscriptTextControl>().First();
                var first = (Wave: ((ISolidColorBrush)wave.WaveBrush!).Color, Played: ((ISolidColorBrush)wave.PlayedBrush!).Color, Text: ((ISolidColorBrush)text.Foreground!).Color);
                window.RequestedThemeVariant = ThemeVariant.Dark;
                UiHarness.Pump(6);
                var second = (Wave: ((ISolidColorBrush)wave.WaveBrush!).Color, Played: ((ISolidColorBrush)wave.PlayedBrush!).Color, Text: ((ISolidColorBrush)text.Foreground!).Color);
                window.Close();
                return (first, second);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(Palette("Light")["dashedBorder"], light.Wave);
        Assert.Equal(Palette("Light")["ink"], light.Played);
        Assert.Equal(Palette("Light")["graphite"], light.Text);
        Assert.Equal(Palette("Dark")["dashedBorder"], dark.Wave);
        Assert.Equal(Palette("Dark")["ink"], dark.Played);
        Assert.Equal(Palette("Dark")["graphite"], dark.Text);
    }

    [Fact]
    public async Task Orb_RebuildsItsBrushes_WhenTheThemeVariantChanges()
    {
        var (first, second, third) = await Session.Dispatch(
            () =>
            {
                var orb = new OrbControl { Width = 240, Height = 240 };
                var window = new Window { Content = orb, RequestedThemeVariant = ThemeVariant.Light, Width = 320, Height = 320 };
                window.Show();
                UiHarness.Pump();
                var a = orb.BodyFill;
                window.RequestedThemeVariant = ThemeVariant.Dark;
                UiHarness.Pump();
                var b = orb.BodyFill;
                window.RequestedThemeVariant = ThemeVariant.Light;
                UiHarness.Pump();
                var c = orb.BodyFill;
                window.Close();
                return (a, b, c);
            },
            TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);
        Assert.NotSame(first, second);
        Assert.NotSame(second, third);
    }

    // ThemeApplier ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ThemePreference.System)]
    [InlineData(ThemePreference.Light)]
    [InlineData(ThemePreference.Dark)]
    public async Task ThemeApplier_AppliesTheStoredThemeAtStartUp(ThemePreference preference)
    {
        var store = new FakeSettingsStore(new AppSettings { Theme = preference });

        var applied = await Session.Dispatch(
            () =>
            {
                var app = Application.Current!;
                var before = app.RequestedThemeVariant;
                try
                {
                    using var applier = new ThemeApplier(app, store, action => action());
                    return app.RequestedThemeVariant;
                }
                finally
                {
                    app.RequestedThemeVariant = before;
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            preference switch { ThemePreference.Light => ThemeVariant.Light, ThemePreference.Dark => ThemeVariant.Dark, _ => ThemeVariant.Default },
            applied);
    }

    [Fact]
    public async Task ThemeApplier_FollowsTheStore_AndSystemMeansFollowTheOs()
    {
        var store = new FakeSettingsStore();

        var seen = await Session.Dispatch(
            async () =>
            {
                var app = Application.Current!;
                var before = app.RequestedThemeVariant;
                try
                {
                    using var applier = new ThemeApplier(app, store, action => action());
                    var list = new List<ThemeVariant?> { app.RequestedThemeVariant };
                    foreach (var theme in new[] { ThemePreference.Dark, ThemePreference.Light, ThemePreference.System })
                    {
                        await store.SaveAsync(store.Current with { Theme = theme }, CancellationToken.None);
                        list.Add(app.RequestedThemeVariant);
                    }

                    return list;
                }
                finally
                {
                    app.RequestedThemeVariant = before;
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal([ThemeVariant.Default, ThemeVariant.Dark, ThemeVariant.Light, ThemeVariant.Default], seen);
    }

    [Fact]
    public async Task ThemeApplier_DoesNotTouchTheApplicationOnTheThreadThatRaisedTheChange()
    {
        // The store raises Changed on a pool thread (JsonSettingsStore after its awaits): the change must be handed to
        // the UI thread (post) instead of being applied on the spot.
        var store = new FakeSettingsStore();

        var (raisedOffThread, before, queued, after) = await Session.Dispatch(
            () =>
            {
                var app = Application.Current!;
                var original = app.RequestedThemeVariant;
                try
                {
                    var posted = new System.Collections.Concurrent.ConcurrentQueue<Action>();
                    using var applier = new ThemeApplier(app, store, posted.Enqueue);
                    var uiThread = Environment.CurrentManagedThreadId;
                    var raisedOn = Task.Run(
                        async () =>
                        {
                            await store.SaveAsync(new AppSettings { Theme = ThemePreference.Dark }, CancellationToken.None);
                            return Environment.CurrentManagedThreadId;
                        }).GetAwaiter().GetResult();

                    var untouched = app.RequestedThemeVariant;
                    var count = posted.Count;
                    while (posted.TryDequeue(out var action))
                    {
                        action();
                    }

                    return (raisedOn != uiThread, untouched, count, app.RequestedThemeVariant);
                }
                finally
                {
                    app.RequestedThemeVariant = original;
                }
            },
            TestContext.Current.CancellationToken);

        Assert.True(raisedOffThread);
        Assert.Equal(ThemeVariant.Default, before);
        Assert.Equal(1, queued);
        Assert.Equal(ThemeVariant.Dark, after);
    }

    [Fact]
    public async Task ThemeApplier_AppliesTheStoredTheme_NotTheOneOfAStaleEvent()
    {
        // Two saves on different threads can deliver their events out of order: the later action must still end on the
        // theme the store holds now (Dark), not on the payload of the event that arrived last (Light).
        var store = new FakeSettingsStore();

        var result = await Session.Dispatch(
            () =>
            {
                var app = Application.Current!;
                var original = app.RequestedThemeVariant;
                try
                {
                    var posted = new Queue<Action>();
                    using var applier = new ThemeApplier(app, store, posted.Enqueue);
                    store.SetCurrentSilently(new AppSettings { Theme = ThemePreference.Dark });
                    store.Raise(new AppSettings { Theme = ThemePreference.Dark });
                    store.Raise(new AppSettings { Theme = ThemePreference.Light });
                    var queued = posted.Count;
                    while (posted.TryDequeue(out var action))
                    {
                        action();
                    }

                    return (queued, app.RequestedThemeVariant);
                }
                finally
                {
                    app.RequestedThemeVariant = original;
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.queued);
        Assert.Equal(ThemeVariant.Dark, result.Item2);
    }

    [Fact]
    public async Task ThemeApplier_Dispose_StopsListening()
    {
        var store = new FakeSettingsStore();

        var variant = await Session.Dispatch(
            async () =>
            {
                var app = Application.Current!;
                var original = app.RequestedThemeVariant;
                try
                {
                    var applier = new ThemeApplier(app, store, action => action());
                    var subscribed = store.SubscriberCount;
                    applier.Dispose();
                    await store.SaveAsync(new AppSettings { Theme = ThemePreference.Dark }, CancellationToken.None);
                    return (subscribed, store.SubscriberCount, app.RequestedThemeVariant);
                }
                finally
                {
                    app.RequestedThemeVariant = original;
                }
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, variant.subscribed);
        Assert.Equal(0, variant.Item2);
        Assert.Equal(ThemeVariant.Default, variant.Item3);
    }

    [Fact]
    public void AppHasNoHardCodedThemeVariant()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoPaths.AppDirectory, "App.axaml"));

        Assert.DoesNotContain("RequestedThemeVariant", xaml, StringComparison.Ordinal);
    }
}
