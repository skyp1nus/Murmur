using Avalonia.Headless;
using Murmur.App.Diagnostics;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;

namespace Murmur.App.Tests;

/// <summary><c>--probe-open-latest</c> (#102): opens the newest session's details, renders, and reports with its exit code.</summary>
public sealed class OpenSessionProbeTests : IDisposable
{
    private readonly SessionFixture _fixture = new();

    private static HeadlessUnitTestSession Session => UiHarness.Session;

    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData(true, "--probe-open-latest")]
    [InlineData(true, "--x", "--probe-open-latest")]
    [InlineData(false)]
    [InlineData(false, "--startup-probe", "out.json")]
    [InlineData(false, "--probe-open")]
    public void IsRequested_FindsTheFlag(bool expected, params string[] args) => Assert.Equal(expected, OpenSessionProbe.IsRequested(args));

    [Fact]
    public async Task RunAsync_OpensTheNewestSession_RendersTwoFrames_AndReturnsZero()
    {
        _fixture.UseShortTranscript();
        var harness = new UiHarness();
        harness.Store.Add(Sessions.Make("Older", UiHarness.Now.AddDays(-2), TimeSpan.FromMinutes(3)));
        harness.Store.Add(_fixture.Record);
        var shell = CreateShell(harness);
        var log = new StringWriter();

        var (code, frames, page) = await Session.Dispatch(
            async () =>
            {
                var window = UiHarness.Show(shell);
                var rendered = 0;
                var result = await OpenSessionProbe.RunAsync(
                    shell,
                    () =>
                    {
                        UiHarness.Pump();
                        rendered++;
                        return Task.CompletedTask;
                    },
                    log);
                var opened = shell.CurrentPage;
                window.Close();
                return (result, rendered, opened);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(OpenSessionProbe.Opened, code);
        Assert.Equal(2, frames);
        Assert.Equal(_fixture.Record.Id, Assert.IsType<SessionViewModel>(page).Id);
        Assert.Contains(_fixture.Record.Id.ToString(), log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WithAnEmptyLibrary_ReturnsNoSession()
    {
        var shell = CreateShell(new UiHarness());
        var log = new StringWriter();

        var code = await Session.Dispatch(
            () => OpenSessionProbe.RunAsync(shell, () => Task.CompletedTask, log),
            TestContext.Current.CancellationToken);

        Assert.Equal(OpenSessionProbe.NoSession, code);
        Assert.IsNotType<SessionViewModel>(shell.CurrentPage);
        Assert.Contains("no session", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenTheDetailsDoNotOpen_ReturnsFailed()
    {
        // Without a session factory the shell ignores the request to open the details.
        var harness = new UiHarness();
        harness.Store.Add(_fixture.Record);
        var shell = harness.CreateShell();

        var code = await Session.Dispatch(
            () => OpenSessionProbe.RunAsync(shell, () => Task.CompletedTask, TextWriter.Null),
            TestContext.Current.CancellationToken);

        Assert.Equal(OpenSessionProbe.Failed, code);
    }

    private ShellViewModel CreateShell(UiHarness harness) =>
        harness.CreateShell(
            record => new SessionViewModel(record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action()),
            _fixture.Processing);
}
