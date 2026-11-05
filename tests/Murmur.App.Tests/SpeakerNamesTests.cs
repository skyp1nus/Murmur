using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Murmur.App.Localization;
using Murmur.App.Services;
using Murmur.App.Tests.Fakes;
using Murmur.App.ViewModels;
using Murmur.App.Views;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.App.Tests;

/// <summary>Speaker names: the chips, the rename popover (Enter, Esc, empty), the hint, and where the names show (lines, export). English UI.</summary>
public sealed class SpeakerNamesTests : IDisposable
{
    private readonly IDisposable _english = UiCulture.Override("en");
    private readonly SessionFixture _fixture = new();
    private readonly FakeSpeakerNameStore _names = new();
    private readonly FakeFileSaver _saver = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HeadlessUnitTestSession Session => UiHarness.Session;

    public void Dispose()
    {
        _english.Dispose();
        _fixture.Dispose();
    }

    /// <summary>Three lines of the microphone speaker and two of the other side.</summary>
    private void UseTranscript()
    {
        _fixture.UseTranscript(new Transcript(
            _fixture.Record.Id,
            [
                TranscriptFactory.Segment(Speaker.Me, 10, "Привіт", "усім"),
                TranscriptFactory.Segment(Speaker.Others, 20, "Добрий", "день"),
                TranscriptFactory.Segment(Speaker.Me, 30, "Починаємо"),
                TranscriptFactory.Segment(Speaker.Others, 40, "Згодні"),
                TranscriptFactory.Segment(Speaker.Me, 50, "Дякую"),
            ],
            "fake"));
    }

    private SessionViewModel Create(bool withStore = true)
    {
        UseTranscript();
        return new SessionViewModel(
            _fixture.Record,
            _fixture.Files,
            _fixture.Transcripts,
            _fixture.Player,
            _fixture.Processing,
            _fixture.Time,
            action => action(),
            services: new SessionServices(SpeakerNames: withStore ? _names : null)) { FileSaver = _saver };
    }

    private async Task<SessionViewModel> LoadAsync(bool withStore = true)
    {
        var vm = Create(withStore);
        await vm.LoadAsync();
        return vm;
    }

    // The chips -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheRow_HasAChipForEachSpeaker_WithTheDefaultWords_AndTheirLineCounts()
    {
        var vm = await LoadAsync();

        Assert.True(vm.ShowSpeakerChips);
        Assert.Equal(["You", "Others"], vm.SpeakerChips.Select(c => c.Label));
        Assert.Equal(["Y", "O"], vm.SpeakerChips.Select(c => c.Initial));
        Assert.Equal([3, 2], vm.SpeakerChips.Select(c => c.LineCount));
        Assert.Equal([true, false], vm.SpeakerChips.Select(c => c.IsMe));
    }

    [Fact]
    public async Task ASpeakerWithoutLines_HasNoChip()
    {
        UseTranscript();
        _fixture.UseTranscript(new Transcript(_fixture.Record.Id, [TranscriptFactory.Segment(Speaker.Me, 10, "Привіт")], "fake"));
        var vm = new SessionViewModel(_fixture.Record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action(), services: new SessionServices(SpeakerNames: _names));
        await vm.LoadAsync();

        Assert.Equal([Speaker.Me], vm.SpeakerChips.Select(c => c.Speaker));
    }

    [Fact]
    public async Task WithoutAStore_TheRowIsNotOffered_AndTheLinesKeepTheDefaultWords()
    {
        var vm = await LoadAsync(withStore: false);

        Assert.False(vm.ShowSpeakerChips);
        Assert.Equal(["You", "Others", "You", "Others", "You"], vm.Segments.Select(s => s.SpeakerLabel));
    }

