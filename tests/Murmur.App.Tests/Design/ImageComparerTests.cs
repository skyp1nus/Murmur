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
        slightly.Fill(280, 40, 80, 30, 10, 10, 10);
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

        // The new place is white in the reference and black in the app (all of it differs); the old place is black in the
        // reference and white in the app, except its rim, which has a white pixel in its 3x3 neighbourhood.
        const long Expected = (80 * 30) + (78 * 28);
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

        Assert.Equal(148 * 38, result.Sidebar.Mismatched);
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
        Assert.True(result.Total.Mismatched >= 200L * 300);
    }

    [Fact]
    public void TheDiffImage_IsGreyWhereTheImagesAgreeAndRedWhereTheyDiffer()
    {
        var changed = Page();
        changed.Fill(290, 45, 10, 10, 252, 252, 252);

        var result = ImageComparer.Compare(Page(), changed);

        var diff = result.Diff;
        var red = ((50 * diff.Width) + 295) * 4;
        Assert.True(diff.Rgba[red] > 200 && diff.Rgba[red + 1] < 60);
        var grey = ((250 * diff.Width) + 300) * 4;
        Assert.Equal(diff.Rgba[grey], diff.Rgba[grey + 1]);
        Assert.Equal(diff.Rgba[grey], diff.Rgba[grey + 2]);
    }

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
