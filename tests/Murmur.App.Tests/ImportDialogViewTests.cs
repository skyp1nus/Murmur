using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Murmur.App.Localization;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Import;

namespace Murmur.App.Tests;

/// <summary>
/// The import dialog on the Library (#67): the "Import files" pill (only with an import queue), the dialog on the overlay layer and the ways
/// it closes, how it fits a 900 x 600 window, and headless renders in both themes at 1100 x 720 and 900 x 600.
/// </summary>
public sealed class ImportDialogViewTests : IDisposable
{
    private static readonly DateTimeOffset Now = UiHarness.Now;

    private readonly IDisposable _english = UiCulture.Override("en");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "murmur-import-view-" + Guid.NewGuid().ToString("N"));
    private readonly FakeImportQueue _queue = new();
    private readonly FakeMediaDecoder _decoder = new();
    private readonly TestTime _time = new(Now);

    public ImportDialogViewTests()
    {
        Directory.CreateDirectory(_root);
        _decoder.Answer("Zoom_Meeting_2026-10-30.mp4", MediaKind.Video, 412_000_000, new TimeSpan(0, 45, 10));
        _decoder.Answer("voice-memo-1029.m4a", MediaKind.Audio, 6_000_000, new TimeSpan(0, 6, 28));
        _decoder.Answer("standup-2026-10-28.wav", MediaKind.Audio, 21_000_000, new TimeSpan(0, 14, 2));
        _decoder.Answer("interview.mkv", MediaKind.Video, 1_240_000_000, new TimeSpan(1, 7, 12));
    }

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

    private string P(string name) => Path.Combine(_root, name);

    /// <summary>The Downloads folder of the mockup: one podcast of last week.</summary>
    private FakeDownloadsFolder Downloads(params (string Name, int Days, long Bytes)[] files)
    {
        var folder = Directory.CreateDirectory(P("Downloads")).FullName;
        foreach (var (name, days, bytes) in files.Length == 0 ? [("podcast-ep42.mp3", 8, 58_000_000L)] : files)
        {
            var path = Path.Combine(folder, name);
            using (var stream = File.Create(path))
            {
                stream.SetLength(bytes);
            }

            File.SetLastWriteTimeUtc(path, (Now - TimeSpan.FromDays(days)).UtcDateTime);
        }

        return new FakeDownloadsFolder(folder);
    }

    /// <summary>A Library with three recordings; with <paramref name="imports"/> it has the import queue (and the pill).</summary>
    private async Task<ShellViewModel> ShellAsync(bool imports = true, IDownloadsFolder? downloads = null)
    {
        var store = new FakeSessionStore();
        store.Add(Sessions.Make("Weekly product sync", Now.AddHours(-2), new TimeSpan(0, 47, 12)));
        store.Add(Sessions.Make("Client call — Acme", Now.AddDays(-1), new TimeSpan(0, 32, 5)));
        store.Add(Sessions.Make("Design review", Now.AddDays(-3), new TimeSpan(1, 4, 40)));
        var library = new LibraryViewModel(
            store,
            _time,
            imports: imports ? _queue : null,
            mediaProbe: imports ? _decoder : null,
            downloads: downloads);
        var recording = new RecordingViewModel(new FakeRecorder(), new FakeCapture(), store, action => action());
        var shell = new ShellViewModel(library, recording);
        await library.LoadAsync();
        return shell;
    }

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

    private static LibraryView LibraryOf(Window window) => window.GetVisualDescendants().OfType<LibraryView>().Single();

    private static Button Pill(Window window) => window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ImportFilesButton");

    private static T Named<T>(Visual root, string name)
        where T : Control => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    /// <summary>A real click: the pointer goes down and up in the middle of the control.</summary>
    private static void Click(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        UiHarness.Pump(3);
    }

    /// <summary>Opens the dialog with the pill and waits for the Downloads search; returns the view and its view model.</summary>
    private static (ImportDialog Dialog, ImportDialogViewModel ViewModel) Open(Window window)
    {
        Click(window, Pill(window));
        var dialog = LibraryOf(window).OpenImportDialog ?? throw new InvalidOperationException("The import dialog did not open.");
        var vm = (ImportDialogViewModel)dialog.DataContext!;
        vm.RecentTask.Wait(Ct);
        UiHarness.Pump(4);
        return (dialog, vm);
    }

    /// <summary>Adds files and lets their probes (thread pool) and the posted results (UI thread) finish.</summary>
    private void Add(ImportDialogViewModel vm, params string[] names)
    {
        vm.AddFiles(names.Select(P));
        vm.WhenProbed().Wait(Ct);
        UiHarness.Pump(4);
    }

    // The pill ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePill_IsShownWithAnImportQueue_AndHiddenWithout()
    {
        var with = await ShellAsync();
        var without = await ShellAsync(imports: false);

        var (shown, hidden, label) = await Session.Dispatch(
            () =>
            {
                var a = Show(with);
                var pill = Pill(a);
                var result1 = pill.IsEffectivelyVisible && pill.Bounds.Height == 32;
                var text = pill.GetVisualDescendants().OfType<TextBlock>().Single().Text;
                a.Close();
                var b = Show(without);
                var result2 = !Pill(b).IsVisible;
                b.Close();
                return (result1, result2, text);
            },
            Ct);

        Assert.True(shown);
        Assert.True(hidden);
        Assert.Equal("Import files", label);
        Assert.True(with.Library.CanImport);
        Assert.False(without.Library.CanImport);
        Assert.False(without.Library.OpenImportCommand.CanExecute(null));
    }

    [Fact]
    public async Task ThePill_OpensTheDialogOnTheOverlay_InTheMiddle_AndEscClosesIt_WithoutImporting()
    {
        var shell = await ShellAsync();

        var (inLayer, width, centered, closed, focusBack) = await Session.Dispatch(
            () =>
            {
                var window = Show(shell);
                var pill = Pill(window);
                var (dialog, _) = Open(window);
                var layer = OverlayLayer.GetOverlayLayer(window)!;
                var sheet = Named<Border>(dialog, "Sheet");
                var origin = sheet.TranslatePoint(default, window)!.Value;
                var result = (
                    layer.Children.Contains(dialog),
                    sheet.Bounds.Width,
                    Math.Abs(origin.X + (sheet.Bounds.Width / 2) - (window.ClientSize.Width / 2)) < 1);

                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                UiHarness.Pump(3);
                var gone = LibraryOf(window).OpenImportDialog is null && !layer.Children.Contains(dialog);
                var focus = ReferenceEquals(window.FocusManager?.GetFocusedElement(), pill);
                window.Close();
                return (result.Item1, result.Item2, result.Item3, gone, focus);
            },
            Ct);

        Assert.True(inLayer, "the dialog is on the overlay layer");
        Assert.Equal(520, width);
        Assert.True(centered, "the card is in the middle of the window");
        Assert.True(closed, "Esc closes it");
        Assert.True(focusBack, "the focus is on the pill again");
        Assert.Equal(0, _queue.Calls);
    }

    [Fact]
    public async Task TheCross_Cancel_AndAClickOnTheScrim_CloseTheDialog_AClickOnTheCardDoesNot()
    {
        var shell = await ShellAsync();

        var (afterCard, afterCross, afterCancel, afterScrim) = await Session.Dispatch(
            () =>
            {
                var window = Show(shell);
                var library = LibraryOf(window);

                var (dialog, vm) = Open(window);
                Add(vm, "voice-memo-1029.m4a");
                var sheet = Named<Border>(dialog, "Sheet");
                Click(window, Named<TextBlock>(dialog, "FilesTitle"));
                var stillOpen = library.OpenImportDialog is not null;

                Click(window, Named<Button>(dialog, "CloseButton"));
                var byCross = library.OpenImportDialog is null && vm.IsClosed;

                (dialog, _) = Open(window);
                Click(window, Named<Button>(dialog, "CancelButton"));
                var byCancel = library.OpenImportDialog is null;

                Open(window);
                var corner = new Point(10, 300);
                window.MouseDown(corner, MouseButton.Left);
                window.MouseUp(corner, MouseButton.Left);
                UiHarness.Pump(2);
                var byScrim = library.OpenImportDialog is null;
                window.Close();
                return (stillOpen, byCross, byCancel, byScrim);
            },
            Ct);

        Assert.True(afterCard, "a click in the card leaves it open");
        Assert.True(afterCross);
        Assert.True(afterCancel);
        Assert.True(afterScrim);
        Assert.Equal(0, _queue.Calls);
    }

    [Fact]
    public async Task ImportNFiles_HandsTheFilesOver_AndClosesTheDialog()
    {
        var shell = await ShellAsync();

        var (label, enabledBefore, closed) = await Session.Dispatch(
            () =>
            {
                var window = Show(shell);
                var (dialog, vm) = Open(window);
                var button = Named<Button>(dialog, "ImportButton");
                var before = button.IsEffectivelyEnabled;
                Add(vm, "Zoom_Meeting_2026-10-30.mp4", "notes.pdf", "voice-memo-1029.m4a");
                var text = button.GetVisualDescendants().OfType<TextBlock>().Single().Text;
                Click(window, Named<RadioButton>(dialog, "LeaveInPlace"));
                Click(window, button);
                UiHarness.Pump(3);
                var gone = LibraryOf(window).OpenImportDialog is null;
                window.Close();
                return (text, before, gone);
            },
            Ct);

        Assert.False(enabledBefore, "Import 0 files is disabled");
        Assert.Equal("Import 2 files", label);
        Assert.True(closed);
        Assert.Equal([P("Zoom_Meeting_2026-10-30.mp4"), P("voice-memo-1029.m4a")], _queue.Requests.Select(r => r.Path));
        Assert.All(_queue.Requests, r => Assert.Equal(new ImportOptions(null, OriginalFileMode.LeaveInPlace), r.Options));
    }

    [Fact]
    public async Task TheSections_FollowTheViewModel()
    {
        var withDownloads = await ShellAsync(downloads: Downloads());
        var withoutDownloads = await ShellAsync(downloads: null);

        var (recentShown, queueBefore, queueAfter, recentHidden, formats) = await Session.Dispatch(
            () =>
            {
                var window = Show(withDownloads);
                var (dialog, vm) = Open(window);
                var recent = Named<Panel>(dialog, "RecentSection").IsEffectivelyVisible;
                var before = Named<Panel>(dialog, "QueueSection").IsVisible;
                Add(vm, "notes.pdf");
                var after = Named<Panel>(dialog, "QueueSection").IsVisible;
                var text = Named<TextBlock>(dialog, "FormatsText").Text;
                window.Close();

                var other = Show(withoutDownloads);
                var (dialog2, _) = Open(other);
                var hidden = !Named<Panel>(dialog2, "RecentSection").IsVisible;
                other.Close();
                return (recent, before, after, hidden, text);
            },
            Ct);

        Assert.True(recentShown);
        Assert.False(queueBefore, "no list while there are no files");
        Assert.True(queueAfter);
        Assert.True(recentHidden);
        Assert.Equal("MP3 · WAV · M4A · FLAC · OGG · MP4 · MKV · MOV · WEBM", formats);
    }

    [Theory]
    [InlineData(900, 600)]
    [InlineData(1100, 720)]
    public async Task TheCard_FitsTheWindow_TheBodyScrolls_TheHeaderAndFooterStay(int width, int height)
    {
        var shell = await ShellAsync(downloads: Downloads(
            ("podcast-ep42.mp3", 8, 58_000_000),
            ("lecture.m4a", 2, 31_000_000),
            ("call-recording.wav", 1, 12_000_000),
            ("webinar.mp4", 5, 380_000_000),
            ("memo.ogg", 10, 2_000_000)));

        var (fits, header, footer, scrolls) = await Session.Dispatch(
            () =>
            {
                var window = Show(shell, width: width, height: height);
                var (dialog, vm) = Open(window);
                Add(vm, "Zoom_Meeting_2026-10-30.mp4", "voice-memo-1029.m4a", "notes.pdf", "standup-2026-10-28.wav", "interview.mkv");
                Rect InWindow(Control c) => new(c.TranslatePoint(default, window)!.Value, c.Bounds.Size);
                var sheet = InWindow(Named<Border>(dialog, "Sheet"));
                var client = new Rect(window.ClientSize);
                var headerBox = InWindow(dialog.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("importHeader")));
                var footerBox = InWindow(dialog.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("importFooter")));
                var body = Named<ScrollViewer>(dialog, "Body");
                var result = (
                    client.Contains(sheet),
                    headerBox.Height == 48 && sheet.Contains(headerBox),
                    footerBox.Height == 52 && sheet.Contains(footerBox) && footerBox.Bottom <= client.Bottom,
                    body.Extent.Height > body.Viewport.Height);
                window.Close();
                return result;
            },
            Ct);

        Assert.True(fits, "the card is inside the window");
        Assert.True(header, "the header is in the card");
        Assert.True(footer, "the footer is in the card and the window");
        Assert.True(scrolls, "five downloads and five files are more than the body shows: it scrolls");
    }

    // Renders ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Light", "en", 1100, 720)]
    [InlineData("Dark", "en", 1100, 720)]
    [InlineData("Light", "en", 900, 600)]
    [InlineData("Dark", "en", 900, 600)]
    [InlineData("Light", "uk", 900, 600)]
    public async Task TheDialog_WithTheMockupsRows_WritesPng(string theme, string language, int width, int height)
    {
        using var culture = UiCulture.Override(language);
        var shell = await ShellAsync(downloads: Downloads());
        var path = Path.Combine(RepoPaths.ScreenshotsDirectory, $"import-dialog-{theme.ToLowerInvariant()}-{language}-{width}x{height}.png");

        var (rows, title) = await Session.Dispatch(
            () =>
            {
                var window = Show(shell, theme, width, height);
                var (dialog, vm) = Open(window);
                Add(vm, "Zoom_Meeting_2026-10-30.mp4", "voice-memo-1029.m4a", "notes.pdf");
                UiHarness.Save(window, path);
                var result = (vm.Files.Count, Named<TextBlock>(dialog, "FilesTitle").Text);
                window.Close();
                return result;
            },
            Ct);

        Assert.Equal(3, rows);
        Assert.Equal(language == "uk" ? "2 файли до імпорту" : "2 files to import", title);
        Assert.True(new FileInfo(path).Length > 10_000, "Screenshot looks empty.");
    }

    [Theory]
    [InlineData("Light", 1100, 720)]
    [InlineData("Dark", 900, 600)]
    public async Task TheLibrary_WithThePill_WritesPng(string theme, int width, int height)
    {
        var shell = await ShellAsync();

        // As in the app: the Ask your library card beside it and the record hotkey after the buttons.
        shell.Library.CanAskAll = true;
        shell.Library.RecordHotkeyText = "Ctrl Shift R";
        var path = Path.Combine(RepoPaths.ScreenshotsDirectory, $"import-pill-{theme.ToLowerInvariant()}-{width}x{height}.png");

        var inCard = await Session.Dispatch(
            () =>
            {
                var window = Show(shell, theme, width, height);
                var pill = Pill(window);
                var card = pill.FindAncestorOfType<Border>(includeSelf: false);
                while (card is not null && !card.Classes.Contains("heroCard"))
                {
                    card = card.FindAncestorOfType<Border>();
                }

                var inside = card is not null
                    && new Rect(card.Bounds.Size).Contains(new Rect(pill.TranslatePoint(default, card)!.Value, pill.Bounds.Size));
                UiHarness.Save(window, path);
                window.Close();
                return inside;
            },
            Ct);

        Assert.True(inCard, "the pill stays inside the New recording card");
        Assert.True(new FileInfo(path).Length > 10_000, "Screenshot looks empty.");
    }
}
