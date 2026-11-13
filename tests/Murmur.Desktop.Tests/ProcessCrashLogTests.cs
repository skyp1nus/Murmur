using Murmur.Core.Diagnostics;
using Murmur.Core.Settings;
using Murmur.Core.Tests.Fakes;

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
