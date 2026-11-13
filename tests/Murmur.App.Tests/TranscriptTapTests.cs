using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Murmur.App.Controls;

namespace Murmur.App.Tests;

/// <summary>#85: on touch and pen the transcript seeks on a tap (press and release without a scroll), not on the press.</summary>
public sealed class TranscriptTapTests
{
    private const double Slop = 8;

    private static readonly Point Origin = new(100, 50);

    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The state machine -------------------------------------------------------------------------------------------------

    [Fact]
    public void ATap_SeeksToTheWordUnderThePress_OnRelease()
    {
        var tap = new TapGesture();
        tap.Press(1, Origin, 4);
        Assert.True(tap.IsActive);

        tap.Move(1, Origin + new Vector(2, -3), Slop);
        Assert.Equal(4, tap.Release(1, Origin + new Vector(2, -3), Slop));
        Assert.False(tap.IsActive);
    }

    [Fact]
    public void AMoveBeyondTheSlop_IsADrag_EvenWhenItComesBack()
    {
        var tap = new TapGesture();
        tap.Press(1, Origin, 4);
        tap.Move(1, Origin + new Vector(0, 30), Slop);
        Assert.False(tap.IsActive);

        tap.Move(1, Origin, Slop);
        Assert.Equal(-1, tap.Release(1, Origin, Slop));
    }

    [Fact]
    public void AReleaseBeyondTheSlop_IsNoTap()
    {
        var tap = new TapGesture();
        tap.Press(1, Origin, 4);
        Assert.Equal(-1, tap.Release(1, Origin + new Vector(Slop + 1, 0), Slop));
    }

    [Fact]
    public void ACancel_IsNoTap_AndAReleaseAfterItDoesNothing()
    {
        var tap = new TapGesture();
        tap.Press(1, Origin, 4);
        tap.Cancel();
        Assert.Equal(-1, tap.Release(1, Origin, Slop));
    }

    [Fact]
    public void ASecondContact_CancelsTheGesture()
    {
        var tap = new TapGesture();
        tap.Press(1, Origin, 4);
        tap.Press(2, Origin, 5);
        Assert.False(tap.IsActive);
        Assert.Equal(-1, tap.Release(1, Origin, Slop));
        Assert.Equal(-1, tap.Release(2, Origin, Slop));
    }

    [Fact]
    public void AnotherPointersMovesAndReleases_AreIgnored()
    {
        var tap = new TapGesture();
        tap.Press(1, Origin, 4);
        tap.Move(2, Origin + new Vector(100, 100), Slop);
        Assert.Equal(-1, tap.Release(2, Origin, Slop));
        Assert.Equal(4, tap.Release(1, Origin, Slop));
    }

    [Fact]
    public void ATapNoWord_IsNoSeek()
    {
        var tap = new TapGesture();
        tap.Press(1, Origin, -1);
        Assert.Equal(-1, tap.Release(1, Origin, Slop));
    }

    // The control -------------------------------------------------------------------------------------------------------

    private const string Line = "Does it run on the parquet model fully offline";

    private static TranscriptTextControl Control(List<int> sought)
    {
        var starts = new List<int>();
        var lengths = new List<int>();
        for (var at = 0; at < Line.Length;)
        {
            var end = Line.IndexOf(' ', at);
            if (end < 0)
            {
                end = Line.Length;
            }

            starts.Add(at);
            lengths.Add(end - at);
            at = end + 1;
        }

        return new TranscriptTextControl
        {
            Text = Line,
            WordStarts = [.. starts],
            WordLengths = [.. lengths],
            FontFamily = new FontFamily("Inter"),
            FontSize = 13,
            LineHeight = 20,
            Foreground = Brushes.Gray,
            Command = new CommunityToolkit.Mvvm.Input.RelayCommand<int>(sought.Add),
        };
    }

    /// <summary>The centre of word <paramref name="word"/> in window coordinates.</summary>
    private static Point Centre(TranscriptTextControl control, Window window, int word)
    {
        var start = Line.Split(' ').Take(word).Sum(w => w.Length + 1);
        var rect = control.CurrentLayout!.HitTestTextRange(start, Line.Split(' ')[word].Length).First();
        return control.TranslatePoint(rect.Center, window)!.Value;
    }

