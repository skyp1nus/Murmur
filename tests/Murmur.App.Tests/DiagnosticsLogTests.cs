using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Murmur.App.Diagnostics;
using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Diagnostics;
using Murmur.Core.Settings;
using Murmur.Core.Tests.Fakes;
using FakeRecorder = Murmur.App.Tests.Fakes.FakeRecorder; // TestSupport has a FakeRecorder of its own (the Core one)
using Murmur.Storage;

namespace Murmur.App.Tests;

/// <summary>The diagnostic log in the UI (#100): the Library's crash notice and the two buttons under Settings -> About.</summary>
public sealed class DiagnosticsLogTests : IDisposable
{
    private static readonly string LogsDir = Path.Combine(Path.GetTempPath(), "murmur-app-logs-" + Guid.NewGuid().ToString("N"));
    private static readonly string CrashFile = Path.Combine(LogsDir, "crash-20261113-120000-000.txt");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(LogsDir))
        {
            Directory.Delete(LogsDir, recursive: true);
        }
    }

    // ---- The crash notice -------------------------------------------------------------------------------------------------

    [Fact]
    public void CrashNotice_ShowsForAnUnseenCrash_OpensTheFile_AndDismissMarksItSeen()
    {
        var log = new RecordingAppLog(LogsDir, CrashFile);
        var launcher = new FakeFolderLauncher();
        var notice = new CrashNoticeViewModel(log, launcher);

        Assert.True(notice.IsVisible);

        notice.OpenLogCommand.Execute(null);
        Assert.Equal([CrashFile], launcher.Opened);
        Assert.True(notice.IsVisible); // opening is not dismissing

        notice.DismissCommand.Execute(null);
        Assert.False(notice.IsVisible);
        Assert.Equal(1, log.CrashSeenCalls);
    }

    [Fact]
    public void CrashNotice_OpensTheFolder_WhenTheFileCannotBeOpened()
    {
        var launcher = new FakeFolderLauncher { Failure = "no app for .txt" };
        var notice = new CrashNoticeViewModel(new RecordingAppLog(LogsDir, CrashFile), launcher);

        notice.OpenLogCommand.Execute(null);

        Assert.Equal([CrashFile, LogsDir], launcher.Opened);
    }

    [Fact]
    public void CrashNotice_NeverShows_WithoutACrash_OrWithTheNullLog()
    {
        Assert.False(new CrashNoticeViewModel(new RecordingAppLog(LogsDir)).IsVisible);
        Assert.False(new CrashNoticeViewModel().IsVisible);
        Assert.False(new CrashNoticeViewModel(NullAppLog.Instance).IsVisible);
    }

    [Fact]
    public void TheShell_HandsTheLogToTheLibraryAndTheSettings()
    {
        var log = new RecordingAppLog(LogsDir, CrashFile);
        var services = new AppServices(new FakeRecorder(), new FakeDisplayCapture(), new FakeSessionStore(), new TestTime(UiHarness.Now), Settings: new FakeSettingsStore(), Log: log);

        var shell = ShellViewModel.Create(services);
        var without = ShellViewModel.Create(new AppServices(new FakeRecorder(), new FakeDisplayCapture(), new FakeSessionStore(), new TestTime(UiHarness.Now), Settings: new FakeSettingsStore()));

        Assert.True(shell.Library.CrashNotice.IsVisible);
        Assert.True(shell.Settings!.About.HasDiagnostics);
        Assert.False(without.Library.CrashNotice.IsVisible);
        Assert.False(without.Settings!.About.HasDiagnostics);
        Assert.Same(NullAppLog.Instance, new AppServices(new FakeRecorder(), new FakeDisplayCapture(), new FakeSessionStore(), TimeProvider.System).AppLog);
    }

    [Fact]
    public async Task TheLibrary_ShowsTheCrashNoticeOfAFileLog_AtTheTopOfTheSmallestWindow_AndDismissHidesItForGood()
    {
        using var english = UiCulture.Override("en");
        var root = Path.Combine(LogsDir, "data");
        var paths = new StoragePaths(root);
        using (var crashed = new FileAppLog(paths, TimeProvider.System))
        {
            crashed.Crash(new InvalidOperationException("previous run"), "app");
        }

        using var log = new FileAppLog(paths, TimeProvider.System);
        var harness = new UiHarness();
        harness.SeedSessions();
        var shell = new ShellViewModel(new LibraryViewModel(harness.Store, harness.Time, post: action => action(), log: log, launcher: new FakeFolderLauncher()), new RecordingViewModel(harness.Recorder, harness.Capture, harness.Store, action => action()));
        await shell.Library.LoadAsync();

        var (notice, texts, dismissBounds) = await UiHarness.Session.Dispatch(
            () =>
            {
                var window = new MainWindow { DataContext = shell, Width = 900, Height = 600 };
                window.Show();
                UiHarness.Pump();
                var border = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "CrashNotice");
                var visibleTexts = border.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
                var dismiss = border.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CrashNoticeDismiss");
                var bounds = (new Rect(border.TranslatePoint(default, window) ?? default, border.Bounds.Size), new Rect(dismiss.TranslatePoint(default, window) ?? default, dismiss.Bounds.Size));
                dismiss.Command!.Execute(null);
                UiHarness.Pump();
                var stillVisible = border.IsVisible;
                window.Close();
                return (bounds.Item1, visibleTexts, (bounds.Item2, stillVisible));
            },
            Ct);

        Assert.True(notice.Top < 100, $"the notice is at the top of the page: {notice}");
        Assert.Contains(Strings.Library_CrashNotice, texts);
        Assert.Contains(Strings.Library_OpenCrashLog, texts);
        Assert.True(dismissBounds.Item1.Right <= 900 && dismissBounds.Item1.Bottom <= 600, "Dismiss is inside the window");
        Assert.False(dismissBounds.stillVisible);

        using var next = new FileAppLog(paths, TimeProvider.System);
        Assert.Null(next.UnseenCrash);
    }

    [Fact]
    public async Task TheLibrary_HasNoCrashNotice_WithoutALog()
    {
        var harness = new UiHarness();
        var shell = harness.CreateShell();
        await shell.Library.LoadAsync();

        var height = await UiHarness.Session.Dispatch(
            () =>
            {
                var window = new MainWindow { DataContext = shell, Width = 900, Height = 600 };
                window.Show();
                UiHarness.Pump();
                var border = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "CrashNotice");
                var result = border.IsEffectivelyVisible ? border.Bounds.Height : 0;
                window.Close();
                return result;
            },
            Ct);

        Assert.Equal(0, height);
    }

    // ---- Settings -> About --------------------------------------------------------------------------------------------------

    [Fact]
    public void About_HasNoDiagnostics_WithTheNullLog()
    {
        var about = new AboutViewModel();

        Assert.False(about.HasDiagnostics);
        about.OpenLogsFolderCommand.Execute(null);
        Assert.Null(about.DiagnosticsStatus);
    }

    [Fact]
    public void About_OpenLogsFolder_CreatesAndOpensIt_AndSaysWhenItCannot()
    {
        using var english = UiCulture.Override("en");
        var launcher = new FakeFolderLauncher();
        var about = new AboutViewModel(log: new RecordingAppLog(LogsDir), launcher: launcher);
        Assert.True(about.HasDiagnostics);

        about.OpenLogsFolderCommand.Execute(null);

        Assert.True(Directory.Exists(LogsDir));
        Assert.Equal([LogsDir], launcher.Opened);
        Assert.Null(about.DiagnosticsStatus);

        launcher.Failure = "xdg-open is missing";
        about.OpenLogsFolderCommand.Execute(null);
        Assert.Equal("Could not open the folder: xdg-open is missing", about.DiagnosticsStatus);
    }

    [Fact]
    public async Task About_CopyDiagnostics_PutsTheSnapshotOnTheClipboard_AndSaysCopiedForTwoSeconds()
    {
        using var english = UiCulture.Override("en");
        var time = new TestTime(UiHarness.Now);
        var log = new RecordingAppLog(LogsDir) { SnapshotText = "Murmur diagnostics\nRecent events" };
        var about = new AboutViewModel(log: log, time: time);
        var copied = new List<string>();
        about.ClipboardWriter = text =>
        {
            copied.Add(text);
            return Task.FromResult(true);
        };

        await about.CopyDiagnosticsCommand.ExecuteAsync(null);

        Assert.Equal(["Murmur diagnostics\nRecent events"], copied);
        Assert.Equal("Copied", about.DiagnosticsStatus);
        Assert.True(about.HasDiagnosticsStatus);

        time.Advance(AboutViewModel.CopiedFor);
        Assert.True(SpinWait.SpinUntil(() => about.DiagnosticsStatus is null, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task About_CopyDiagnostics_WithoutAClipboard_SaysSo()
    {
        using var english = UiCulture.Override("en");
        var about = new AboutViewModel(log: new RecordingAppLog(LogsDir));

        await about.CopyDiagnosticsCommand.ExecuteAsync(null);

        Assert.Equal("Could not copy the diagnostics: the clipboard is not available", about.DiagnosticsStatus);

        about.ClipboardWriter = _ => Task.FromResult(false);
        await about.CopyDiagnosticsCommand.ExecuteAsync(null);
        Assert.Equal("Could not copy the diagnostics: the clipboard is not available", about.DiagnosticsStatus);
    }

    [Theory]
    [InlineData("en", "Open logs folder", "Copy diagnostics", "Murmur closed unexpectedly last time.", "Open log")]
    [InlineData("uk", "Відкрити теку журналів", "Копіювати діагностику", "Минулого разу Murmur закрився неочікувано.", "Відкрити журнал")]
    public void TheTexts_AreTranslated(string language, string openLogs, string copy, string notice, string openLog)
    {
        using var scope = UiCulture.Override(language);

        Assert.Equal(openLogs, Strings.About_OpenLogs);
        Assert.Equal(copy, Strings.About_CopyDiagnostics);
        Assert.Equal(notice, Strings.Library_CrashNotice);
        Assert.Equal(openLog, Strings.Library_OpenCrashLog);
    }

    [Fact]
    public async Task TheSettingsPage_ShowsTheDiagnosticsButtons_OnlyWithARealLog()
    {
        using var english = UiCulture.Override("en");
        var (withLog, withoutLog) = await UiHarness.Session.Dispatch(
            () => (Visible(new RecordingAppLog(LogsDir)), Visible(null)),
            Ct);

        Assert.True(withLog);
        Assert.False(withoutLog);

        static bool Visible(IAppLog? log)
        {
            var settings = new SettingsViewModel(new FakeSettingsStore(new AppSettings { OnboardingCompleted = true }), new FakeDisplayCapture(), post: action => action(), log: log);
            var harness = new UiHarness();
            var shell = new ShellViewModel(harness.CreateShell().Library, new RecordingViewModel(harness.Recorder, harness.Capture, harness.Store, action => action()), settings: settings);
            shell.ShowSettings();
            var window = new MainWindow { DataContext = shell, Height = 2800 };
            window.Show();
            UiHarness.Pump(6);
            var row = window.GetVisualDescendants().OfType<WrapPanel>().Single(p => p.Name == "AboutDiagnostics");
            var visible = row.IsEffectivelyVisible && row.Bounds.Height > 0;
            Assert.NotNull(settings.About.ClipboardWriter); // the view hands over the window's clipboard
            window.Close();
            return visible;
        }
    }

    [Fact]
    public void CrashProbe_IsAHiddenFlag()
    {
        Assert.True(CrashProbe.IsRequested(["--probe-crash"]));
        Assert.False(CrashProbe.IsRequested(["--startup-probe", "x.json"]));
        Assert.Contains(CrashProbe.Flag, new CrashProbeException().Message, StringComparison.Ordinal);
    }
}
