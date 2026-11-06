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
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.App.Tests;

/// <summary>#58: the underline of uncertain words, "Did you mean" and the fix (line, store, index, vocabulary).</summary>
public sealed class UncertainWordsTests : IDisposable
{
    private readonly SessionFixture _fixture = new();
    private readonly FakeVocabularyStore _vocabulary = new("Gemma", "Parakeets", "parakeet", "Paris");
    private readonly FakeWordSuggester _suggester = new();

    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fixture.Dispose();

    private static TranscriptWord W(string text, double start, float? confidence = null) =>
        new(text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(start + 0.4)) { Confidence = confidence };

    /// <summary>
    /// Three lines: the first has no uncertain word, the second has "parquet?" (0.31) and "fully" (0.59), the third one has only words whose
    /// confidence is unknown or at the threshold.
    /// </summary>
    private Transcript UseTranscript()
    {
        var transcript = new Transcript(
            _fixture.Record.Id,
            [
                new TranscriptSegment(Speaker.Me, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12), "Okay, a quick run.", [W("Okay,", 10, 0.97f), W("a", 10.5, 0.99f), W("quick", 11, 0.95f), W("run.", 11.5, 0.9f)]),
                new TranscriptSegment(Speaker.Others, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(23), "Does it run on the parquet? model fully offline", [W("Does", 20, 0.99f), W("it", 20.4, 0.98f), W("run", 20.8, 0.93f), W("on", 21.2, 0.98f), W("the", 21.6, 0.97f), W("parquet?", 22, 0.31f), W("model", 22.4, 0.88f), W("fully", 22.8, 0.59f), W("offline", 23.2, 0.9f)]),
                new TranscriptSegment(Speaker.Me, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(31), "Yes it does", [W("Yes", 30), W("it", 30.4, 0.6f), W("does", 30.8, 0.61f)]),
            ],
            "fake",
            "en");
        _fixture.UseTranscript(transcript);
        return transcript;
    }

    private SessionViewModel Create(bool vocabulary = true, bool suggester = true) =>
        new(_fixture.Record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action(),
            services: new SessionServices(Vocabulary: vocabulary ? _vocabulary : null, WordSuggester: suggester ? _suggester : null));

    private async Task<SessionViewModel> LoadAsync(bool vocabulary = true, bool suggester = true)
    {
        UseTranscript();
        var vm = Create(vocabulary, suggester);
        await vm.LoadAsync();
        return vm;
    }

    // The threshold --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyWordsBelowTheThreshold_AreUncertain()
    {
        var vm = await LoadAsync();

        Assert.Null(vm.Segments[0].UncertainWords);
        Assert.Equal([false, false, false, false, false, true, false, true, false], vm.Segments[1].UncertainWords!);

        // Unknown (an older transcript) and exactly 0.60 are not uncertain.
        Assert.Null(vm.Segments[2].UncertainWords);
    }

    [Fact]
    public async Task OnlyUncertainWords_OpenThePopover_TheOthersStillSeek()
    {
        var vm = await LoadAsync();
        var row = vm.Segments[1];

        Assert.True(row.OpenWordCommand.CanExecute(5));
        Assert.False(row.OpenWordCommand.CanExecute(4));
        Assert.False(row.OpenWordCommand.CanExecute(42));

        row.OpenWordCommand.Execute(4);
        Assert.Null(vm.DidYouMean);
        row.OpenWordCommand.Execute(5);
        Assert.NotNull(vm.DidYouMean);
        Assert.Equal(0, _fixture.Player.Plays);
    }

    // The suggestions ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePopover_OpensAtOnce_WithKeep_AndTheWordsThatOnlyStartAlikeWaitForTheModel()
    {
        var vm = await LoadAsync();

        vm.Segments[1].OpenWordCommand.Execute(5);

        var popover = vm.DidYouMean!;
        Assert.True(vm.IsDidYouMeanOpen);
        Assert.Equal("parquet", popover.Word);
        Assert.Equal("Did you mean", popover.Title);

        // The model has not answered. The vocabulary's words only share "par" (3, 4 and 4 edits; Gemma looks nothing like it): they rank after
        // the model's ideas, so they wait for it rather than give their places away later.
        Assert.False(vm.LastSuggestions.IsCompleted);
        Assert.Empty(popover.Alternatives);
        var keep = Assert.Single(popover.Choices);
        Assert.True(keep.IsKeep);

        // The model has nothing to add: then they come, the closest first, in front of the same Keep chip.
        _suggester.Answer();
        await vm.LastSuggestions;
        Assert.Equal(["parakeet", "Parakeets", "Paris", "Keep parquet"], popover.Choices.Select(c => c.Text));
        Assert.Same(keep, popover.Choices[^1]);
        Assert.True(popover.Choices[0].IsPrimary);
        Assert.False(popover.Choices[1].IsPrimary);
        Assert.True(popover.Choices[^1].IsKeep);
        Assert.Equal([(1, 5)], _suggester.Requests);
    }

    [Fact]
    public async Task TheModelsIdeas_ComeAfterCloseVocabularyWords_AndBeforeWordsThatOnlyStartAlike()
    {
        // "parquets" is one edit away (close); "Parakeet" only shares "par" (a weak hint).
        var vocabulary = new FakeVocabularyStore("Parakeet", "parquets");
        UseTranscript();
        var vm = new SessionViewModel(_fixture.Record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action(),
            services: new SessionServices(Vocabulary: vocabulary, WordSuggester: _suggester));
        await vm.LoadAsync();

        vm.Segments[1].OpenWordCommand.Execute(5);
        Assert.Equal(["parquets"], vm.DidYouMean!.Alternatives);
        var shown = vm.DidYouMean.Choices[0];

        _suggester.Answer("parakeet", "parquet", "Paraquat", "Parkit");
        await vm.LastSuggestions;

        // The chip on screen keeps its place (and its object, so its button); the answer only adds chips after it.
        Assert.Same(shown, vm.DidYouMean!.Choices[0]);

        // The model's "parakeet" is the vocabulary's "Parakeet" (any case) and takes its place; "parquet" is the word itself; three at most.
        Assert.Equal(["parquets", "parakeet", "Paraquat"], vm.DidYouMean!.Alternatives);
        Assert.Equal(4, vm.DidYouMean.Choices.Count);
        Assert.Equal("Keep parquet", vm.DidYouMean.Choices[^1].Text);
    }

    [Fact]
    public async Task WithoutTheModel_OrWhenItFails_OnlyTheVocabularyIsOffered()
    {
        var withoutModel = await LoadAsync(suggester: false);
        withoutModel.Segments[1].OpenWordCommand.Execute(5);
        await withoutModel.LastSuggestions;
        Assert.Equal(["parakeet", "Parakeets", "Paris"], withoutModel.DidYouMean!.Alternatives);

        var failing = await LoadAsync();
        failing.Segments[1].OpenWordCommand.Execute(7);
        _suggester.Fail(new InvalidOperationException("llama-server did not start"));
        await failing.LastSuggestions;
        Assert.NotNull(failing.DidYouMean);
        Assert.Equal(["Keep fully"], failing.DidYouMean!.Choices.Select(c => c.Text));
    }

    [Fact]
    public async Task WithNeitherSource_ThePopoverStillOffersKeep()
    {
        var vm = await LoadAsync(vocabulary: false, suggester: false);

        vm.Segments[1].OpenWordCommand.Execute(5);
        await vm.LastSuggestions;

        var keep = Assert.Single(vm.DidYouMean!.Choices);
        Assert.True(keep.IsKeep);
    }

    [Fact]
    public async Task Closing_StopsAskingTheModel()
    {
        var vm = await LoadAsync();
        vm.Segments[1].OpenWordCommand.Execute(5);
        var token = _suggester.LastToken;

        vm.DidYouMean!.CloseCommand.Execute(null);
        await vm.LastSuggestions;

        Assert.True(token.IsCancellationRequested);
        Assert.Null(vm.DidYouMean);
        Assert.False(vm.IsDidYouMeanOpen);

        // A late answer of the model changes nothing.
        _suggester.Answer("Parakeet");
        Assert.Null(vm.DidYouMean);
    }

    [Fact]
    public async Task OpeningAnotherWord_ReplacesThePopover_AndCancelsTheFirstRequest()
    {
        var vm = await LoadAsync();
        vm.Segments[1].OpenWordCommand.Execute(5);
        var first = _suggester.LastToken;

        vm.Segments[1].OpenWordCommand.Execute(7);

        Assert.True(first.IsCancellationRequested);
        Assert.Equal("fully", vm.DidYouMean!.Word);
    }

    // The fix --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PickingAnAlternative_FixesTheLine_TheStoreAndTheVocabulary()
    {
        var vm = await LoadAsync();
        var row = vm.Segments[1];
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.SearchText = "parakeet";
        Assert.Empty(vm.SearchHits);
        row.OpenWordCommand.Execute(5);
        _suggester.Answer("Paraquat");
        await vm.LastSuggestions;

        var choice = vm.DidYouMean!.Choices.First(c => c.Value == "parakeet");
        Assert.Equal("parakeet", choice.Value);
        choice.PickCommand.Execute(null);
        await WaitForAsync(() => _vocabulary.Adds.Count > 0);

        // The stored transcript (and with it the search index) gets the word with its punctuation kept.
        Assert.Equal([(_fixture.Record.Id, 1, 5, "parakeet?")], _fixture.Transcripts.Replacements);

        // The line at once: the new word, no underline there (the other uncertain word keeps its own).
        Assert.Equal("Does it run on the parakeet? model fully offline", row.Text);
        Assert.Equal([false, false, false, false, false, false, false, true, false], row.UncertainWords!);
        Assert.Contains(nameof(SegmentViewModel.Text), changed);
        Assert.Contains(nameof(SegmentViewModel.UncertainWords), changed);
        Assert.False(row.OpenWordCommand.CanExecute(5));

        // The vocabulary, the popover, the search hits of this view.
        Assert.Equal(["parakeet"], _vocabulary.Adds);
        Assert.Equal("parakeet", _vocabulary.Words[0]);
        Assert.Null(vm.DidYouMean);
        Assert.Equal([1], vm.SearchHits);
        Assert.Null(vm.ActionMessage);
    }

    [Fact]
    public async Task Keep_ConfirmsTheWordAsItIs_AndAddsItToTheVocabulary()
    {
        var vm = await LoadAsync();
        var row = vm.Segments[1];
        row.OpenWordCommand.Execute(5);

        vm.DidYouMean!.Choices[^1].PickCommand.Execute(null);
        await WaitForAsync(() => _vocabulary.Adds.Count > 0);

        Assert.Equal([(_fixture.Record.Id, 1, 5, "parquet?")], _fixture.Transcripts.Replacements);
        Assert.Equal("Does it run on the parquet? model fully offline", row.Text);
        Assert.False(row.UncertainWords![5]);
        Assert.Equal(["parquet"], _vocabulary.Adds);
        Assert.Null(_fixture.Transcripts.Transcript!.Segments[1].Words[5].Confidence);
    }

    [Fact]
    public async Task TheLastFixOfALine_TakesItsLastUnderline()
    {
        var vm = await LoadAsync();
        var row = vm.Segments[1];

        foreach (var word in new[] { 5, 7 })
        {
            row.OpenWordCommand.Execute(word);
            vm.DidYouMean!.Choices[^1].PickCommand.Execute(null);
            var count = word == 5 ? 1 : 2;
            await WaitForAsync(() => _vocabulary.Adds.Count == count);
        }

        Assert.Null(row.UncertainWords);
    }

    [Fact]
    public async Task AFixThatCannotBeStored_ChangesNothing_AndSaysSo()
    {
        var vm = await LoadAsync();
        _fixture.Transcripts.ReplaceFailure = new IOException("disk full");
        var row = vm.Segments[1];
        row.OpenWordCommand.Execute(5);

        vm.DidYouMean!.Choices[0].PickCommand.Execute(null);
        await WaitForAsync(() => vm.ActionMessage is not null);

        Assert.Contains("disk full", vm.ActionMessage, StringComparison.Ordinal);
        Assert.Equal("Does it run on the parquet? model fully offline", row.Text);
        Assert.True(row.UncertainWords![5]);
        Assert.Empty(_vocabulary.Adds);
        Assert.Null(vm.DidYouMean);
    }

    [Fact]
    public async Task AVocabularyThatCannotBeWritten_StillKeepsTheFix()
    {
        var vm = await LoadAsync();
        _vocabulary.AddFailure = new IOException("locked");
        var row = vm.Segments[1];
        row.OpenWordCommand.Execute(5);

        vm.DidYouMean!.Choices[0].PickCommand.Execute(null);
        await WaitForAsync(() => _fixture.Transcripts.Replacements.Count > 0);

        Assert.False(row.UncertainWords![5]);
        Assert.Null(vm.ActionMessage);
    }

    [Fact]
    public async Task TheExport_AndTheAi_ReadTheFixedTranscript()
    {
        var vm = await LoadAsync();
        vm.Segments[1].OpenWordCommand.Execute(5);
        _suggester.Answer();
        await vm.LastSuggestions;
        vm.DidYouMean!.Choices[0].PickCommand.Execute(null);
        await WaitForAsync(() => _vocabulary.Adds.Count > 0);

        // What the export (and the model's next suggestion) is built from.
        var segment = vm.CurrentTranscript!.Segments[1];
        Assert.Equal("parakeet?", segment.Words[5].Text);
        Assert.Null(segment.Words[5].Confidence);
        Assert.Equal("Does it run on the parakeet? model fully offline", segment.Text);
        Assert.Same(vm.Segments[0].Segment, vm.CurrentTranscript.Segments[0]);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.True(condition(), "The condition was not met in time.");
    }

    // The merge rules ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheVocabulary_MatchesByEditDistanceOrByTheFirstThreeLetters_AnyCase()
    {
        string[] vocabulary = ["Kubernetes", "Parakeet", "parquetry", "PARQUET", "Gemma", "Parfait", "Київ", "par", "Pa"];

        // "PARQUET" is the word itself; "Gemma", "Kubernetes" and "Pa" look nothing like it; "parquetry" is 2 edits away (close); "Parakeet",
        // "Parfait" (3 edits) and "par" (4) only share its first three letters.
        var (close, prefix) = WordSuggestions.FromVocabulary("parquet", vocabulary);
        Assert.Equal(["parquetry"], close);
        Assert.Equal(["Parakeet", "Parfait", "par"], prefix);
        Assert.Equal(["Київ"], WordSuggestions.FromVocabulary("київа", vocabulary).Close);
        Assert.Equal((0, 0), (WordSuggestions.FromVocabulary("zzz", vocabulary).Close.Count, WordSuggestions.FromVocabulary("zzz", vocabulary).Prefix.Count));
    }

    [Fact]
    public void TheEditDistance_NeedsFourLetters_ShortWordsAreNotCloseToEverything()
    {
        string[] vocabulary = ["I", "on", "API", "ape", "apex"];

        // "an" is one or two edits from "I", "on", "API" and "ape", but such short words match nearly anything.
        Assert.Empty(WordSuggestions.FromVocabulary("an", vocabulary).Close);
        Assert.Empty(WordSuggestions.FromVocabulary("apt", vocabulary).Close);
        Assert.Equal(["apex"], WordSuggestions.FromVocabulary("apes", vocabulary).Close);
    }

    [Fact]
    public void TheVocabulary_KeepsItsOrder_AmongEquallyCloseWords_AndGivesAtMostThree()
    {
        string[] newestFirst = ["mode", "modal", "models", "modem", "mod"];

        // mode, modal, models and modem are all one edit away: the newest three of them, in the vocabulary's order.
        Assert.Equal(["mode", "modal", "models"], WordSuggestions.FromVocabulary("model", newestFirst).Close);

        // "mod" is too short for the edit distance; it shares "mod".
        Assert.Equal(["mod"], WordSuggestions.FromVocabulary("model", newestFirst).Prefix);
    }

    [Fact]
    public void Merge_PutsTheFirstListFirst_DropsTheWordAndRepeats_AndStopsAtThree()
    {
        Assert.Equal(["a", "B", "c"], WordSuggestions.Merge("w", ["a", "B", "W"], ["b", "A", "c", "d"]));
        Assert.Equal(["x"], WordSuggestions.Merge("w", [], [" x ", "", "  "]));
        Assert.Equal(["a", "b", "c"], WordSuggestions.Merge("w", ["a"], ["b"], ["c", "d"]));
    }

    [Theory]
    [InlineData("parquet?", "", "parquet", "?")]
    [InlineData("(Київ),", "(", "Київ", "),")]
    [InlineData("model", "", "model", "")]
    [InlineData("—", "", "—", "")]
    public void AWord_IsSplitIntoItsPunctuationAndItself(string text, string prefix, string core, string suffix) =>
        Assert.Equal((prefix, core, suffix), WordSuggestions.Split(text));

    [Fact]
    public void Distance_IsLevenshtein_CutOffAboveTheLimit()
    {
        Assert.Equal(0, WordSuggestions.Distance("abc", "abc", 2));
        Assert.Equal(1, WordSuggestions.Distance("abc", "abd", 2));
        Assert.Equal(3, WordSuggestions.Distance("parquet", "parakeet", 5));
        Assert.Equal(3, WordSuggestions.Distance("parquet", "parakeet", 2));
        Assert.Equal(3, WordSuggestions.Distance("abc", "xyz", 2));
        Assert.Equal(3, WordSuggestions.Distance("a", "abcdef", 2));
    }

    // The control ----------------------------------------------------------------------------------------------------

    private const string Line = "Does it run on the parquet? model fully offline";

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

    private static TranscriptTextControl Text(bool[]? uncertain, int active = -1)
    {
        var (starts, lengths) = Words(Line);
        return new TranscriptTextControl
        {
            Text = Line,
            WordStarts = starts,
            WordLengths = lengths,
            ActiveWord = active,
            UncertainWords = uncertain,
            FontFamily = new FontFamily("Inter"),
            FontSize = 13,
            LineHeight = 20,
            LetterSpacing = 0.13,
            Foreground = Brushes.Gray,
            UncertainForeground = Brushes.Black,
            UnderlineBrush = Brushes.Black,
            UnderlineThickness = 2,
            UnderlineOffset = 4,
            HighlightBrush = Brushes.Black,
            HighlightForeground = Brushes.White,
            UpcomingForeground = Brushes.Gray,
            HighlightPadding = 3,
            HighlightCornerRadius = new CornerRadius(4),
        };
    }

    private static readonly bool[] Flags = [false, false, false, false, false, true, false, true, false];

    [Fact]
    public async Task TheUnderline_IsDrawnUnderTheUncertainWordsOnly_WithoutItsPunctuation()
    {
        await Session.Dispatch(
            () =>
            {
                var control = Text(Flags);
                control.Measure(new Size(2000, double.PositiveInfinity));
                control.Arrange(new Rect(control.DesiredSize));
                var (starts, lengths) = Words(Line);
                var layout = control.CurrentLayout!;

                var underlines = control.Underlines.ToArray();
                Assert.Equal([5, 7], underlines.Select(u => u.Word));

                // Under "parquet", not under its "?".
                var word = layout.HitTestTextRange(starts[5], "parquet".Length).Single();
                Assert.Equal(word.X, underlines[0].Left, 0.01);
                Assert.Equal(word.Right, underlines[0].Right, 0.01);

                // 4 px under the baseline (plus half the 2 px line).
                var baseline = layout.TextLines[0].Baseline;
                Assert.Equal(baseline + 4 + 1, underlines[0].Y, 0.01);

                Assert.Equal(0, Text(null).UnderlineCount);
                _ = lengths;
            },
            Ct);
    }

    [Fact]
    public async Task TheUnderline_AndTheColour_ChangeNoMetric()
    {
        await Session.Dispatch(
            () =>
            {
                foreach (var width in new[] { 2000.0, 160.0 })
                {
                    var plain = Text(null);
                    var marked = Text(Flags);
                    plain.Measure(new Size(width, double.PositiveInfinity));
                    plain.Arrange(new Rect(plain.DesiredSize));
                    marked.Measure(new Size(width, double.PositiveInfinity));
                    marked.Arrange(new Rect(marked.DesiredSize));

                    Assert.Equal(plain.DesiredSize, marked.DesiredSize);
                    Assert.Equal(plain.CurrentLayout!.TextLines.Count, marked.CurrentLayout!.TextLines.Count);
                    var (starts, lengths) = Words(Line);
                    for (var i = 0; i < starts.Length; i++)
                    {
                        var expected = plain.CurrentLayout!.HitTestTextRange(starts[i], lengths[i]).First();
                        var actual = marked.CurrentLayout!.HitTestTextRange(starts[i], lengths[i]).First();
                        Assert.Equal(expected.X, actual.X, 0.01);
                        Assert.Equal(expected.Y, actual.Y, 0.01);
                    }
                }
            },
            Ct);
    }

    [Fact]
    public async Task TheDottedUnderline_IsOnScreen_UnderTheWord_AndNotUnderTheOthers()
    {
        await Session.Dispatch(
            () =>
            {
                var control = Text(Flags);
                control.Foreground = Brushes.White;
                control.UncertainForeground = Brushes.White;
                var window = new Window { Width = 900, Height = 60, Background = Brushes.White, Content = control };
                window.Show();
                UiHarness.Pump();

                var under = control.Underlines.ToArray();
                var y = (int)Math.Round(under[0].Y);
                var dark = DarkPixels(window, (int)under[0].Left, (int)under[0].Right, y);
                var (starts, lengths) = Words(Line);
                var model = control.CurrentLayout!.HitTestTextRange(starts[6], lengths[6]).Single();
                var none = DarkPixels(window, (int)model.X, (int)model.Right, y);
                window.Close();

                // Dots: some pixels along the line are dark, the gaps between them are not.
                Assert.InRange(dark, 4, (int)(under[0].Right - under[0].Left) - 4);
                Assert.Equal(0, none);
            },
            Ct);
    }

    [Fact]
    public async Task TheFollowAlongChip_WinsOnTheActiveWord()
    {
        await Session.Dispatch(
            () =>
            {
                var control = Text(Flags, active: 5);
                control.HighlightBrush = Brushes.White; // invisible chip: only the underline could darken the pixels
                control.Foreground = Brushes.White;
                control.UncertainForeground = Brushes.White;
                control.HighlightForeground = Brushes.White;
                control.UpcomingForeground = Brushes.White;
                var window = new Window { Width = 900, Height = 60, Background = Brushes.White, Content = control };
                window.Show();
                UiHarness.Pump();

                var under = control.Underlines.ToArray();
                var active = DarkPixels(window, (int)under[0].Left, (int)under[0].Right, (int)Math.Round(under[0].Y));
                var other = DarkPixels(window, (int)under[1].Left, (int)under[1].Right, (int)Math.Round(under[1].Y));
                window.Close();

                Assert.Equal(0, active);
                Assert.True(other > 0);
            },
            Ct);
    }

    [Fact]
    public async Task TheUnderlines_AreWorkedOutOncePerLayout_NotPerFrame()
    {
        await Session.Dispatch(
            () =>
            {
                // No colour of their own: more uncertain words would split the text into more runs, which costs the text drawing, not the underline.
                var control = Text(Flags);
                control.UncertainForeground = null;
                var window = new Window { Width = 900, Height = 60, Content = control };
                window.Show();
                UiHarness.Pump();
                var store = control.UnderlineStore;
                Assert.NotNull(store);
                var count = control.UnderlineCount;

                for (var frame = 0; frame < 20; frame++)
                {
                    control.InvalidateVisual();
                    UiHarness.Pump(1);
                }

                Assert.Same(store, control.UnderlineStore);
                Assert.Equal(count, control.UnderlineCount);

                // A frame draws the cached dots with one call: what it allocates does not grow with the number of uncertain words.
                var few = RenderAllocations(control);
                control.UncertainWords = [.. Enumerable.Repeat(true, Flags.Length)];
                _ = RenderAllocations(control);
                var many = RenderAllocations(control);
                control.UnderlineBrush = null;
                var none = RenderAllocations(control);
                window.Close();
                Assert.True(Math.Abs(many - few) <= 32, $"{few} bytes per frame with 2 underlines, {many} with 9.");
                Assert.True(few - none <= 32, $"{few} bytes per frame with the underlines, {none} without.");
            },
            Ct);
    }

    /// <summary>Bytes this thread allocates for one Render of <paramref name="control"/> into a bitmap (after a warm-up).</summary>
    private static long RenderAllocations(TranscriptTextControl control)
    {
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(900, 60));
        long best = long.MaxValue;
        for (var round = 0; round < 6; round++)
        {
            using var context = bitmap.CreateDrawingContext();
            var before = GC.GetAllocatedBytesForCurrentThread();
            control.Render(context);
            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return best;
    }

    [Fact]
    public async Task AClickOnAnUncertainWord_OpensIt_AndAClickElsewhereSeeks()
    {
        await Session.Dispatch(
            () =>
            {
                var opened = new List<int>();
                var sought = new List<int>();
                var invoked = new List<(int Word, Rect Bounds)>();
                var control = Text(Flags);
                control.UncertainCommand = new CommunityToolkit.Mvvm.Input.RelayCommand<int>(opened.Add);
                control.Command = new CommunityToolkit.Mvvm.Input.RelayCommand<int>(sought.Add);
                control.UncertainWordInvoked += (_, e) => invoked.Add((e.Word, e.WordBounds));
                var window = new Window { Width = 900, Height = 60, Content = control };
                window.Show();
                UiHarness.Pump();
                var (starts, lengths) = Words(Line);
                var layout = control.CurrentLayout!;
                var parquet = layout.HitTestTextRange(starts[5], lengths[5]).Single();
                var model = layout.HitTestTextRange(starts[6], lengths[6]).Single();

                window.MouseDown(control.TranslatePoint(parquet.Center, window)!.Value, MouseButton.Left);
                window.MouseUp(control.TranslatePoint(parquet.Center, window)!.Value, MouseButton.Left);
                window.MouseDown(control.TranslatePoint(model.Center, window)!.Value, MouseButton.Left);
                window.MouseUp(control.TranslatePoint(model.Center, window)!.Value, MouseButton.Left);
                window.Close();

                Assert.Equal([5], opened);
                Assert.Equal([6], sought);
                var (word, bounds) = Assert.Single(invoked);
                Assert.Equal(5, word);
                Assert.Equal(parquet.X, bounds.X, 0.01);
            },
            Ct);
    }

    [Fact]
    public async Task TheLine_IsATabStop_OnlyWithUncertainWords_AndArrowsMoveBetweenThem()
    {
        await Session.Dispatch(
            () =>
            {
                Assert.False(Text(null).Focusable);

                var opened = new List<int>();
                var control = Text(Flags);
                control.UncertainCommand = new CommunityToolkit.Mvvm.Input.RelayCommand<int>(opened.Add);
                Assert.True(control.Focusable);
                var window = new Window { Width = 900, Height = 60, Content = control };
                window.Show();
                UiHarness.Pump();

                control.Focus(NavigationMethod.Tab);
                Assert.Equal(5, control.FocusedWord);
                window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
                Assert.Equal(7, control.FocusedWord);
                window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
                Assert.Equal(7, control.FocusedWord);
                window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
                Assert.Equal(5, control.FocusedWord);
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.Close();

                Assert.Equal([5], opened);
            },
            Ct);
    }

    private static int DarkPixels(Window window, int left, int right, int y)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var buffer = frame.Lock();
        var dark = 0;
        var pixel = new byte[4];
        for (var x = left; x < right; x++)
        {
            var darkest = 255;
            for (var dy = -1; dy <= 1; dy++)
            {
                System.Runtime.InteropServices.Marshal.Copy(buffer.Address + ((y + dy) * buffer.RowBytes) + (x * 4), pixel, 0, 4);
                darkest = Math.Min(darkest, pixel[1]);
            }

            if (darkest < 128)
            {
                dark++;
            }
        }

        return dark;
    }

    // The view -------------------------------------------------------------------------------------------------------

    private async Task<ShellViewModel> OpenShellAsync(FakeVocabularyStore? vocabulary = null)
    {
        UseTranscript();
        var harness = new UiHarness();
        harness.Store.Add(_fixture.Record);
        var shell = harness.CreateShell(
            _ => vocabulary is null
                ? Create()
                : new SessionViewModel(_fixture.Record, _fixture.Files, _fixture.Transcripts, _fixture.Player, _fixture.Processing, _fixture.Time, action => action(),
                    services: new SessionServices(Vocabulary: vocabulary, WordSuggester: _suggester)),
            _fixture.Processing);
        await shell.Library.LoadAsync();
        shell.Library.SelectedSession = shell.Library.Sessions[0];
        await shell.SessionLoadTask;
        return shell;
    }

    [Fact]
    public async Task SessionShortcuts_SpaceOnAFocusedLine_OpensThePopover_AndDoesNotTogglePlayback()
    {
        _suggester.Immediate = ["Paraquat"];
        var shell = await OpenShellAsync();

        var (opened, playing) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                UiHarness.Pump(4);
                var line = window.GetVisualDescendants().OfType<TranscriptTextControl>().First(c => c.UncertainWords is not null);
                line.Focus(NavigationMethod.Tab);
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                UiHarness.Pump(6);
                var result = (shell.Session!.IsDidYouMeanOpen, shell.Session.IsPlaying);
                window.Close();
                return result;
            },
            Ct);

        Assert.True(opened);
        Assert.False(playing);
        Assert.Equal(0, _fixture.Player.Plays);
    }

    [Fact]
    public async Task SessionShortcuts_SpaceHeldOnAFocusedLine_RepeatsDoNotPressTheFirstChip()
    {
        _suggester.Immediate = ["Paraquat"];
        var shell = await OpenShellAsync();

        var (pressed, clicked, playing, opened) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                UiHarness.Pump(4);
                var line = window.GetVisualDescendants().OfType<TranscriptTextControl>().First(c => c.UncertainWords is not null);
                var host = window.GetVisualDescendants().OfType<DidYouMeanPopover>().Single();
                line.Focus(NavigationMethod.Tab);
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                UiHarness.Pump(6);
                var chips = host.ChipButtons();
                var clicks = 0;
                chips[0].Click += (_, _) => clicks++;
                // Auto-repeat: more presses before the release, now coming from inside the popup.
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                UiHarness.Pump(2);
                var isPressed = chips[0].IsPressed;
                window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                UiHarness.Pump(4);
                var result = (isPressed || chips[0].IsPressed, clicks, shell.Session!.IsPlaying, shell.Session.IsDidYouMeanOpen);
                window.Close();
                return result;
            },
            Ct);

        Assert.False(pressed);
        Assert.Equal(0, clicked);
        Assert.False(playing);
        Assert.True(opened);
    }

    [Fact]
    public async Task SessionShortcuts_SpaceHeld_IsForgottenWhenTheWindowIsDeactivated()
    {
        var shell = await OpenShellAsync();

        var (plays, playing) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                UiHarness.Pump(4);
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                UiHarness.Pump(2);
                // The release never arrives (alt-tab); a later press is a new press, not a repeat.
                var deactivated = typeof(WindowBase).GetMethod("HandleDeactivated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                deactivated.Invoke(window, null);
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                UiHarness.Pump(4);
                var result = (_fixture.Player.Plays, shell.Session!.IsPlaying);
                window.Close();
                return result;
            },
            Ct);

        Assert.Equal(1, plays);
        Assert.False(playing);
    }

    [Fact]
    public async Task SessionShortcuts_EscWithTheDidYouMeanPopoverOpen_ClosesItFirst_ThenGoesBack()
    {
        _suggester.Immediate = ["Paraquat"];
        var shell = await OpenShellAsync();

        var (closedFirst, stillOpen, wentBack) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                UiHarness.Pump(4);
                var line = window.GetVisualDescendants().OfType<TranscriptTextControl>().First(c => c.UncertainWords is not null);
                line.Focus(NavigationMethod.Tab);
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                UiHarness.Pump(6);
                var opened = shell.Session!.IsDidYouMeanOpen;
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                UiHarness.Pump(4);
                var first = opened && !shell.Session.IsDidYouMeanOpen;
                var open = shell.Session is not null;
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                UiHarness.Pump(4);
                var result = (first, open, shell.Session is null);
                window.Close();
                return result;
            },
            Ct);

        Assert.True(closedFirst);
        Assert.True(stillOpen);
        Assert.True(wentBack);
    }

    [Fact]
    public async Task SessionShortcuts_UpAndDownInThePopover_MoveBetweenChips_AndDoNotSeek()
    {
        _suggester.Immediate = ["Paraquat"];
        var shell = await OpenShellAsync();

        var (moved, seeks) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                UiHarness.Pump(4);
                var line = window.GetVisualDescendants().OfType<TranscriptTextControl>().First(c => c.UncertainWords is not null);
                var host = window.GetVisualDescendants().OfType<DidYouMeanPopover>().Single();
                line.Focus(NavigationMethod.Tab);
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                UiHarness.Pump(6);
                var chips = host.ChipButtons();
                window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                UiHarness.Pump(2);
                var down = chips[1].IsFocused;
                window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
                UiHarness.Pump(2);
                var result = (down && chips[0].IsFocused, _fixture.Player.Seeks.Count);
                window.Close();
                return result;
            },
            Ct);

        Assert.True(moved);
        Assert.Equal(0, seeks);
    }

    [Fact]
    public async Task SessionShortcuts_EscWithThePopoverOpen_ClosesItAndDoesNotGoBack()
    {
        _suggester.Immediate = ["Paraquat"];
        var shell = await OpenShellAsync();

        var (closed, stayed) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                UiHarness.Pump(4);
                var line = window.GetVisualDescendants().OfType<TranscriptTextControl>().First(c => c.UncertainWords is not null);
                line.Focus(NavigationMethod.Tab);
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                UiHarness.Pump(6);
                Assert.True(shell.Session!.IsDidYouMeanOpen);
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                UiHarness.Pump(6);
                var result = (!shell.Session.IsDidYouMeanOpen, shell.Session is not null);
                window.Close();
                return result;
            },
            Ct);

        Assert.True(closed);
        Assert.True(stayed);
    }

    [Fact]
    public async Task InTheApp_TheKeyboardAlone_OpensPicksAndCloses()
    {
        _suggester.Immediate = ["Paraquat"];
        var shell = await OpenShellAsync();

        var (chipsFocused, afterRight, closedByEscape, focusBack, picked) = await Session.Dispatch(
            async () =>
            {
                var window = UiHarness.Show(shell);
                UiHarness.Pump(4);
                var line = window.GetVisualDescendants().OfType<TranscriptTextControl>().First(c => c.UncertainWords is not null);
                var host = window.GetVisualDescendants().OfType<DidYouMeanPopover>().Single();

                // Tab to the line, Enter opens the first uncertain word; the focus is on the first chip.
                line.Focus(NavigationMethod.Tab);
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                UiHarness.Pump(6);
                var chips = host.ChipButtons();
                var focused = chips.Count == 4 && chips[0].IsFocused;

                // Right moves to the next chip.
                window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
                UiHarness.Pump(2);
                var right = chips[1].IsFocused;

                // Esc closes without a fix; the focus goes back to the line.
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                UiHarness.Pump(6);
                var closed = !host.PopupControl.IsOpen && shell.Session!.DidYouMean is null && _fixture.Transcripts.Replacements.Count == 0;
                var back = line.IsFocused;

                // Enter again, End to "Keep", Enter picks it.
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                UiHarness.Pump(6);
                window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None);
                UiHarness.Pump(2);
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                for (var i = 0; i < 50 && _vocabulary.Adds.Count == 0; i++)
                {
                    await Task.Delay(10, Ct);
                    UiHarness.Pump(1);
                }

                var result = (focused, right, closed, back, (_fixture.Transcripts.Replacements.ToArray(), _vocabulary.Adds.ToArray(), line.UncertainWords?[5]));
                window.Close();
                return result;
            },
            Ct);

        Assert.True(chipsFocused, "The first chip has the focus (with its ring) when the popover opens from the keyboard.");
        Assert.Equal(0, _fixture.Player.Plays); // Enter on the line opened the word, it did not play the row
        Assert.True(afterRight);
        Assert.True(closedByEscape);
        Assert.True(focusBack);
        Assert.Equal([(_fixture.Record.Id, 1, 5, "parquet?")], picked.Item1);
        Assert.Equal(["parquet"], picked.Item2);
        Assert.False(picked.Item3);
    }

    [Fact]
    public async Task InTheApp_ThePopover_SitsUnderTheWord()
    {
        var shell = await OpenShellAsync();

        var (dx, gap, height) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                UiHarness.Pump(4);
                var line = window.GetVisualDescendants().OfType<TranscriptTextControl>().First(c => c.UncertainWords is not null);
                var word = line.WordBounds(5)!.Value;
                var at = line.TranslatePoint(word.Center, window)!.Value;
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                UiHarness.Pump(6);

                var popup = window.GetVisualDescendants().OfType<Popup>().First(p => p.IsOpen);
                var card = (Border)popup.Child!;
                var cardAt = card.TranslatePoint(default, window)!.Value;
                var wordAt = line.TranslatePoint(word.TopLeft, window)!.Value;
                var chip = card.GetVisualDescendants().OfType<Button>().First();
                var result = (cardAt.X - wordAt.X, cardAt.Y - (wordAt.Y + word.Height), chip.Bounds.Height);
                window.Close();
                return result;
            },
            Ct);

        // Left-aligned with the word, unless the popup has to slide left to stay on screen (it does near the right edge of the window).
        Assert.InRange(dx, -60, 0.5);
        Assert.Equal(6, gap, 1);
        Assert.Equal(26, height, 0.5);
    }

    // Review round 1 ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhileThePopoverIsOpen_FollowAlongDoesNotMoveTheList_AndAUserScrollClosesIt()
    {
        var vm = await LoadAsync();
        Assert.True(vm.FollowAlong);
        var scrolls = new List<int>();
        vm.ScrollToSegmentRequested += scrolls.Add;
        vm.PlayPauseCommand.Execute(null);
        _fixture.Player.Position = TimeSpan.FromSeconds(20.1);
        vm.Tick();
        scrolls.Clear();

        vm.Segments[1].OpenWordCommand.Execute(5);
        _fixture.Player.Position = TimeSpan.FromSeconds(30.5);
        vm.Tick();

        Assert.Empty(scrolls);
        Assert.True(vm.Segments[2].IsCurrent);
        Assert.NotNull(vm.DidYouMean);

        vm.NotifyUserScrolled();
        Assert.Null(vm.DidYouMean);
    }

    [Fact]
    public async Task InTheApp_ThePopover_StaysOpen_WhilePlaybackMovesToTheNextLine()
    {
        var shell = await OpenShellAsync();

        var (open, offsetBefore, offsetAfter) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                UiHarness.Pump(4);
                var session = shell.Session!;
                session.PlayPauseCommand.Execute(null);
                _fixture.Player.Position = TimeSpan.FromSeconds(20.1);
                session.Tick();
                UiHarness.Pump(4);
                var line = window.GetVisualDescendants().OfType<TranscriptTextControl>().First(c => c.UncertainWords is not null);
                var at = line.TranslatePoint(line.WordBounds(5)!.Value.Center, window)!.Value;
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                UiHarness.Pump(6);
                var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "TranscriptScroller");
                var before = scroller.Offset;

                _fixture.Player.Position = TimeSpan.FromSeconds(30.5);
                session.Tick();
                UiHarness.Pump(6);
                var popup = window.GetVisualDescendants().OfType<DidYouMeanPopover>().Single().PopupControl;
                var result = (popup.IsOpen && session.DidYouMean is not null, before, scroller.Offset);
                window.Close();
                return result;
            },
            Ct);

        Assert.True(open);
        Assert.Equal(offsetBefore, offsetAfter);
    }

    [Fact]
    public async Task AClick_FocusesTheFirstChipWithoutARing_TheKeyboardWithOne()
    {
        var shell = await OpenShellAsync();

        var (clickFocused, clickRing, keyFocused, keyRing) = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                UiHarness.Pump(4);
                var line = window.GetVisualDescendants().OfType<TranscriptTextControl>().First(c => c.UncertainWords is not null);
                var host = window.GetVisualDescendants().OfType<DidYouMeanPopover>().Single();
                var at = line.TranslatePoint(line.WordBounds(5)!.Value.Center, window)!.Value;
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                UiHarness.Pump(6);
                var clicked = host.ChipButtons()[0];
                var byPointer = (clicked.IsFocused, clicked.Classes.Contains(":focus-visible"));

                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                UiHarness.Pump(6);
                line.Focus(NavigationMethod.Tab);
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                UiHarness.Pump(6);
                var typed = host.ChipButtons()[0];
                var result = (byPointer.IsFocused, byPointer.Item2, typed.IsFocused, typed.Classes.Contains(":focus-visible"));
                window.Close();
                return result;
            },
            Ct);

        Assert.True(clickFocused);
        Assert.False(clickRing);
        Assert.True(keyFocused);
        Assert.True(keyRing);
    }

    [Fact]
    public async Task NearTheRightEdge_TheCardKeepsSixteenPixelsFromTheWindow()
    {
        var shell = await OpenShellAsync();

        var (left, right, width) = await Session.Dispatch(
            () =>
            {
                var window = new MainWindow { DataContext = shell, Width = 900, Height = 600 };
                window.Show();
                UiHarness.Pump(4);
                var line = window.GetVisualDescendants().OfType<TranscriptTextControl>().First(c => c.UncertainWords is not null);
                var at = line.TranslatePoint(line.WordBounds(7)!.Value.Center, window)!.Value;
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                UiHarness.Pump(8);
                var card = (Border)window.GetVisualDescendants().OfType<Popup>().First(p => p.IsOpen).Child!;
                var cardAt = card.TranslatePoint(default, window)!.Value;
                var result = (cardAt.X, cardAt.X + card.Bounds.Width, window.Bounds.Width);
                window.Close();
                return result;
            },
            Ct);

        Assert.True(left >= 16 - 0.5, $"The card starts at {left}.");
        Assert.True(right <= width - 16 + 0.5, $"The card ends at {right} in a window {width} wide.");
    }

    [Fact]
    public async Task ASearchMarkInsideAnUncertainWord_DoesNotShortenItsUnderline()
    {
        await Session.Dispatch(
            () =>
            {
                var control = Text(Flags);
                control.MarkBrush = Brushes.Black;
                control.MarkForeground = Brushes.White;
                control.HighlightTerm = "rqu";
                control.Measure(new Size(2000, double.PositiveInfinity));
                control.Arrange(new Rect(control.DesiredSize));
                Assert.Equal(1, control.MarkCount);

                var (starts, _) = Words(Line);
                var first = control.LaidOutIndex(starts[5]);
                var end = control.LaidOutIndex(starts[5] + "parquet".Length - 1) + 1;
                var word = control.CurrentLayout!.HitTestTextRange(first, end - first).ToArray();
                var underline = control.Underlines.First(u => u.Word == 5);

                Assert.Equal(word[0].X, underline.Left, 0.01);
                Assert.Equal(word[^1].Right, underline.Right, 0.01);
            },
            Ct);
    }

    [Fact]
    public async Task TheFocusRing_IsWorkedOutOnce_NotPerFrame()
    {
        await Session.Dispatch(
            () =>
            {
                var control = Text(Flags);
                control.UncertainForeground = null;
                control.FocusBrush = Brushes.Black;
                control.UncertainCommand = new CommunityToolkit.Mvvm.Input.RelayCommand<int>(_ => { });
                var window = new Window { Width = 900, Height = 60, Content = control };
                window.Show();
                UiHarness.Pump();
                var unfocused = RenderAllocations(control);

                control.Focus(NavigationMethod.Tab);
                UiHarness.Pump();
                var ring = control.FocusRingRect;
                Assert.NotNull(ring);
                var focused = RenderAllocations(control);
                Assert.Equal(ring, control.FocusRingRect);
                window.Close();

                Assert.True(focused - unfocused <= 32, $"{focused} bytes per frame focused, {unfocused} without the focus.");
            },
            Ct);
    }

    [Theory]
    [InlineData("a b c", "a,b,c", 1, "X")]
    [InlineData("a  b,  c", "a,b,c", 1, "X")]
    [InlineData("b b b", "b,b,b", 2, "X")]
    [InlineData("1 2 3", "one,two,three", 1, "2")]
    [InlineData("Okay, a quick run.", "Okay,|a|quick|run.", 0, "Fine,")]
    [InlineData("Does it run on the parquet? model", "Does,it,run,on,the,parquet?,model", 5, "Parakeet?")]
    public async Task TheLineAndTheStore_RebuildTheTextAlike(string text, string words, int index, string replacement)
    {
        var list = words.Split(words.Contains('|') ? '|' : ',').Select((w, i) => W(w, i)).ToArray();
        var root = Path.Combine(Path.GetTempPath(), "murmur-app-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var storage = new Murmur.Storage.SqliteStorage(root);
            var id = (await storage.CreateSessionAsync("t", UiHarness.Now, Ct)).Id;
            await storage.SaveTranscriptAsync(new Transcript(id, [new TranscriptSegment(Speaker.Me, TimeSpan.Zero, TimeSpan.FromSeconds(9), text, list)], "e"), Ct);

            await storage.ReplaceWordAsync(id, 0, index, replacement, Ct);

            var stored = (await storage.GetTranscriptAsync(id, Ct))!.Segments[0].Text;
            Assert.Equal(stored, WordSuggestions.ReplaceInText(text, list, index, replacement));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // Review round 2 ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ALateAnswer_KeepsTheKeyboardOnItsChip_AndTheChipsInTheirPlaces()
    {
        // "parquets" is one edit away (shown at once); "Parakeet" only starts alike (waits for the model).
        var shell = await OpenShellAsync(new FakeVocabularyStore("Parakeet", "parquets"));

        var (before, onKeep, after, stillOnKeep, left) = await Session.Dispatch(
            async () =>
            {
                var window = UiHarness.Show(shell);
                UiHarness.Pump(4);
                var line = window.GetVisualDescendants().OfType<TranscriptTextControl>().First(c => c.UncertainWords is not null);
                var host = window.GetVisualDescendants().OfType<DidYouMeanPopover>().Single();
                line.Focus(NavigationMethod.Tab);
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                UiHarness.Pump(6);
                var first = host.ChipButtons();
                var texts = first.Select(ChipText).ToArray();
                window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
                UiHarness.Pump(2);
                var keep = first[^1];
                var focusedKeep = keep.IsFocused;

                _suggester.Answer("Paraquat", "Parkit");
                for (var tick = 0; tick < 200 && host.ChipButtons().Count < 4; tick++)
                {
                    await Task.Delay(5, Ct);
                    UiHarness.Pump(1);
                }

                UiHarness.Pump(4);
                var chips = host.ChipButtons();
                var now = chips.Select(ChipText).ToArray();
                var still = keep.IsFocused && ReferenceEquals(first[0], chips[0]) && ReferenceEquals(keep, chips[^1]);

                // The arrows still move between the chips.
                window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
                UiHarness.Pump(2);
                var result = (texts, focusedKeep, now, still, ChipText(chips.Single(c => c.IsFocused)));
                window.Close();
                return result;
            },
            Ct);

        Assert.Equal(["parquets", "Keep parquet"], before);
        Assert.True(onKeep);
        Assert.Equal(["parquets", "Paraquat", "Parkit", "Keep parquet"], after);
        Assert.True(stillOnKeep, "The focused Keep chip and the chip before it are the same buttons after the answer.");
        Assert.Equal("Parkit", left);
    }

    [Fact]
    public async Task ALateAnswer_LeavesTheChipUnderThePointerAsItWas()
    {
        var shell = await OpenShellAsync(new FakeVocabularyStore("parquets"));

        var (textBefore, textAfter, sameButton, movedBy) = await Session.Dispatch(
            async () =>
            {
                var window = new MainWindow { DataContext = shell, Width = 900, Height = 600 };
                window.Show();
                UiHarness.Pump(4);
                var line = window.GetVisualDescendants().OfType<TranscriptTextControl>().First(c => c.UncertainWords is not null);
                var host = window.GetVisualDescendants().OfType<DidYouMeanPopover>().Single();
                var at = line.TranslatePoint(line.WordBounds(5)!.Value.Center, window)!.Value;
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                UiHarness.Pump(6);

                // The pointer rests on the first chip, about to click it, when the model answers.
                var chip = host.ChipButtons()[0];
                var root = TopLevel.GetTopLevel(chip)!;
                var center = chip.TranslatePoint(new Point(chip.Bounds.Width / 2, chip.Bounds.Height / 2), root)!.Value;
                root.MouseMove(center);
                UiHarness.Pump(2);
                var before = (ChipText(chip), chip.TranslatePoint(default, root)!.Value);

                _suggester.Answer("Paraquat", "Parkit");
                for (var tick = 0; tick < 200 && host.ChipButtons().Count < 4; tick++)
                {
                    await Task.Delay(5, Ct);
                    UiHarness.Pump(1);
                }

                UiHarness.Pump(4);
                var after = host.ChipButtons()[0];
                var result = (before.Item1, ChipText(after), ReferenceEquals(chip, after), after.TranslatePoint(default, root)!.Value - before.Item2);
                window.Close();
                return result;
            },
            Ct);

        Assert.Equal("parquets", textBefore);
        Assert.Equal("parquets", textAfter);
        Assert.True(sameButton);
        Assert.Equal(default, movedBy);
    }

    [Fact]
    public async Task IfTheChipsChangeUnderTheFocus_TheFocusStaysInThePopover()
    {
        var shell = await OpenShellAsync(new FakeVocabularyStore("parquets"));

        var focusedInCard = await Session.Dispatch(
            () =>
            {
                var window = UiHarness.Show(shell);
                UiHarness.Pump(4);
                var line = window.GetVisualDescendants().OfType<TranscriptTextControl>().First(c => c.UncertainWords is not null);
                var host = window.GetVisualDescendants().OfType<DidYouMeanPopover>().Single();
                line.Focus(NavigationMethod.Tab);
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                UiHarness.Pump(6);
                Assert.True(host.ChipButtons()[0].IsFocused);

                // Nothing in the app does this any more; the popover still catches it.
                shell.Session!.DidYouMean!.SetAlternatives(["something else"]);
                UiHarness.Pump(6);
                var result = host.ChipButtons().Exists(c => c.IsFocused);
                window.Close();
                return result;
            },
            Ct);

        Assert.True(focusedInCard);
    }

    private static string ChipText(Button chip) => chip.GetVisualDescendants().OfType<TextBlock>().First().Text ?? string.Empty;
}
