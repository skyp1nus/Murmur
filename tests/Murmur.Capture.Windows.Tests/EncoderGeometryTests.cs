using Murmur.Capture.Windows.Screen;

namespace Murmur.Capture.Windows.Tests;

public sealed class EncoderGeometryTests
{
    [Theory]
    [InlineData(1920, 1080, 1080, 1920, 1080)] // fits: unchanged
    [InlineData(1280, 720, 1080, 1280, 720)]   // never scaled up
    [InlineData(3840, 2160, 1080, 1920, 1080)] // 4K -> 1080p
    [InlineData(2560, 1440, 1080, 1920, 1080)]
    [InlineData(2560, 1600, 1080, 1728, 1080)] // 16:10
    [InlineData(1366, 768, 720, 1280, 720)]    // 1280.6 -> 1280 (even)
    [InlineData(1921, 1081, 1080, 1918, 1080)] // scaled: 1919.2 -> 1918 (even), height exactly the cap
    [InlineData(1023, 767, 1080, 1022, 766)]   // fits but odd: even-size crop
    [InlineData(5120, 1440, 1440, 4096, 1152)] // super-wide: width capped at 4096
    [InlineData(1080, 1920, 1080, 608, 1080)]  // portrait: 607.5 rounds to even 608
    [InlineData(3, 3, 1080, 2, 2)]             // minimum 2x2
    public void FitEncoderSize_KeepsAspect_EvenAndWithinLimits(int width, int height, int maxHeight, int expectedWidth, int expectedHeight)
    {
        var size = EncoderGeometry.FitEncoderSize(width, height, maxHeight);

        Assert.Equal(new PixelSize(expectedWidth, expectedHeight), size);
        Assert.Equal(0, size.Width % 2);
        Assert.Equal(0, size.Height % 2);
        Assert.True(size.Height <= maxHeight);
        Assert.True(size.Width <= EncoderGeometry.MaxEncoderWidth);
    }

    [Fact]
    public void FitEncoderSize_AspectErrorIsAtMostOneAndAHalfPixels()
    {
        for (var w = 640; w <= 7680; w += 37)
        {
            for (var h = 480; h <= 4320; h += 53)
            {
                var size = EncoderGeometry.FitEncoderSize(w, h, 1080);
                var idealWidth = size.Height * (w / (double)h);
                var idealHeight = size.Width * (h / (double)w);
                if (h <= 1080 && w <= EncoderGeometry.MaxEncoderWidth)
                {
                    Assert.Equal(new PixelSize(w & ~1, h & ~1), size); // fits: only the even-size crop
                }
                else if (size.Width < EncoderGeometry.MaxEncoderWidth)
                {
                    Assert.InRange(size.Width, idealWidth - 1.5, idealWidth + 1.5);
                }
                else
                {
                    Assert.InRange(size.Height, idealHeight - 1.5, idealHeight + 1.5);
                }

                Assert.True(size.Width <= w && size.Height <= h);
            }
        }
    }

    [Theory]
    [InlineData(0, 1080, 1080)]
    [InlineData(1920, 0, 1080)]
    [InlineData(1920, 1080, 1)]
    public void FitEncoderSize_RejectsInvalidInput(int width, int height, int maxHeight) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => EncoderGeometry.FitEncoderSize(width, height, maxHeight));

    [Fact]
    public void Place_SameSize_IsDirectCopyOfTheWholeFrame()
    {
        var placement = EncoderGeometry.Place(new PixelSize(1920, 1080), new PixelSize(1920, 1080), new PixelSize(1920, 1080));

        Assert.True(placement.DirectCopy);
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), placement.Source);
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), placement.Destination);
    }

    [Fact]
    public void Place_OddCapture_IsDirectCopyWithOnePixelCrop()
    {
        var placement = EncoderGeometry.Place(new PixelSize(1023, 767), new PixelSize(1023, 767), new PixelSize(1022, 766));

        Assert.True(placement.DirectCopy);
        Assert.Equal(new PixelRect(0, 0, 1022, 766), placement.Source);
    }

    [Fact]
    public void Place_LargerCapture_ScalesToFill()
    {
        var placement = EncoderGeometry.Place(new PixelSize(3840, 2160), new PixelSize(3840, 2160), new PixelSize(1920, 1080));

        Assert.False(placement.DirectCopy);
        Assert.Equal(new PixelRect(0, 0, 3840, 2160), placement.Source);
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), placement.Destination);
    }

    [Fact]
    public void Place_DifferentAspect_LetterboxesCentred()
    {
        // Resolution changed mid-segment from 16:9 to 4:3: pillarbox in the 16:9 encoder frame.
        var placement = EncoderGeometry.Place(new PixelSize(1024, 768), new PixelSize(1024, 768), new PixelSize(1920, 1080));

        Assert.False(placement.DirectCopy);
        Assert.Equal(new PixelRect(240, 0, 1680, 1080), placement.Destination);
    }

    [Fact]
    public void Place_SmallerCapture_ScalesUpInsteadOfLeavingStalePixels()
    {
        var placement = EncoderGeometry.Place(new PixelSize(1280, 720), new PixelSize(1280, 720), new PixelSize(1920, 1080));

        Assert.False(placement.DirectCopy);
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), placement.Destination);
    }

    [Fact]
    public void Place_ContentLargerThanTexture_IsClampedToTheTexture()
    {
        // WGC reports the new content size before the pool is recreated.
        var placement = EncoderGeometry.Place(new PixelSize(2560, 1440), new PixelSize(1920, 1080), new PixelSize(1920, 1080));

        Assert.True(placement.DirectCopy);
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), placement.Source);
    }

    [Fact]
    public void Pack_PutsHighInUpperDword() =>
        Assert.Equal(0x0000_0780_0000_0438UL, EncoderGeometry.Pack(1920, 1080));
}
