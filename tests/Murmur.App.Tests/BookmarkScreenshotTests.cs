using Avalonia.Styling;
using Murmur.App.Localization;
using Murmur.App.Tests.Design;
using Murmur.App.Tests.Fakes;
using Murmur.App.Views;

namespace Murmur.App.Tests;

/// <summary>Renders the recording with bookmarks (#59): the flags over the waveform, the Bookmarks card and the pills in the transcript, headlessly into the screenshots directory.</summary>
public sealed class BookmarkScreenshotTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Light", "en", 1100, 720, true)]
    [InlineData("Dark", "en", 1100, 720, true)]
    [InlineData("Light", "uk", 900, 600, true)]
    [InlineData("Dark", "en", 900, 600, true)]
    [InlineData("Light", "en", 1100, 720, false)]
    public async Task SessionWithBookmarks_WritesPng(string theme, string language, int width, int height, bool withBookmarks)
    {
        using var culture = UiCulture.Override(language);
        using var rig = new DesignRig { Bookmarks = new FakeBookmarkStore() };
        await rig.Shell.Library.LoadAsync();
        if (withBookmarks)
        {
            rig.Bookmarks!.Put(rig.ClientDemo.Id, (761, "Pricing question - follow up"), (1205, "Demo date"), (1500, ""));
        }

        rig.Shell.OpenSession(rig.ClientDemo);
        await rig.Shell.SessionLoadTask;
        var session = rig.Shell.Session!;
        session.ShowFollowAlong = true;
        var path = Path.Combine(RepoPaths.ScreenshotsDirectory, $"bookmarks-{(withBookmarks ? "list" : "empty")}-{theme.ToLowerInvariant()}-{language}-{width}x{height}.png");

        await UiHarness.Session.Dispatch(
            () =>
            {
                var window = new MainWindow { DataContext = rig.Shell, Width = width, Height = height, RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light };
                window.Show();
                UiHarness.Pump();
                session.SeekTo(DesignDemoData.SessionPosition);
                if (withBookmarks)
                {
                    session.PlayPauseCommand.Execute(null);
                    session.Tick();
                }

                UiHarness.Pump(6);
                UiHarness.Save(window, path);
                window.Close();
            },
            Ct);

        Assert.True(new FileInfo(path).Length > 10_000, "Screenshot looks empty.");
    }
}
