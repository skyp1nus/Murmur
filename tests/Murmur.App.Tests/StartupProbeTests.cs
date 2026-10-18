using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Threading;
using Murmur.App.Diagnostics;
using Murmur.App.ViewModels;
using Murmur.App.Views;

namespace Murmur.App.Tests;

public sealed class StartupProbeTests
{
    [Fact]
    public void TryGetPath_FindsPathAfterFlag()
    {
        Assert.True(StartupProbe.TryGetPath(["--x", "--startup-probe", "/tmp/out.json"], out var path));
        Assert.Equal("/tmp/out.json", path);
    }

    [Theory]
    [InlineData]
    [InlineData("--startup-probe")]
    [InlineData("--other", "/tmp/out.json")]
    public void TryGetPath_WithoutFlagOrValue_ReturnsFalse(params string[] args)
    {
        Assert.False(StartupProbe.TryGetPath(args, out var path));
        Assert.Equal(string.Empty, path);
    }

    [Fact]
    public void FormatJson_IsValidJsonWithInvariantNumbers()
    {
        var json = StartupProbe.FormatJson(123.4567, new DateTimeOffset(2026, 10, 18, 1, 2, 3, 456, TimeSpan.FromHours(2)));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(123.457, doc.RootElement.GetProperty("startupMs").GetDouble(), 3);
        Assert.Equal("2026-10-17T23:02:03.456Z", doc.RootElement.GetProperty("utc").GetString());
    }

    [Fact]
    public async Task Attach_WritesFileAfterFirstFrames_AndRequestsExitZero()
    {
        var path = Path.Combine(Path.GetTempPath(), "murmur-probe-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var code = await RunProbe(path);

            Assert.Equal(0, code);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(doc.RootElement.GetProperty("startupMs").GetDouble() > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Attach_WhenFileCannotBeWritten_RequestsExitOne()
    {
        var path = Path.Combine(Path.GetTempPath(), "murmur-missing-" + Guid.NewGuid().ToString("N"), "out.json");

        Assert.Equal(1, await RunProbe(path));
    }

    /// <summary>Shows a window with the probe attached and pumps frames until the exit callback fires.</summary>
    private static Task<int> RunProbe(string path)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(StartupProbeTests).Assembly);
        return session.Dispatch(
            () =>
            {
                int? exitCode = null;
                var window = new MainWindow { DataContext = new MainWindowViewModel() };
                StartupProbe.Attach(window, path, code => exitCode = code);
                window.Show();

                for (var i = 0; i < 500 && exitCode is null; i++)
                {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(5);
                }

                window.Close();
                return exitCode ?? -1;
            },
            TestContext.Current.CancellationToken);
    }
}
