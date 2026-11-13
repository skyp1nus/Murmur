using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Murmur.App.Localization;
using Murmur.App.Services;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.App.Tests;

/// <summary>
/// The export dialog on a session (#60): the Export button opens it, what the session gives it (the chapter the playhead is in, the names
/// of the speakers) and headless renders in both themes at 1100 x 720 and 900 x 600.
/// </summary>
public sealed class ExportDialogViewTests : IDisposable
{
    private readonly IDisposable _english = UiCulture.Override("en");
    private readonly SessionFixture _fixture = new();
    private readonly FakeSummaryStore _summaries = new();
    private readonly FakeChapterStore _chapters = new();
    private readonly FakeSpeakerNameStore _names = new();
    private readonly SaverSpy _saver = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HeadlessUnitTestSession Session => UiHarness.Session;

    public void Dispose()
    {
        _english.Dispose();
        _fixture.Dispose();
        try
        {
            if (Environment.GetEnvironmentVariable("MURMUR_EXPORT_PICTURES") is not { Length: > 0 } && Directory.Exists(Pictures))
            {
                Directory.Delete(Pictures, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static Button ExportButton(SessionActionsView actions) =>
        actions.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("sessionGhost") && !b.Classes.Contains("round"));

    /// <summary>A real click: the pointer goes down and up in the middle of the control.</summary>
    private static void Click(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        UiHarness.Pump(3);
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private SessionServices Services(bool chapters = true, bool summary = true)
    {
        if (chapters)
        {
            _chapters.Put(_fixture.Record.Id, FakeChapterGenerator.Sample);
        }

        if (summary)
        {
            _summaries.Put(FakeSummarizer.Sample(_fixture.Record.Id));
        }

        return new SessionServices(Summaries: _summaries, Chapters: _chapters, SpeakerNames: _names);
    }

    private SessionViewModel Create(SessionServices services)
    {
        _fixture.UseShortTranscript();
        return new SessionViewModel(
            _fixture.Record,
            _fixture.Files,
            _fixture.Transcripts,
            _fixture.Player,
            _fixture.Processing,
            _fixture.Time,
            action => action(),
            services: services) { FileSaver = _saver };
    }

    private async Task<SessionViewModel> LoadAsync(bool chapters = true, bool summary = true)
    {
        var vm = Create(Services(chapters, summary));
        await vm.LoadAsync();
        return vm;
    }

    private async Task<ShellViewModel> OpenShellAsync(bool chapters = true, bool summary = true)
    {
        var services = Services(chapters, summary);
        var harness = new UiHarness();
        harness.Store.Add(_fixture.Record);
        _fixture.UseShortTranscript();
        var shell = harness.CreateShell(
            record => new SessionViewModel(record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action(), services: services) { FileSaver = _saver },
            _fixture.Processing);
        await shell.Library.LoadAsync();
        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;
        return shell;
    }

    // The session gives the dialog its data ---------------------------------------------------------------------

    [Fact]
    public async Task TheExportCommand_RaisesTheRequest_WithTheDialogOfTheRecording()
    {
        var vm = await LoadAsync();
        ExportDialogViewModel? requested = null;
        vm.ExportDialogRequested += dialog => requested = dialog;

        Assert.True(vm.OpenExportCommand.CanExecute(null));
        vm.OpenExportCommand.Execute(null);

        Assert.NotNull(requested);
        await requested.PreviewTask;
        Assert.Equal(vm.Title, requested.Title);
        Assert.Contains("Walked the client through the roadmap", requested.Preview, StringComparison.Ordinal);
        Assert.Contains("**You** `00:10`", requested.Preview, StringComparison.Ordinal);
        requested.Dispose();
    }

    [Fact]
    public async Task WithoutATranscript_TheExportCannotOpen()
    {
        var vm = Create(Services());
        _fixture.Transcripts.Transcript = null;
        await vm.LoadAsync();

        Assert.False(vm.OpenExportCommand.CanExecute(null));
        Assert.Null(vm.CreateExportDialog());
        Assert.Equal("There is no transcript to export yet.", vm.ActionMessage);
    }

    [Fact]
    public async Task TheChapterOption_IsThere_OnlyWhenThePlayheadIsInAChapter()
    {
        var withoutChapters = await LoadAsync(chapters: false);
        using (var none = withoutChapters.CreateExportDialog())
        {
            Assert.NotNull(none);
            Assert.False(none.HasChapterRange);
        }

        var vm = await LoadAsync();
        vm.UpdateCurrentChapter(S(800));
        using var dialog = vm.CreateExportDialog();
        Assert.NotNull(dialog);
        Assert.True(dialog.HasChapterRange);
        Assert.Equal("Chapter: PDF export", dialog.ChapterLabel);
        Assert.Equal("12:15 – 24:30", dialog.ChapterSpan);
        Assert.Equal("0:00:00 – 1:00:00", dialog.WholeSpan);
    }

    [Fact]
    public async Task TheLastChapter_EndsWhereTheRecordingEnds()
    {
        var vm = await LoadAsync();
        vm.UpdateCurrentChapter(S(1500));

        using var dialog = vm.CreateExportDialog();

        Assert.NotNull(dialog);
        Assert.Equal("Chapter: Next steps & pricing", dialog.ChapterLabel);
        Assert.Equal("0:24:30 – 1:00:00", dialog.ChapterSpan);
    }

    [Fact]
    public async Task TheWholeRecording_AndTheLastChapter_EndAtTheLengthThePlayerReports()
    {
        // The player reports 70 minutes while the stored record says 60: both ranges of the dialog follow the player (the title bar).
        var vm = Create(Services());
        _fixture.Player.Duration = TimeSpan.FromMinutes(70);
        await vm.LoadAsync();
        vm.UpdateCurrentChapter(S(1500));

        using var dialog = vm.CreateExportDialog();

        Assert.NotNull(dialog);
        Assert.Equal("0:00:00 – 1:10:00", dialog.WholeSpan);
        Assert.Equal("0:24:30 – 1:10:00", dialog.ChapterSpan);
    }

    [Fact]
    public async Task TheSpeakerNames_AreInTheExport_UntilTheBoxIsUnchecked()
    {
        _names.Put(_fixture.Record.Id, new SpeakerNames("Олена", "Anna K."));
        var vm = await LoadAsync();
        using var dialog = vm.CreateExportDialog();
        Assert.NotNull(dialog);
        await dialog.PreviewTask;

        Assert.Contains("**Олена** `00:10`", dialog.Preview, StringComparison.Ordinal);
        Assert.Contains("**Anna K.** `00:20`", dialog.Preview, StringComparison.Ordinal);

        dialog.IncludeSpeakerNames = false;
        await dialog.PreviewTask;
        Assert.DoesNotContain("Олена", dialog.Preview, StringComparison.Ordinal);
        Assert.DoesNotContain("Anna K.", dialog.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDialog_UsesTheTitleThePlayerShows()
    {
        var vm = await LoadAsync();

        using var dialog = vm.CreateExportDialog();

        Assert.NotNull(dialog);
        dialog.SaveCommand.Execute(null);
        await dialog.ActionTask;
        Assert.Equal("Щотижневий стендап команди.md", _saver.Texts.Single().SuggestedName);
    }

    // The view --------------------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheExportButton_OpensTheDialogOnTheOverlay_AndEscClosesIt()
    {
        var shell = await OpenShellAsync();

        var focused = false;
        var (opened, centered, escaped) = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("en");
                var window = UiHarness.Show(shell);
                var actions = window.GetVisualDescendants().OfType<SessionActionsView>().Single();
                var button = ExportButton(actions);
                Assert.Null(button.Flyout);
                Assert.Null(actions.OpenDialog);

                Click(window, button);
                UiHarness.Pump(6);
                var dialog = actions.OpenDialog;
                focused = window.FocusManager?.GetFocusedElement() is Control { Name: "FormatMarkdown" };
                var layer = OverlayLayer.GetOverlayLayer(window)!;
                var sheet = dialog?.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Sheet");
                var bounds = sheet?.Bounds ?? default;
                var inLayer = dialog is not null && layer.Children.Contains(dialog);
                var middle = bounds.Width == 840 && bounds.Height == 540
                    && Math.Abs((bounds.X + (bounds.Width / 2)) - (window.ClientSize.Width / 2)) < 1
                    && Math.Abs((bounds.Y + (bounds.Height / 2)) - (window.ClientSize.Height / 2)) < 1;

                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                UiHarness.Pump(3);
                var closed = actions.OpenDialog is null && dialog is not null && !layer.Children.Contains(dialog);
                window.Close();
                return (inLayer, middle, closed);
            },
            Ct);

        Assert.True(opened, "the dialog is on the overlay layer");
        Assert.True(centered, "840 x 540 in the middle of the window");
        Assert.True(escaped, "Esc closes it");
        Assert.True(focused, "the focus is in the dialog");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Esc_ClosesTheDialog_NotTheSession_TheFocusGoesBack_AndTheDialogOpensAgain()
    {
        var shell = await OpenShellAsync();
        var session = shell.Session!;
        var backs = 0;
        session.BackRequested += () => backs++;

        var result = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var actions = window.GetVisualDescendants().OfType<SessionActionsView>().Single();
                var layer = OverlayLayer.GetOverlayLayer(window)!;
                var button = ExportButton(actions);

                Click(window, button);
                var first = actions.OpenDialog;
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                UiHarness.Pump(3);
                var closed = actions.OpenDialog is null && first is not null && !layer.Children.Contains(first);
                var sessionStays = ReferenceEquals(shell.Session, session) && ReferenceEquals(shell.CurrentPage, session);
                var focusBack = ReferenceEquals(window.FocusManager?.GetFocusedElement(), button);

                // Once more: a new dialog, the old one is gone, and it closes the same way.
                Click(window, button);
                var second = actions.OpenDialog;
                var onlyOne = layer.Children.OfType<ExportDialog>().Count() == 1;
                var other = second is not null && !ReferenceEquals(first, second);
                var usable = second?.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "SaveButton" && b.IsEffectivelyEnabled) == true;
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                UiHarness.Pump(3);
                var closedAgain = actions.OpenDialog is null && !layer.Children.OfType<ExportDialog>().Any();
                window.Close();
                return (closed, sessionStays, focusBack, onlyOne, other, usable, closedAgain);
            },
            Ct);

        Assert.True(result.closed, "Esc closes the dialog");
        Assert.True(result.sessionStays, "the session page is still there");
        Assert.Equal(0, backs);
        Assert.True(result.focusBack, "the focus is on the Export button again");
        Assert.True(result.onlyOne, "one dialog on the layer");
        Assert.True(result.other, "a second opening makes a new dialog");
        Assert.True(result.usable);
        Assert.True(result.closedAgain);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [Trait("Category", "Slow")]
    public async Task TheCopyLabel_IsDimmed_WhenCopyIsDisabled(string theme)
    {
        var shell = await OpenShellAsync();

        var (enabled, disabled, ash, ink) = await Session.Dispatch(
            () =>
            {
                var window = new MainWindow { DataContext = shell, Width = 1100, Height = 720, RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light };
                window.Show();
                UiHarness.Pump();
                var actions = window.GetVisualDescendants().OfType<SessionActionsView>().Single();
                Click(window, ExportButton(actions));
                var dialog = actions.OpenDialog!;
                var label = dialog.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "CopyLabel");
                var copy = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CopyButton");
                Color Colour(string key) => label.TryFindResource(key, window.ActualThemeVariant, out var found) && found is ISolidColorBrush brush ? brush.Color : default;

                var on = ((ISolidColorBrush)label.Foreground!).Color;
                var enabledNow = copy.IsEffectivelyEnabled;
                dialog.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == "FormatDocx").IsChecked = true;
                UiHarness.Pump(4);
                var off = ((ISolidColorBrush)label.Foreground!).Color;
                var result = ((enabledNow, on), (copy.IsEffectivelyEnabled, off), Colour("Brush.Ash"), Colour("Brush.Ink"));
                window.Close();
                return result;
            },
            Ct);

        Assert.True(enabled.enabledNow);
        Assert.Equal(ink, enabled.on);
        Assert.False(disabled.IsEffectivelyEnabled);
        Assert.Equal(ash, disabled.off);
        Assert.NotEqual(enabled.on, disabled.off);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheCross_AndAClickOnTheScrim_CloseTheDialog_AClickOnTheSheetDoesNot()
    {
        var shell = await OpenShellAsync();

        var (afterSheet, afterCross, afterScrim) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var actions = window.GetVisualDescendants().OfType<SessionActionsView>().Single();
                var button = ExportButton(actions);

                Click(window, button);
                UiHarness.Pump(3);
                var dialog = actions.OpenDialog!;
                var sheet = dialog.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Sheet");
                var center = sheet.TranslatePoint(new Point(sheet.Bounds.Width / 2, sheet.Bounds.Height / 2), window)!.Value;
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                UiHarness.Pump(2);
                var stillOpen = actions.OpenDialog is not null;

                Click(window, dialog.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("exportClose")));
                UiHarness.Pump(2);
                var closedByCross = actions.OpenDialog is null;

                Click(window, button);
                UiHarness.Pump(3);
                var corner = new Point(10, 200);
                window.MouseDown(corner, MouseButton.Left);
                window.MouseUp(corner, MouseButton.Left);
                UiHarness.Pump(2);
                var closedByScrim = actions.OpenDialog is null;
                window.Close();
                return (stillOpen, closedByCross, closedByScrim);
            },
            Ct);

