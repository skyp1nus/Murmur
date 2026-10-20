namespace Murmur.App.Tests.Design;

/// <summary>The parity comparer itself: what it forgives and what it reports.</summary>
[Trait("Category", "DesignParity")]
public sealed class ImageComparerTests
{
    /// <summary>A light page with a sidebar strip, a dark block and a 1 px line, like a small screen.</summary>
    private static PixelImage Page()
    {
        var image = PixelImage.Filled(400, 300, 252, 252, 252);
        image.Fill(0, 0, 241, 300, 245, 244, 242);
        image.Fill(240, 0, 1, 300, 214, 209, 203);
        image.Fill(280, 40, 80, 30, 0, 0, 0);
        image.Fill(280, 120, 100, 1, 214, 209, 203);
        image.Fill(20, 20, 150, 40, 0, 0, 0);
        return image;
    }

    [Fact]
    public void IdenticalImages_HaveNoMismatch()
    {
        var result = ImageComparer.Compare(Page(), Page(), hasSidebar: true);

        Assert.Equal(0, result.Total.Percent);
        Assert.Equal(0, result.Sidebar.Percent);
        Assert.Equal(0, result.Content.Percent);
        Assert.Equal(400L * 300, result.Total.Compared);
        Assert.False(result.SizeDiffers);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(1, -1)]
    public void AOnePixelShift_IsForgiven(int dx, int dy)
    {
        var shifted = Page().Shifted(dx, dy);

        var result = ImageComparer.Compare(Page(), shifted, hasSidebar: true);

        Assert.Equal(0, result.Total.Percent);
    }

    [Fact]
    public void ASmallColourDifference_IsForgiven_ALargeOneIsNot()
    {
        var slightly = Page();
        slightly.Fill(280, 40, 80, 30, 4, 4, 4);
        var strongly = Page();
        strongly.Fill(280, 40, 80, 30, 120, 120, 120);

        Assert.Equal(0, ImageComparer.Compare(Page(), slightly).Total.Percent);
        var result = ImageComparer.Compare(Page(), strongly);
        Assert.Equal(80 * 30, result.Total.Mismatched);
    }

    [Fact]
    public void AMovedBlock_IsReportedInTheAreaItLeftAndTheOneItEntered()
    {
        var moved = Page();
        moved.Fill(280, 40, 80, 30, 252, 252, 252);
        moved.Fill(300, 200, 80, 30, 0, 0, 0);

        var result = ImageComparer.Compare(Page(), moved, hasSidebar: true);

        // The new place is white in the reference and black in the app, the old place the other way round: both count in full
        // (the app has nothing like the old block, the reference has nothing like the new one).
        const long Expected = 2 * 80 * 30;
        Assert.Equal(0, result.Sidebar.Mismatched);
        Assert.Equal(Expected, result.Content.Mismatched);
        Assert.InRange(result.Total.Percent, 100.0 * Expected / 120_000 - 0.01, 100.0 * Expected / 120_000 + 0.01);
    }

    [Fact]
    public void Mismatches_AreCountedPerArea_SidebarAndContent()
    {
        var changed = Page();
        changed.Fill(20, 20, 150, 40, 252, 252, 252);

        var result = ImageComparer.Compare(Page(), changed, hasSidebar: true);

        Assert.Equal(150 * 40, result.Sidebar.Mismatched);
        Assert.Equal(0, result.Content.Mismatched);
        Assert.Equal(241L * 300, result.Sidebar.Compared);
        Assert.Equal((400L - 241) * 300, result.Content.Compared);
    }

    [Fact]
    public void WithoutASidebar_EverythingIsContent()
    {
        var result = ImageComparer.Compare(Page(), Page(), hasSidebar: false);

        Assert.Equal(0, result.Sidebar.Compared);
        Assert.Equal(400L * 300, result.Content.Compared);
    }

    [Fact]
    public void IgnoredRectangles_AreNeitherCountedNorCompared()
    {
        var changed = Page();
        changed.Fill(280, 40, 80, 30, 252, 252, 252);
        var ignore = new IgnoreRect(270, 30, 100, 50, "test");

        var result = ImageComparer.Compare(Page(), changed, [ignore]);

        Assert.Equal(0, result.Total.Mismatched);
        Assert.Equal(100 * 50, result.IgnoredPixels);
        Assert.Equal((400L * 300) - (100 * 50), result.Total.Compared);
    }

    [Fact]
    public void ASmallerRender_CountsTheMissingPixelsAsMismatches()
    {
        var small = PixelImage.Filled(200, 300, 252, 252, 252);

        var result = ImageComparer.Compare(Page(), small);

        Assert.True(result.SizeDiffers);
        Assert.True(result.Total.Mismatched > 0, "the content of the reference that the smaller render lacks is reported");
    }

