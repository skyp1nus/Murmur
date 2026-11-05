using Avalonia;
using Avalonia.Controls;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Murmur.App.Controls;
using Murmur.App.Localization;
using Murmur.App.Services;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Recording;
using Murmur.Core.Settings;

namespace Murmur.App.Tests;

/// <summary>
/// The floating recorder orb's window and its glue (#46) on the headless platform: when it is created and shown, that the circle
/// stays put when the pill opens, click against drag, the saved position, the moving layers and the rendered pictures. What only
/// Windows can show (topmost over other programs, no focus, no taskbar button, mixed DPI) is in the PR's Windows check.
/// </summary>
public sealed class FloatingOrbWindowTests
{
    private static readonly WorkArea Screen = new(0, 0, 1920, 1040, 1.0);

    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig : IDisposable
    {
        private readonly IDisposable _english = UiCulture.Override("en");

        public Rig(ThemeVariant? theme = null, bool withMainWindow = true)
        {
            Harness = new UiHarness();
            Marks = new RecordingMarks(Harness.Recorder, new FakeBookmarkStore());
            Settings = new FakeSettingsStore();
            var library = Harness.CreateShell().Library;
            var recording = new RecordingViewModel(Harness.Recorder, Harness.Capture, Harness.Store, action => action(), time: Harness.Time, marks: Marks);
            Shell = new ShellViewModel(library, recording, post: action => action());
            Main = new MainWindow { DataContext = Shell, Width = 1100, Height = 720 };
            if (theme is not null)
            {
                Main.RequestedThemeVariant = theme;
            }

            if (withMainWindow)
            {
                Main.Show();
                UiHarness.Pump();
            }

            var actions = new FloatingOrbActions(Shell.TogglePauseFromHotkeyAsync, Shell.Recording.StopAsync, Open);
            ViewModel = new FloatingOrbViewModel(Harness.Recorder, actions, Marks, Settings, time: Harness.Time, post: action => action());
            Controller = new FloatingOrbController(Main, Shell, ViewModel, vm =>
            {
                var window = new FloatingOrbWindow { DataContext = vm, WorkAreaSource = () => Areas };
                if (theme is not null)
                {
                    window.RequestedThemeVariant = theme;
                }

                return window;
            });
        }

        public UiHarness Harness { get; }

        public RecordingMarks Marks { get; }

        public FakeSettingsStore Settings { get; }

        public ShellViewModel Shell { get; }

        public MainWindow Main { get; }

        public FloatingOrbViewModel ViewModel { get; }

        public FloatingOrbController Controller { get; }

        public IReadOnlyList<WorkArea> Areas { get; set; } = [Screen];

        public int Opens { get; private set; }

        public FloatingOrbWindow Window => Controller.Window ?? throw new InvalidOperationException("The orb's window was not created.");

        public bool OrbShown => Controller.Window is { IsVisible: true };

        private void Open()
        {
            Opens++;
            if (!ReferenceEquals(Shell.CurrentPage, Shell.Recording))
            {
                Shell.ShowRecording();
            }

            Main.BringToFront();
        }

        public async Task StartRecordingAsync() => await Harness.Recorder.StartAsync(new RecordingOptions(), Ct);

        public void Dispose()
        {
            Controller.Dispose();
            Main.Close();
            Marks.Dispose();
            _english.Dispose();
        }
    }

    // ---- the window ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheWindow_IsBorderlessTransparentTopmostAndOutOfTheTaskbar_AndDoesNotTakeFocusWhenShown()
    {
        var (decorations, topmost, taskbar, activated, resizable, transparent, background, sizing) = await Session.Dispatch(
            () =>
            {
                using var rig = new Rig();
                var orb = new FloatingOrbWindow { DataContext = rig.ViewModel };
                var result = (
                    orb.WindowDecorations,
                    orb.Topmost,
                    orb.ShowInTaskbar,
                    orb.ShowActivated,
                    orb.CanResize,
                    orb.TransparencyLevelHint.Contains(WindowTransparencyLevel.Transparent),
                    orb.Background is ISolidColorBrush { Color.A: 0 },
                    orb.SizeToContent);
                orb.Close();
                return result;
            },
            Ct);

        Assert.Equal(WindowDecorations.None, decorations);
        Assert.True(topmost);
        Assert.False(taskbar);
        Assert.False(activated);
        Assert.False(resizable);
        Assert.True(transparent);
        Assert.True(background);
        Assert.Equal(SizeToContent.WidthAndHeight, sizing);
    }