    [Fact]
    public async Task StoredNames_AreOnTheChipsAndOnEveryLine_FromTheFirstFrame()
    {
        _names.Put(_fixture.Record.Id, new SpeakerNames("Олена", "Marek W."));
        var vm = await LoadAsync();

        Assert.Equal(["Олена", "Marek W."], vm.SpeakerChips.Select(c => c.Label));
        Assert.Equal(["О", "M"], vm.SpeakerChips.Select(c => c.Initial));
        Assert.Equal(["Олена", "Marek W.", "Олена", "Marek W.", "Олена"], vm.Segments.Select(s => s.SpeakerLabel));
        Assert.Equal(["О", "M", "О", "M", "О"], vm.Segments.Select(s => s.Initial));
    }

    [Fact]
    public async Task TheRow_IsHidden_WhileTheTranscriptListIsNot()
    {
        var vm = await LoadAsync();
        Assert.True(vm.ShowSpeakerChips);

        vm.SelectedTab = SessionTab.Summary;

        Assert.False(vm.ShowSpeakerChips);
    }

    // The popover -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheHint_SaysHowManyLinesTheNameWillAppearOn()
    {
        var vm = await LoadAsync();

        Assert.Equal("Updates all 3 lines in this recording", vm.SpeakerChips[0].HintText);
        Assert.Equal("Updates all 2 lines in this recording", vm.SpeakerChips[1].HintText);
    }

    [Fact]
    public async Task TheHint_UsesTheSingularForOneLine()
    {
        _fixture.UseTranscript(new Transcript(_fixture.Record.Id, [TranscriptFactory.Segment(Speaker.Others, 10, "Привіт")], "fake"));
        var vm = new SessionViewModel(_fixture.Record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action(), services: new SessionServices(SpeakerNames: _names));
        await vm.LoadAsync();

        Assert.Equal("Updates 1 line in this recording", vm.SpeakerChips[0].HintText);
    }

    [Fact]
    public async Task AClick_OpensThePopover_WithTheCurrentNameInTheBox()
    {
        _names.Put(_fixture.Record.Id, new SpeakerNames(null, "Anna K."));
        var vm = await LoadAsync();

        vm.SpeakerChips[1].BeginEditCommand.Execute(null);
        vm.SpeakerChips[0].BeginEditCommand.Execute(null);

        Assert.Equal("Anna K.", vm.SpeakerChips[1].EditText);
        Assert.True(vm.SpeakerChips[1].IsEditing);
        Assert.Equal("You", vm.SpeakerChips[0].EditText);
    }

    [Fact]
    public async Task Enter_SavesTheName_ClosesThePopover_AndRelabelsEveryLineOfThatSpeaker()
    {
        var vm = await LoadAsync();
        var chip = vm.SpeakerChips[1];
        var changed = new List<string?>();
        vm.Segments[1].PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        chip.BeginEditCommand.Execute(null);
        chip.EditText = "  Anna K.  ";
        await chip.CommitCommand.ExecuteAsync(null);

        Assert.False(chip.IsEditing);
        Assert.Equal([(_fixture.Record.Id, Speaker.Others, (string?)"Anna K.")], _names.Writes);
        Assert.Equal("Anna K.", chip.Label);
        Assert.Equal("A", chip.Initial);
        Assert.Equal(["You", "Anna K.", "You", "Anna K.", "You"], vm.Segments.Select(s => s.SpeakerLabel));
        Assert.Equal(["Y", "A", "Y", "A", "Y"], vm.Segments.Select(s => s.Initial));
        Assert.Contains("SpeakerLabel", changed);
        Assert.Contains("Initial", changed);
        Assert.Equal("You", vm.SpeakerChips[0].Label);
    }

    [Fact]
    public async Task TheName_IsShownAsTheStoreKeptIt_TrimmedAndCut()
    {
        var vm = await LoadAsync();
        var chip = vm.SpeakerChips[0];

        chip.BeginEditCommand.Execute(null);
        chip.EditText = new string('x', 55);
        await chip.CommitCommand.ExecuteAsync(null);

        Assert.Equal(new string('x', 40), chip.Label);
        Assert.Equal(40, chip.MaxNameLength);
        Assert.All(vm.Segments.Where(s => s.Speaker == Speaker.Me), s => Assert.Equal(new string('x', 40), s.SpeakerLabel));
    }

