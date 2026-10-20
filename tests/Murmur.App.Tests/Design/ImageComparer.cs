namespace Murmur.App.Tests.Design;

/// <summary>A rectangle of the reference image that is left out of the comparison, with the reason why.</summary>
internal sealed record IgnoreRect(int X, int Y, int Width, int Height, string Reason)
{
    public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>How many pixels were compared and how many of them differ.</summary>
internal readonly record struct MismatchCounts(long Compared, long Mismatched)
{
    public double Percent => Compared == 0 ? 0 : 100.0 * Mismatched / Compared;

    public static MismatchCounts operator +(MismatchCounts a, MismatchCounts b) => new(a.Compared + b.Compared, a.Mismatched + b.Mismatched);
}

internal sealed class ImageComparison
{
    public required MismatchCounts Total { get; init; }

    /// <summary>The strip of the left navigation (x &lt; <see cref="ImageComparer.SidebarWidth"/>); empty when the board has none.</summary>
    public required MismatchCounts Sidebar { get; init; }

    public required MismatchCounts Content { get; init; }

    /// <summary>
    /// The mismatches against the pixels that are not page background in the reference or in the app (text, controls, cards). A
    /// screen is mostly empty, so this tells better than <see cref="Total"/> how much of what is drawn is wrong.
    /// </summary>
    public required MismatchCounts Ink { get; init; }

    /// <summary>Pixels of the reference inside the ignore rectangles (not part of any count).</summary>
    public required long IgnoredPixels { get; init; }

    public required bool SizeDiffers { get; init; }

    /// <summary>The reference greyed out, mismatching pixels red, ignored rectangles tinted blue.</summary>
    public required PixelImage Diff { get; init; }
}

/// <summary>
/// Compares a render of the app with a reference image pixel by pixel. A pixel matches when some pixel of the reference's
/// 3x3 neighbourhood is within <c>tolerance</c> on every channel: that forgives 1 px shifts, anti-aliasing and sub-pixel text
/// differences, and still reports moved blocks, other colours and missing elements.
/// </summary>
internal static class ImageComparer
{
    /// <summary>The width of the sidebar strip of the main screens (the 240 px column and its hairline).</summary>
    public const int SidebarWidth = 241;

    /// <summary>The largest per-channel difference (of 255) that still counts as the same colour.</summary>
    public const int DefaultTolerance = 24;

    public static ImageComparison Compare(
        PixelImage reference,
        PixelImage actual,
        IReadOnlyList<IgnoreRect>? ignored = null,
        bool hasSidebar = false,
        int tolerance = DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(actual);
        ignored ??= [];
        var diff = new PixelImage(reference.Width, reference.Height);
        long sidebarCompared = 0, sidebarMismatched = 0, contentCompared = 0, contentMismatched = 0, ignoredPixels = 0;
        long inkCompared = 0, inkMismatched = 0;

        // The page background: the bottom right pixel of the reference (every artboard leaves it empty).
        var corner = ((reference.Height - 1) * reference.Width + (reference.Width - 1)) * 4;
        byte bgR = reference.Rgba[corner], bgG = reference.Rgba[corner + 1], bgB = reference.Rgba[corner + 2];

        for (var y = 0; y < reference.Height; y++)
        {
            for (var x = 0; x < reference.Width; x++)
            {
                var o = ((y * reference.Width) + x) * 4;
                var lum = ((reference.Rgba[o] * 54) + (reference.Rgba[o + 1] * 183) + (reference.Rgba[o + 2] * 19)) >> 8;
                var grey = (byte)(255 - ((255 - lum) * 4 / 10));
                var inIgnored = false;
                for (var i = 0; i < ignored.Count; i++)
                {
                    if (ignored[i].Contains(x, y))
                    {
                        inIgnored = true;
                        break;
                    }
                }

                if (inIgnored)
                {
                    ignoredPixels++;
                    diff.Set(x, y, (byte)(grey * 80 / 100), (byte)(grey * 90 / 100), 255);
                    continue;
                }

                var mismatch = !Matches(reference, actual, x, y, tolerance);
                if (IsInk(reference, x, y, bgR, bgG, bgB, tolerance) || IsInk(actual, x, y, bgR, bgG, bgB, tolerance))
                {
                    inkCompared++;
                    inkMismatched += mismatch ? 1 : 0;
                }

                if (mismatch)
                {
                    diff.Set(x, y, 230, 30, 30);
                }
                else
                {
                    diff.Set(x, y, grey, grey, grey);
                }

                if (hasSidebar && x < SidebarWidth)
                {
                    sidebarCompared++;
                    sidebarMismatched += mismatch ? 1 : 0;
                }
                else
                {
                    contentCompared++;
                    contentMismatched += mismatch ? 1 : 0;
                }
            }
        }

        var sidebar = new MismatchCounts(sidebarCompared, sidebarMismatched);
        var content = new MismatchCounts(contentCompared, contentMismatched);
        return new ImageComparison
        {
            Total = sidebar + content,
            Sidebar = sidebar,
            Content = content,
            Ink = new MismatchCounts(inkCompared, inkMismatched),
            IgnoredPixels = ignoredPixels,
            SizeDiffers = reference.Width != actual.Width || reference.Height != actual.Height,
            Diff = diff,
        };
    }

    private static bool IsInk(PixelImage image, int x, int y, byte bgR, byte bgG, byte bgB, int tolerance)
    {
        if (x >= image.Width || y >= image.Height)
        {
            return false;
        }

        var o = ((y * image.Width) + x) * 4;
        return Math.Abs(image.Rgba[o] - bgR) > tolerance || Math.Abs(image.Rgba[o + 1] - bgG) > tolerance || Math.Abs(image.Rgba[o + 2] - bgB) > tolerance;
    }

    /// <summary>The app pixel at (x, y) against the reference pixels around it; a pixel the app image does not have never matches.</summary>
    private static bool Matches(PixelImage reference, PixelImage actual, int x, int y, int tolerance)
    {
        if (x >= actual.Width || y >= actual.Height)
        {
            return false;
        }

        var a = ((y * actual.Width) + x) * 4;
        var ar = actual.Rgba[a];
        var ag = actual.Rgba[a + 1];
        var ab = actual.Rgba[a + 2];
        for (var dy = -1; dy <= 1; dy++)
        {
            var ry = y + dy;
            if (ry < 0 || ry >= reference.Height)
            {
                continue;
            }

            for (var dx = -1; dx <= 1; dx++)
            {
                var rx = x + dx;
                if (rx < 0 || rx >= reference.Width)
                {
                    continue;
                }

                var r = ((ry * reference.Width) + rx) * 4;
                if (Math.Abs(reference.Rgba[r] - ar) <= tolerance
                    && Math.Abs(reference.Rgba[r + 1] - ag) <= tolerance
                    && Math.Abs(reference.Rgba[r + 2] - ab) <= tolerance)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
