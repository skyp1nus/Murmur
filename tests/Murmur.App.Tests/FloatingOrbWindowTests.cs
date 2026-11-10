using Avalonia;
using Avalonia.Controls;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Murmur.App.Controls;
using Murmur.App.Localization;
using Murmur.App.Services;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Platform;
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

    private sealed class RecordingStyle : Murmur.Core.Platform.IFloatingWindowStyle
    {
        public List<(nint Handle, bool Shown, string Step)> Calls { get; } = [];

        public Func<bool>? IsShown { get; set; }

        public string Step { get; set; } = "create";

        public List<FloatingWindowRect[]> Regions { get; } = [];

        public void MakeNonActivating(nint nativeHandle) => Calls.Add((nativeHandle, IsShown?.Invoke() ?? false, Step));

        public void SetInputRegion(nint nativeHandle, ReadOnlySpan<FloatingWindowRect> rectangles) => Regions.Add(rectangles.ToArray());
    }

    [Fact]
    public async Task TheWindowStyle_IsApplied_BeforeTheFirstShow_AfterEveryShow_AndWhenTopmostChanges()
    {
        var (calls, shownAfter) = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                rig.Controller.Dispose();
                var style = new RecordingStyle();
                var viewModel = new FloatingOrbViewModel(rig.Harness.Recorder, new FloatingOrbActions(() => Task.CompletedTask, () => Task.CompletedTask, () => { }), post: action => action());
                using var controller = new FloatingOrbController(
                    rig.Main,
                    rig.Shell,
                    viewModel,
                    vm => new FloatingOrbWindow { DataContext = vm, WorkAreaSource = () => rig.Areas },
                    style,
                    _ => 4242);
                style.IsShown = () => controller.Window is { IsVisible: true };

                await rig.StartRecordingAsync();
                UiHarness.Pump(4);
                style.Step = "topmost";
                controller.Window!.Topmost = false;
                controller.Window.Topmost = true;
                style.Step = "hidden";
                await rig.Harness.Recorder.StopAsync(Ct);
                UiHarness.Pump(2);
                style.Step = "shown again";
                await rig.StartRecordingAsync();
                UiHarness.Pump(4);
                return (style.Calls.ToArray(), controller.Window is { IsVisible: true });
            },
            Ct);

        Assert.All(calls, call => Assert.Equal((nint)4242, call.Handle));
        Assert.Equal(("create", false), (calls[0].Step, calls[0].Shown));
        Assert.Contains(calls, call => call.Step == "create" && call.Shown);
        Assert.Equal(2, calls.Count(call => call.Step == "topmost"));
        Assert.Contains(calls, call => call.Step == "shown again" && call.Shown);
        Assert.DoesNotContain(calls, call => call.Step == "hidden");
        Assert.True(shownAfter);
    }

    [Fact]
    public async Task TheOnboardingPage_NeverShowsTheOrb_WhateverTheStateOfTheWindow()
    {
        var seen = new List<(string Step, bool Created)>();
        await Session.Dispatch(
            async () =>
            {
                using var english = UiCulture.Override("en");
                using var rig = new OnboardingRig();
                var main = new MainWindow { DataContext = rig.Shell, Width = 1100, Height = 720 };
                main.Show();
                UiHarness.Pump();
                var viewModel = new FloatingOrbViewModel(rig.Recorder, new FloatingOrbActions(() => Task.CompletedTask, () => Task.CompletedTask, () => { }), post: action => action());
                using var controller = new FloatingOrbController(main, rig.Shell, viewModel, vm => new FloatingOrbWindow { DataContext = vm, WorkAreaSource = () => [Screen] });
                Assert.IsType<OnboardingViewModel>(rig.Shell.CurrentPage);

                await rig.Recorder.StartAsync(new RecordingOptions(), Ct);
                UiHarness.Pump(3);
                seen.Add(("onboarding", controller.Window is { IsVisible: true }));

                main.WindowState = WindowState.Minimized;
                UiHarness.Pump(3);
                seen.Add(("onboarding, minimised", controller.Window is { IsVisible: true }));

                main.Hide();
                UiHarness.Pump(3);
                seen.Add(("onboarding, hidden", controller.Window is { IsVisible: true }));
                main.Close();
            },
            Ct);

        Assert.All(seen, item => Assert.False(item.Created, item.Step));
        Assert.Equal(3, seen.Count);
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

    [Fact]
    public async Task WhenTheScreensChange_TheOrbIsPulledIntoTheRemainingWorkArea_AndReturnsWhenTheMonitorIsBack_WithoutSaving()
    {
        var (gone, back, saved, windowGone) = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                var second = new WorkArea(1920, 0, 1920, 1080, 1.0);
                rig.Areas = [Screen, second];
                rig.Settings.SetCurrentSilently(new AppSettings { FloatingOrbPosition = new FloatingOrbPosition(2500, 400) });
                await rig.StartRecordingAsync();
                UiHarness.Pump(4);
                var window = rig.Window;

                rig.Areas = [Screen];
                window.Reposition();
                UiHarness.Pump(2);
                var gone = window.OrbAnchor;
                var circle = window.FindControl<Border>("Circle")!;
                var windowGone = window.PointToScreen(circle.TranslatePoint(default, window)!.Value);

                rig.Areas = [Screen, second];
                window.Reposition();
                UiHarness.Pump(2);
                return (gone, window.OrbAnchor, rig.Settings.Current.FloatingOrbPosition, windowGone);
            },
            Ct);

        Assert.Equal(new PixelPoint(1920 - 44, 400), gone);
        Assert.Equal(gone, windowGone);
        Assert.Equal(new PixelPoint(2500, 400), back);
        Assert.Equal(new FloatingOrbPosition(2500, 400), saved);
    }

    [Fact]
    public async Task AfterADrag_AScreenChangeKeepsTheDraggedSpot_NotTheSavedOne()
    {
        var anchor = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                await rig.StartRecordingAsync();
                UiHarness.Pump(4);
                var window = rig.Window;
                var start = window.OrbAnchor;
                var centre = CircleCentre(window);
                window.MouseDown(centre, MouseButton.Left);
                window.MouseMove(centre + new Vector(-40, -30), RawInputModifiers.LeftMouseButton);
                window.MouseUp(centre + new Vector(-40, -30), MouseButton.Left);
                UiHarness.Pump(2);
                var dragged = window.OrbAnchor;

                // The saved value is replaced behind the window's back (the save is still pending, say): the drag wins.
                rig.Settings.SetCurrentSilently(new AppSettings());
                window.Reposition();
                UiHarness.Pump(2);
                return (start, dragged, window.OrbAnchor);
            },
            Ct);

        Assert.Equal(anchor.start + new PixelPoint(-40, -30), anchor.dragged);
        Assert.Equal(anchor.dragged, anchor.Item3);
    }

    [Fact]
    public async Task NearTheLeftEdgeOfItsScreen_ThePillOpensToTheRight_AndTheWindowStaysOnThatScreen()
    {
        var (right, circleScreen, anchor, windowLeft, pillLeft, circleRight, chipLeft, circleLeft, size) = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                var second = new WorkArea(1920, 0, 1920, 1080, 1.0);
                rig.Areas = [Screen, second];

                // 30 px into the right monitor: with the pill to the left the window would reach 122 px into the left one.
                rig.Settings.SetCurrentSilently(new AppSettings { FloatingOrbPosition = new FloatingOrbPosition(1950, 400) });
                await rig.StartRecordingAsync();
                UiHarness.Pump(4);
                var window = rig.Window;
                rig.ViewModel.IsPillOpen = true;
                rig.ViewModel.MarkCommand.Execute(null);
                UiHarness.Pump(4);

                var pill = window.FindControl<Border>("Pill")!;
                var circle = window.FindControl<Border>("Circle")!;
                var chip = window.FindControl<Border>("Chip")!;
                var circleTopLeft = circle.TranslatePoint(default, window)!.Value;
                return (
                    window.IsPillOnRight,
                    window.PointToScreen(circleTopLeft),
                    window.OrbAnchor,
                    window.Position.X,
                    pill.TranslatePoint(default, window)!.Value.X,
                    circleTopLeft.X + circle.Bounds.Width,
                    chip.TranslatePoint(default, window)!.Value.X,
                    circleTopLeft.X,
                    window.Bounds.Size);
            },
            Ct);

        Assert.True(right);
        Assert.Equal(new PixelPoint(1950, 400), anchor);
        Assert.Equal(anchor, circleScreen);
        Assert.True(windowLeft >= 1920 - 4, "Only the shadow margin may reach into the left monitor.");
        Assert.Equal(circleRight + 6, pillLeft, 0.5);
        Assert.Equal(circleLeft, chipLeft, 0.5);
        Assert.Equal(new Size(200, 124), size);
    }

    [Fact]
    public async Task DraggingTheOrbToTheLeftEdgeAndBack_MovesThePillToTheRightAndBack()
    {
        var (atEdge, back) = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                rig.Settings.SetCurrentSilently(new AppSettings { FloatingOrbPosition = new FloatingOrbPosition(400, 400) });
                await rig.StartRecordingAsync();
                UiHarness.Pump(4);
                var window = rig.Window;

                bool Drag(Vector by)
                {
                    var centre = CircleCentre(window);
                    window.MouseDown(centre, MouseButton.Left);
                    window.MouseMove(centre + by, RawInputModifiers.LeftMouseButton);
                    window.MouseUp(centre + by, MouseButton.Left);
                    UiHarness.Pump(4);
                    return window.IsPillOnRight;
                }

                var edge = Drag(new Vector(-380, 0));
                var middle = Drag(new Vector(400, 0));
                return (edge, middle);
            },
            Ct);

        Assert.True(atEdge);
        Assert.False(back);
    }

    // ---- click and drag ----------------------------------------------------------------------------------------------------

    private static Point CircleCentre(FloatingOrbWindow window)
    {
        var circle = window.FindControl<Border>("Circle")!;
        return circle.TranslatePoint(new Point(circle.Bounds.Width / 2, circle.Bounds.Height / 2), window)!.Value;
    }

    [Fact]
    public async Task AClickOnTheCircle_OpensThePillToItsLeft_AndTheWindowNeitherMovesNorResizes()
    {
        var (open, pillRight, circleLeft, anchorBefore, anchorAfter, circleScreen, windowBefore, windowAfter) = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                await rig.StartRecordingAsync();
                UiHarness.Pump(4);
                var window = rig.Window;
                var before = window.OrbAnchor;
                var windowBefore = (window.Position, window.Bounds.Size);
                var centre = CircleCentre(window);

                window.MouseDown(centre, MouseButton.Left);
                window.MouseUp(centre, MouseButton.Left);
                UiHarness.Pump(4);

                var pill = window.FindControl<Border>("Pill")!;
                var circle = window.FindControl<Border>("Circle")!;
                var circleTopLeft = circle.TranslatePoint(default, window)!.Value;
                var pillRightEdge = pill.TranslatePoint(new Point(pill.Bounds.Width, 0), window)!.Value.X;
                var screenPoint = window.PointToScreen(circleTopLeft);
                return (rig.ViewModel.IsPillOpen, pillRightEdge, circleTopLeft.X, before, window.OrbAnchor, screenPoint, windowBefore, (window.Position, window.Bounds.Size));
            },
            Ct);

        Assert.True(open);
        Assert.Equal(anchorBefore, anchorAfter);
        Assert.Equal(anchorAfter, circleScreen);
        Assert.Equal(windowBefore, windowAfter);
        Assert.Equal(circleLeft - 6, pillRight, 0.5);
    }

    [Fact]
    public async Task TheWindow_KeepsOneSize_ThroughPillAndChip_AndOnlyTheShownShapesTakeClicks()
    {
        var (sizes, positions, closedRegion, pillRegion, chipRegion, styleRegions, slots) = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                rig.Controller.Dispose();
                var style = new RecordingStyle();
                var viewModel = new FloatingOrbViewModel(rig.Harness.Recorder, new FloatingOrbActions(() => Task.CompletedTask, () => Task.CompletedTask, () => { }), rig.Marks, post: action => action());
                using var controller = new FloatingOrbController(
                    rig.Main,
                    rig.Shell,
                    viewModel,
                    vm => new FloatingOrbWindow { DataContext = vm, WorkAreaSource = () => rig.Areas },
                    style,
                    _ => 4242);

                await rig.StartRecordingAsync();
                UiHarness.Pump(4);
                var window = controller.Window!;
                var sizes = new List<Size> { window.Bounds.Size };
                var positions = new List<PixelPoint> { window.Position };
                var closed = window.InputRegion.ToArray();

                viewModel.IsPillOpen = true;
                UiHarness.Pump(4);
                sizes.Add(window.Bounds.Size);
                positions.Add(window.Position);
                var pill = window.InputRegion.ToArray();

                viewModel.MarkCommand.Execute(null);
                UiHarness.Pump(4);
                sizes.Add(window.Bounds.Size);
                positions.Add(window.Position);
                var chip = window.InputRegion.ToArray();

                viewModel.IsPillOpen = false;
                UiHarness.Pump(4);
                sizes.Add(window.Bounds.Size);
                positions.Add(window.Position);
                return (sizes, positions, closed, pill, chip, style.Regions.ToList(), (window.PillSlot, window.ChipSlot));
            },
            Ct);

        Assert.Single(sizes.Distinct());
        Assert.Single(positions.Distinct());

        // 4 px shadow margin + 148 pill slot (142 pill + 6 gap) + 44 circle + 4; 4 + 36 chip slot + 44 + 36 + 4.
        Assert.Equal(new Size(200, 124), sizes[0]);
        Assert.Equal((148.0, 36.0), slots);

        // Closed: the circle with its margin only, at the right of the middle row.
        Assert.Equal([new FloatingWindowRect(148, 36, 52, 52)], closedRegion);

        // Open: the pill (40 high, centred on the circle's row) with its margin as well.
        Assert.Equal(2, pillRegion.Length);
        Assert.Contains(new FloatingWindowRect(0, 38, 150, 48), pillRegion);

        // With the chip: three parts, the chip's under the circle (the default spot is near the bottom, so it may flip above).
        Assert.Equal(3, chipRegion.Length);
        Assert.All(chipRegion, part => Assert.True(part.X >= 0 && part.Y >= 0 && part.Right <= 200 && part.Bottom <= 124));

        // Every change reached the platform style (and the region is sent again when the window opens).
        Assert.Contains(styleRegions, region => region.SequenceEqual(closedRegion));
        Assert.Contains(styleRegions, region => region.SequenceEqual(pillRegion));
        Assert.Contains(styleRegions, region => region.SequenceEqual(chipRegion));
    }

    [Theory]
    [InlineData(1.0, 0, 0, 52, 52)]
    [InlineData(1.5, 0, 0, 78, 78)]
    [InlineData(1.25, 0, 0, 65, 65)]
    public void TheInputRegion_GrowsEachShapeByTheShadowMargin_AndRoundsOutwards(double scale, int x, int y, int width, int height)
    {
        var part = FloatingOrbInputRegion.ToPixels(new Rect(4, 4, 44, 44), new Thickness(4), new Rect(0, 0, 200, 124), scale);

        Assert.Equal(new FloatingWindowRect(x, y, width, height), part);
    }

    [Fact]
    public void TheInputRegion_StaysInsideTheWindow_AndAShapeOutsideItIsEmpty()
    {
        var clipped = FloatingOrbInputRegion.ToPixels(new Rect(2, 100, 50, 30), new Thickness(4), new Rect(0, 0, 200, 124), 1);
        var outside = FloatingOrbInputRegion.ToPixels(new Rect(300, 0, 10, 10), new Thickness(4), new Rect(0, 0, 200, 124), 1);

        Assert.Equal(new FloatingWindowRect(0, 96, 56, 28), clipped);
        Assert.Equal(default, outside);
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

    // ---- the moving orb ----------------------------------------------------------------------------------------------------

    private static FloatingOrbDisc DiscOf(FloatingOrbWindow window) => window.FindControl<FloatingOrbDisc>("Disc")!;

    [Fact]
    public async Task WhileRecording_TheOrbLoopsOnTheCompositor_StartedOnce_AndNoOrbRunsItsOwn()
    {
        var (moving, looping, starts, orbTimers, angle) = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                await rig.StartRecordingAsync();
                UiHarness.Pump(30);
                var disc = DiscOf(rig.Window);
                return (disc.IsMoving, disc.IsLooping, disc.Starts, rig.Window.GetVisualDescendants().OfType<GlassOrbControl>().Count(o => o.IsFrameLoopRunning), disc.Angle);
            },
            Ct);

        Assert.True(moving);
        Assert.True(looping);
        Assert.Equal(1, starts);
        Assert.Equal(0, orbTimers);

        // The compositor turns it: the UI-side angle is never written per frame.
        Assert.Equal(0f, angle);
    }

    [Fact]
    public async Task ADiscThatStartsAtItsFirstArrange_TurnsAndScalesAboutItsCentre()
    {
        var centres = await Session.Dispatch(
            () =>
            {
                // Active before it is shown: the loops start from the first arrange, when Bounds is still empty.
                var disc = new FloatingOrbDisc { Width = 28, Height = 28, IsActive = true };
                var window = new Window { Width = 100, Height = 100, Content = disc };
                window.Show();
                UiHarness.Pump(3);
                var result = disc.GetVisualChildren().Select(child => ElementComposition.GetElementVisual(child)!.CenterPoint).ToArray();
                var looping = disc.IsLooping;
                window.Close();
                return (result, looping);
            },
            Ct);

        Assert.True(centres.looping);
        Assert.All(centres.result, centre => Assert.Equal(new Vector3D(14, 14, 0), centre));
    }

    [Fact]
    public async Task AHiddenOrMinimisedWindow_StopsTheLoops_AndShowingItAgainStartsThem()
    {
        var (hidden, shownAgain, angle, ring) = await Session.Dispatch(
            () =>
            {
                var disc = new FloatingOrbDisc { Width = 28, Height = 28, IsActive = true };
                var window = new Window { Width = 100, Height = 100, Content = disc };
                window.Show();
                UiHarness.Pump(3);

                window.Hide();
                disc.Gate.Check();
                var hiddenLooping = disc.IsLooping;
                var restAngle = disc.Angle;
                var restRing = disc.RingOpacity;

                window.Show();
                UiHarness.Pump(3);
                disc.Gate.Check();
                UiHarness.Pump(2);
                var again = disc.IsLooping;
                window.Close();
                return (hiddenLooping, again, restAngle, restRing);
            },
            Ct);

        Assert.False(hidden);
        Assert.True(shownAgain);
        Assert.Equal(0f, angle);
        Assert.Equal(0d, ring);
    }

    [Fact]
    public async Task TheCompositor_MovesTheOrbAndTheRing_WithoutAnyUiWork()
    {
        var (moved, ringOut, ringAtRest, restAgain) = await Session.Dispatch(
            () =>
            {
                var disc = new FloatingOrbDisc { Width = 120, Height = 120 };
                var window = new Window { Width = 300, Height = 300, Content = disc, Background = Brushes.White };
                window.Show();
                UiHarness.Pump(3);
                UiHarness.RedrawWhole(window);
                var rest = UiHarness.Pixels(window);
                disc.IsActive = true;
                UiHarness.Pump(2);

                // Real time passes on the render clock; the UI thread only lets the headless renderer draw. 0.6 s into the ping the
                // ring is about 1.45 times the orb: a point 75 px from the centre (the orb's radius is 60) is tinted.
                Thread.Sleep(600);
                UiHarness.Pump(2);
                var moving = UiHarness.Pixels(window);
                disc.IsActive = false;
                UiHarness.Pump(2);

                // Drawn whole: the moving ring's dirty rectangles can leave anti-aliasing residue in the kept frame.
                UiHarness.RedrawWhole(window);
                var after = UiHarness.Pixels(window);
                window.Close();
                return (!moving.AsSpan().SequenceEqual(rest), !IsWhite(moving, 300, 150 + 75, 150), IsWhite(rest, 300, 150 + 75, 150), after.AsSpan().SequenceEqual(rest));
            },
            Ct);

        Assert.True(moved, "the running loops turn the orb on the compositor");
        Assert.True(ringAtRest, "the ring is not seen at rest");
        Assert.True(ringOut, "the ring goes out around the orb");
        Assert.True(restAgain, "a stopped orb is back at its rest frame");
    }

    private static bool IsWhite(byte[] pixels, int width, int x, int y)
    {
        var i = ((y * width) + x) * 4;
        return pixels[i] == 255 && pixels[i + 1] == 255 && pixels[i + 2] == 255;
    }

    [Fact]
    public async Task Paused_TheOrbIsStill_AndGrey_AndTheRingIsGone()
    {
        var (moving, wash, angle, opacity) = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                await rig.StartRecordingAsync();
                UiHarness.Pump(4);
                await rig.Harness.Recorder.PauseAsync(Ct);
                UiHarness.Pump(4);
                var disc = DiscOf(rig.Window);
                return (disc.IsMoving, rig.Window.FindControl<Ellipse>("PausedWash")!.IsVisible, disc.Angle, disc.RingOpacity);
            },
            Ct);

        Assert.False(moving);
        Assert.True(wash);
        Assert.Equal(0f, angle);
        Assert.Equal(0d, opacity);
    }

    [Fact]
    public async Task UnderReducedMotion_NothingLoops_AndNothingIsGrey()
    {
        var (moving, wash) = await Session.Dispatch(
            async () =>
            {
                using var rig = new Rig();
                rig.ViewModel.ReduceMotion = true;
                await rig.StartRecordingAsync();
                UiHarness.Pump(6);
                return (DiscOf(rig.Window).IsMoving || DiscOf(rig.Window).IsLooping, rig.Window.FindControl<Ellipse>("PausedWash")!.IsVisible);
            },
            Ct);

        Assert.False(moving);
        Assert.False(wash);
    }

    [Fact]
    public async Task TheDisc_StartsWhenActivated_StopsWhenNot_AndNeverTwiceInARow()
    {
        var (afterOn, afterOff, afterOnAgain, starts, reduced) = await Session.Dispatch(
            () =>
            {
                var disc = new FloatingOrbDisc { Width = 28, Height = 28 };
                var window = new Window { Width = 100, Height = 100, Content = disc };
                window.Show();
                UiHarness.Pump();

                disc.IsActive = true;
                UiHarness.Pump(3);
                var on = disc.IsMoving;
                disc.IsActive = true;
                UiHarness.Pump(3);
                disc.IsActive = false;
                UiHarness.Pump(3);
                var off = disc.IsMoving;
                disc.IsActive = true;
                UiHarness.Pump(3);
                var again = disc.IsMoving;

                Motion.SetReduceMotion(window, true);
                UiHarness.Pump(3);
                var reducedRuns = disc.IsMoving;
                window.Close();
                return (on, off, again, disc.Starts, reducedRuns);
            },
            Ct);

        Assert.True(afterOn);
        Assert.False(afterOff);
        Assert.True(afterOnAgain);
        Assert.Equal(2, starts);
        Assert.False(reduced);
    }

    [Fact]
    public async Task UnderATestClock_TheOrbTurnsOnceInNineSeconds_Linear_AndThePingRingGrowsAndFadesInTheFirstSixtyPercentOfThreeSeconds()
    {
        var samples = await Session.Dispatch(
            () =>
            {
                var clock = new TestTime(UiHarness.Now);
                var disc = new FloatingOrbDisc { Width = 28, Height = 28, Clock = clock };
                var window = new Window { Width = 100, Height = 100, Content = disc };
                window.Show();
                UiHarness.Pump();
                disc.IsActive = true;
                UiHarness.Pump(2);

                var result = new List<(double Seconds, float Angle, double Scale, double Opacity)>();
                foreach (var seconds in new[] { 0.0, 1.0, 2.25, 4.5, 8.99 })
                {
                    disc.Clock = clock;
                    clock.Advance(TimeSpan.FromSeconds(seconds));
                    disc.Tick();
                    result.Add((seconds, disc.Angle!.Value, disc.RingScale!.Value, disc.RingOpacity!.Value));
                }

                window.Close();
                return result;
            },
            Ct);

        // The angle is the share of nine seconds, in radians.
        Assert.Equal(0f, samples[0].Angle, 0.01f);
        Assert.Equal((float)(Math.Tau * 1 / 9), samples[1].Angle, 0.01f);
        Assert.Equal((float)(Math.Tau * 2.25 / 9), samples[2].Angle, 0.01f);
        Assert.Equal((float)(Math.Tau * 4.5 / 9), samples[3].Angle, 0.01f);
        Assert.Equal((float)(Math.Tau * 8.99 / 9), samples[4].Angle, 0.01f);

        // The ring: out at 0 s (full size, 0.55), grown to 1.9 and gone by 1.8 s, resting for the other 1.2 s of the three.
        Assert.Equal(1.0, samples[0].Scale, 0.001);
        Assert.Equal(0.55, samples[0].Opacity, 0.01);
        Assert.InRange(samples[1].Scale, 1.0, 1.9);
        Assert.InRange(samples[1].Opacity, 0.0, 0.55);

        // 1 s is 5/9 of the ring's 1.8 s: CSS ease-out of the key frames, the same curve the compositor runs.
        var eased = CompositionLoop.Ease(LoopEasing.EaseOut, 1.0 / 1.8);
        Assert.Equal(1 + (0.9 * eased), samples[1].Scale, 0.001);
        Assert.Equal(0.55 * (1 - eased), samples[1].Opacity, 0.001);

        // 2.25 s is 75 % into a ping period of 3 s, in the last 40 %: the ring holds its end frame (1.9, not seen), like the CSS.
        Assert.Equal(1.9, samples[2].Scale, 0.001);
        Assert.Equal(0.0, samples[2].Opacity, 0.001);
    }

    [Fact]
    public async Task ATestClockFrame_AllocatesNothing_InTheRingsActivePartAndInItsRest()
    {
        var (activeBytes, restBytes) = await Session.Dispatch(
            () =>
            {
                var clock = new TestTime(UiHarness.Now);
                var disc = new FloatingOrbDisc { Width = 28, Height = 28, Clock = clock };
                var window = new Window { Width = 100, Height = 100, Content = disc };
                window.Show();
                UiHarness.Pump();
                disc.IsActive = true;
                UiHarness.Pump(2);

                // Warm up both branches, then measure many ticks of each.
                for (var i = 0; i < 50; i++)
                {
                    clock.Advance(TimeSpan.FromMilliseconds(40));
                    disc.Tick();
                }

                disc.Clock = clock;
                var active = Measure(disc, clock, TimeSpan.FromMilliseconds(5), 200);
                clock.Advance(TimeSpan.FromSeconds(1.2));
                var rest = Measure(disc, clock, TimeSpan.FromMilliseconds(1), 200);
                window.Close();
                return (active, rest);
            },
            Ct);

        Assert.Equal(0, activeBytes);
        Assert.Equal(0, restBytes);
    }

    private static long Measure(FloatingOrbDisc disc, TestTime clock, TimeSpan step, int ticks)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < ticks; i++)
        {
            clock.Advance(step);
            disc.Tick();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void TheTurnTakesNineSeconds_AndThePingIsTheMockupsKeyFramesEveryThree()
    {
        Assert.Equal(TimeSpan.FromSeconds(9), FloatingOrbDisc.SpinPeriod);
        Assert.Equal(TimeSpan.FromSeconds(3), FloatingOrbDisc.PingPeriod);

        // @keyframes ping{0%{transform:scale(1);opacity:.55}60%{transform:scale(1.9);opacity:0}100%{transform:scale(1.9);opacity:0}}, ease-out.
        Assert.Equal([new LoopKey(0, 1), new LoopKey(0.6f, 1.9f, LoopEasing.EaseOut), new LoopKey(1, 1.9f, LoopEasing.EaseOut)], FloatingOrbDisc.PingScaleKeys);
        Assert.Equal([new LoopKey(0, 0.55f), new LoopKey(0.6f, 0, LoopEasing.EaseOut), new LoopKey(1, 0, LoopEasing.EaseOut)], FloatingOrbDisc.PingOpacityKeys);
        var (scale, opacity) = FloatingOrbDisc.PingAt(2.5);
        Assert.Equal(1.9, scale, 0.0001);
        Assert.Equal(0, opacity, 0.0001);
    }

    // ---- the pictures ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Light", "recording")]
    [InlineData("Dark", "recording")]
    [InlineData("Light", "pill")]
    [InlineData("Dark", "pill")]
    [InlineData("Light", "ping")]
    [InlineData("Dark", "ping")]
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

                if (scene == "ping")
                {
                    // A frame in the middle of a ping: 0.6 s into the three seconds, the ring is bigger than the circle's orb.
                    var clock = new TestTime(UiHarness.Now);
                    var disc = DiscOf(rig.Window);
                    disc.Clock = clock;
                    clock.Advance(TimeSpan.FromSeconds(0.6));
                    disc.Tick();
                }

                UiHarness.Pump(6);
                UiHarness.Save(rig.Window, path);
            },
            Ct);

        Assert.True(new FileInfo(path).Length > 500, "Screenshot looks empty.");
    }
}
