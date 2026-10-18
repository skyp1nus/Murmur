using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Murmur.App.ViewModels;
using Murmur.App.Views;

namespace Murmur.App.Tests;

public sealed class MainWindowTests
{
    private static HeadlessUnitTestSession Session =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(MainWindowTests).Assembly);

    [Fact]
    public async Task MainWindow_HasLibraryShellSizingAndCopy()
    {
        var snapshot = await Session.Dispatch(
            () =>
            {
                var window = new MainWindow { DataContext = new MainWindowViewModel() };
                window.Show();
                var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
                var buttons = window.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string).ToArray();
                var search = window.GetVisualDescendants().OfType<TextBox>().Single();
                var result = (window.Title, window.Width, window.Height, window.MinWidth, window.MinHeight, texts, buttons, search.PlaceholderText);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("Murmur", snapshot.Title);
        Assert.Equal(1200, snapshot.Width);
        Assert.Equal(780, snapshot.Height);
        Assert.Equal(900, snapshot.MinWidth);
        Assert.Equal(600, snapshot.MinHeight);
        Assert.Contains("Murmur", snapshot.texts);
        Assert.Contains("Ще немає записів", snapshot.texts);
        Assert.Contains("00:00:00", snapshot.texts);
        Assert.Contains("Натисни «Почати запис», щоб записати перший мітинг. Усе лишається на твоєму компʼютері.", snapshot.texts);
        Assert.Equal(["Почати запис", "Налаштування"], snapshot.buttons);
        Assert.Equal("Пошук по записах", snapshot.PlaceholderText);
    }

    [Fact]
    public async Task TypographyClasses_ResolveTokens()
    {
        var (display, mono, primaryRadius, cardRadius) = await Session.Dispatch(
            () =>
            {
                var window = new MainWindow { DataContext = new MainWindowViewModel() };
                window.Show();
                var heading = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("heading"));
                var timer = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("mono"));
                var primary = window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("primary"));
                var card = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("card"));
                var result = (
                    (heading.FontWeight, heading.FontSize, heading.LetterSpacing),
                    (timer.FontFamily.Name, timer.FontSize, timer.LineHeight),
                    primary.CornerRadius,
                    card.CornerRadius);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(FontWeight.Light, display.FontWeight);
        Assert.Equal(36, display.FontSize);
        Assert.Equal(-0.72, display.LetterSpacing, 3);
        Assert.Equal("Geist Mono", mono.Name);
        Assert.Equal(13, mono.FontSize);
        Assert.Equal(21.97, mono.LineHeight, 3);
        Assert.Equal(new CornerRadius(9999), primaryRadius);
        Assert.Equal(new CornerRadius(20), cardRadius);
    }

    [Fact]
    public async Task InterLight_IsARealLightFace_NotAFallback()
    {
        var (light, regular, lightWidth, regularWidth) = await Session.Dispatch(
            () =>
            {
                var fonts = FontManager.Current;
                var family = new FontFamily("fonts:Inter#Inter");
                Assert.True(fonts.TryGetGlyphTypeface(new Typeface(family, FontStyle.Normal, FontWeight.Light), out var lightFace));
                Assert.True(fonts.TryGetGlyphTypeface(new Typeface(family, FontStyle.Normal, FontWeight.Normal), out var regularFace));
                return (lightFace!.Weight, regularFace!.Weight, MeasureWidth(family, FontWeight.Light), MeasureWidth(family, FontWeight.Normal));
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(FontWeight.Light, light);
        Assert.Equal(FontWeight.Normal, regular);
        Assert.True(lightWidth < regularWidth, $"Light ({lightWidth}) should be narrower than Regular ({regularWidth}).");
    }

    [Fact]
    public async Task RenderScreenshot_WritesPhase0Png()
    {
        Directory.CreateDirectory(RepoPaths.ScreenshotsDirectory);
        var path = Path.Combine(RepoPaths.ScreenshotsDirectory, "phase0-main.png");

        await Session.Dispatch(
            () =>
            {
                var window = new MainWindow { DataContext = new MainWindowViewModel() };
                window.Show();
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(path, new PngBitmapEncoderOptions());
                window.Close();
            },
            TestContext.Current.CancellationToken);

        Assert.True(new FileInfo(path).Length > 10_000, "Screenshot looks empty.");
    }

    private static double MeasureWidth(FontFamily family, FontWeight weight)
    {
        var text = new TextBlock
        {
            Text = "Murmur records meetings locally",
            FontFamily = family,
            FontWeight = weight,
            FontSize = 36,
        };
        text.Measure(Size.Infinity);
        return text.DesiredSize.Width;
    }
}