        Assert.True(afterSheet, "a click on the sheet leaves it open");
        Assert.True(afterCross);
        Assert.True(afterScrim);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task LeavingTheSession_TakesTheDialogWithIt()
    {
        var shell = await OpenShellAsync();

        var (gone, layerEmpty) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var actions = window.GetVisualDescendants().OfType<SessionActionsView>().Single();
                var layer = OverlayLayer.GetOverlayLayer(window)!;
                Click(window, ExportButton(actions));
                UiHarness.Pump(3);
                Assert.NotNull(actions.OpenDialog);

                shell.Session!.BackCommand.Execute(null);
                UiHarness.Pump(4);
                var result = (actions.OpenDialog is null, !layer.Children.OfType<ExportDialog>().Any());
                window.Close();
                return result;
            },
            Ct);

        Assert.True(gone);
        Assert.True(layerEmpty);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheBoxes_FollowTheViewModel_AndSubtitlesLockThem()
    {
        var shell = await OpenShellAsync();

        var (initial, locked, back) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var actions = window.GetVisualDescendants().OfType<SessionActionsView>().Single();
                Click(window, ExportButton(actions));
                UiHarness.Pump(3);
                var dialog = actions.OpenDialog!;
                (bool Checked, bool Enabled)[] Boxes() =>
                    new[] { "IncludeSummary", "IncludeActionItems", "IncludeTimestamps", "IncludeSpeakers" }
                        .Select(name => dialog.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == name))
                        .Select(c => (c.IsChecked == true, c.IsEnabled))
                        .ToArray();

                var first = Boxes();
                dialog.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == "FormatSrt").IsChecked = true;
                UiHarness.Pump(3);
                var srt = Boxes();
                dialog.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == "FormatMarkdown").IsChecked = true;
                UiHarness.Pump(3);
                var markdown = Boxes();
                window.Close();
                return (first, srt, markdown);
            },
            Ct);

        Assert.Equal([(true, true), (true, true), (true, true), (true, true)], initial);
        Assert.Equal([(false, false), (false, false), (true, false), (true, true)], locked);
        Assert.Equal(initial, back);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheChapterRow_ShowsOnlyWithAChapter_AndTheCopyButtonFollowsTheFormat()
    {
        var shell = await OpenShellAsync();
        var vm = shell.Session!;

        var (withChapter, withoutChapter, copyEnabled, copyForWord) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var actions = window.GetVisualDescendants().OfType<SessionActionsView>().Single();
                var open = () =>
                {
                    Click(window, ExportButton(actions));
                    UiHarness.Pump(3);
                    return actions.OpenDialog!;
                };

                vm.UpdateCurrentChapter(S(800));
                var dialog = open();
                var chapterRow = dialog.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == "RangeChapter");
                var shown = chapterRow.IsEffectivelyVisible;
                var copy = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CopyButton");
                var copyOn = copy.IsEffectivelyEnabled;
                dialog.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == "FormatDocx").IsChecked = true;
                UiHarness.Pump(3);
                var copyWord = copy.IsEffectivelyEnabled;
                dialog.Close();

                vm.UpdateCurrentChapter(S(-1));
                var plain = open();
                var hidden = !plain.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == "RangeChapter").IsEffectivelyVisible;
                window.Close();
                return (shown, hidden, copyOn, copyWord);
            },
            Ct);

        Assert.True(withChapter);
        Assert.True(withoutChapter);
        Assert.True(copyEnabled);
        Assert.False(copyForWord);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheDialog_IsInUkrainian_WhenTheUiIs()
    {
        var shell = await OpenShellAsync();

        var texts = await Session.Dispatch(
            () =>
            {
                using var scope = UiCulture.Override("uk");
                var window = UiHarness.Show(shell);
                var actions = window.GetVisualDescendants().OfType<SessionActionsView>().Single();
                Click(window, ExportButton(actions));
                UiHarness.Pump(4);
                var dialog = actions.OpenDialog!;
                var all = dialog.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Text is not null).Select(t => t.Text!).ToArray();
                var save = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SaveButton").Content as string;
                window.Close();
                return all.Append(save!).ToArray();
            },
            Ct);

        Assert.Contains("Формат", texts);
        Assert.Contains("Що включити", texts);
        Assert.Contains("Діапазон", texts);
        Assert.Contains("Увесь запис", texts);
        Assert.Contains("Попередній перегляд", texts);
        Assert.Contains("Копіювати", texts);
        Assert.Contains("Зберегти .md", texts);
        Assert.Contains("Звичайний текст", texts);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ThePreview_ShowsTheText_InTheFaceOfTheFormat()
    {
        var shell = await OpenShellAsync();

        var (markdown, plain, preview) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var actions = window.GetVisualDescendants().OfType<SessionActionsView>().Single();
                Click(window, ExportButton(actions));
                UiHarness.Pump(4);
                var dialog = actions.OpenDialog!;
                var text = dialog.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Name == "PreviewText");
                var markdownFace = text.FontFamily.Name;
                var markdownText = text.Text;
                dialog.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == "FormatPlainText").IsChecked = true;
                UiHarness.Pump(4);
                var plainFace = text.FontFamily.Name;
                window.Close();
                return (markdownFace, plainFace, markdownText);
            },
            Ct);

        Assert.DoesNotContain("Geist", markdown, StringComparison.Ordinal);
        Assert.Contains("Geist", plain, StringComparison.Ordinal);
        Assert.StartsWith("# Щотижневий стендап команди", preview, StringComparison.Ordinal);
    }

    // Screenshots -----------------------------------------------------------------------------------------------

    /// <summary>
    /// One folder for the PNGs of this test process, so parallel runs never write the same file; deleted when the test ends.
    /// <c>MURMUR_EXPORT_PICTURES=&lt;folder&gt;</c> writes them there instead and keeps them, for a look at the renders.
    /// </summary>
    private static readonly string Pictures = Environment.GetEnvironmentVariable("MURMUR_EXPORT_PICTURES") is { Length: > 0 } kept
        ? kept
        : Path.Combine(Path.GetTempPath(), "murmur-export-dialog-" + Environment.ProcessId);

    [Theory]
    [InlineData("Light", 1100, 720)]
    [InlineData("Dark", 1100, 720)]
    [InlineData("Light", 900, 600)]
    [InlineData("Dark", 900, 600)]
    [Trait("Category", "Slow")]
    public async Task TheDialog_Renders_InBothThemes_AtBothSizes(string theme, int width, int height)
    {
        var shell = await OpenShellAsync();
        var path = Path.Combine(Pictures, $"export-{theme.ToLowerInvariant()}-{width}x{height}.png");

        var sheetBounds = await Session.Dispatch(
            () =>
            {
                var window = new MainWindow { DataContext = shell, Width = width, Height = height, RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light };
                window.Show();
                UiHarness.Pump();
                shell.Session!.UpdateCurrentChapter(S(800));
                var actions = window.GetVisualDescendants().OfType<SessionActionsView>().Single();
                Click(window, ExportButton(actions));
                UiHarness.Pump(8);
                var sheet = actions.OpenDialog!.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Sheet");
                var bounds = sheet.TranslatePoint(default, window)!.Value;
                var result = new Rect(bounds, sheet.Bounds.Size);
                UiHarness.Save(window, path);
                window.Close();
                return result;
            },
            Ct);

        Assert.True(new FileInfo(path).Length > 10_000, "Screenshot looks empty.");
        Assert.Equal(840, sheetBounds.Width);
        Assert.Equal(540, sheetBounds.Height);
        Assert.InRange(sheetBounds.X, 0, width - 840);
        Assert.InRange(sheetBounds.Y, 0, height - 540);
    }

    [Theory]
    [InlineData("Light", ExportFormat.Srt)]
    [InlineData("Dark", ExportFormat.Docx)]
    [Trait("Category", "Slow")]
    public async Task TheSubtitleAndWordStates_Render(string theme, ExportFormat format)
    {
        var shell = await OpenShellAsync();
        var path = Path.Combine(Pictures, $"export-{format.ToString().ToLowerInvariant()}-{theme.ToLowerInvariant()}-1100x720.png");

        await Session.Dispatch(
            () =>
            {
                var window = new MainWindow { DataContext = shell, Width = 1100, Height = 720, RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light };
                window.Show();
                UiHarness.Pump();
                shell.Session!.UpdateCurrentChapter(S(800));
                var actions = window.GetVisualDescendants().OfType<SessionActionsView>().Single();
                Click(window, ExportButton(actions));
                var dialog = actions.OpenDialog!;
                var viewModel = (ExportDialogViewModel)dialog.DataContext!;
                dialog.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == (format == ExportFormat.Srt ? "FormatSrt" : "FormatDocx")).IsChecked = true;
                dialog.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == "RangeChapter").IsChecked = true;
                // The preview is formatted off the UI thread: pump until its result has arrived.
                for (var i = 0; i < 100 && !viewModel.PreviewTask.IsCompleted; i++)
                {
                    Thread.Sleep(20);
                    UiHarness.Pump(1);
                }

                UiHarness.Pump(8);
                Assert.Equal(format, viewModel.Format);
                Assert.Equal("Nothing was said in this range.", viewModel.Preview);
                UiHarness.Save(window, path);
                window.Close();
            },
            Ct);

        Assert.True(new FileInfo(path).Length > 10_000, "Screenshot looks empty.");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheLightAndTheDarkDialog_AreDifferentPictures_WithAScrimOverThePage()
    {
        var shell = await OpenShellAsync();

        var (light, dark, plainLight) = await Session.Dispatch(
            () =>
            {
                byte[] Shot(ThemeVariant theme, bool open)
                {
                    var window = new MainWindow { DataContext = shell, Width = 1100, Height = 720, RequestedThemeVariant = theme };
                    window.Show();
                    UiHarness.Pump();
                    var actions = window.GetVisualDescendants().OfType<SessionActionsView>().Single();
                    if (open)
                    {
                        Click(window, ExportButton(actions));
                        UiHarness.Pump(8);
                    }

                    var pixels = UiHarness.Pixels(window);
                    window.Close();
                    return pixels;
                }

                return (Shot(ThemeVariant.Light, true), Shot(ThemeVariant.Dark, true), Shot(ThemeVariant.Light, false));
            },
            Ct);

        Assert.NotEqual(light, dark);
        // The page under the scrim is dimmed: the top-left corner is darker with the dialog open than without it.
        Assert.True(light[0] < plainLight[0] || light[1] < plainLight[1] || light[2] < plainLight[2], "scrim over the page");
    }

    private sealed class SaverSpy : IFileSaver
    {
        public List<(string SuggestedName, string Extension, string Content)> Texts { get; } = [];

        public Task<string?> SaveTextAsync(string title, string suggestedFileName, string fileTypeName, string extension, string content)
        {
            Texts.Add((suggestedFileName, extension, content));
            return Task.FromResult<string?>(suggestedFileName);
        }

        public Task<string?> SaveBytesAsync(string title, string suggestedFileName, string fileTypeName, string extension, byte[] content) =>
            Task.FromResult<string?>(suggestedFileName);
    }
}
