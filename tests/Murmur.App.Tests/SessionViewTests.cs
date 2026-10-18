using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Murmur.App.Controls;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;

namespace Murmur.App.Tests;

/// <summary>The recording view on the headless platform: virtualisation, cached waveform, polling timer, clicks.</summary>
public sealed class SessionViewTests : IDisposable
{
    private readonly SessionFixture _fixture = new();

    private static HeadlessUnitTestSession Session => UiHarness.Session;

    public void Dispose() => _fixture.Dispose();

    private async Task<(UiHarness Harness, ShellViewModel Shell)> OpenAsync()
    {
        var harness = new UiHarness();
        harness.Store.Add(_fixture.Record);
        var shell = harness.CreateShell(
            record => new SessionViewModel(record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action()),
            _fixture.Processing);
        await shell.Library.LoadAsync();
        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;
        return (harness, shell);
    }

    [Fact]
    public async Task TenThousandWordTranscript_RealisesOnlyTheVisibleRows()
    {
        _fixture.UseTranscript(TranscriptFactory.Make(_fixture.Record.Id, 10_000));
        var (_, shell) = await OpenAsync();

        var (rows, total, hasVirtualPanel) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var result = (
                    window.GetVisualDescendants().OfType<TranscriptTextControl>().Count(),
                    shell.Session!.Segments.Count,
                    window.GetVisualDescendants().OfType<VirtualizingStackPanel>().Any());
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.True(hasVirtualPanel);
        Assert.Equal(834, total);
        Assert.InRange(rows, 1, 60);
    }

    [Fact]
    public async Task TheSpokenWord_IsHighlightedInTheRealisedRow()
    {
        _fixture.UseShortTranscript();
        var (_, shell) = await OpenAsync();

        var (active, texts) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                _fixture.Player.Position = TimeSpan.FromSeconds(11.0);
                shell.Session!.Tick();
                UiHarness.Pump();
                var controls = window.GetVisualDescendants().OfType<TranscriptTextControl>().ToArray();
                var result = (controls.Select(c => c.ActiveWord).ToArray(), controls.Select(c => c.Text).ToArray());
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(["Привіт усім починаємо мітинг", "Добрий день колеги"], texts);
        Assert.Equal([2, -1], active);
    }

    [Fact]
    public async Task ClickingAWord_SeeksThePlayerToThatWord()
    {
        _fixture.UseShortTranscript();
        var (_, shell) = await OpenAsync();

        var seeks = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var control = window.GetVisualDescendants().OfType<TranscriptTextControl>().First(c => c.Text!.StartsWith("Добрий", StringComparison.Ordinal));

                // The first character of the second segment is the word "Добрий" (word 0 of 3, 20.0 s).
                var inWindow = control.TranslatePoint(new Point(3, 3), window)!.Value;
                window.MouseDown(inWindow, MouseButton.Left);
                window.MouseUp(inWindow, MouseButton.Left);
                UiHarness.Pump();
                var result = _fixture.Player.Seeks.ToArray();
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal([TimeSpan.FromSeconds(20)], seeks);
        Assert.Equal(1, _fixture.Player.Plays);
    }

    [Fact]
    public async Task ClickingTheWaveform_SeeksToThatFractionOfTheSession()
    {
        _fixture.UseShortTranscript();
        _fixture.WritePeaks(new byte[3600 * 50]);
        var (_, shell) = await OpenAsync();

        var seeks = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var waveform = window.GetVisualDescendants().OfType<WaveformControl>().Single();
                var inWindow = waveform.TranslatePoint(new Point(waveform.Bounds.Width * 0.25, 10), window)!.Value;
                window.MouseDown(inWindow, MouseButton.Left);
                window.MouseUp(inWindow, MouseButton.Left);
                UiHarness.Pump();
                var result = _fixture.Player.Seeks.ToArray();
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        var seek = Assert.Single(seeks);
        Assert.Equal(900, seek.TotalSeconds, 1);
    }

