using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Murmur.App.Localization;
using Murmur.App.Resources;
using Murmur.App.Services;
using Murmur.App.Tests.Design;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;

namespace Murmur.App.Tests;

/// <summary>The keyboard shortcuts of the recording (#61): the one key router, its guards, the commands behind the keys and the bar (English UI).</summary>
public sealed class SessionShortcutsTests : IDisposable
{
    private readonly IDisposable _english = UiCulture.Override("en");
    private readonly SessionFixture _fixture = new();
    private readonly FakeBookmarkStore _store = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HeadlessUnitTestSession Session => UiHarness.Session;

    public void Dispose()
    {
        _english.Dispose();
        _fixture.Dispose();
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private SessionViewModel Create()
    {
        _fixture.UseShortTranscript();
        return new SessionViewModel(_fixture.Record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action(),
            services: new SessionServices(Bookmarks: _store));
    }

    private async Task<SessionViewModel> LoadAsync()
    {
        var vm = Create();
        await vm.LoadAsync();
        return vm;
    }

    private async Task<ShellViewModel> OpenAsync()
    {
        var harness = new UiHarness();
        harness.Store.Add(_fixture.Record);
        _fixture.UseShortTranscript();
        var shell = harness.CreateShell(
            record => new SessionViewModel(record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action(), services: new SessionServices(Bookmarks: _store)),
            _fixture.Processing);
        await shell.Library.LoadAsync();
        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;
        return shell;
    }

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
        UiHarness.Pump();
    }

