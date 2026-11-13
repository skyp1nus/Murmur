using Murmur.Core.Diagnostics;
using Murmur.Core.Settings;
using Murmur.Core.Tests.Fakes;
using Murmur.Storage;

namespace Murmur.Desktop.Tests;

public sealed class ProcessCrashLogTests
{
    [Fact]
    public void AnUnhandledException_IsACrash()
    {
        var log = new RecordingAppLog();
        var error = new InvalidOperationException("boom");

        ProcessCrashLog.OnUnhandled(log, error);
        ProcessCrashLog.OnUnhandled(log, "not an exception");

        Assert.Equal(2, log.Crashes.Count);
        Assert.Same(error, log.Crashes[0].Exception);
        Assert.Equal("app", log.Crashes[0].Area);
        Assert.IsType<ProcessCrashLog.NonExceptionThrown>(log.Crashes[1].Exception);
        Assert.DoesNotContain("not an exception", log.Crashes[1].Exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnobservedTaskFailure_IsAnErrorLine_AndCountsAsObserved()
    {
        var log = new RecordingAppLog();
        var args = new UnobservedTaskExceptionEventArgs(new AggregateException(new IOException("disk", unchecked((int)0x80070070))));

        ProcessCrashLog.OnUnobserved(log, args);

        Assert.True(args.Observed);
        var line = Assert.Single(log.Events);
        Assert.Equal((LogLevel.Error, "app", "task.unobserved", "IOException 0x80070070"), line);
        Assert.Empty(log.Crashes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true, "--startup-probe", "probe.json")]
    [InlineData(true, "--autorecord", "60", "--perf-out", "perf.json")]
    [InlineData(true, "--probe-open-latest")]
    [InlineData(true, "--probe-crash")]
    public void ASecondStartOrADiagnosticRun_LeavesTheUnseenCrashForTheUser(bool primary, params string[] args)
    {
        var paths = CrashedDataFolder(out var root);
        try
        {
            using (var run = new FileAppLog(paths, TimeProvider.System))
            {
                Assert.NotNull(run.UnseenCrash);
                run.Write(LogLevel.Warning, "test", "pending");
                ProcessCrashLog.OnExit(run, args, primary);
            }

            using var next = new FileAppLog(paths, TimeProvider.System);
            Assert.NotNull(next.UnseenCrash);
            Assert.Contains(Directory.GetFiles(paths.LogsDirectory, "murmur-*.log"), f => File.ReadAllText(f).Contains(" test pending", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TheUsersOwnCleanExit_MarksTheCrashSeen()
    {
        var paths = CrashedDataFolder(out var root);
        try
        {
            using (var run = new FileAppLog(paths, TimeProvider.System))
            {
                ProcessCrashLog.OnExit(run, [], isPrimary: true);
            }

            using var next = new FileAppLog(paths, TimeProvider.System);
            Assert.Null(next.UnseenCrash);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static StoragePaths CrashedDataFolder(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "murmur-desktop-exit-" + Guid.NewGuid().ToString("N"));
        var paths = new StoragePaths(root);
        using var crashed = new FileAppLog(paths, TimeProvider.System);
        crashed.Crash(new InvalidOperationException("earlier run"), "app");
        return paths;
    }

    [Fact]
    public void TheDiagnosticContext_NamesTheVersionAndTheMainFlags_NeverTheFolder()
    {
        var context = new DiagnosticContext();
        Assert.StartsWith("App: Murmur " + Murmur.App.BuildInfo.Version, context.Describe(), StringComparison.Ordinal);

        context.Settings = new FixedSettings(new AppSettings { Language = "uk", Theme = ThemePreference.Dark, RecordingsDirectory = @"D:\Meetings of Olena", DetectCalls = false });
        var text = context.Describe();

        Assert.Contains("language=uk", text, StringComparison.Ordinal);
        Assert.Contains("theme=Dark", text, StringComparison.Ordinal);
        Assert.Contains("detectCalls=False", text, StringComparison.Ordinal);
        Assert.Contains("customFolder=True", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Olena", text, StringComparison.Ordinal);
    }

    private sealed class FixedSettings(AppSettings current) : ISettingsStore
    {
        public AppSettings Current => current;

        public event Action<AppSettings>? Changed
        {
            add { }
            remove { }
        }

        public ValueTask SaveAsync(AppSettings settings, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