    [Fact]
    public void TheDiffImage_IsGreyWhereTheImagesAgree_PurpleWhereBothDiffer()
    {
        var changed = Page();
        changed.Fill(290, 45, 10, 10, 252, 252, 252);

        var result = ImageComparer.Compare(Page(), changed);

        var diff = result.Diff;
        var purple = ((50 * diff.Width) + 295) * 4;
        Assert.True(diff.Rgba[purple] > 150 && diff.Rgba[purple + 1] < 60 && diff.Rgba[purple + 2] > 150);
        var grey = ((250 * diff.Width) + 300) * 4;
        Assert.Equal(diff.Rgba[grey], diff.Rgba[grey + 1]);
        Assert.Equal(diff.Rgba[grey], diff.Rgba[grey + 2]);
    }

    [Fact]
    public void AThinLineOnlyInTheApp_IsRed_AndOnlyInTheReference_IsBlue()
    {
        var withLine = Page();
        withLine.Fill(300, 200, 60, 1, 0, 0, 0);

        var extra = ImageComparer.Compare(Page(), withLine);
        var missing = ImageComparer.Compare(withLine, Page());

        var o = ((200 * 400) + 330) * 4;
        Assert.Equal(60, extra.Total.Mismatched);
        Assert.True(extra.Diff.Rgba[o] > 200 && extra.Diff.Rgba[o + 2] < 60, "only in the app: red");
        Assert.Equal(60, missing.Total.Mismatched);
        Assert.True(missing.Diff.Rgba[o + 2] > 200 && missing.Diff.Rgba[o] < 60, "only in the reference: blue");
    }

    [Fact]
    public void AWrongFillAndAMissingHairline_AreReported_ByTheLowTolerance()
    {
        // A card (#f5f3f1) on the page (#fdfcfc) and a hairline (#ebe8e4): token-level differences of 8 and 18.
        var reference = PixelImage.Filled(200, 100, 253, 252, 252);
        reference.Fill(20, 20, 100, 50, 245, 243, 241);
        reference.Fill(0, 90, 200, 1, 235, 232, 228);
        var flat = PixelImage.Filled(200, 100, 253, 252, 252);

        var result = ImageComparer.Compare(reference, flat);

        Assert.Equal((100 * 50) + 200, result.Total.Mismatched);
        Assert.Equal(0, ImageComparer.Compare(reference, reference).Total.Mismatched);
    }

    [Fact]
    public void TheDrawnMetric_CountsOnlyPixelsThatAreNotBackground_InEitherImage()
    {
        var changed = Page();
        changed.Fill(280, 40, 80, 30, 252, 252, 252);

        var result = ImageComparer.Compare(Page(), changed, hasSidebar: true);

        // The sidebar's grey fill and the blocks are drawn; the white page is not.
        Assert.True(result.Drawn.Compared < result.Total.Compared);
        Assert.True(result.Drawn.Percent > result.Total.Percent);
        Assert.Equal(result.Total.Mismatched, result.Drawn.Mismatched);
        Assert.Equal(result.Drawn.Compared, result.DrawnSidebar.Compared + result.DrawnContent.Compared);
    }

    [Fact]
    public void ALargerRender_IsComparedOverTheLargerSize_ExtraContentMismatches_ExtraPageDoesNot()
    {
        var reference = PixelImage.Filled(100, 100, 253, 252, 252);
        var blankLarger = PixelImage.Filled(150, 140, 253, 252, 252);
        var withContent = PixelImage.Filled(150, 140, 253, 252, 252);
        withContent.Fill(110, 20, 20, 20, 0, 0, 0);

        var blank = ImageComparer.Compare(reference, blankLarger);
        var content = ImageComparer.Compare(reference, withContent);

        Assert.True(blank.SizeDiffers);
        Assert.Equal(0, blank.Total.Mismatched);
        Assert.Equal(150L * 140, blank.Total.Compared);
        Assert.Equal(20 * 20, content.Total.Mismatched);
        Assert.Equal((150, 140), (content.Diff.Width, content.Diff.Height));
    }

    [Fact]
    public void TheBackground_IsTheGivenColour_NotTheCornerPixel()
    {
        // The corner of this "reference" is covered by a card; the page colour is the token one.
        var reference = PixelImage.Filled(100, 100, 253, 252, 252);
        reference.Fill(0, 0, 100, 100, 245, 243, 241);
        var app = PixelImage.Filled(100, 100, 245, 243, 241);

        var corner = ImageComparer.Compare(reference, app);
        var token = ImageComparer.Compare(reference, app, background: (253, 252, 252));

        Assert.Equal(0, corner.Drawn.Compared);
        Assert.Equal(100 * 100, token.Drawn.Compared);
    }

    [Theory]
    [InlineData(false, 0xfd, 0xfc, 0xfc)]
    [InlineData(true, 0x14, 0x13, 0x12)]
    public void TheCanvasColour_ComesFromTheTokens(bool dark, int r, int g, int b) =>
        Assert.Equal(((byte)r, (byte)g, (byte)b), ImageComparer.CanvasColor(dark));

    [Fact]
    public void APngRoundTrip_KeepsThePixels()
    {
        var image = Page();
        var path = Path.Combine(Path.GetTempPath(), "murmur-parity-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            image.SavePng(path);
            var loaded = PixelImage.LoadPng(path);

            Assert.Equal(image.Width, loaded.Width);
            Assert.Equal(image.Rgba, loaded.Rgba);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