    private async Task<T> InWindowAsync<T>(ShellViewModel shell, Func<MainWindow, T> body)
    {
        var result = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                try
                {
                    return body(window);
                }
                finally
                {
                    window.Close();
                }
            },
            Ct);
        await Task.Delay(30, Ct);
        return result;
    }

    // The commands behind the keys --------------------------------------------------------------------------------

    [Fact]
    public async Task SeekBackAndForward_MoveFiveSeconds_ThroughTheSeekPath()
    {
        var vm = await LoadAsync();
        vm.SeekTo(S(100));

        vm.SeekForwardCommand.Execute(null);
        Assert.Equal(S(105), vm.Position);
        vm.SeekBackCommand.Execute(null);
        vm.SeekBackCommand.Execute(null);

        Assert.Equal(S(95), vm.Position);
        Assert.Equal([S(100), S(105), S(100), S(95)], _fixture.Player.Seeks);
    }

    [Fact]
    public async Task SeekBack_StopsAtTheStart_AndSeekForward_AtTheDuration()
    {
        var vm = await LoadAsync();
        vm.SeekTo(S(3));
        vm.SeekBackCommand.Execute(null);
        Assert.Equal(TimeSpan.Zero, vm.Position);

        vm.SeekTo(vm.Duration - S(2));
        vm.SeekForwardCommand.Execute(null);
        Assert.Equal(vm.Duration, vm.Position);
        Assert.Equal(vm.Duration, _fixture.Player.Position);
    }

    [Fact]
    public async Task NextLine_SeeksToTheStartOfTheFollowingLine_AndPreviousToTheOneBefore()
    {
        var vm = await LoadAsync();
        var scrolled = new List<int>();
        vm.ScrollToSegmentRequested += scrolled.Add;
        vm.SeekTo(S(2));

        vm.NextLineCommand.Execute(null);
        Assert.Equal(S(10), vm.Position);

        vm.NextLineCommand.Execute(null);
        Assert.Equal(S(20), vm.Position);

        vm.PreviousLineCommand.Execute(null);
        Assert.Equal(S(10), vm.Position);
        Assert.Contains(1, scrolled);
        Assert.Equal(0, scrolled[^1]);
    }

    [Fact]
    public async Task OnTheFirstAndLastLine_TheArrowsStayPut()
    {
        var vm = await LoadAsync();

        vm.PreviousLineCommand.Execute(null); // before the first line: nothing
        Assert.Equal(TimeSpan.Zero, vm.Position);

        vm.SeekTo(S(12));
        vm.PreviousLineCommand.Execute(null); // on the first line: back to its start
        Assert.Equal(S(10), vm.Position);

        vm.SeekTo(S(25));
        var seeks = _fixture.Player.Seeks.Count;
        vm.NextLineCommand.Execute(null); // on the last line: nothing
        Assert.Equal(S(25), vm.Position);
        Assert.Equal(seeks, _fixture.Player.Seeks.Count);
    }

    [Fact]
    public void WithoutATranscript_TheLineKeysDoNothing()
    {
        var vm = _fixture.Create();

        vm.NextLineCommand.Execute(null);
        vm.PreviousLineCommand.Execute(null);

        Assert.Empty(_fixture.Player.Seeks);
    }

    [Fact]
    public async Task CloseOpenPopover_CancelsTheNoteBoxOfABookmark()
    {
        var ids = _store.Put(_fixture.Record.Id, (12, "Intro"));
        var vm = await LoadAsync();
        Assert.False(vm.CloseOpenPopover());

        vm.Bookmarks[0].BeginEditCommand.Execute(null);
        vm.Bookmarks[0].EditText = "Typed";
        Assert.True(vm.CloseOpenPopover());

        Assert.False(vm.Bookmarks[0].IsEditing);
        Assert.Equal("Intro", vm.Bookmarks[0].Note);
        Assert.False(vm.CloseOpenPopover());
        Assert.Single(ids);
    }

    // The keys ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Space_TogglesPlayback()
    {
        var shell = await OpenAsync();

        await InWindowAsync(shell, window =>
        {
            Press(window, PhysicalKey.Space);
            Assert.True(shell.Session!.IsPlaying);
            Press(window, PhysicalKey.Space);
            Assert.False(shell.Session.IsPlaying);
            return 0;
        });

        Assert.Equal(1, _fixture.Player.Plays);
        Assert.Equal(1, _fixture.Player.Pauses);
    }

    [Fact]
    public async Task Space_DoesNotClickAFocusedButton_AndDoesNotScrollTheList()
    {
        var shell = await OpenAsync();

        var (changed, offset) = await InWindowAsync(shell, window =>
        {
            var toggle = window.GetVisualDescendants().OfType<ToggleButton>().First(t => t.Classes.Contains("filter"));
            var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "TranscriptScroller");
            toggle.Focus();
            UiHarness.Pump();
            var before = scroller.Offset;
            var checkedBefore = toggle.IsChecked;
            Press(window, PhysicalKey.Space);
            return (toggle.IsChecked != checkedBefore, scroller.Offset - before);
        });

        Assert.False(changed);
        Assert.Equal(default, offset);
        Assert.Equal(1, _fixture.Player.Plays);
    }

    [Fact]
    public async Task JAndL_SeekFiveSeconds()
    {
        var shell = await OpenAsync();
        shell.Session!.SeekTo(S(50));

        await InWindowAsync(shell, window =>
        {
            Press(window, PhysicalKey.L);
            Assert.Equal(S(55), shell.Session.Position);
            Press(window, PhysicalKey.J);
            Press(window, PhysicalKey.J);
            Assert.Equal(S(45), shell.Session.Position);
            return 0;
        });
    }

    [Fact]
    public async Task JAtTheStart_ClampsToZero()
    {
        var shell = await OpenAsync();

        await InWindowAsync(shell, window =>
        {
            Press(window, PhysicalKey.J);
            Assert.Equal(TimeSpan.Zero, shell.Session!.Position);
            shell.Session.SeekTo(shell.Session.Duration - S(1));
            Press(window, PhysicalKey.L);
            Assert.Equal(shell.Session.Duration, shell.Session.Position);
            return 0;
        });
    }

    [Fact]
    public async Task TheArrows_MoveBetweenLines_AndScrollThemIntoView()
    {
        var shell = await OpenAsync();
        var scrolled = new List<int>();
        shell.Session!.ScrollToSegmentRequested += scrolled.Add;

        await InWindowAsync(shell, window =>
        {
            Press(window, PhysicalKey.ArrowDown);
            Assert.Equal(S(10), shell.Session.Position);
            Press(window, PhysicalKey.ArrowDown);
            Assert.Equal(S(20), shell.Session.Position);
            Press(window, PhysicalKey.ArrowDown);
            Assert.Equal(S(20), shell.Session.Position);
            Press(window, PhysicalKey.ArrowUp);
            Assert.Equal(S(10), shell.Session.Position);
            return 0;
        });

        Assert.Equal(0, scrolled[0]);
        Assert.Equal(1, scrolled[1]);
        Assert.Equal(0, scrolled[^1]);
    }

    [Fact]
    public async Task MAndF_StillWorkAfterTheMove()
    {
        var shell = await OpenAsync();
        shell.Session!.SeekTo(S(42));
        var follow = shell.Session.FollowAlong;

        await InWindowAsync(shell, window =>
        {
            Press(window, PhysicalKey.M);
            Press(window, PhysicalKey.F);
            return 0;
        });
        await Session.Dispatch(() => UiHarness.Pump(), Ct);

        Assert.Equal([S(42)], _store.All.Select(b => b.At));
        Assert.Equal(!follow, shell.Session.FollowAlong);
    }

    [Fact]
    public async Task Esc_ClosesAnOpenNoteBoxFirst_ThenGoesBack()
    {
        _store.Put(_fixture.Record.Id, (12, "Intro"));
        var shell = await OpenAsync();
        var row = shell.Session!.Bookmarks[0];

        await InWindowAsync(shell, window =>
        {
            row.BeginEditCommand.Execute(null);
            UiHarness.Pump(6);
            window.Focus(); // the popover is open, the focus is not in it: the router must close it
            UiHarness.Pump();
            Press(window, PhysicalKey.Escape);
            Assert.False(row.IsEditing);
            Assert.NotNull(shell.Session);

            Press(window, PhysicalKey.Escape);
            Assert.Null(shell.Session);
            return 0;
        });
    }

    [Fact]
    public async Task Esc_InTheSearchBox_StillGoesBack()
    {
        var shell = await OpenAsync();

        await InWindowAsync(shell, window =>
        {
            window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SearchBox").Focus();
            UiHarness.Pump();
            Press(window, PhysicalKey.Escape);
            Assert.Null(shell.Session);
            return 0;
        });
    }

    // The guards --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PhysicalKey.Space)]
    [InlineData(PhysicalKey.J)]
    [InlineData(PhysicalKey.L)]
    [InlineData(PhysicalKey.M)]
    [InlineData(PhysicalKey.F)]
    [InlineData(PhysicalKey.ArrowDown)]
    [InlineData(PhysicalKey.ArrowUp)]
    public async Task WhileTheSearchBoxHasTheFocus_NoShortcutRuns(PhysicalKey key)
    {
        var shell = await OpenAsync();
        shell.Session!.SeekTo(S(15));
        var seeks = _fixture.Player.Seeks.Count;
        var follow = shell.Session.FollowAlong;

        await InWindowAsync(shell, window =>
        {
            window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SearchBox").Focus();
            UiHarness.Pump();
            Press(window, key);
            return 0;
        });

        Assert.Equal(0, _fixture.Player.Plays);
        Assert.Equal(seeks, _fixture.Player.Seeks.Count);
        Assert.Empty(_store.All);
        Assert.Equal(follow, shell.Session.FollowAlong);
    }

    [Fact]
    public async Task WhileANoteBoxHasTheFocus_TheLettersAreTyped_NotShortcuts()
    {
        _store.Put(_fixture.Record.Id, (12, "Intro"));
        var shell = await OpenAsync();
        var follow = shell.Session!.FollowAlong;

        await InWindowAsync(shell, window =>
        {
            shell.Session.Bookmarks[0].BeginEditCommand.Execute(null);
            UiHarness.Pump(6);
            Assert.IsType<TextBox>(window.FocusManager!.GetFocusedElement());
            Press(window, PhysicalKey.F);
            Press(window, PhysicalKey.M);
            Press(window, PhysicalKey.Space);
            return 0;
        });

        Assert.Equal(follow, shell.Session.FollowAlong);
        Assert.Equal(0, _fixture.Player.Plays);
        Assert.Single(_store.All);
    }

    [Theory]
    [InlineData(PhysicalKey.Space, RawInputModifiers.Control)]
    [InlineData(PhysicalKey.J, RawInputModifiers.Control)]
    [InlineData(PhysicalKey.L, RawInputModifiers.Alt)]
    [InlineData(PhysicalKey.M, RawInputModifiers.Control)]
    [InlineData(PhysicalKey.F, RawInputModifiers.Alt)]
    [InlineData(PhysicalKey.ArrowDown, RawInputModifiers.Control)]
    [InlineData(PhysicalKey.Escape, RawInputModifiers.Control)]
    public async Task WithAModifierOtherThanShift_NoShortcutRuns(PhysicalKey key, RawInputModifiers modifiers)
    {
        var shell = await OpenAsync();
        var follow = shell.Session!.FollowAlong;

        await InWindowAsync(shell, window =>
        {
            Press(window, key, modifiers);
            Assert.NotNull(shell.Session);
            return 0;
        });

        Assert.Equal(0, _fixture.Player.Plays);
        Assert.Empty(_fixture.Player.Seeks);
        Assert.Empty(_store.All);
        Assert.Equal(follow, shell.Session.FollowAlong);
    }

    [Fact]
    public async Task ShiftAlone_IsNotAModifierForTheShortcuts()
    {
        var shell = await OpenAsync();
        shell.Session!.SeekTo(S(50));

        await InWindowAsync(shell, window =>
        {
            Press(window, PhysicalKey.L, RawInputModifiers.Shift);
            return 0;
        });

        Assert.Equal(S(55), shell.Session.Position);
    }

    [Fact]
    public async Task InsideSomethingThatOwnsItsKeys_TheShortcutsStayOut()
    {
        var shell = await OpenAsync();

        var changed = await InWindowAsync(shell, window =>
        {
            var toggle = window.GetVisualDescendants().OfType<ToggleButton>().First(t => t.Classes.Contains("filter"));
            SessionShortcuts.SetOwnsKeys(toggle, true);
            toggle.Focus();
            UiHarness.Pump();
            var before = toggle.IsChecked;
            Press(window, PhysicalKey.Space);
            Press(window, PhysicalKey.L);
            return toggle.IsChecked != before;
        });

        Assert.True(changed, "the button got its own Space");
        Assert.Equal(0, _fixture.Player.Plays);
        Assert.Empty(_fixture.Player.Seeks);
    }

    [Fact]
    public async Task OnceThePageIsLeft_TheKeysAreNoLongerBound()
    {
        var shell = await OpenAsync();
        var session = shell.Session!;

        await InWindowAsync(shell, window =>
        {
            Press(window, PhysicalKey.Escape);
            Assert.Null(shell.Session);
            Press(window, PhysicalKey.Space);
            return 0;
        });

        Assert.Equal(0, _fixture.Player.Plays);
        Assert.False(session.IsPlaying);
    }

    // The bar -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheBar_IsOnByDefault_AndHiddenByTheSwitch()
    {
        var shell = await OpenAsync();

        var (shown, hidden) = await InWindowAsync(shell, window =>
        {
            var bar = window.GetVisualDescendants().OfType<ShortcutsBar>().Single();
            var on = bar.IsEffectivelyVisible;
            shell.Session!.ShowShortcuts = false;
            UiHarness.Pump();
            return (on, bar.IsEffectivelyVisible);
        });

        Assert.True(shown);
        Assert.False(hidden);
    }

    [Fact]
    public async Task TheBar_ListsEveryShortcut_InEnglish()
    {
        var shell = await OpenAsync();

        var texts = await InWindowAsync(shell, window =>
            window.GetVisualDescendants().OfType<ShortcutsBar>().Single().GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray());

        Assert.Equal(["Space", "Play / pause", "J", "L", "−5 s / +5 s", "↑", "↓", "Previous / next line", "M", "Mark moment", "F", "Follow along", "Esc", "Close"], texts);
    }

    [Fact]
    public void TheStrings_ExistInEnglishAndUkrainian_WithTheSamePlaceholders()
    {
        string[] keys = ["Space", "Esc", "PlayPause", "Seek", "Lines", "Bookmark", "Follow", "Close", "BarName"];
        string[] english;
        string[] ukrainian;
        using (UiCulture.Override("en"))
        {
            english = [.. keys.Select(k => Strings.Get("Shortcuts_" + k))];
        }

        using (UiCulture.Override("uk"))
        {
            ukrainian = [.. keys.Select(k => Strings.Get("Shortcuts_" + k))];
        }

        Assert.All(english, s => Assert.False(string.IsNullOrWhiteSpace(s)));
        Assert.All(ukrainian, s => Assert.False(string.IsNullOrWhiteSpace(s)));
        Assert.Equal("Пробіл", ukrainian[0]);
        Assert.Equal("Відтворити / пауза", ukrainian[2]);
        Assert.NotEqual(english[2], ukrainian[2]);
    }

    [Theory]
    [InlineData("Light", "en", 1100, 720)]
    [InlineData("Dark", "en", 1100, 720)]
    [InlineData("Light", "uk", 900, 600)]
    [InlineData("Dark", "uk", 900, 600)]
    [InlineData("Light", "en", 900, 600)]
    public async Task TheBar_WritesPng(string theme, string language, int width, int height)
    {
        using var culture = UiCulture.Override(language);
        using var rig = new DesignRig();
        await rig.Shell.Library.LoadAsync();
        rig.Shell.OpenSession(rig.ClientDemo);
        await rig.Shell.SessionLoadTask;
        var session = rig.Shell.Session!;
        session.ShowShortcuts = true;
        var path = Path.Combine(RepoPaths.ScreenshotsDirectory, $"shortcuts-bar-{theme.ToLowerInvariant()}-{language}-{width}x{height}.png");

        await Session.Dispatch(
            () =>
            {
                var window = new MainWindow { DataContext = rig.Shell, Width = width, Height = height, RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light };
                window.Show();
                UiHarness.Pump();
                session.SeekTo(DesignDemoData.SessionPosition);
                UiHarness.Pump(6);
                var bar = window.GetVisualDescendants().OfType<ShortcutsBar>().Single();
                Assert.True(bar.Bounds.Height >= 40, "the bar is at least 40 high");
                Assert.True(bar.Bounds.Right <= width, "the bar fits the window");
                UiHarness.Save(window, path);
                window.Close();
            },
            Ct);

        Assert.True(new FileInfo(path).Length > 10_000, "Screenshot looks empty.");
    }
}
