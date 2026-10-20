using SkiaSharp;

namespace Murmur.App.Tests.Design;

/// <summary>
/// Renders every artboard with the app and compares it with the reference: the shape layer by pixels, the text by geometry.
/// Report-only until a board gets a gate (<see cref="DesignBoard.MaxShapeMismatchPercent"/>, <see cref="DesignBoard.MinTextMatchedPercent"/>,
/// <see cref="DesignBoard.MaxTextGeometryErrors"/>): a board passes when it can be rendered.
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

        var (actual, shapes, appTexts, notes) = await UiHarness.Session.Dispatch(
            async () =>
            {
                using var scene = await DesignScene.BuildAsync(board);
                var frame = scene.Render();
                var texts = AppTexts.Collect(scene.Window);
                var shapeLayer = scene.RenderShapesOnly();
                return (frame, shapeLayer, texts, scene.Notes.ToArray());
            },
            TestContext.Current.CancellationToken);

        Assert.Equal((board.Width, board.Height), (actual.Width, actual.Height));
        var shapesReference = PixelImage.LoadPng(board.ShapesReferencePath);
        Assert.Equal((board.Width, board.Height), (shapesReference.Width, shapesReference.Height));
        var referenceTexts = ReferenceTexts.Load(name);
        var run = new BoardRun(
            board,
            reference,
            actual,
            shapesReference,
            shapes,
            ImageComparer.Compare(reference, actual, board.Ignored, board.HasSidebar, background: board.Background),
            ImageComparer.Compare(reference, actual, board.Ignored, board.HasSidebar, ImageComparer.LenientTolerance, board.Background),
            ImageComparer.Compare(shapesReference, shapes, board.ShapeIgnored, board.HasSidebar, background: board.Background),
            referenceTexts,
            appTexts,
            TextMatcher.Compare(referenceTexts, appTexts, board.ExcludedTexts),
            notes);
        var result = ParityReport.Write(run);

        if (board.MaxShapeMismatchPercent is { } maxShape)
        {
            Assert.True(result.ShapeDrawn <= maxShape, $"{board.Name}: {result.ShapeDrawn:0.00} % of the drawn shape pixels differ, the limit is {maxShape:0.00} %.");
        }

        if (board.MinTextMatchedPercent is { } minText)
        {
            Assert.True(result.TextMatchedPercent >= minText, $"{board.Name}: {result.TextMatchedPercent:0.0} % of the texts were found, at least {minText:0.0} % are required.");
        }

        if (board.MaxTextGeometryErrors is { } maxErrors)
        {
            Assert.True(result.TextErrors <= maxErrors, $"{board.Name}: {result.TextErrors} texts are off, at most {maxErrors} are allowed.");
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
            foreach (var path in new[] { board.ReferencePath, board.ShapesReferencePath })
            {
                using var codec = SKCodec.Create(path);
                Assert.NotNull(codec);
                Assert.Equal((board.Width, board.Height), (codec.Info.Width, codec.Info.Height));
            }
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
