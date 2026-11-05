using Avalonia.Styling;
using Murmur.App.Localization;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Recording;

namespace Murmur.App.Tests;

/// <summary>Renders the Mark button, the feedback chip and the failed-save Library notice (#45) headlessly into the screenshots directory.</summary>
public sealed class RecordingMarkScreenshotTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Light", 1100, 720)]
    [InlineData("Dark", 1100, 720)]
    [InlineData("Light", 900, 600)]
    [InlineData("Dark", 900, 600)]
    public async Task Recording_WithTheMarkButtonAndChip_WritesPng(string theme, int width, int height)
    {
        using var english = UiCulture.Override("en");
        var harness = new UiHarness();
        using var marks = new RecordingMarks(harness.Recorder, new FakeBookmarkStore());
        var vm = new RecordingViewModel(harness.Recorder, harness.Capture, harness.Store, action => action(), time: harness.Time, marks: marks);
        var shell = new ShellViewModel(harness.CreateShell().Library, vm);
        shell.ShowRecording();
        var path = Path.Combine(RepoPaths.ScreenshotsDirectory, $"recording-mark-{theme.ToLowerInvariant()}-{width}x{height}.png");

        await UiHarness.Session.Dispatch(
            async () =>
            {
                var window = new MainWindow { DataContext = shell, Width = width, Height = height, RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light };
                window.Show();
                UiHarness.Pump();
                await vm.StartStopAsync();
                vm.MarkCommand.Execute(null);
                UiHarness.Pump(6);
                UiHarness.Save(window, path);
                window.Close();
            },
            Ct);

        Assert.True(new FileInfo(path).Length > 10_000, "Screenshot looks empty.");
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task Library_WithTheMarksNotSavedNotice_WritesPng(string theme)
    {
        using var english = UiCulture.Override("en");
        var harness = new UiHarness();
        var bookmarks = new FakeBookmarkStore { Failure = new IOException("database is locked") };
        using var marks = new RecordingMarks(harness.Recorder, bookmarks);
        var vm = new RecordingViewModel(harness.Recorder, harness.Capture, harness.Store, action => action(), time: harness.Time, marks: marks);
        var shell = new ShellViewModel(harness.CreateShell().Library, vm, post: action => action());
        await shell.Library.LoadAsync();
        await vm.StartStopAsync();
        vm.MarkCommand.Execute(null);
        await vm.StartStopAsync();
        await marks.PendingFlush;
        var path = Path.Combine(RepoPaths.ScreenshotsDirectory, $"library-marks-notice-{theme.ToLowerInvariant()}-900x600.png");

        await UiHarness.Session.Dispatch(
            () =>
            {
                var window = new MainWindow { DataContext = shell, Width = 900, Height = 600, RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light };
                window.Show();
                UiHarness.Pump(6);
                UiHarness.Save(window, path);
                window.Close();
            },
            Ct);

        Assert.True(shell.Library.HasNotice);
        Assert.True(new FileInfo(path).Length > 10_000, "Screenshot looks empty.");
    }
}