    [Fact]
    public async Task TheWaveformGeometry_IsBuiltOnceAndNotPerPlayheadUpdate()
    {
        _fixture.UseShortTranscript();
        _fixture.WritePeaks(Enumerable.Range(0, 3600 * 50).Select(i => (byte)(i % 200)).ToArray());
        var (_, shell) = await OpenAsync();

        var (afterFirst, afterManyUpdates, afterResize) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var waveform = window.GetVisualDescendants().OfType<WaveformControl>().Single();
                var first = waveform.GeometryBuilds;
                for (var i = 0; i < 100; i++)
                {
                    _fixture.Player.Position = TimeSpan.FromSeconds(i * 10);
                    shell.Session!.Tick();
                    UiHarness.Pump(1);
                }

                var many = waveform.GeometryBuilds;
                waveform.Width = 200;
                UiHarness.Pump();
                var resized = waveform.GeometryBuilds;
                window.Close();
                return (first, many, resized);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, afterFirst);
        Assert.Equal(1, afterManyUpdates);
        Assert.Equal(2, afterResize);
    }

    [Fact]
    public async Task ThePollTimer_RunsOnlyWhilePlaying()
    {
        _fixture.UseShortTranscript();
        var (_, shell) = await OpenAsync();

        var (idle, playing, paused) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var view = window.GetVisualDescendants().OfType<SessionView>().Single();
                var before = view.IsPollTimerRunning;
                shell.Session!.PlayPauseCommand.Execute(null);
                var during = view.IsPollTimerRunning;
                shell.Session.PlayPauseCommand.Execute(null);
                var after = view.IsPollTimerRunning;
                window.Close();
                return (before, during, after);
            },
            TestContext.Current.CancellationToken);

        Assert.False(idle);
        Assert.True(playing);
        Assert.False(paused);
    }

    [Fact]
    public async Task ThePlayerMovingFar_ScrollsTheListToTheCurrentSegment()
    {
        _fixture.UseTranscript(TranscriptFactory.Make(_fixture.Record.Id, 10_000));
        var (_, shell) = await OpenAsync();

        var (before, after) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "TranscriptScroller");
                var first = scroller.Offset.Y;
                _fixture.Player.Position = shell.Session!.Segments[500].Start;
                shell.Session.Tick();
                UiHarness.Pump();
                var result = (first, scroller.Offset.Y);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, before);
        Assert.True(after > 20_000, $"Offset after the jump: {after}.");
    }

    [Fact]
    public async Task AfterTheUserScrolledTheList_AutoScrollWaitsForThreeSeconds()
    {
        _fixture.UseTranscript(TranscriptFactory.Make(_fixture.Record.Id, 10_000));
        var (_, shell) = await OpenAsync();

        var (suspended, offsetWhileSuspended, offsetAfterHold) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "TranscriptScroller");
                var inWindow = scroller.TranslatePoint(new Point(100, 100), window)!.Value;
                window.MouseWheel(inWindow, new Vector(0, -1));
                UiHarness.Pump();
                var wasSuspended = shell.Session!.AutoScrollSuspended;
                var userOffset = scroller.Offset.Y;

                _fixture.Player.Position = shell.Session.Segments[500].Start;
                shell.Session.Tick();
                UiHarness.Pump();
                var whileSuspended = scroller.Offset.Y;

                _fixture.Time.Advance(TimeSpan.FromSeconds(3));
                _fixture.Player.Position = shell.Session.Segments[600].Start;
                shell.Session.Tick();
                UiHarness.Pump();
                var afterHold = scroller.Offset.Y;
                window.Close();
                return (wasSuspended, whileSuspended - userOffset, afterHold);
            },
            TestContext.Current.CancellationToken);

        Assert.True(suspended);
        Assert.Equal(0, offsetWhileSuspended);
        Assert.True(offsetAfterHold > 30_000, $"Offset after the hold: {offsetAfterHold}.");
    }

    [Fact]
    public async Task TheTabs_ShowTheTranscript_AndPhase3Placeholders()
    {
        _fixture.UseShortTranscript();
        var (_, shell) = await OpenAsync();

        var (pills, summary, ask) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var pillTexts = window.GetVisualDescendants().OfType<ToggleButton>().Select(b => b.Content as string).ToArray();
                shell.Session!.IsSummaryTab = true;
                UiHarness.Pump();
                var summaryTexts = VisibleTexts(window);
                shell.Session.IsAskTab = true;
                UiHarness.Pump();
                var askTexts = VisibleTexts(window);
                window.Close();
                return (pillTexts, summaryTexts, askTexts);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(["Транскрипт", "Резюме", "Запитати AI"], pills);
        Assert.Contains(summary, t => t.StartsWith("Зʼявиться у фазі 3", StringComparison.Ordinal));
        Assert.DoesNotContain("Привіт усім починаємо мітинг", summary);
        Assert.Contains(ask, t => t.StartsWith("Зʼявиться у фазі 3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheInstallCard_ExplainsHowToGetTheModel()
    {
        _fixture.Processing.IsAsrInstalled = false;
        var (_, shell) = await OpenAsync();

        var (texts, buttons) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var visible = VisibleTexts(window);
                var primary = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).ToArray();
                window.Close();
                return (visible, primary);
            },
            TestContext.Current.CancellationToken);

        Assert.Contains("Модель Parakeet не встановлена", texts);
        Assert.Contains(texts, t => t.Contains("scripts/fetch-models.ps1", StringComparison.Ordinal));
        Assert.Contains("Спробувати ще", buttons);
    }

    private static string[] VisibleTexts(Window window) =>
        [.. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Text is not null).Select(t => t.Text!)];
}
