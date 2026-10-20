using SkiaSharp;

namespace Murmur.App.Tests.Design;

/// <summary>
/// Renders every artboard with the app and compares it with the reference render. Report-only until a board gets a
/// <see cref="DesignBoard.MaxMismatchPercent"/>: a board passes when it can be rendered.
/// Run with <c>scripts/design/parity.sh</c>; the output goes to <c>artifacts/design-parity</c>.
/// </summary>
[Trait("Category", "DesignParity")]
public sealed class DesignParityTests
{
    public static TheoryData<string> BoardNames => DesignBoards.Names();

    [Theory]
    [MemberData(nameof(BoardNames))]
    public async Task Board_IsRenderedAndComparedWithTheReference(string name)
    {
        var board = DesignBoards.Get(name);
        if (Environment.GetEnvironmentVariable("MURMUR_PARITY_BOARDS") is { Length: > 0 } only
            && !only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(name, StringComparer.Ordinal))
        {
            Assert.Skip("Not in MURMUR_PARITY_BOARDS.");
        }

        Assert.True(File.Exists(board.ReferencePath), "Missing reference render " + board.ReferencePath);
        var reference = PixelImage.LoadPng(board.ReferencePath);
        Assert.Equal((board.Width, board.Height), (reference.Width, reference.Height));

        var (actual, notes) = await UiHarness.Session.Dispatch(
            async () =>
            {
                using var scene = await DesignScene.BuildAsync(board);
                return (scene.Render(), scene.Notes.ToArray());
            },
            TestContext.Current.CancellationToken);

        var comparison = ImageComparer.Compare(reference, actual, board.Ignored, board.HasSidebar);
        var result = ParityReport.Write(board, reference, actual, comparison, notes);

        Assert.Equal((board.Width, board.Height), (actual.Width, actual.Height));
        if (board.MaxMismatchPercent is { } max)
        {
            Assert.True(result.Total <= max, $"{board.Name}: {result.Total:0.00} % of the pixels differ, the limit is {max:0.00} %.");
        }
    }

    [Fact]
    public void TheTable_HasEveryArtboardOfTheCanvas_ExceptTheComponentSheets()
    {
        var canvas = File.ReadAllText(Path.Combine(RepoPaths.Root, "docs", "design", "source", "canvas.json"));
        var boards = System.Text.RegularExpressions.Regex.Matches(canvas, "\"([A-Za-z0-9]+)\\.dc\\.html\":")
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.Equal(24, boards.Length);
        Assert.Equal(22, DesignBoards.All.Count);
        Assert.Equal(boards.Where(b => !DesignBoards.Excluded.Contains(b)).Order(), DesignBoards.All.Select(b => b.Name).Order());
        Assert.Equal(DesignBoards.All.Count, DesignBoards.All.Select(b => b.Name).Distinct().Count());
    }

    [Fact]
    public void EveryReferenceRender_HasTheSizeOfItsBoard()
    {
        foreach (var board in DesignBoards.All)
        {
            using var codec = SKCodec.Create(board.ReferencePath);
            Assert.NotNull(codec);
            Assert.Equal((board.Width, board.Height), (codec.Info.Width, codec.Info.Height));
        }
    }

    [Fact]
    public void IgnoredRectangles_LieInsideTheirBoard_AndNameAReason()
    {
        foreach (var board in DesignBoards.All)
        {
            foreach (var rect in board.Ignored)
            {
                Assert.True(rect.X >= 0 && rect.Y >= 0 && rect.X + rect.Width <= board.Width && rect.Y + rect.Height <= board.Height, board.Name + ": " + rect.Reason);
                Assert.False(string.IsNullOrWhiteSpace(rect.Reason));
            }
        }
    }
}
