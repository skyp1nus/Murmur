using Avalonia.Styling;
using Murmur.App.Localization;
using Murmur.App.Tests.Design;
using Murmur.App.Views;

namespace Murmur.App.Tests;

/// <summary>Renders the transcript while it plays with "Follow along" on and off (#56) headlessly into the screenshots directory.</summary>
public sealed class FollowAlongScreenshotTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Light", "en", 1100, 720, true)]
    [InlineData("Dark", "en", 1100, 720, true)]
    [InlineData("Light", "uk", 900, 600, true)]
    [InlineData("Light", "en", 1100, 720, false)]
    public async Task SessionTranscript_WhilePlaying_WritesPng(string theme, string language, int width, int height, bool follow)
    {
        using var culture = UiCulture.Override(language);
        using var rig = new DesignRig();
        await rig.Shell.Library.LoadAsync();
        rig.Shell.OpenSession(rig.ClientDemo);
        await rig.Shell.SessionLoadTask;
        var session = rig.Shell.Session!;
        session.ShowFollowAlong = true;
        session.FollowAlong = follow;
        var path = Path.Combine(RepoPaths.ScreenshotsDirectory, $"follow-along-{(follow ? "on" : "off")}-{theme.ToLowerInvariant()}-{language}-{width}x{height}.png");

        await UiHarness.Session.Dispatch(
            () =>
            {
                var window = new MainWindow { DataContext = rig.Shell, Width = width, Height = height, RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light };
                window.Show();
                UiHarness.Pump();
                session.PlayPauseCommand.Execute(null);
                session.SeekTo(DesignDemoData.SessionPosition + TimeSpan.FromSeconds(3));
                session.Tick();
                UiHarness.Pump(6);
                UiHarness.Save(window, path);
                window.Close();
            },
            Ct);

        Assert.True(new FileInfo(path).Length > 10_000, "Screenshot looks empty.");
    }
}