    [Fact]
    public async Task TheCircle_Is44Dips_WithA28DipOrbInside_AndNoTimer()
    {
        var (circle, orbs, timers, activeOrbs) = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                await rig.StartRecordingAsync();
                rig.Main.WindowState = WindowState.Minimized;
                UiHarness.Pump();
                var window = rig.Window;
                UiHarness.Pump(6);
                var orbControls = window.GetVisualDescendants().OfType<GlassOrbControl>().ToArray();
                return (
                    window.FindControl<Border>("Circle")!.Bounds.Size,
                    orbControls.Select(o => o.Bounds.Size).Distinct().ToArray(),
                    orbControls.Count(o => o.IsFrameLoopRunning),
                    orbControls.Count(o => o.IsActive));
            },
            Ct);

        Assert.Equal(new Size(44, 44), circle);
        Assert.Equal([new Size(28, 28)], orbs);
        Assert.Equal(0, timers);
        Assert.Equal(0, activeOrbs);
    }

    // ---- when it is shown ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Idle_NoWindowIsCreatedAtAll()
    {
        var created = await Session.Dispatch(
            () =>
            {
                using var rig = new Rig();
                UiHarness.Pump();
                return rig.Controller.Window is not null;
            },
            Ct);

        Assert.False(created);
    }

    [Fact]
    public async Task ARecordingWithTheMainWindowOnAnotherPage_ShowsTheOrb_InTheBottomRightCorner()
    {
        var (shown, anchor, position) = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                await rig.StartRecordingAsync();
                UiHarness.Pump(4);
                return (rig.OrbShown, rig.Window.OrbAnchor, rig.Window.Position);
            },
            Ct);

        Assert.True(shown);
        Assert.Equal(new PixelPoint(1920 - 24 - 44, 1040 - 24 - 44), anchor);
        Assert.True(position.X < anchor.X && position.Y < anchor.Y, "The window starts a little up and left of the circle, for its shadow.");
    }

    [Fact]
    public async Task TheOrb_FollowsTheMainWindow_PageMinimisedAndHidden()
    {
        var seen = new List<(string Step, bool Shown)>();
        await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                await rig.StartRecordingAsync();
                UiHarness.Pump();
                seen.Add(("library page", rig.OrbShown));

                rig.Shell.ShowRecording();
                UiHarness.Pump();
                seen.Add(("recording screen", rig.OrbShown));

                rig.Shell.ShowLibrary();
                UiHarness.Pump();
                seen.Add(("library again", rig.OrbShown));

                rig.Shell.ShowRecording();
                rig.Main.WindowState = WindowState.Minimized;
                UiHarness.Pump();
                seen.Add(("recording screen, minimised", rig.OrbShown));

                rig.Main.WindowState = WindowState.Normal;
                UiHarness.Pump();
                seen.Add(("restored", rig.OrbShown));

                rig.Main.Hide();
                UiHarness.Pump();
                seen.Add(("hidden", rig.OrbShown));

                rig.Main.Show();
                UiHarness.Pump();
                seen.Add(("shown again", rig.OrbShown));
            },
            Ct);

        Assert.Equal(
            [
                ("library page", true),
                ("recording screen", false),
                ("library again", true),
                ("recording screen, minimised", true),
                ("restored", false),
                ("hidden", true),
                ("shown again", false),
            ],
            seen);
    }

    [Fact]
    public async Task TheOrb_GoesWhenTheRecordingEnds_AndComesBackWithTheNextOne()
    {
        var seen = new List<bool>();
        await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                await rig.StartRecordingAsync();
                UiHarness.Pump();
                seen.Add(rig.OrbShown);

                await rig.Harness.Recorder.PauseAsync(Ct);
                UiHarness.Pump();
                seen.Add(rig.OrbShown);

                await rig.Harness.Recorder.StopAsync(Ct);
                UiHarness.Pump();
                seen.Add(rig.OrbShown);

                await rig.StartRecordingAsync();
                UiHarness.Pump();
                seen.Add(rig.OrbShown);
            },
            Ct);

        Assert.Equal([true, true, false, true], seen);
    }

    [Fact]
    public async Task TheOpenButton_BringsTheMainWindowToTheRecordingScreen()
    {
        var (page, opens, shown, pillOpen) = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                await rig.StartRecordingAsync();
                rig.Main.WindowState = WindowState.Minimized;
                UiHarness.Pump();
                rig.ViewModel.PointerPressed();
                rig.ViewModel.PointerReleased();
                rig.ViewModel.OpenCommand.Execute(null);
                UiHarness.Pump();
                return (rig.Shell.CurrentPage, rig.Opens, rig.OrbShown, rig.ViewModel.IsPillOpen);
            },
            Ct);

        Assert.IsType<RecordingViewModel>(page);
        Assert.Equal(1, opens);
        Assert.False(shown);
        Assert.False(pillOpen);
    }

    [Fact]
    public async Task ThePauseAndStopButtons_DoWhatTheRecordingScreenDoes()
    {
        var (afterPause, afterResume, afterStop, orbAfterStop) = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                await rig.StartRecordingAsync();
                var recorder = rig.Harness.Recorder;
                await rig.ViewModel.PauseResumeCommand.ExecuteAsync(null);
                var paused = recorder.State;
                await rig.ViewModel.PauseResumeCommand.ExecuteAsync(null);
                var resumed = recorder.State;
                await rig.ViewModel.StopCommand.ExecuteAsync(null);
                UiHarness.Pump();
                return (paused, resumed, recorder.State, rig.OrbShown);
            },
            Ct);

        Assert.Equal(RecorderState.Paused, afterPause);
        Assert.Equal(RecorderState.Recording, afterResume);
        Assert.Equal(RecorderState.Idle, afterStop);
        Assert.False(orbAfterStop);
    }

    [Fact]
    public async Task ClosingTheMainWindow_ClosesTheOrbToo()
    {
        var (closed, created) = await Session.Dispatch(
            async () =>
            {
                var rig = new Rig();
                await rig.StartRecordingAsync();
                var window = rig.Window;
                var closedEvent = false;
                window.Closed += (_, _) => closedEvent = true;
                rig.Controller.Dispose();
                rig.Main.Close();
                rig.Marks.Dispose();
                return (closedEvent, window is not null);
            },
            Ct);

        Assert.True(closed);
        Assert.True(created);
    }

    // ---- the position -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASavedPosition_IsWhereTheOrbAppears()
    {
        var anchor = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                rig.Settings.SetCurrentSilently(new AppSettings { FloatingOrbPosition = new FloatingOrbPosition(1200, 300) });
                await rig.StartRecordingAsync();
                UiHarness.Pump(4);
                return rig.Window.OrbAnchor;
            },
            Ct);

        Assert.Equal(new PixelPoint(1200, 300), anchor);
    }

    [Fact]
    public async Task ASavedPositionOnAnUnpluggedMonitor_AppearsInsideTheVisibleWorkArea()
    {
        var anchor = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                rig.Settings.SetCurrentSilently(new AppSettings { FloatingOrbPosition = new FloatingOrbPosition(3500, 400) });
                await rig.StartRecordingAsync();
                UiHarness.Pump(4);
                return rig.Window.OrbAnchor;
            },
            Ct);

        Assert.Equal(new PixelPoint(1920 - 44, 400), anchor);
    }

    // ---- click and drag ----------------------------------------------------------------------------------------------------

    private static Point CircleCentre(FloatingOrbWindow window)
    {
        var circle = window.FindControl<Border>("Circle")!;
        return circle.TranslatePoint(new Point(circle.Bounds.Width / 2, circle.Bounds.Height / 2), window)!.Value;
    }

    [Fact]
    public async Task AClickOnTheCircle_OpensThePillToItsLeft_AndTheCircleStaysWhereItWas()
    {
        var (open, pillRight, circleLeft, anchorBefore, anchorAfter, circleScreen, windowWidthBefore, windowWidthAfter) = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                await rig.StartRecordingAsync();
                UiHarness.Pump(4);
                var window = rig.Window;
                var before = window.OrbAnchor;
                var widthBefore = window.Bounds.Width;
                var centre = CircleCentre(window);

                window.MouseDown(centre, MouseButton.Left);
                window.MouseUp(centre, MouseButton.Left);
                UiHarness.Pump(4);

                var pill = window.FindControl<Border>("Pill")!;
                var circle = window.FindControl<Border>("Circle")!;
                var circleTopLeft = circle.TranslatePoint(default, window)!.Value;
                var pillRightEdge = pill.TranslatePoint(new Point(pill.Bounds.Width, 0), window)!.Value.X;
                var screenPoint = window.PointToScreen(circleTopLeft);
                return (rig.ViewModel.IsPillOpen, pillRightEdge, circleTopLeft.X, before, window.OrbAnchor, screenPoint, widthBefore, window.Bounds.Width);
            },
            Ct);

        Assert.True(open);
        Assert.Equal(anchorBefore, anchorAfter);
        Assert.Equal(anchorAfter, circleScreen);
        Assert.True(windowWidthAfter > windowWidthBefore, "The window grows to hold the pill.");
        Assert.Equal(circleLeft - 6, pillRight, 0.5);
    }

    [Fact]
    public async Task ThePill_IsFortyHigh_WithFourThirtyTwoButtonsTwoApart()
    {
        var (pill, buttons, gaps, radius) = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                await rig.StartRecordingAsync();
                rig.ViewModel.IsPillOpen = true;
                UiHarness.Pump(4);
                var window = rig.Window;
                var pillBorder = window.FindControl<Border>("Pill")!;
                var tools = pillBorder.GetVisualDescendants().OfType<Button>().ToArray();
                var lefts = tools.Select(b => b.TranslatePoint(default, window)!.Value.X).ToArray();
                return (pillBorder.Bounds.Size, tools.Select(b => b.Bounds.Size).Distinct().ToArray(), new[] { lefts[1] - lefts[0] - 32, lefts[2] - lefts[1] - 32, lefts[3] - lefts[2] - 32 }, pillBorder.CornerRadius);
            },
            Ct);

        Assert.Equal(40, pill.Height);
        Assert.Equal(4 + (4 * 32) + (3 * 2) + 4, pill.Width);
        Assert.Equal([new Size(32, 32)], buttons);
        Assert.All(gaps, gap => Assert.Equal(2, gap, 0.5));
        Assert.True(radius.TopLeft >= 9999);
    }

    [Fact]
    public async Task ADragMovesTheOrb_SavesThePositionOnRelease_AndDoesNotOpenThePill()
    {
        var (open, anchor, saved, offset) = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                await rig.StartRecordingAsync();
                UiHarness.Pump(4);
                var window = rig.Window;
                var start = window.OrbAnchor;
                var centre = CircleCentre(window);

                window.MouseDown(centre, MouseButton.Left);
                window.MouseMove(centre + new Vector(-5, -3), RawInputModifiers.LeftMouseButton);
                window.MouseMove(centre + new Vector(-120, -80), RawInputModifiers.LeftMouseButton);
                window.MouseUp(centre + new Vector(-120, -80), MouseButton.Left);
                UiHarness.Pump(4);
                await SettingsWriter.For(rig.Settings).LastSave;

                return (rig.ViewModel.IsPillOpen, window.OrbAnchor, rig.Settings.Current.FloatingOrbPosition, window.OrbAnchor - start);
            },
            Ct);

        Assert.False(open);
        Assert.NotNull(saved);
        Assert.Equal(anchor.X, saved!.X, 0.5);
        Assert.Equal(anchor.Y, saved.Y, 0.5);
        Assert.True(offset.X <= -100 && offset.Y <= -60, $"The orb moved by {offset}.");
    }

    [Fact]
    public async Task AClickWithoutMovement_SavesNothing()
    {
        var saved = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                await rig.StartRecordingAsync();
                UiHarness.Pump(4);
                var centre = CircleCentre(rig.Window);

                rig.Window.MouseDown(centre, MouseButton.Left);
                rig.Window.MouseUp(centre, MouseButton.Left);
                UiHarness.Pump(2);
                return rig.Settings.Current.FloatingOrbPosition;
            },
            Ct);

        Assert.Null(saved);
    }

    [Fact]
    public async Task TheMarkButton_ShowsTheChipUnderTheCircle_AndItGoesAfterTwoSeconds()
    {
        var (shown, text, time, chipBelow, chipRightEdge, circleRightEdge, anchorMoved, afterTwo) = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                rig.Settings.SetCurrentSilently(new AppSettings { FloatingOrbPosition = new FloatingOrbPosition(1200, 300) });
                await rig.StartRecordingAsync();
                rig.Harness.Recorder.Elapsed = TimeSpan.FromSeconds(761);
                UiHarness.Pump(4);
                var window = rig.Window;
                var anchor = window.OrbAnchor;

                rig.ViewModel.MarkCommand.Execute(null);
                UiHarness.Pump(4);

                var chip = window.FindControl<Border>("Chip")!;
                var circle = window.FindControl<Border>("Circle")!;
                var texts = chip.GetVisualDescendants().OfType<TextBlock>().ToArray();
                var chipTop = chip.TranslatePoint(default, window)!.Value.Y;
                var circleBottom = circle.TranslatePoint(new Point(0, circle.Bounds.Height), window)!.Value.Y;
                var result = (
                    chip.IsVisible,
                    texts[0].Text,
                    texts[1].Text,
                    chipTop - circleBottom,
                    chip.TranslatePoint(new Point(chip.Bounds.Width, 0), window)!.Value.X,
                    circle.TranslatePoint(new Point(circle.Bounds.Width, 0), window)!.Value.X,
                    window.OrbAnchor != anchor,
                    false);
                rig.Harness.Time.Advance(TimeSpan.FromMilliseconds(2100));
                UiHarness.Pump(4);
                return result with { Item8 = chip.IsVisible };
            },
            Ct);

        Assert.True(shown);
        Assert.Equal("Marked", text);
        Assert.Equal("12:41", time);
        Assert.Equal(8, chipBelow, 0.5);
        Assert.Equal(circleRightEdge, chipRightEdge, 0.5);
        Assert.False(anchorMoved);
        Assert.False(afterTwo);
    }

    [Fact]
    public async Task AtTheBottomOfTheScreen_TheChipGoesAboveTheCircle_AndTheCircleStillStaysPut()
    {
        var (above, chipBottomToCircleTop, anchorBefore, anchorAfter, circleScreen) = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();

                // The default spot is 24 px from the bottom of the work area: the chip (8 + 28) does not fit under it.
                await rig.StartRecordingAsync();
                UiHarness.Pump(4);
                var window = rig.Window;
                var before = window.OrbAnchor;

                rig.ViewModel.MarkCommand.Execute(null);
                UiHarness.Pump(4);

                var chip = window.FindControl<Border>("Chip")!;
                var circle = window.FindControl<Border>("Circle")!;
                var chipBottom = chip.TranslatePoint(new Point(0, chip.Bounds.Height), window)!.Value.Y;
                var circleTop = circle.TranslatePoint(default, window)!.Value.Y;
                var result = (
                    window.IsChipAbove,
                    circleTop - chipBottom,
                    before,
                    window.OrbAnchor,
                    window.PointToScreen(circle.TranslatePoint(default, window)!.Value));
                return result;
            },
            Ct);

        Assert.True(above);
        Assert.Equal(8, chipBottomToCircleTop, 0.5);
        Assert.Equal(anchorBefore, anchorAfter);
        Assert.Equal(anchorAfter, circleScreen);
    }

    // ---- the moving layers -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhileRecording_TheOrbTurnsAndPings_OnTheCompositor_StartedOnce()
    {
        var (spinning, pinging, starts, pingShown) = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                await rig.StartRecordingAsync();
                UiHarness.Pump(30);
                var layers = rig.Window.GetVisualDescendants().OfType<OrbMotionLayer>().ToArray();
                var spin = layers.Single(l => l.Kind == OrbMotionKind.Spin);
                var ping = layers.Single(l => l.Kind == OrbMotionKind.Ping);
                return (spin.IsRunning, ping.IsRunning, new[] { spin.Starts, ping.Starts }, ping.IsVisible);
            },
            Ct);

        Assert.True(spinning);
        Assert.True(pinging);
        Assert.Equal([1, 1], starts);
        Assert.True(pingShown);
    }

    [Fact]
    public async Task Paused_TheOrbIsStill_AndGrey_AndThePingIsGone()
    {
        var (spinning, pinging, wash, angle) = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                await rig.StartRecordingAsync();
                UiHarness.Pump(4);
                await rig.Harness.Recorder.PauseAsync(Ct);
                UiHarness.Pump(4);
                var layers = rig.Window.GetVisualDescendants().OfType<OrbMotionLayer>().ToArray();
                var spin = layers.Single(l => l.Kind == OrbMotionKind.Spin);
                var ping = layers.Single(l => l.Kind == OrbMotionKind.Ping);
                return (spin.IsRunning, ping.IsRunning, rig.Window.FindControl<Ellipse>("PausedWash")!.IsVisible, spin.RestAngle);
            },
            Ct);

        Assert.False(spinning);
        Assert.False(pinging);
        Assert.True(wash);
        Assert.Equal(0f, angle);
    }

    [Fact]
    public async Task UnderReducedMotion_NeitherLayerRuns_AndNothingIsGrey()
    {
        var (spinning, pinging, wash) = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                rig.ViewModel.ReduceMotion = true;
                await rig.StartRecordingAsync();
                UiHarness.Pump(6);
                var layers = rig.Window.GetVisualDescendants().OfType<OrbMotionLayer>().ToArray();
                return (layers.Any(l => l.Kind == OrbMotionKind.Spin && l.IsRunning), layers.Any(l => l.Kind == OrbMotionKind.Ping && l.IsRunning), rig.Window.FindControl<Ellipse>("PausedWash")!.IsVisible);
            },
            Ct);

        Assert.False(spinning);
        Assert.False(pinging);
        Assert.False(wash);
    }

    [Fact]
    public async Task AMotionLayer_StartsWhenActivated_StopsWhenNot_AndNeverTwiceInARow()
    {
        var (afterOn, afterOff, afterOnAgain, starts, reduced) = await Session.Dispatch(
            () =>
            {
                var layer = new OrbMotionLayer { Kind = OrbMotionKind.Spin, Child = new Border { Width = 28, Height = 28 } };
                var window = new Window { Width = 100, Height = 100, Content = layer };
                window.Show();
                UiHarness.Pump();

                layer.IsActive = true;
                UiHarness.Pump(3);
                var on = layer.IsRunning;
                layer.IsActive = true;
                UiHarness.Pump(3);
                layer.IsActive = false;
                UiHarness.Pump(3);
                var off = layer.IsRunning;
                layer.IsActive = true;
                UiHarness.Pump(3);
                var again = layer.IsRunning;

                Motion.SetReduceMotion(window, true);
                UiHarness.Pump(3);
                var reducedRuns = layer.IsRunning;
                window.Close();
                return (on, off, again, layer.Starts, reducedRuns);
            },
            Ct);

        Assert.True(afterOn);
        Assert.False(afterOff);
        Assert.True(afterOnAgain);
        Assert.Equal(2, starts);
        Assert.False(reduced);
    }

    [Fact]
    public void TheTurnTakesNineSeconds_AndThePingComesEveryThree()
    {
        Assert.Equal(TimeSpan.FromSeconds(9), OrbMotionLayer.SpinPeriod);
        Assert.Equal(TimeSpan.FromSeconds(3), OrbMotionLayer.PingPeriod);
        Assert.Equal(1.9f, OrbMotionLayer.PingScale);
    }

    // ---- the pictures ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Light", "recording")]
    [InlineData("Dark", "recording")]
    [InlineData("Light", "pill")]
    [InlineData("Dark", "pill")]
    [InlineData("Light", "paused")]
    [InlineData("Dark", "paused")]
    [InlineData("Light", "marked")]
    [InlineData("Dark", "marked")]
    [InlineData("Light", "marked-pill")]
    [InlineData("Dark", "marked-pill")]
    public async Task TheOrb_RendersInBothThemes_WritesPng(string theme, string scene)
    {
        var path = Path.Combine(Path.GetTempPath(), $"murmur-floating-orb-{Environment.ProcessId}", $"floating-orb-{scene}-{theme.ToLowerInvariant()}.png");
        await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig(theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light);
                await rig.StartRecordingAsync();
                rig.Harness.Recorder.Elapsed = TimeSpan.FromSeconds(761);
                UiHarness.Pump(4);
                if (scene.StartsWith("paused", StringComparison.Ordinal))
                {
                    await rig.Harness.Recorder.PauseAsync(Ct);
                }

                if (scene.EndsWith("pill", StringComparison.Ordinal))
                {
                    rig.ViewModel.IsPillOpen = true;
                }

                if (scene.StartsWith("marked", StringComparison.Ordinal))
                {
                    rig.ViewModel.MarkCommand.Execute(null);
                }

                UiHarness.Pump(6);
                UiHarness.Save(rig.Window, path);
            },
            Ct);

        Assert.True(new FileInfo(path).Length > 500, "Screenshot looks empty.");
    }
}
