using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Murmur.App.Controls;
using Murmur.App.Localization;
using Murmur.App.Services;
using Murmur.App.Tests.Design;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Transcription;

namespace Murmur.App.Tests;

/// <summary>
/// Renders the uncertain words of #58 headlessly into the screenshots directory: the demo transcript with "semantic." and "reports."
/// doubted, and "Did you mean" open on "reports." (the model's ideas and "Keep").
/// </summary>
[Trait("Category", "Slow")]
public sealed class UncertainWordsScreenshotTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Light", "en", 1100, 720)]
    [InlineData("Dark", "en", 1100, 720)]
    [InlineData("Light", "uk", 900, 600)]
    [InlineData("Dark", "uk", 900, 600)]
    public async Task SessionTranscript_WithDidYouMeanOpen_WritesPng(string theme, string language, int width, int height)
    {
        using var culture = UiCulture.Override(language);
        using var rig = new DesignRig();
        var record = rig.ClientDemo;
        using var fixture = new SessionFixture(duration: record.Duration, record: record);
        var transcript = DesignRig.BuildTranscript(record.Id);
        fixture.UseTranscript(transcript with { Segments = [.. transcript.Segments.Select(Doubt)] });
        var suggester = new FakeWordSuggester { Immediate = ["exports", "records"] };
        var vocabulary = new FakeVocabularyStore("reporting");
        var harness = new UiHarness();
        harness.Store.Add(record);
        var shell = harness.CreateShell(
            r => new SessionViewModel(r, fixture.Files, fixture.Transcripts, fixture.Player, fixture.Processing, fixture.Time, action => action(),
                services: new SessionServices(Vocabulary: vocabulary, WordSuggester: suggester)),
            fixture.Processing);
        await shell.Library.LoadAsync();
        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;
        var path = Path.Combine(RepoPaths.ScreenshotsDirectory, $"uncertain-words-{theme.ToLowerInvariant()}-{language}-{width}x{height}.png");

        await UiHarness.Session.Dispatch(
            async () =>
            {
                using var scope = UiCulture.Override(language);
                var window = new MainWindow { DataContext = shell, Width = width, Height = height, RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light };
                window.Show();
                UiHarness.Pump(4);
                var line = window.GetVisualDescendants().OfType<TranscriptTextControl>().Single(c => c.UncertainWords is { } flags && flags[^1]);
                var word = line.WordBounds(line.UncertainWords!.Length - 1)!.Value;
                var at = line.TranslatePoint(word.Center, window)!.Value;
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                await shell.Session!.LastSuggestions;
                UiHarness.Pump(8);
                Assert.Contains(window.GetVisualDescendants().OfType<Popup>(), p => p.IsOpen);
                UiHarness.Save(window, path);
                window.Close();
            },
            Ct);

        Assert.True(new FileInfo(path).Length > 10_000, "Screenshot looks empty.");
    }

    private static TranscriptSegment Doubt(TranscriptSegment segment)
    {
        var words = segment.Words.Select(w => w.Text is "semantic." or "reports." ? w with { Confidence = 0.31f } : w with { Confidence = 0.93f }).ToArray();
        return segment with { Words = words };
    }
}
