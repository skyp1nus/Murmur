using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.VisualTree;
using Murmur.App.Tests.Fakes;
using Murmur.App.Views;
using Murmur.Core.Sessions;

namespace Murmur.App.Tests;

public sealed class MainWindowTests
{
    private static HeadlessUnitTestSession Session => UiHarness.Session;

    [Fact]
    public async Task EmptyLibrary_HasShellSizingAndPhase0Copy()
    {
        var harness = new UiHarness();
        var shell = harness.CreateShell();
        await shell.Library.LoadAsync();

        var snapshot = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToArray();
                var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b is not ToggleButton && b.IsEffectivelyVisible).Select(b => b.Content as string).ToArray();
                var search = window.GetVisualDescendants().OfType<TextBox>().Single();
                var pills = window.GetVisualDescendants().OfType<ToggleButton>().Select(b => b.Content as string).ToArray();
                var result = (window.Title, window.Width, window.Height, window.MinWidth, window.MinHeight, texts, buttons, pills, search.PlaceholderText);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("Murmur", snapshot.Title);
        Assert.Equal(1200, snapshot.Width);
        Assert.Equal(780, snapshot.Height);
        Assert.Equal(900, snapshot.MinWidth);
        Assert.Equal(600, snapshot.MinHeight);
        Assert.Contains("Murmur", snapshot.texts);
        Assert.Contains("Ще немає записів", snapshot.texts);
        Assert.Contains("Натисни «Новий запис», щоб записати перший мітинг. Усе лишається на твоєму компʼютері.", snapshot.texts);
        Assert.Equal(["Новий запис", "Новий запис"], snapshot.buttons);
        Assert.Equal(["Сьогодні", "Тиждень", "Усі"], snapshot.pills);
        Assert.Equal("Пошук по записах", snapshot.PlaceholderText);
    }

    [Fact]
    public async Task Library_ListsSessionsInAVirtualisedList()
    {
        var harness = new UiHarness();
        harness.SeedSessions();
        var shell = harness.CreateShell();
        await shell.Library.LoadAsync();

        var (titles, durations, listBoxes, hasVirtualPanel, emptyVisible) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var texts = window.GetVisualDescendants().OfType<TextBlock>().ToArray();
                var list = window.GetVisualDescendants().OfType<ListBox>().Where(l => l.IsEffectivelyVisible).ToArray();
                var panel = window.GetVisualDescendants().OfType<VirtualizingStackPanel>().Any();
                var empty = texts.Any(t => t.Text == "Ще немає записів" && t.IsEffectivelyVisible);
                var result = (
                    texts.Select(t => t.Text).ToArray(),
                    texts.Where(t => t.Classes.Contains("mono")).Select(t => t.Text).ToArray(),
                    list.Length,
                    panel,
                    empty);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Contains("Щотижневий стендап команди", titles);
        Assert.Contains("Сьогодні, 14:00", titles);
        Assert.Contains("00:23:41", durations);
        Assert.Contains("Перервано", titles);
        Assert.Equal(1, listBoxes);
        Assert.True(hasVirtualPanel);
        Assert.False(emptyVisible);
    }

    [Fact]
    public async Task NewRecordingButton_SwitchesToTheRecordingScreen_ThroughTheDataTemplate()
    {
        var harness = new UiHarness();
        var shell = harness.CreateShell();
        await shell.Library.LoadAsync();

        var (isRecordingView, hasLibraryView, labels) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var header = window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("primary"));
                header.Command!.Execute(null);
                UiHarness.Pump();
                var result = (
                    window.GetVisualDescendants().OfType<RecordingView>().Any(),
                    window.GetVisualDescendants().OfType<LibraryView>().Any(),
                    window.GetVisualDescendants().OfType<Button>().Where(b => b is not ToggleButton).Select(b => b.Content as string).ToArray());
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.True(isRecordingView);
        Assert.False(hasLibraryView);
        Assert.Contains("Почати запис", labels);
        Assert.Contains("Назад", labels);
    }

    [Fact]
    public async Task ARecorderWarning_ShowsAsAnInlineNoticeOnTheRecordingScreen()
    {
        var text = Murmur.App.Resources.Strings.Recording_ScreenFramesDropped;
        var harness = new UiHarness();
        var shell = harness.CreateShell();
        shell.ShowRecording();
        harness.Recorder.ForceRecording(Sessions.Make("Live", UiHarness.Now, TimeSpan.Zero, state: SessionState.Recording));

        var (before, after) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                bool Visible() => window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsVisible && t.Text == text);
                var visibleBefore = Visible();
                harness.Recorder.RaiseWarning("core text", Murmur.Core.Capture.CaptureFaultKind.FramesDropped);
                UiHarness.Pump();
                var visibleAfter = Visible();
                window.Close();
                return (visibleBefore, visibleAfter);
            },
            TestContext.Current.CancellationToken);

        Assert.False(before);
        Assert.True(after);
    }

    [Fact]
    public async Task Close_WhileTheStopIsPending_KeepsTheWindowOpenUntilTheRecorderIsIdle()
    {
        var harness = new UiHarness();
        var shell = harness.CreateShell();
        await shell.Library.LoadAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Recorder.StopGate = gate.Task;

        var (openAfterFirst, openAfterSecond, stateWhilePending, stops, closedAtEnd, stateAtEnd) = await Session.Dispatch(
            async () =>
            {
                var window = UiHarness.Show(shell);
                await shell.Recording.StartStopAsync();
                Assert.True(shell.Recording.IsRecording);

                window.Close();
                var first = window.IsVisible;
                var stopping = window.StoppingTask;
                Assert.NotNull(stopping);

                // The recorder is now Stopping (IsRecording is false): further close requests must still be refused.
                window.Close();
                var second = window.IsVisible;
                var pending = shell.Recording.State;

                gate.SetResult();
                await stopping;
                UiHarness.Pump();
                return (first, second, pending, harness.Recorder.Stops, !window.IsVisible, shell.Recording.State);
            },
            TestContext.Current.CancellationToken);

        Assert.True(openAfterFirst);
        Assert.True(openAfterSecond);
        Assert.Equal(Murmur.Core.Recording.RecorderState.Stopping, stateWhilePending);
        Assert.Equal(1, stops);
        Assert.True(closedAtEnd);
        Assert.Equal(Murmur.Core.Recording.RecorderState.Idle, stateAtEnd);
    }

    [Fact]
    public async Task Close_AfterAnEarlierStop_StopsTheNextRecordingBeforeClosing()
    {
        // The fake recorder stops synchronously: an earlier finished stop must not make the close guard skip this one.
        var harness = new UiHarness();
        var shell = harness.CreateShell();
        await shell.Library.LoadAsync();

        var (stops, stateWhenClosed, closedAtEnd) = await Session.Dispatch(
            async () =>
            {
                var window = UiHarness.Show(shell);
                shell.ShowRecording();
                await shell.Recording.StartStopAsync();
                await shell.Recording.StartStopAsync();
                await shell.Recording.StartStopAsync();
                Assert.True(shell.Recording.IsRecording);
                Murmur.Core.Recording.RecorderState? closedIn = null;
                window.Closed += (_, _) => closedIn = harness.Recorder.State;

                window.Close();
                await window.StoppingTask!;
                UiHarness.Pump();
                return (harness.Recorder.Stops, closedIn, !window.IsVisible);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, stops);
        Assert.Equal(Murmur.Core.Recording.RecorderState.Idle, stateWhenClosed);
        Assert.True(closedAtEnd);
    }

    [Fact]
    public async Task Close_WhenIdle_ClosesImmediately()
    {
        var harness = new UiHarness();
        var shell = harness.CreateShell();
        await shell.Library.LoadAsync();

        var (visible, stopping) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                window.Close();
                return (window.IsVisible, window.StoppingTask);
            },
            TestContext.Current.CancellationToken);

        Assert.False(visible);
        Assert.Null(stopping);
    }

    [Fact]
    public async Task TypographyClasses_ResolveTokens()
    {
        var harness = new UiHarness();
        harness.SeedSessions();
        var shell = harness.CreateShell();
        await shell.Library.LoadAsync();

        var (display, mono, primaryRadius, cardRadius, pillRadius, badgeRadius) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var heading = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("heading"));
                var timer = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("mono"));
                var primary = window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("primary"));
                var card = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("card"));
                var pill = window.GetVisualDescendants().OfType<ToggleButton>().First();
                var badge = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("badge"));
                var result = (
                    (heading.FontWeight, heading.FontSize, heading.LetterSpacing),
                    (timer.FontFamily.Name, timer.FontSize, timer.LineHeight),
                    primary.CornerRadius,
                    card.CornerRadius,
                    pill.CornerRadius,
                    badge.CornerRadius);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(FontWeight.Light, display.FontWeight);
        Assert.Equal(36, display.FontSize);
        Assert.Equal(-0.72, display.LetterSpacing, 3);
        Assert.Equal("Geist Mono", mono.Name);
        Assert.Equal(13, mono.FontSize);
        Assert.Equal(21.97, mono.LineHeight, 3);
        Assert.Equal(new CornerRadius(9999), primaryRadius);
        Assert.Equal(new CornerRadius(20), cardRadius);
        Assert.Equal(new CornerRadius(9999), pillRadius);
        Assert.Equal(new CornerRadius(9999), badgeRadius);
    }

    [Fact]
    public async Task InterLight_IsARealLightFace_NotAFallback()
    {
        var (light, regular, lightWidth, regularWidth) = await Session.Dispatch(
            () =>
            {
                var fonts = FontManager.Current;
                var family = new FontFamily("fonts:Inter#Inter");
                Assert.True(fonts.TryGetGlyphTypeface(new Typeface(family, FontStyle.Normal, FontWeight.Light), out var lightFace));
                Assert.True(fonts.TryGetGlyphTypeface(new Typeface(family, FontStyle.Normal, FontWeight.Normal), out var regularFace));
                return (lightFace!.Weight, regularFace!.Weight, MeasureWidth(family, FontWeight.Light), MeasureWidth(family, FontWeight.Normal));
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(FontWeight.Light, light);
        Assert.Equal(FontWeight.Normal, regular);
        Assert.True(lightWidth < regularWidth, $"Light ({lightWidth}) should be narrower than Regular ({regularWidth}).");
    }

    private static double MeasureWidth(FontFamily family, FontWeight weight)
    {
        var text = new TextBlock
        {
            Text = "Murmur records meetings locally",
            FontFamily = family,
            FontWeight = weight,
            FontSize = 36,
        };
        text.Measure(Size.Infinity);
        return text.DesiredSize.Width;
    }
}
