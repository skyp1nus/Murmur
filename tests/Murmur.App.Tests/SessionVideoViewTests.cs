using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Murmur.App.Controls;
using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

/// <summary>The session view with the screen recording: panel, picture, placeholder and chip in both themes and languages.</summary>
public sealed class SessionVideoViewTests
{
    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private static TimeSpan M(double minutes) => TimeSpan.FromMinutes(minutes);

    /// <summary>Screenshots go where the architect asked for them (set <c>MURMUR_SCREENSHOT_DIR</c>); they are not tracked files.</summary>
    private static void Screenshot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("MURMUR_SCREENSHOT_DIR") is { Length: > 0 } directory)
        {
            UiHarness.Save(window, Path.Combine(directory, name));
        }
    }

    public static TheoryData<string, string> ThemesAndLanguages() => new()
    {
        { "Light", "uk" },
        { "Dark", "uk" },
        { "Light", "en" },
        { "Dark", "en" },
    };

    private static async Task<(ShellViewModel Shell, FakeVideoPlayer Video)> OpenAsync(SessionFixture fixture, bool withVideo, bool firstFileMissing = false)
    {
        fixture.UseShortTranscript();
        fixture.WritePeaks(SessionScreenshotTests.FakePeaks(23 * 60));
        ScreenSegment[] segments = [new(0, TimeSpan.Zero, M(10)), new(1, M(15), M(8))];
        var record = fixture.Record with { Duration = M(23), ScreenSegments = withVideo ? segments : [] };
        fixture.Player.Duration = M(23);
        foreach (var segment in segments.Where(x => !(firstFileMissing && x.Index == 0)))
        {
            File.WriteAllBytes(fixture.Files.ScreenSegment(segment.Index), [0]);
        }

        var video = new FakeVideoPlayer(M(10)) { Color = 0xFF3A5FCD };
        var harness = new UiHarness();
        harness.Store.Add(record);
        var shell = harness.CreateShell(
            r => new SessionViewModel(r, fixture.Files, fixture.Transcripts, fixture.Player, fixture.Processing, fixture.Time, action => action(), videoPlayback: new FakeVideoPlayback(video)),
            fixture.Processing);
        await shell.Library.LoadAsync();
        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;
        return (shell, video);
    }

    private static T Find<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [Theory]
    [MemberData(nameof(ThemesAndLanguages))]
    public async Task TheSessionView_ShowsThePictureAndTheChip_AndThePlaceholderInTheGap(string theme, string language)
    {
        using var fixture = new SessionFixture(duration: M(23));

        var result = await Session.Dispatch(
            async () =>
            {
                using var scope = UiCulture.Override(language);
                var (shell, video) = await OpenAsync(fixture, withVideo: true);
                var window = new MainWindow { DataContext = shell, RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light };
                window.Show();
                UiHarness.Pump(4);
                var view = window.GetVisualDescendants().OfType<SessionView>().Single();
                var panel = Find<Border>(view, "VideoPanel");
                var surface = Find<VideoSurface>(view, "Video");
                var chip = Find<Panel>(view, "VideoChip");
                var placeholder = Find<StackPanel>(view, "VideoPlaceholder");

                // At the start: segment 0 is open and shows the first picture.
                surface.CopyFrame();
                shell.Session!.SeekTo(M(5));
                surface.CopyFrame();
                UiHarness.Pump(3);
                var centre = surface.TranslatePoint(new Point(surface.Bounds.Width / 2, surface.Bounds.Height / 2), window)!.Value;
                var open = (
                    panel.IsEffectivelyVisible,
                    chip.IsEffectivelyVisible,
                    placeholder.IsEffectivelyVisible,
                    chip.GetVisualDescendants().OfType<TextBlock>().Single().Text,
                    panel.Bounds.Size,
                    Pixel(window, (int)centre.X, (int)centre.Y),
                    video.Position);
                Screenshot(window, $"session-video-{theme.ToLowerInvariant()}-{language}.png");

                // In the gap between the segments the screen was off.
                shell.Session!.SeekTo(M(12));
                UiHarness.Pump(3);
                var gap = (
                    chip.IsEffectivelyVisible,
                    placeholder.IsEffectivelyVisible,
                    placeholder.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray(),
                    placeholder.GetVisualDescendants().OfType<IconControl>().Count());
                Screenshot(window, $"session-video-gap-{theme.ToLowerInvariant()}-{language}.png");
                window.Close();
                return (open, gap);
            },
            TestContext.Current.CancellationToken);

        var (open2, gap2) = result;
        Assert.True(open2.Item1);
        Assert.True(open2.Item2);
        Assert.False(open2.Item3);
        Assert.Equal("1920×1080 · H.264", open2.Item4);
        Assert.Equal(9.0 / 16.0, open2.Item5.Height / open2.Item5.Width, 2);
        Assert.Equal((0x3A, 0x5F, 0xCD), open2.Item6);
        Assert.Equal(M(5), open2.Item7);
        Assert.False(gap2.Item1);
        Assert.True(gap2.Item2);
        Assert.Equal([language == "en" ? "Screen off" : "Екран вимкнено"], gap2.Item3);
        Assert.Equal(1, gap2.Item4);
    }

    [Theory]
    [InlineData("uk", "Не вдалося відтворити відео цього фрагмента")]
    [InlineData("en", "This part of the screen recording cannot be played")]
    public async Task AnUnplayableSegment_ShowsTheNoteInTheChosenLanguage(string language, string expected)
    {
        using var fixture = new SessionFixture(duration: M(23));

        var texts = await Session.Dispatch(
            async () =>
            {
                using var scope = UiCulture.Override(language);
                var (shell, _) = await OpenAsync(fixture, withVideo: true, firstFileMissing: true); // segment 0 can never be opened
                var window = UiHarness.Show(shell);
                var view = window.GetVisualDescendants().OfType<SessionView>().Single();
                var result = Find<StackPanel>(view, "VideoPlaceholder").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
                Screenshot(window, $"session-video-unplayable-{language}.png");
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal([expected], texts);
    }

    [Fact]
    public async Task WithoutVideo_TheSessionViewLooksAsBefore()
    {
        using var fixture = new SessionFixture(duration: M(23));

        var (panel, playerWidth) = await Session.Dispatch(
            async () =>
            {
                var (shell, _) = await OpenAsync(fixture, withVideo: false);
                var window = UiHarness.Show(shell);
                var view = window.GetVisualDescendants().OfType<SessionView>().Single();
                var result = (Find<Border>(view, "VideoPanel").IsEffectivelyVisible, Find<StackPanel>(view, "VideoPlaceholder").IsEffectivelyVisible);
                var column = view.GetVisualDescendants().OfType<StackPanel>().First(p => p.Classes.Contains("player"));
                var width = column.Bounds.Width;
                window.Close();
                return (result.Item1 || result.Item2, width);
            },
            TestContext.Current.CancellationToken);

        Assert.False(panel);
        Assert.Equal(340, playerWidth);
    }

    private static (int R, int G, int B) Pixel(Window window, int x, int y)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var buffer = frame.Lock();
        var pixel = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address + (y * buffer.RowBytes) + (x * 4), pixel, 0, 4);
        return buffer.Format == Avalonia.Platform.PixelFormat.Bgra8888 ? (pixel[2], pixel[1], pixel[0]) : (pixel[0], pixel[1], pixel[2]);
    }
}
