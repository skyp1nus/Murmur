using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Murmur.App.Controls;

namespace Murmur.App.Tests;

/// <summary>The search mark of a transcript line: every hit is boxed, the box has room of 3 px on each side, the click still finds the word.</summary>
public sealed class TranscriptMarkTests
{
    private static HeadlessUnitTestSession Session => UiHarness.Session;

    private static TranscriptTextControl Control(string text, string? term, int[]? starts = null, int[]? lengths = null) => new()
    {
        Text = text,
        HighlightTerm = term,
        WordStarts = starts,
        WordLengths = lengths,
        FontFamily = new FontFamily("Inter"),
        FontSize = 15,
        LineHeight = 24,
        LetterSpacing = 0.14,
        Foreground = Brushes.Black,
        MarkBrush = Brushes.Black,
        MarkForeground = Brushes.White,
    };

    private static double WidthOf(TranscriptTextControl control)
    {
        control.Measure(new Size(2000, double.PositiveInfinity));
        control.Arrange(new Rect(control.DesiredSize));
        return control.DesiredSize.Width == 2000 ? 0 : control.DesiredSize.Width;
    }

    [Fact]
    public async Task EveryHit_IsMarked_InAnyCase()
    {
        await Session.Dispatch(
            () =>
            {
                var control = Control("Timestamps and more timestamps, TIMESTAMPS", "timestamps");
                control.Measure(new Size(600, double.PositiveInfinity));

                Assert.Equal(3, control.MarkCount);
                Assert.Equal(0, Control("nothing here", "timestamps").MarkCount);
                Assert.Equal(0, Control("nothing here", null).MarkCount);
            },
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AMarkedWord_GetsRoomOfThreePixelsOnEachSide()
    {
        await Session.Dispatch(
            () =>
            {
                var plain = Control("Answers come back with timestamps, so", null);
                var marked = Control("Answers come back with timestamps, so", "timestamps");
                var plainFit = new TextBlock { Text = "x" };
                plainFit.Measure(default);

                plain.Measure(new Size(2000, double.PositiveInfinity));
                marked.Measure(new Size(2000, double.PositiveInfinity));

                // Both sides: 2 x 3 px, within the rounding of the space of the font.
                var extra = marked.MarkWidth - plain.MarkWidth;
                Assert.InRange(extra, 5.5, 6.5);
            },
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AClickBehindAMark_FindsTheRightWord()
    {
        await Session.Dispatch(
            () =>
            {
                string[] words = ["Answers", "come", "back", "with", "timestamps,", "so", "one", "click"];
                var text = string.Join(' ', words);
                var starts = new int[words.Length];
                var lengths = new int[words.Length];
                var at = 0;
                for (var i = 0; i < words.Length; i++)
                {
                    starts[i] = at;
                    lengths[i] = words[i].Length;
                    at += words[i].Length + 1;
                }

                var control = Control(text, "timestamps", starts, lengths);
                control.Measure(new Size(2000, double.PositiveInfinity));
                control.Arrange(new Rect(control.DesiredSize));

                // The right end of the line is "click", whatever the marks shifted.
                Assert.Equal(7, control.WordAt(new Point(control.DesiredSize.Width - 2, 8)));
                Assert.Equal(0, control.WordAt(new Point(2, 8)));
            },
            TestContext.Current.CancellationToken);
    }
}