    [Fact]
    public async Task Escape_ClosesThePopover_WithoutSaving_AndForgetsWhatWasTyped()
    {
        var vm = await LoadAsync();
        var chip = vm.SpeakerChips[0];

        chip.BeginEditCommand.Execute(null);
        chip.EditText = "Typed";
        chip.CancelCommand.Execute(null);

        Assert.False(chip.IsEditing);
        Assert.Empty(_names.Writes);
        Assert.Equal("You", chip.Label);
        Assert.Equal("You", chip.EditText);
        Assert.All(vm.Segments, s => Assert.DoesNotContain("Typed", s.SpeakerLabel));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("You")]
    public async Task AnEmptyName_OrTheDefaultWord_BringsBackTheDefault(string typed)
    {
        _names.Put(_fixture.Record.Id, new SpeakerNames("Олена", "Anna K."));
        var vm = await LoadAsync();
        var chip = vm.SpeakerChips[0];

        chip.BeginEditCommand.Execute(null);
        chip.EditText = typed;
        await chip.CommitCommand.ExecuteAsync(null);

        Assert.Equal([(_fixture.Record.Id, Speaker.Me, (string?)null)], _names.Writes);
        Assert.Equal("You", chip.Label);
        Assert.Equal("Y", chip.Initial);
        Assert.Equal(["You", "Anna K.", "You", "Anna K.", "You"], vm.Segments.Select(s => s.SpeakerLabel));
    }

    [Fact]
    public async Task AStoreThatFails_KeepsTheOldName_AndSaysSo()
    {
        var vm = await LoadAsync();
        _names.SetFailure = new IOException("disk full");
        var chip = vm.SpeakerChips[0];

        chip.BeginEditCommand.Execute(null);
        chip.EditText = "Олена";
        await chip.CommitCommand.ExecuteAsync(null);

        Assert.Equal("You", chip.Label);
        Assert.All(vm.Segments.Where(s => s.Speaker == Speaker.Me), s => Assert.Equal("You", s.SpeakerLabel));
        Assert.Equal("Couldn’t save the speaker name: disk full", vm.ActionMessage);
    }

    [Fact]
    public async Task ANameChosenEarlier_SurvivesReopeningTheRecording()
    {
        var first = await LoadAsync();
        first.SpeakerChips[1].BeginEditCommand.Execute(null);
        first.SpeakerChips[1].EditText = "Anna K.";
        await first.SpeakerChips[1].CommitCommand.ExecuteAsync(null);

        var second = await LoadAsync();

        Assert.Equal("Anna K.", second.SpeakerChips[1].Label);
        Assert.Equal("Anna K.", second.Segments[1].SpeakerLabel);
    }

    // Exports ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ExportFormat.Markdown, "**Anna K.**")]
    [InlineData(ExportFormat.PlainText, "Anna K.: Добрий день")]
    [InlineData(ExportFormat.Srt, "Anna K.: Добрий день")]
    [InlineData(ExportFormat.WebVtt, "<v Anna K>")] // WebVTT voice names carry no dots
    public async Task TheExport_UsesTheNames(ExportFormat format, string expected)
    {
        var vm = await LoadAsync();
        await vm.RenameSpeakerAsync(Speaker.Others, "Anna K.");
        await vm.RenameSpeakerAsync(Speaker.Me, "Олена");

        switch (format)
        {
            case ExportFormat.Markdown:
                vm.ExportMarkdownCommand.Execute(null);
                break;
            case ExportFormat.PlainText:
                vm.ExportTextCommand.Execute(null);
                break;
            case ExportFormat.Srt:
                vm.ExportSrtCommand.Execute(null);
                break;
            default:
                vm.ExportWebVttCommand.Execute(null);
                break;
        }

        await vm.ExportTask;

        var content = Assert.Single(_saver.Saved).Content;
        Assert.Contains(expected, content);
        Assert.Contains("Олена", content);
        Assert.DoesNotContain("You", content);
        Assert.DoesNotContain("Others", content);
    }