    private static async Task InWindow(Action<TranscriptTextControl, Window, List<int>> body)
    {
        await Session.Dispatch(
            () =>
            {
                var sought = new List<int>();
                var control = Control(sought);
                var window = new Window { Width = 900, Height = 60, Content = control };
                window.Show();
                UiHarness.Pump();
                try
                {
                    body(control, window, sought);
                }
                finally
                {
                    window.Close();
                }
            },
            Ct);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AMouseClick_StillSeeks_OnThePress()
    {
        await InWindow((control, window, sought) =>
        {
            var at = Centre(control, window, 5);
            window.MouseDown(at, MouseButton.Left);
            Assert.Equal([5], sought);
            window.MouseUp(at, MouseButton.Left);
            Assert.Equal([5], sought);
        });
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ATouchTap_SeeksOnTheRelease_NotOnThePress()
    {
        await InWindow((control, window, sought) =>
        {
            var at = Centre(control, window, 5);
            var touch = window.TouchBegin(at);
            Assert.Empty(sought);
            window.TouchEnd(touch, at);
            Assert.Equal([5], sought);
        });
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ATouchTap_WithAFewDipsOfJitter_StillSeeks()
    {
        await InWindow((control, window, sought) =>
        {
            var at = Centre(control, window, 2);
            var touch = window.TouchBegin(at);
            window.TouchMove(touch, at + new Vector(2, 1));
            window.TouchEnd(touch, at + new Vector(2, 1));
            Assert.Equal([2], sought);
        });
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ATouchDragBeyondTheSlop_DoesNotSeek()
    {
        await InWindow((control, window, sought) =>
        {
            var at = Centre(control, window, 5);
            var touch = window.TouchBegin(at);
            window.TouchMove(touch, at + new Vector(0, 40));
            window.TouchEnd(touch, at + new Vector(0, 40));
            Assert.Empty(sought);
        });
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ATouchDragThatReturnsToThePress_DoesNotSeek()
    {
        await InWindow((control, window, sought) =>
        {
            var at = Centre(control, window, 5);
            var touch = window.TouchBegin(at);
            window.TouchMove(touch, at + new Vector(0, 40));
            window.TouchMove(touch, at);
            window.TouchEnd(touch, at);
            Assert.Empty(sought);
        });
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ATouchWhoseCaptureIsLost_DoesNotSeek()
    {
        await InWindow((control, window, sought) =>
        {
            var at = Centre(control, window, 5);
            IPointer? pointer = null;
            control.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer, RoutingStrategies.Bubble, handledEventsToo: true);
            var touch = window.TouchBegin(at);
            // What a ScrollViewer does when it takes the gesture over.
            Assert.NotNull(pointer);
            Assert.Same(control, pointer.Captured);
            pointer.Capture(window);

            // Even a release that still reaches the control is no tap now.
            control.RaiseEvent(new PointerReleasedEventArgs(control, pointer, window, at, 3, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
            window.TouchEnd(touch, at);
            Assert.Empty(sought);
        });
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AScrollGesture_CancelsTheTap()
    {
        await InWindow((control, window, sought) =>
        {
            var at = Centre(control, window, 5);
            var touch = window.TouchBegin(at);
            control.RaiseEvent(new ScrollGestureEventArgs(1, new Vector(0, -20)) { RoutedEvent = InputElement.ScrollGestureEvent, Source = control });
            window.TouchEnd(touch, at);
            Assert.Empty(sought);
        });
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ATouchDragInAScrollViewer_DoesNotSeek()
    {
        await Session.Dispatch(
            () =>
            {
                var sought = new List<int>();
                var control = Control(sought);
                var scroller = new ScrollViewer
                {
                    Content = new StackPanel { Children = { control, new Border { Height = 600 } } },
                };
                var window = new Window { Width = 900, Height = 100, Content = scroller };
                window.Show();
                UiHarness.Pump();
                var at = Centre(control, window, 5);
                var touch = window.TouchBegin(at);
                Assert.Empty(sought);
                window.TouchMove(touch, at + new Vector(0, -20));
                window.TouchMove(touch, at + new Vector(0, -60));
                window.TouchEnd(touch, at + new Vector(0, -60));
                window.Close();
                Assert.Empty(sought);
            },
            Ct);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APenTap_SeeksOnTheRelease_AndAPenDragDoesNot()
    {
        await InWindow((control, window, sought) =>
        {
            var at = Centre(control, window, 6);

            // A pen the headless platform cannot simulate: the events are raised on the control the way the input manager does.
            var pen = new Pointer(Pointer.GetNextFreeId(), PointerType.Pen, isPrimary: true);
            var onPress = PenPress(control, window, pen, at);
            Assert.True(onPress);
            Assert.Empty(sought);
            PenRelease(control, window, pen, at);
            Assert.Equal([6], sought);

            var drag = new Pointer(Pointer.GetNextFreeId(), PointerType.Pen, isPrimary: true);
            PenPress(control, window, drag, at);
            PenMove(control, window, drag, at + new Vector(0, 40));
            PenRelease(control, window, drag, at + new Vector(0, 40));
            Assert.Equal([6], sought);
        });
    }

    private static bool PenPress(TranscriptTextControl control, Window window, Pointer pen, Point at)
    {
        var args = new PointerPressedEventArgs(control, pen, window, at, 1, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None);
        control.RaiseEvent(args);
        return !args.Handled;
    }

    private static void PenMove(TranscriptTextControl control, Window window, Pointer pen, Point at) =>
        control.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, control, pen, window, at, 2, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));

    private static void PenRelease(TranscriptTextControl control, Window window, Pointer pen, Point at) =>
        control.RaiseEvent(new PointerReleasedEventArgs(control, pen, window, at, 3, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
}
