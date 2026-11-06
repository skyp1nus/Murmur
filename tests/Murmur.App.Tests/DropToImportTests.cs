using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Murmur.App.Controls;
using Murmur.App.Localization;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Import;

namespace Murmur.App.Tests;

/// <summary>Files dropped onto the window (#69): the overlay view model, the hand-off to the import dialog, and the window's drag and drop.</summary>
public sealed class DropToImportTests : IDisposable
{
    private static readonly DateTimeOffset Now = UiHarness.Now;

    private readonly IDisposable _english = UiCulture.Override("en");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "murmur-drop-" + Guid.NewGuid().ToString("N"));
    private readonly FakeImportQueue _queue = new();
    private readonly FakeMediaDecoder _decoder = new();
    private readonly FakeRecorder _recorder = new();

    public DropToImportTests() => Directory.CreateDirectory(_root);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HeadlessUnitTestSession Session => UiHarness.Session;

    public void Dispose()
    {
        _english.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string P(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private DropOverlayViewModel Overlay(Func<bool>? recording = null, Func<bool>? enabled = null) =>
        new(_queue.SupportedExtensions, recording ?? (() => false), enabled);

    private async Task<ShellViewModel> ShellAsync(bool imports = true)
    {
        var store = new FakeSessionStore();
        store.Add(Sessions.Make("Weekly product sync", Now.AddHours(-2), new TimeSpan(0, 47, 12)));
        var time = new TestTime(Now);
        var library = new LibraryViewModel(store, time, imports: imports ? _queue : null, mediaProbe: imports ? _decoder : null);
        var recording = new RecordingViewModel(_recorder, new FakeCapture(), store, action => action());
        var shell = new ShellViewModel(
            library,
            recording,
            imports: imports ? _queue : null);
        await library.LoadAsync();
        return shell;
    }

    // The overlay ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Dragging_CountsTheSupportedFiles_AndOffersToCopy()
    {
        var overlay = Overlay();

        var accepted = overlay.DragOver(["/a/talk.MP3", "/a/clip.mp4", "/a/notes.pdf", "/a/memo.m4a"]);

        Assert.True(accepted);
        Assert.True(overlay.CanDrop);
        Assert.True(overlay.IsVisible);
        Assert.Equal("Drop to import 3 files", overlay.Title);
        Assert.Equal("They’ll be transcribed one by one", overlay.Hint);
    }

    [Fact]
    public void OneFile_SaysFile()
    {
        var overlay = Overlay();

        overlay.DragOver(["/a/talk.wav"]);

        Assert.Equal("Drop to import 1 file", overlay.Title);
    }

    [Fact]
    public void UkrainianTitle_UsesThePluralForms()
    {
        using var culture = UiCulture.Override("uk");
        var overlay = Overlay();

        overlay.DragOver(["/a/1.mp3"]);
        var one = overlay.Title;
        overlay.DragOver(["/a/1.mp3", "/a/2.mp3"]);
        var few = overlay.Title;
        overlay.DragOver(["/a/1.mp3", "/a/2.mp3", "/a/3.mp3", "/a/4.mp3", "/a/5.mp3"]);
        var many = overlay.Title;

        Assert.Equal("Відпустіть, щоб імпортувати 1 файл", one);
        Assert.Equal("Відпустіть, щоб імпортувати 2 файли", few);
        Assert.Equal("Відпустіть, щоб імпортувати 5 файлів", many);
    }

    [Fact]
    public void OnlyUnsupportedFiles_SayTheyCannotBeImported_AndTakeNoDrop()
    {
        var overlay = Overlay();
        var dropped = new List<IReadOnlyList<string>>();
        overlay.FilesDropped += dropped.Add;

        var accepted = overlay.DragOver(["/a/notes.pdf", "/a/photo.png"]);

        Assert.False(accepted);
        Assert.False(overlay.CanDrop);
        Assert.True(overlay.IsVisible);
        Assert.Equal("These files can’t be imported", overlay.Title);
    }

    [Fact]
    public void NonFileData_ShowsNothing_AndRefusesTheDrop()
    {
        var overlay = Overlay();
        overlay.DragOver(["/a/talk.mp3"]);

        var accepted = overlay.DragOver(null);

        Assert.False(accepted);
        Assert.False(overlay.IsVisible);
        Assert.False(overlay.Drop(null));
    }

    [Fact]
    public void WhileRecording_TheOverlayExplains_AndTheDropIsIgnored()
    {
        var overlay = Overlay(recording: () => true);
        var dropped = new List<IReadOnlyList<string>>();
        overlay.FilesDropped += dropped.Add;

        var accepted = overlay.DragOver(["/a/talk.mp3"]);
        var title = overlay.Title;
        var visible = overlay.IsVisible;
        var taken = overlay.Drop(["/a/talk.mp3"]);

        Assert.False(accepted);
        Assert.True(visible);
        Assert.Equal("Imports wait until the recording stops", title);
        Assert.False(taken);
        Assert.Empty(dropped);
        Assert.False(overlay.IsVisible);
    }

    [Fact]
    public void LeavingTheWindow_OrACancelledDrag_HidesTheOverlay()
    {
        var overlay = Overlay();
        overlay.DragOver(["/a/talk.mp3"]);

        overlay.DragLeave();

        Assert.False(overlay.IsVisible);
        Assert.False(overlay.CanDrop);
    }

    [Fact]
    public void ADrop_HidesTheOverlay_AndHandsOverEveryFileInOrder()
    {
        var overlay = Overlay();
        var dropped = new List<IReadOnlyList<string>>();
        overlay.FilesDropped += dropped.Add;
        overlay.DragOver(["/a/talk.mp3", "/a/notes.pdf"]);

        var taken = overlay.Drop(["/a/talk.mp3", "/a/notes.pdf"]);

        Assert.True(taken);
        Assert.False(overlay.IsVisible);
        Assert.Equal([["/a/talk.mp3", "/a/notes.pdf"]], dropped);
    }

    [Fact]
    public void ADisabledPage_TakesNoDrops()
    {
        var overlay = Overlay(enabled: () => false);

        Assert.False(overlay.DragOver(["/a/talk.mp3"]));
        Assert.False(overlay.IsVisible);
        Assert.False(overlay.Drop(["/a/talk.mp3"]));
    }

    [Fact]
    public void Extensions_AreComparedWithoutCase_WhateverTheQueuesSetDoes()
    {
        var overlay = new DropOverlayViewModel(new HashSet<string>(StringComparer.Ordinal) { ".mp3" }, () => false);

        overlay.DragOver(["/a/TALK.Mp3"]);

        Assert.Equal("Drop to import 1 file", overlay.Title);
    }

    [Fact]
    public void WithTheDialogOpen_TheOverlayStaysHidden_ButTheDropIsStillTaken()
    {
        var overlay = new DropOverlayViewModel(_queue.SupportedExtensions, () => false, dialogOpen: () => true);
        var dropped = new List<IReadOnlyList<string>>();
        overlay.FilesDropped += dropped.Add;
        IReadOnlyList<string> files = ["/a/talk.mp3"];

        var accepted = overlay.DragOver(files);
        var taken = overlay.Drop(files);

        Assert.True(accepted);
        Assert.False(overlay.IsVisible);
        Assert.True(taken);
        Assert.Single(dropped);
    }

    // The shell -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutAnImportQueue_ThereIsNoOverlay()
    {
        var shell = await ShellAsync(imports: false);

        Assert.Null(shell.DropOverlay);
    }

    [Fact]
    public async Task ADrop_OpensTheImportDialogWithTheFiles()
    {
        var shell = await ShellAsync();
        var requested = new List<ImportDialogViewModel>();
        shell.ImportDialogRequested += requested.Add;
        var talk = P("talk.mp3");
        var notes = P("notes.pdf");

        shell.DropOverlay!.Drop([talk, notes]);

        var dialog = Assert.Single(requested);
        Assert.Equal([talk, notes], dialog.Files.Select(f => f.Path));
        Assert.Same(dialog, shell.OpenImportDialog);
        dialog.Dispose();
    }

    [Fact]
    public async Task ADrop_WhileTheDialogIsOpen_AddsToIt()
    {
        var shell = await ShellAsync();
        var requested = new List<ImportDialogViewModel>();
        shell.ImportDialogRequested += requested.Add;
        var first = P("first.mp3");
        var second = P("second.wav");
        shell.DropOverlay!.Drop([first]);

        shell.DropOverlay.Drop([second, first]);

        var dialog = Assert.Single(requested);
        Assert.Equal([first, second], dialog.Files.Select(f => f.Path));
        dialog.Dispose();
    }

    [Fact]
    public async Task ADrop_AfterTheDialogClosed_OpensANewOne()
    {
        var shell = await ShellAsync();
        var requested = new List<ImportDialogViewModel>();
        shell.ImportDialogRequested += requested.Add;
        shell.DropOverlay!.Drop([P("a.mp3")]);
        requested[0].CloseCommand.Execute(null);
        requested[0].Dispose();

        shell.DropOverlay.Drop([P("b.mp3")]);

        Assert.Equal(2, requested.Count);
        Assert.Same(requested[1], shell.OpenImportDialog);
        requested[1].Dispose();
    }

    [Fact]
    public async Task ChangingThePage_ClosesTheDialogOfADrop()
    {
        var shell = await ShellAsync();
        var requested = new List<ImportDialogViewModel>();
        shell.ImportDialogRequested += requested.Add;
        shell.DropOverlay!.Drop([P("a.mp3")]);
        var dialog = Assert.Single(requested);

        shell.ShowRecording();

        Assert.True(dialog.IsClosed);
        Assert.Null(shell.OpenImportDialog);
        dialog.Dispose();
    }

    [Fact]
    public async Task TheDialogOfThePill_TakesTheDropToo()
    {
        var shell = await ShellAsync();
        var requested = new List<ImportDialogViewModel>();
        shell.ImportDialogRequested += requested.Add;
        ImportDialogViewModel? pill = null;
        shell.Library.ImportDialogRequested += d => pill = d;
        shell.Library.OpenImportCommand.Execute(null);
        var path = P("talk.mp3");

        shell.DropOverlay!.Drop([path]);

        Assert.NotNull(pill);
        Assert.Empty(requested);
        Assert.Equal([path], pill.Files.Select(f => f.Path));
        pill.Dispose();
    }

    [Fact]
    public async Task WhileRecording_TheShellIgnoresTheDrop()
    {
        var shell = await ShellAsync();
        var requested = new List<ImportDialogViewModel>();
        shell.ImportDialogRequested += requested.Add;
        await shell.Recording.StartStopCommand.ExecuteAsync(null);

        var accepted = shell.DropOverlay!.DragOver([P("talk.mp3")]);
        var taken = shell.DropOverlay.Drop([P("talk.mp3")]);

        Assert.False(accepted);
        Assert.False(taken);
        Assert.Empty(requested);
        Assert.Equal("Imports wait until the recording stops", shell.DropOverlay.Title);
    }

    // The window ----------------------------------------------------------------------------------------------------

    private static MainWindow Show(ShellViewModel shell, string theme = "Light", int width = 1100, int height = 720)
    {
        var window = new MainWindow
        {
            DataContext = shell,
            Width = width,
            Height = height,
            RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light,
        };
        window.Show();
        UiHarness.Pump();
        return window;
    }

    private static DropOverlayView OverlayOf(Window window) => window.GetVisualDescendants().OfType<DropOverlayView>().Single();

    private static Panel RootOf(Window window) => OverlayOf(window).GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "Root");

    /// <summary>A file dragged from the file manager, as a drag data object.</summary>
    private static async Task<DataTransfer> FilesAsync(Window window, params string[] paths)
    {
        var data = new DataTransfer();
        foreach (var path in paths)
        {
            var file = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(path));
            data.Add(DataTransferItem.CreateFile(file!));
        }

        return data;
    }

    private static DataTransfer TextData()
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText("hello"));
        return data;
    }

    /// <summary>Remembers the drop effect the window answers a drag with (read after the window's own handler).</summary>
    private sealed class EffectProbe
    {
        public DragDropEffects Last { get; private set; } = DragDropEffects.Link;

        public void Attach(Window window) =>
            window.AddHandler(DragDrop.DragOverEvent, (_, e) => Last = e.DragEffects, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static Point InContent(Window window) => new(window.ClientSize.Width - 100, window.ClientSize.Height / 2);

    [Fact]
    public async Task DraggingFilesOverTheWindow_ShowsTheOverlay_AndLeavingHidesIt()
    {
        var shell = await ShellAsync();
        var probe = new EffectProbe();
        var a = P("a.mp3");
        var b = P("b.mp4");

        var (shown, title, effect, afterLeave) = await Session.Dispatch(
            async () =>
            {
                var window = Show(shell);
                var data = await FilesAsync(window, a, b);
                probe.Attach(window);
                var point = InContent(window);
                var before = RootOf(window).Opacity;
                window.DragDrop(point, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
                window.DragDrop(point, RawDragEventType.DragOver, data, DragDropEffects.Copy, RawInputModifiers.None);
                UiHarness.Pump(30);
                var visible = shell.DropOverlay!.IsVisible && before == 0;
                var text = OverlayOf(window).GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "TitleText").Text;
                window.DragDrop(point, RawDragEventType.DragLeave, data, DragDropEffects.Copy, RawInputModifiers.None);
                UiHarness.Pump(3);
                var hidden = !shell.DropOverlay.IsVisible;
                window.Close();
                return (visible, text, probe.Last, hidden);
            },
            Ct);

        Assert.True(shown, "the overlay shows over the content");
        Assert.Equal("Drop to import 2 files", title);
        Assert.Equal(DragDropEffects.Copy, effect);
        Assert.True(afterLeave, "leaving the window hides it");
    }

    [Fact]
    public async Task MovingOverOtherElements_KeepsTheOverlay_AndParsesTheFilesOnce()
    {
        var shell = await ShellAsync();
        var first = P("a.mp3");

        var (visibleChanges, leaves, parses, stillVisible, title, gone) = await Session.Dispatch(
            async () =>
            {
                var window = Show(shell);
                var data = await FilesAsync(window, first);
                var leaveEvents = 0;
                window.AddHandler(DragDrop.DragLeaveEvent, (_, _) => leaveEvents++, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
                var content = InContent(window);
                window.DragDrop(content, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
                var firstTitle = Named(window, "TitleText").Text;
                var changes = 0;
                shell.DropOverlay!.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(DropOverlayViewModel.IsVisible) ? 1 : 0;

                // The pointer crosses elements of the window: one raw move raises the leave of the old element and, at once, the enter of
                // the next one. Sweep the content area so that it crosses several.
                var width = window.ClientSize.Width;
                var height = window.ClientSize.Height;
                for (var y = 60.0; y < height - 20; y += 40)
                {
                    for (var x = width - 20; x > width - 700; x -= 60)
                    {
                        window.DragDrop(new Point(x, y), RawDragEventType.DragOver, data, DragDropEffects.Copy, RawInputModifiers.None);
                    }
                }

                UiHarness.Pump(3);
                var still = shell.DropOverlay.IsVisible;
                var parseCount = window.DragParseCount;
                var changesInWindow = changes;
                var leavesInWindow = leaveEvents;
                var sameTitle = Named(window, "TitleText").Text;
                window.DragDrop(content, RawDragEventType.DragLeave, data, DragDropEffects.Copy, RawInputModifiers.None);
                UiHarness.Pump(3);
                var hidden = !shell.DropOverlay.IsVisible;
                window.Close();
                return (changesInWindow, leavesInWindow, parseCount, still, sameTitle == firstTitle, hidden);
            },
            Ct);

        Assert.True(leaves >= 3, "the window really saw a DragLeave between the moves");
        Assert.Equal(0, visibleChanges);
        Assert.True(stillVisible, "moving between elements keeps the overlay");
        Assert.True(title);
        Assert.Equal(1, parses);
        Assert.True(gone, "leaving the window hides it");

        static TextBlock Named(Window w, string name) => OverlayOf(w).GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == name);
    }

    [Fact]
    public async Task TheHiddenOverlay_LeavesTheTree_AfterTheFade_AndAtOnceUnderReducedMotion()
    {
        var shell = await ShellAsync();

        var (initially, shown, reducedGone) = await Session.Dispatch(
            () =>
            {
                var window = Show(shell);
                var root = RootOf(window);
                var start = root.IsVisible;
                shell.DropOverlay!.DragOver(["/a/talk.mp3"]);
                UiHarness.Pump(2);
                var visible = root.IsVisible;
                Motion.SetReduceMotion(window, true);
                shell.DropOverlay.DragLeave();
                UiHarness.Pump(2);
                var gone = !root.IsVisible;
                window.Close();
                return (start, visible, gone);
            },
            Ct);

        Assert.False(initially);
        Assert.True(shown);
        Assert.True(reducedGone);
    }

    [Fact]
    public async Task ADrop_OnTheWindow_OpensTheDialogOnTheOverlayLayer_WithTheFiles()
    {
        var shell = await ShellAsync();
        var first = P("talk.mp3");
        var second = P("notes.pdf");
        var probe = new EffectProbe();

        var (open, rows, effect, dialogAfterSecond) = await Session.Dispatch(
            async () =>
            {
                var window = Show(shell);
                probe.Attach(window);
                var data = await FilesAsync(window, first, second);
                var point = InContent(window);
                window.DragDrop(point, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
                window.DragDrop(point, RawDragEventType.DragOver, data, DragDropEffects.Copy, RawInputModifiers.None);
                var drop = probe.Last;
                window.DragDrop(point, RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
                UiHarness.Pump(4);
                var dialog = window.DropImportDialog;
                var vm = dialog?.DataContext as ImportDialogViewModel;
                var count = vm?.Files.Count ?? -1;

                // A second drop goes to the same dialog, even over its scrim.
                var more = await FilesAsync(window, P("later.wav"));
                window.DragDrop(point, RawDragEventType.DragEnter, more, DragDropEffects.Copy, RawInputModifiers.None);
                window.DragDrop(point, RawDragEventType.Drop, more, DragDropEffects.Copy, RawInputModifiers.None);
                UiHarness.Pump(3);
                var same = ReferenceEquals(window.DropImportDialog, dialog) ? vm?.Files.Count ?? -1 : -2;
                window.Close();
                return (dialog is not null, count, drop, same);
            },
            Ct);

        Assert.True(open, "the import dialog is on screen");
        Assert.Equal(2, rows);
        Assert.Equal(DragDropEffects.Copy, effect);
        Assert.Equal(3, dialogAfterSecond);
    }

    [Fact]
    public async Task TextDragged_ShowsNothing_AndIsRefused()
    {
        var shell = await ShellAsync();
        var probe = new EffectProbe();

        var (visible, effect) = await Session.Dispatch(
            () =>
            {
                var window = Show(shell);
                probe.Attach(window);
                var point = InContent(window);
                var text = TextData();
                window.DragDrop(point, RawDragEventType.DragEnter, text, DragDropEffects.Copy, RawInputModifiers.None);
                window.DragDrop(point, RawDragEventType.DragOver, text, DragDropEffects.Copy, RawInputModifiers.None);
                UiHarness.Pump(3);
                var shown = shell.DropOverlay!.IsVisible;
                window.Close();
                return (shown, probe.Last);
            },
            Ct);

        Assert.False(visible);
        Assert.Equal(DragDropEffects.None, effect);
    }

    /// <summary>The sidebar is outside the content area (the test shell has none, so a point left of it stands for it).</summary>
    [Fact]
    public async Task OutsideTheContentArea_TheSidebar_TakesNoDrops()
    {
        var shell = await ShellAsync();
        var files = new[] { P("a.mp3") };

        var (over, dropped, visible, inside) = await Session.Dispatch(
            () =>
            {
                var window = Show(shell);
                var overlay = shell.DropOverlay!;
                var left = new Point(-5, 100);
                var result = (
                    window.HandleDragOver(overlay, files, left),
                    window.HandleDrop(overlay, files, left),
                    overlay.IsVisible,
                    window.HandleDragOver(overlay, files, InContent(window)));
                window.Close();
                return result;
            },
            Ct);

        Assert.False(over);
        Assert.False(dropped);
        Assert.False(visible);
        Assert.True(inside);
        Assert.Null(shell.OpenImportDialog);
    }

    [Fact]
    public async Task TheOverlay_IsInvisible_AndNeverInTheWay_WithoutADrag()
    {
        var shell = await ShellAsync();

        var (opacity, hitTest, filling) = await Session.Dispatch(
            () =>
            {
                var window = Show(shell);
                var root = RootOf(window);
                var result = (root.Opacity, OverlayOf(window).IsHitTestVisible, OverlayOf(window).Bounds.Width > 0);
                window.Close();
                return result;
            },
            Ct);

        Assert.Equal(0, opacity);
        Assert.False(hitTest);
        Assert.True(filling);
    }

    [Fact]
    public async Task TheFade_Takes150ms_AndIsDroppedUnderReducedMotion()
    {
        var shell = await ShellAsync();

        var (fade, still) = await Session.Dispatch(
            () =>
            {
                var window = Show(shell);
                var root = RootOf(window);
                var transition = root.Transitions?.OfType<Avalonia.Animation.DoubleTransition>().SingleOrDefault();
                var normal = (transition?.Property == Visual.OpacityProperty, transition?.Duration);
                Motion.SetReduceMotion(window, true);
                UiHarness.Pump(2);
                var reduced = root.Transitions is null or { Count: 0 };
                window.Close();
                return (normal, reduced);
            },
            Ct);

        Assert.True(fade.Item1);
        Assert.Equal(TimeSpan.FromMilliseconds(150), fade.Duration);
        Assert.True(still, "no transition under reduced motion");
    }

    // Renders ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Light", "en", 1100, 720)]
    [InlineData("Dark", "en", 1100, 720)]
    [InlineData("Light", "en", 900, 600)]
    [InlineData("Dark", "en", 900, 600)]
    [InlineData("Light", "uk", 900, 600)]
    public async Task TheOverlay_WritesPng(string theme, string language, int width, int height)
    {
        using var culture = UiCulture.Override(language);
        var shell = await ShellAsync();
        var path = Path.Combine(RepoPaths.ScreenshotsDirectory, $"drop-overlay-{theme.ToLowerInvariant()}-{language}-{width}x{height}.png");

        var title = await Session.Dispatch(
            () =>
            {
                var window = Show(shell, theme, width, height);
                shell.DropOverlay!.DragOver(["/a/Zoom.mp4", "/a/memo.m4a", "/a/standup.wav"]);
                // Settle the fade (opacity transitions are driven by the frame clock).
                Motion.SetReduceMotion(window, true);
                UiHarness.Pump(6);
                UiHarness.Save(window, path);
                var text = OverlayOf(window).GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "TitleText").Text;
                window.Close();
                return text;
            },
            Ct);

        Assert.Equal(language == "uk" ? "Відпустіть, щоб імпортувати 3 файли" : "Drop to import 3 files", title);
        Assert.True(new FileInfo(path).Length > 10_000, "Screenshot looks empty.");
    }
}