    [Fact]
    public void ExportLabels_FallBackToTheUiWords_ForASpeakerWithoutAName()
    {
        var labels = SessionViewModel.ExportTexts(new SpeakerNames(null, "Anna K."));

        Assert.Equal("You", labels.Me);
        Assert.Equal("Anna K.", labels.Others);
        Assert.Equal("You", SessionViewModel.ExportTexts().Me);
    }

    // The view --------------------------------------------------------------------------------------------------

    private async Task<ShellViewModel> OpenShellAsync()
    {
        var harness = new UiHarness();
        harness.Store.Add(_fixture.Record);
        var shell = harness.CreateShell(_ => Create(), _fixture.Processing);
        await shell.Library.LoadAsync();
        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;
        return shell;
    }

    [Fact]
    public async Task TheChips_AreDrawn_28PixelsHigh_AboveTheSearchBox()
    {
        var shell = await OpenShellAsync();

        var (count, heights, above) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                var row = window.GetVisualDescendants().OfType<SpeakerChipsView>().Single();
                var chips = row.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("speakerChip")).ToArray();
                var search = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "SearchBox");
                var result = (
                    chips.Length,
                    chips.Select(c => c.Bounds.Height).ToArray(),
                    row.TranslatePoint(default, window)!.Value.Y < search.TranslatePoint(default, window)!.Value.Y);
                window.Close();
                return result;
            },
            Ct);

        Assert.Equal(2, count);
        Assert.All(heights, h => Assert.Equal(28, h));
        Assert.True(above);
    }

    [Fact]
    public async Task Keyboard_FocusTheChip_Enter_Type_Enter_RenamesTheSpeaker()
    {
        var shell = await OpenShellAsync();

        var (opened, label, boxFocused) = await Session.Dispatch(
            async () =>
            {
                var window = UiHarness.Show(shell);
                var chip = window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("speakerChip"));
                chip.Focus();
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                UiHarness.Pump(6);
                var vmChip = shell.Session!.SpeakerChips[0];
                var open = vmChip.IsEditing;
                var box = window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "NameBox" && t.IsEffectivelyVisible)
                    ?? FindInPopups(window);
                var focused = box?.IsFocused == true;
                window.KeyTextInput("Олена");
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                UiHarness.Pump(6);
                await Task.Delay(50, Ct);
                UiHarness.Pump(3);
                var result = (open, shell.Session.Segments[0].SpeakerLabel, focused);
                window.Close();
                return result;
            },
            Ct);

        Assert.True(opened);
        Assert.Equal("Олена", label);
        Assert.True(boxFocused);
    }

    [Theory]
    [InlineData("Light", 1100, 720)]
    [InlineData("Dark", 1100, 720)]
    [InlineData("Light", 900, 600)]
    public async Task Session_WithTheSpeakerRowAndThePopover_WritesPng(string theme, int width, int height)
    {
        _names.Put(_fixture.Record.Id, new SpeakerNames(null, "Anna K."));
        var shell = await OpenShellAsync();
        var path = System.IO.Path.Combine(RepoPaths.ScreenshotsDirectory, "speakers-session-" + theme.ToLowerInvariant() + "-" + width + "x" + height + ".png");

        await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                window.Width = width;
                window.Height = height;
                window.RequestedThemeVariant = theme == "Dark" ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
                shell.Session!.SpeakerChips[1].BeginEditCommand.Execute(null);
                UiHarness.Pump(6);
                UiHarness.Save(window, path);
                window.Close();
            },
            Ct);

        Assert.True(new FileInfo(path).Length > 10_000, "Screenshot looks empty.");
    }

    private static TextBox? FindInPopups(Window window) =>
        window.GetVisualDescendants().OfType<Popup>().Select(p => p.Child?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()).FirstOrDefault(t => t is not null);

    private sealed class FakeFileSaver : IFileSaver
    {
        public List<(string SuggestedName, string Extension, string Content)> Saved { get; } = [];

        public Task<string?> SaveTextAsync(string title, string suggestedFileName, string fileTypeName, string extension, string content)
        {
            Saved.Add((suggestedFileName, extension, content));
            return Task.FromResult<string?>("export" + extension);
        }
    }
}
