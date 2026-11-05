using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Murmur.App.Controls;
using Murmur.App.Services;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Settings;

namespace Murmur.App.Tests;

/// <summary>"Follow along" (#56): the word chip, the auto-scroll rules, the switch and its stored state.</summary>
public sealed class FollowAlongTests : IDisposable
{
    private readonly SessionFixture _fixture = new();

    private static HeadlessUnitTestSession Session => UiHarness.Session;

    public void Dispose() => _fixture.Dispose();

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private SessionViewModel Create(FakeSettingsStore? store = null) =>
        new(_fixture.Record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action(),
            services: new SessionServices(Settings: store));

    private async Task<SessionViewModel> LoadAsync(int words = 120, FakeSettingsStore? store = null)
    {
        _fixture.UseTranscript(TranscriptFactory.Make(_fixture.Record.Id, words));
        var vm = Create(store);
        await vm.LoadAsync();
        return vm;
    }

    private async Task<ShellViewModel> OpenShellAsync()
    {
        var harness = new UiHarness();
        harness.Store.Add(_fixture.Record);
        var shell = harness.CreateShell(
            record => new SessionViewModel(record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action()),
            _fixture.Processing);
        await shell.Library.LoadAsync();
        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;
        return shell;
    }

    // The word and the line ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePlayhead_MarksTheWordBeingSpoken_AndOnlyThatOne()
    {
        _fixture.UseShortTranscript();
        var vm = Create();
        await vm.LoadAsync();
        vm.PlayPauseCommand.Execute(null);

        _fixture.Player.Position = S(10.6);
        vm.Tick();
        Assert.Equal(1, vm.Segments[0].VisibleWord);
        Assert.Equal(-1, vm.Segments[1].VisibleWord);
        Assert.True(vm.Segments[0].IsCurrent);

        _fixture.Player.Position = S(20.5);
        vm.Tick();
        Assert.Equal(-1, vm.Segments[0].VisibleWord);
        Assert.Equal(1, vm.Segments[1].VisibleWord);
        Assert.True(vm.Segments[1].IsCurrent);
        Assert.False(vm.Segments[0].IsCurrent);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TheMarkedWord_FollowsMediaTime_AtEverySpeed(double speed)
    {
        // The player reports media time, so the lookup does not depend on the speed: one second of wall time is `speed` seconds of media.
        _fixture.UseShortTranscript();
        var vm = Create();
        await vm.LoadAsync();
        vm.PlayPauseCommand.Execute(null);

        _fixture.Player.Position = S(10 + (speed * 1.1));
        vm.Tick();

        var expected = (int)Math.Floor(speed * 1.1 / 0.5);
        Assert.Equal(Math.Min(expected, 3), vm.Segments[0].VisibleWord);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public async Task TheMarkedWord_NeverLagsTheAudioByMoreThan100ms_WhenPolledAt33ms(double speed)
    {
        // AC: "matches the audio within 100 ms ... at every speed". The view polls every 33 ms of wall time (SessionView.PollInterval);
        // the player reports media time, so one wall millisecond is `speed` media milliseconds.
        const int pollMs = 33;
        const int toleranceMs = 100;
        var vm = await LoadAsync(words: 240);
        var timeline = new TranscriptTimeline(_fixture.Transcripts.Transcript!.Segments);
        vm.PlayPauseCommand.Execute(null);

        int Shown()
        {
            for (var i = 0; i < vm.Segments.Count; i++)
            {
                if (vm.Segments[i].VisibleWord >= 0)
                {
                    return vm.Segments[i].FirstWord + vm.Segments[i].VisibleWord;
                }
            }

            return -1;
        }

        var wallEnd = (int)(30_000 / speed);
        for (var wall = 0; wall <= wallEnd; wall++)
        {
            if (wall % pollMs == 0)
            {
                _fixture.Player.Position = TimeSpan.FromMilliseconds(wall * speed);
                vm.Tick();
            }

            if (wall < toleranceMs)
            {
                continue;
            }

            var shown = Shown();
            var spokenNow = timeline.FindWord(TimeSpan.FromMilliseconds(wall * speed));
            var spokenThen = timeline.FindWord(TimeSpan.FromMilliseconds((wall - toleranceMs) * speed));

            // The shown word is the one spoken now, or one spoken at most 100 ms (of wall time) ago; never one that is still to come.
            if (spokenNow >= 0)
            {
                Assert.True(shown <= spokenNow, $"Wall {wall} ms at {speed}x: the mark ({shown}) runs ahead of the audio ({spokenNow}).");
            }

            if (spokenThen >= 0)
            {
                Assert.True(shown >= spokenThen, $"Wall {wall} ms at {speed}x: the mark ({shown}) lags the audio ({spokenNow}) by more than {toleranceMs} ms.");
            }
        }
    }

    [Fact]
    public async Task ASeek_MovesTheMarkAtOnce_AlsoWhilePaused()
    {
        _fixture.UseShortTranscript();
        var vm = Create();
        await vm.LoadAsync();

        vm.Segments[1].SeekWordCommand.Execute(2);

        Assert.Equal(2, vm.Segments[1].ActiveWord);
        Assert.True(vm.Segments[1].IsCurrent);
        Assert.Equal(S(21), vm.Position);
    }

    [Fact]
    public async Task APausedPlayer_ShowsTheLine_NotTheWord()
    {
        _fixture.UseShortTranscript();
        var vm = Create();
        await vm.LoadAsync();

        vm.SeekTo(S(10.6));

        Assert.True(vm.Segments[0].IsCurrent);
        Assert.Equal(1, vm.Segments[0].ActiveWord);
        Assert.Equal(-1, vm.Segments[0].VisibleWord);
    }

    // The switch --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FollowAlong_IsOnByDefault_AndTheSwitchIsShown()
    {
        var vm = await LoadAsync();

        Assert.True(vm.FollowAlong);
        Assert.True(vm.ShowFollowAlong);
        Assert.True(vm.ShowFollowAlongSwitch);
    }

    [Fact]
    public async Task TheSwitch_IsHidden_WhenTheHostTurnsItOff_OrThereIsNoList()
    {
        var vm = await LoadAsync();
        vm.ShowFollowAlong = false;
        Assert.False(vm.ShowFollowAlongSwitch);

        _fixture.Transcripts.Transcript = null;
        var empty = Create();
        await empty.LoadAsync();
        Assert.False(empty.ShowFollowAlongSwitch);
    }

    [Fact]
    public async Task ToggleCommand_FlipsTheSwitch_AndTheMarkFollows()
    {
        _fixture.UseShortTranscript();
        var vm = Create();
        await vm.LoadAsync();
        vm.PlayPauseCommand.Execute(null);
        _fixture.Player.Position = S(10.6);
        vm.Tick();
        Assert.Equal(1, vm.Segments[0].VisibleWord);

        vm.ToggleFollowAlongCommand.Execute(null);

        Assert.False(vm.FollowAlong);
        Assert.Equal(-1, vm.Segments[0].VisibleWord);
        Assert.True(vm.Segments[0].IsCurrent);

        vm.ToggleFollowAlongCommand.Execute(null);

        Assert.True(vm.FollowAlong);
        Assert.Equal(1, vm.Segments[0].VisibleWord);
    }

    [Fact]
    public async Task TheChoice_IsStored_AndReadByTheNextRecording()
    {
        var store = new FakeSettingsStore();
        var vm = await LoadAsync(store: store);

        vm.ToggleFollowAlongCommand.Execute(null);
        await vm.LastFollowAlongSave;

        Assert.False(store.Current.FollowAlong);
        Assert.Equal(1, store.SaveCount);

        var next = Create(store);
        await next.LoadAsync();
        Assert.False(next.FollowAlong);

        next.ToggleFollowAlongCommand.Execute(null);
        await next.LastFollowAlongSave;
        Assert.True(store.Current.FollowAlong);
    }

    [Fact]
    public async Task ReadingTheStoredChoice_WritesNothing()
    {
        var store = new FakeSettingsStore(new AppSettings { FollowAlong = false });

        var vm = await LoadAsync(store: store);

        Assert.False(vm.FollowAlong);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task ASettingsStoreThatCannotBeWritten_KeepsTheSwitchForThisView()
    {
        var store = new FakeSettingsStore { SaveFailure = new IOException("disk full") };
        var vm = await LoadAsync(store: store);

        vm.ToggleFollowAlongCommand.Execute(null);
        await vm.LastFollowAlongSave;

        Assert.False(vm.FollowAlong);
    }

    [Fact]
    public void TheSettingsDefault_IsOn()
    {
        Assert.True(new AppSettings().FollowAlong);
    }

    // Auto-scroll -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithFollowAlongOff_ThePlaybackDoesNotMoveTheList_ButAnExplicitSeekDoes()
    {
        var vm = await LoadAsync();
        var requested = new List<int>();
        vm.ScrollToSegmentRequested += requested.Add;
        vm.FollowAlong = false;

        _fixture.Player.Position = vm.Segments[2].Start;
        vm.Tick();
        Assert.Empty(requested);
        Assert.True(vm.Segments[2].IsCurrent);

        // The waveform, a chapter, a time chip or a word: the user asked for that place, so it is brought into view as before.
        vm.SeekTo(vm.Segments[3].Start);
        Assert.Equal([3], requested);
        vm.Segments[4].SeekCommand.Execute(null);
        Assert.Equal([3, 4], requested);
        vm.BeginScrub();
        vm.ScrubTo(vm.Segments[5].Start / vm.Duration);
        Assert.Contains(5, requested);
        Assert.True(vm.Segments[5].IsCurrent);
    }

    [Fact]
    public async Task TurningFollowAlongOn_BringsTheCurrentLineBack_EvenAfterAManualScroll()
    {
        var vm = await LoadAsync();
        var requested = new List<int>();
        _fixture.Player.Position = vm.Segments[2].Start;
        vm.FollowAlong = false;
        vm.Tick();
        vm.NotifyUserScrolled();
        vm.ScrollToSegmentRequested += requested.Add;

        vm.FollowAlong = true;

        Assert.False(vm.AutoScrollSuspended);
        Assert.Equal([2], requested);
    }

    [Fact]
    public async Task AScrubOnTheWaveform_EndsTheManualScrollPause()
    {
        var vm = await LoadAsync();
        var requested = new List<int>();
        _fixture.Player.Position = vm.Segments[1].Start;
        vm.Tick();
        vm.NotifyUserScrolled();
        vm.ScrollToSegmentRequested += requested.Add;

        vm.BeginScrub();
        vm.ScrubTo(vm.Segments[4].Start / vm.Duration);

        Assert.False(vm.AutoScrollSuspended);
        Assert.Contains(4, requested);
    }

    [Theory]
    [InlineData(5_000, 300, 600, 100_000, 5_100)] // a row 300 below the top of the view goes to 200 (a third of 600)
    [InlineData(0, 50, 600, 100_000, 0)] // near the start: never before the start
    [InlineData(99_000, 700, 600, 100_000, 99_400)] // near the end: never past extent - viewport
    [InlineData(0, 500, 600, 300, 0)] // everything fits: nothing to scroll
    public void TheRow_IsPlacedAThirdOfTheViewportFromTheTop(double offset, double rowTop, double viewport, double extent, double expected)
    {
        Assert.Equal(expected, SessionView.AlignedOffset(offset, rowTop, viewport, extent), 3);
    }

    [Fact]
    public void TheReadingLine_IsAThird()
    {
        Assert.Equal(1.0 / 3, SessionView.ReadingLine, 9);
        Assert.Equal(200, SessionView.AlignedOffset(0, 400, 600, 10_000), 6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheListJumps_ToTheLine_AThirdFromTheTop_WithOrWithoutReducedMotion(bool reduceMotion)
    {
        _fixture.UseTranscript(TranscriptFactory.Make(_fixture.Record.Id, 10_000));
        var shell = await OpenShellAsync();

        var (offset, rowTop, viewport, rowHeight) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                Motion.SetReduceMotion(window, reduceMotion);
                var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "TranscriptScroller");
                var list = window.GetVisualDescendants().OfType<ItemsControl>().Single(v => v.Name == "Transcript");
                _fixture.Player.Position = shell.Session!.Segments[500].Start;
                shell.Session.Tick();
                UiHarness.Pump(2);

                var row = (Control)list.ContainerFromIndex(500)!;
                var top = row.TranslatePoint(default, scroller)!.Value.Y;
                var result = (scroller.Offset.Y, top, scroller.Viewport.Height, row.Bounds.Height);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        // The offset is at the target after the first frames: the list jumps, nothing animates in between.
        Assert.True(offset > 20_000, $"Offset: {offset}.");
        Assert.InRange(rowTop, (viewport / 3) - rowHeight, (viewport / 3) + rowHeight);
    }

    [Fact]
    public async Task TheScrollKeys_PauseTheAutoScroll()
    {
        _fixture.UseTranscript(TranscriptFactory.Make(_fixture.Record.Id, 10_000));
        var shell = await OpenShellAsync();

        var suspended = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "TranscriptScroller");
                var before = shell.Session!.AutoScrollSuspended;
                scroller.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.PageDown, Source = scroller });
                var result = (before, shell.Session.AutoScrollSuspended);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.False(suspended.before);
        Assert.True(suspended.Item2);
    }

    [Fact]
    public async Task ATouchDragGesture_PausesTheAutoScroll()
    {
        _fixture.UseTranscript(TranscriptFactory.Make(_fixture.Record.Id, 10_000));
        var shell = await OpenShellAsync();

        var (before, after) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "TranscriptScroller");
                var wasSuspended = shell.Session!.AutoScrollSuspended;
                scroller.RaiseEvent(new ScrollGestureEventArgs(1, new Vector(0, -20)) { RoutedEvent = InputElement.ScrollGestureEvent, Source = scroller });
                var result = (wasSuspended, shell.Session.AutoScrollSuspended);
                window.Close();
                return result;
            },
            TestContext.Current.CancellationToken);

        Assert.False(before);
        Assert.True(after);
    }

    // A touch press on a word seeks (and so ends the pause), which is why a touch drag is told apart by the scroll gesture and not by the press.
    [Fact]
    public async Task ATapOnALine_EndsThePause_AfterADrag()
    {
        var vm = await LoadAsync();
        _fixture.Player.Position = vm.Segments[1].Start;
        vm.Tick();
        vm.NotifyUserScrolled();

        vm.Segments[1].SeekWordCommand.Execute(0);

        Assert.False(vm.AutoScrollSuspended);
    }

    // The header ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1100, 720)]
    [InlineData(900, 600)]
    public async Task TheHeader_KeepsLongSpeakerNamesAwayFromTheSwitch(int width, int height)
    {
        using var ukrainian = Murmur.App.Localization.UiCulture.Override("uk");
        var names = new FakeSpeakerNameStore();
        names.Put(_fixture.Record.Id, new Murmur.Core.Sessions.SpeakerNames(new string('Ж', 40), new string('W', 40)));
        _fixture.UseTranscript(TranscriptFactory.Make(_fixture.Record.Id, 120));
        var harness = new UiHarness();
        harness.Store.Add(_fixture.Record);
        var shell = harness.CreateShell(
            record => new SessionViewModel(record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action(),
                services: new SessionServices(SpeakerNames: names)),
            _fixture.Processing);
        await shell.Library.LoadAsync();
        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;

        var (chips, toggle, windowWidth) = await Session.Dispatch(
            () =>
            {
                using var scope = Murmur.App.Localization.UiCulture.Override("uk");
                var window = new MainWindow { DataContext = shell, Width = width, Height = height };
                window.Show();
                UiHarness.Pump(4);
                var switchControl = window.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "FollowAlongSwitch");
                var switchBounds = new Rect(switchControl.TranslatePoint(default, window)!.Value, switchControl.Bounds.Size);
                var chipBounds = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("speakerChip") && b.IsEffectivelyVisible)
                    .Select(b => new Rect(b.TranslatePoint(default, window)!.Value, b.Bounds.Size)).ToArray();
                window.Close();
                return (chipBounds, switchBounds, window.Width);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, chips.Length);
        Assert.True(toggle.Right <= windowWidth, $"The switch ends at {toggle.Right} in a window {windowWidth} wide.");
        foreach (var chip in chips)
        {
            Assert.True(chip.Right <= toggle.Left || chip.Bottom <= toggle.Top || chip.Top >= toggle.Bottom, $"A chip {chip} runs into the switch {toggle}.");
        }
    }

    // No allocations ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task LookingUpTheWord_AtThePlayersRate_AllocatesNothing()
    {
        await LoadAsync(words: 10_000);
        var timeline = new TranscriptTimeline(_fixture.Transcripts.Transcript!.Segments);
        var positions = new TimeSpan[512];
        for (var i = 0; i < positions.Length; i++)
        {
            positions[i] = S(i * 2.3);
        }

        var sink = 0;
        for (var i = 0; i < positions.Length; i++)
        {
            sink += timeline.FindWord(positions[i]) + timeline.FindSegment(positions[i]);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var round = 0; round < 20; round++)
        {
            for (var i = 0; i < positions.Length; i++)
            {
                sink += timeline.FindWord(positions[i]) + timeline.FindSegment(positions[i]);
            }
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.NotEqual(int.MinValue, sink);
        Assert.Equal(0, allocated);
    }

    // The playhead is set outside the measured region: only the highlight update itself is measured.
    [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = "set_Position")]
    private static extern void SetPosition(SessionViewModel vm, TimeSpan position);

    [Fact]
    public async Task MovingTheMark_ToAnotherWord_AllocatesNothing()
    {
        var vm = await LoadAsync(words: 3_000);
        vm.PlayPauseCommand.Execute(null);
        var positions = new List<TimeSpan>();
        foreach (var segment in _fixture.Transcripts.Transcript!.Segments)
        {
            foreach (var word in segment.Words)
            {
                positions.Add(word.Start + S(0.01));
            }
        }

        // Warm up: JIT, first-time paths, lazily created caches.
        for (var round = 0; round < 2; round++)
        {
            for (var i = 0; i < positions.Count; i++)
            {
                SetPosition(vm, positions[i]);
                vm.UpdateCurrent(forceScroll: false);
            }
        }

        // Exact count, not a comparison of two runs: the allocation counter of this thread only moves for this thread's own allocations.
        long allocated = 0;
        for (var i = 0; i < positions.Count; i++)
        {
            SetPosition(vm, positions[i]);
            var before = GC.GetAllocatedBytesForCurrentThread();
            vm.UpdateCurrent(forceScroll: false);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task AWordChange_BuildsExactlyOneTextLayout_AndTheSameWordNone()
    {
        await Session.Dispatch(
            () =>
            {
                var control = Chip(-1);
                control.Measure(new Size(400, double.PositiveInfinity));
                control.Arrange(new Rect(control.DesiredSize));
                var (starts, _) = Words(Line);

                var previous = control.CurrentLayout;
                for (var word = 0; word < starts.Length; word++)
                {
                    control.ActiveWord = word;
                    var built = control.CurrentLayout;
                    Assert.NotSame(previous, built);

                    // Measuring, arranging, hit-testing and drawing again reuse that layout; the same word is no change at all.
                    control.Measure(new Size(400, double.PositiveInfinity));
                    control.Arrange(new Rect(control.DesiredSize));
                    _ = control.WordAt(new Point(1, 1));
                    control.ActiveWord = word;
                    Assert.Same(built, control.CurrentLayout);
                    previous = built;
                }
            },
            TestContext.Current.CancellationToken);
    }

    // The control: the chip --------------------------------------------------------------------------------------

    private const string Line = "Both: full-text plus semantic. Answers come back with timestamps, so one click takes you to the exact moment.";

    private static (int[] Starts, int[] Lengths) Words(string text)
    {
        var starts = new List<int>();
        var lengths = new List<int>();
        for (var at = 0; at < text.Length;)
        {
            var end = text.IndexOf(' ', at);
            if (end < 0)
            {
                end = text.Length;
            }

            starts.Add(at);
            lengths.Add(end - at);
            at = end + 1;
        }

        return ([.. starts], [.. lengths]);
    }

    private static TranscriptTextControl Chip(int active, string text = Line)
    {
        var (starts, lengths) = Words(text);
        return new TranscriptTextControl
        {
            Text = text,
            WordStarts = starts,
            WordLengths = lengths,
            ActiveWord = active,
            FontFamily = new FontFamily("Inter"),
            FontSize = 13,
            LineHeight = 20,
            LetterSpacing = 0.13,
            Foreground = Brushes.Black,
            HighlightBrush = Brushes.Black,
            HighlightForeground = Brushes.White,
            UpcomingForeground = Brushes.Gray,
            HighlightPadding = 3,
            HighlightCornerRadius = new CornerRadius(4),
        };
    }

    [Fact]
    public async Task TheChip_ChangesNoMetric_TheTextDoesNotReflow()
    {
        await Session.Dispatch(
            () =>
            {
                foreach (var width in new[] { 2000.0, 260.0 })
                {
                    var plain = Chip(-1);
                    var marked = Chip(7);
                    plain.Measure(new Size(width, double.PositiveInfinity));
                    plain.Arrange(new Rect(plain.DesiredSize));
                    marked.Measure(new Size(width, double.PositiveInfinity));
                    marked.Arrange(new Rect(marked.DesiredSize));

                    Assert.Equal(plain.DesiredSize, marked.DesiredSize);
                    var (starts, lengths) = Words(Line);
                    for (var i = 0; i < starts.Length; i++)
                    {
                        var expected = plain.CurrentLayout!.HitTestTextRange(starts[i], lengths[i]).ToArray();
                        var actual = marked.CurrentLayout!.HitTestTextRange(starts[i], lengths[i]).ToArray();
                        Assert.Equal(expected.Length, actual.Length);
                        for (var r = 0; r < expected.Length; r++)
                        {
                            // Runs split at the word boundary add up with a rounding error of 1e-14, not a pixel.
                            Assert.Equal(expected[r].X, actual[r].X, 0.01);
                            Assert.Equal(expected[r].Y, actual[r].Y, 0.01);
                            Assert.Equal(expected[r].Width, actual[r].Width, 0.01);
                            Assert.Equal(expected[r].Height, actual[r].Height, 0.01);
                        }
                    }

                    Assert.Equal(plain.CurrentLayout!.TextLines.Count, marked.CurrentLayout!.TextLines.Count);
                }
            },
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MovingTheChip_ToAnotherWord_ChangesNoMetricEither()
    {
        await Session.Dispatch(
            () =>
            {
                var control = Chip(2);
                control.Measure(new Size(300, double.PositiveInfinity));
                control.Arrange(new Rect(control.DesiredSize));
                var size = control.DesiredSize;
                var height = control.CurrentLayout!.Height;

                control.ActiveWord = 12;
                control.Measure(new Size(300, double.PositiveInfinity));
                control.Arrange(new Rect(control.DesiredSize));

                Assert.Equal(size, control.DesiredSize);
                Assert.Equal(height, control.CurrentLayout!.Height);
            },
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheChip_IsDrawnBehindTheWord_ThreePixelsBeyondIt()
    {
        await Session.Dispatch(
            () =>
            {
                var control = Chip(-1);
                var window = new Window { Width = 900, Height = 60, Background = Brushes.White, Content = control };
                window.Show();
                UiHarness.Pump();
                var (starts, lengths) = Words(Line);
                var word = control.CurrentLayout!.HitTestTextRange(starts[7], lengths[7]).First();
                var y = (int)(word.Y + (word.Height / 2));
                var insidePadding = (int)(word.X - 1.5);
                var beyondPadding = (int)(word.X - 6);

                var plain = Pixel(window, insidePadding, y);
                Assert.Equal(255, plain.R);

                control.ActiveWord = 7;
                UiHarness.Pump();

                var chip = Pixel(window, insidePadding, y);
                var outside = Pixel(window, beyondPadding, y);
                window.Close();
                Assert.True(chip.R < 40, $"Inside the padding of the chip: {chip}.");
                Assert.Equal(255, outside.R);
            },
            TestContext.Current.CancellationToken);
    }

    private static Color Pixel(Window window, int x, int y)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var buffer = frame.Lock();
        var pixel = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address + (y * buffer.RowBytes) + (x * 4), pixel, 0, 4);
        return buffer.Format == Avalonia.Platform.PixelFormat.Bgra8888 ? Color.FromRgb(pixel[2], pixel[1], pixel[0]) : Color.FromRgb(pixel[0], pixel[1], pixel[2]);
    }

    [Fact]
    public async Task ClickingAWord_StillFindsIt_WithTheChipOn()
    {
        await Session.Dispatch(
            () =>
            {
                var control = Chip(3);
                control.Measure(new Size(2000, double.PositiveInfinity));
                control.Arrange(new Rect(control.DesiredSize));

                Assert.Equal(0, control.WordAt(new Point(2, 8)));
                Assert.Equal(Words(Line).Starts.Length - 1, control.WordAt(new Point(control.DesiredSize.Width - 2, 8)));
            },
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheChip_AndASearchMark_CanBeOnTheSameLine()
    {
        await Session.Dispatch(
            () =>
            {
                var control = Chip(7);
                control.MarkBrush = Brushes.Black;
                control.MarkForeground = Brushes.White;
                control.HighlightTerm = "timestamps";
                control.Measure(new Size(300, double.PositiveInfinity));
                control.Arrange(new Rect(control.DesiredSize));

                Assert.Equal(1, control.MarkCount);
                Assert.True(control.DesiredSize.Height > 0);
            },
            TestContext.Current.CancellationToken);
    }
}
