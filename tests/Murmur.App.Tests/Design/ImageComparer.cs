namespace Murmur.App.Tests.Design;

/// <summary>A rectangle of the reference image that is left out of the comparison, with the reason why. <c>TextOnly</c>: it hides glyphs only, so the shape layer (no glyphs) does not need it.</summary>
internal sealed record IgnoreRect(int X, int Y, int Width, int Height, string Reason, bool TextOnly = false)
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
    /// <summary>Every compared pixel of the board.</summary>
    public required MismatchCounts Total { get; init; }

    /// <summary>The strip of the left navigation (x &lt; <see cref="ImageComparer.SidebarWidth"/>); empty when the board has none.</summary>
    public required MismatchCounts Sidebar { get; init; }

    public required MismatchCounts Content { get; init; }

    /// <summary>
    /// Only the pixels that are not page background in the reference or in the app (the union): text, controls, cards,
    /// hairlines. A screen is mostly empty, so this is the number to gate on.
    /// </summary>
    public required MismatchCounts Drawn { get; init; }

    public required MismatchCounts DrawnSidebar { get; init; }

    public required MismatchCounts DrawnContent { get; init; }

    /// <summary>Pixels of the reference inside the ignore rectangles (not part of any count).</summary>
    public required long IgnoredPixels { get; init; }

    public required bool SizeDiffers { get; init; }

    /// <summary>
    /// The reference greyed out; red = only in the app (nothing like it in the reference), blue = only in the reference (missing
    /// from the app), purple = both; ignored rectangles tinted yellow.
    /// </summary>
    public required PixelImage Diff { get; init; }
}

/// <summary>
/// Compares a render of the app with a reference image, symmetrically. A pixel is a mismatch when the app pixel has no pixel
/// within <c>tolerance</c> (every channel) in the 3x3 neighbourhood of the reference, or the reference pixel has none in the
/// 3x3 neighbourhood of the app. The neighbourhood forgives 1 px shifts and anti-aliasing; the low tolerance still reports a
/// wrong fill or a missing hairline; the second direction reports content that the app does not draw at all.
/// </summary>
internal static class ImageComparer
{
    /// <summary>Page background of a theme, from <c>docs/design/tokens.json</c> (<c>color.canvas</c> / <c>colorDark.canvas</c>).</summary>
    public static (byte R, byte G, byte B) CanvasColor(bool dark)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(RepoPaths.TokensJson));
        var hex = document.RootElement.GetProperty(dark ? "colorDark" : "color").GetProperty("canvas").GetString()!.TrimStart('#');
        return (Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
    }

    /// <summary>The width of the sidebar strip of the main screens (the 240 px column and its hairline).</summary>
    public const int SidebarWidth = 241;

    /// <summary>The largest per-channel difference (of 255) that still counts as the same colour.</summary>
    public const int DefaultTolerance = 5;

    /// <summary>The tolerance that forgives the lighter text of Skia against Chromium (see <c>ParityCalibrationTests</c>); it also forgives fills and hairlines.</summary>
    public const int LenientTolerance = 24;

    public static ImageComparison Compare(
        PixelImage reference,
        PixelImage actual,
        IReadOnlyList<IgnoreRect>? ignored = null,
        bool hasSidebar = false,
        int tolerance = DefaultTolerance,
        (byte R, byte G, byte B)? background = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(actual);
        ignored ??= [];

        // Both images are compared over the larger of the two sizes; what an image does not have is page background.
        var sizeDiffers = reference.Width != actual.Width || reference.Height != actual.Height;
        var corner0 = (((reference.Height - 1) * reference.Width) + (reference.Width - 1)) * 4;
        var bg = background ?? (reference.Rgba[corner0], reference.Rgba[corner0 + 1], reference.Rgba[corner0 + 2]);
        var width = Math.Max(reference.Width, actual.Width);
        var height = Math.Max(reference.Height, actual.Height);
        reference = reference.PaddedTo(width, height, bg);
        actual = actual.PaddedTo(width, height, bg);
        var diff = new PixelImage(reference.Width, reference.Height);
        long sidebarCompared = 0, sidebarMismatched = 0, contentCompared = 0, contentMismatched = 0, ignoredPixels = 0;
        long drawnSidebarCompared = 0, drawnSidebarMismatched = 0, drawnContentCompared = 0, drawnContentMismatched = 0;

        byte bgR = bg.Item1, bgG = bg.Item2, bgB = bg.Item3;

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
                    diff.Set(x, y, 255, (byte)Math.Min(255, grey + 10), (byte)(grey * 80 / 100));
                    continue;
                }

                var onlyApp = !HasMatch(actual, reference, x, y, tolerance);
                var onlyReference = !HasMatch(reference, actual, x, y, tolerance);
                var mismatch = onlyApp || onlyReference;
                if (onlyApp && onlyReference)
                {
                    diff.Set(x, y, 170, 30, 170);
                }
                else if (onlyApp)
                {
                    diff.Set(x, y, 230, 30, 30);
                }
                else if (onlyReference)
                {
                    diff.Set(x, y, 30, 90, 230);
                }
                else
                {
                    diff.Set(x, y, grey, grey, grey);
                }

                var sidebar = hasSidebar && x < SidebarWidth;
                var drawn = IsDrawn(reference, x, y, bgR, bgG, bgB, tolerance) || IsDrawn(actual, x, y, bgR, bgG, bgB, tolerance);
                if (sidebar)
                {
                    sidebarCompared++;
                    sidebarMismatched += mismatch ? 1 : 0;
                    if (drawn)
                    {
                        drawnSidebarCompared++;
                        drawnSidebarMismatched += mismatch ? 1 : 0;
                    }
                }
                else
                {
                    contentCompared++;
                    contentMismatched += mismatch ? 1 : 0;
                    if (drawn)
                    {
                        drawnContentCompared++;
                        drawnContentMismatched += mismatch ? 1 : 0;
                    }
                }
            }
        }

        var sidebarCounts = new MismatchCounts(sidebarCompared, sidebarMismatched);
        var contentCounts = new MismatchCounts(contentCompared, contentMismatched);
        var drawnSidebar = new MismatchCounts(drawnSidebarCompared, drawnSidebarMismatched);
        var drawnContent = new MismatchCounts(drawnContentCompared, drawnContentMismatched);
        return new ImageComparison
        {
            Total = sidebarCounts + contentCounts,
            Sidebar = sidebarCounts,
            Content = contentCounts,
            Drawn = drawnSidebar + drawnContent,
            DrawnSidebar = drawnSidebar,
            DrawnContent = drawnContent,
            IgnoredPixels = ignoredPixels,
            SizeDiffers = sizeDiffers,
            Diff = diff,
        };
    }

    private static bool IsDrawn(PixelImage image, int x, int y, byte bgR, byte bgG, byte bgB, int tolerance)
    {
        var o = ((y * image.Width) + x) * 4;
        return Math.Abs(image.Rgba[o] - bgR) > tolerance || Math.Abs(image.Rgba[o + 1] - bgG) > tolerance || Math.Abs(image.Rgba[o + 2] - bgB) > tolerance;
    }

    /// <summary>The image on a larger page of the given colour (the same image when it already has that size).</summary>
    private static PixelImage PaddedTo(this PixelImage image, int width, int height, (byte R, byte G, byte B) background)
    {
        if (image.Width == width && image.Height == height)
        {
            return image;
        }

        var padded = PixelImage.Filled(width, height, background.R, background.G, background.B);
        for (var y = 0; y < image.Height; y++)
        {
            Buffer.BlockCopy(image.Rgba, y * image.Width * 4, padded.Rgba, y * width * 4, image.Width * 4);
        }

        return padded;
    }

    /// <summary>The pixel of <paramref name="source"/> at (x, y) against the pixels of <paramref name="target"/> around it.</summary>
    private static bool HasMatch(PixelImage source, PixelImage target, int x, int y, int tolerance)
    {
        var s = ((y * source.Width) + x) * 4;
        var sr = source.Rgba[s];
        var sg = source.Rgba[s + 1];
        var sb = source.Rgba[s + 2];
        for (var dy = -1; dy <= 1; dy++)
        {
            var ty = y + dy;
            if (ty < 0 || ty >= target.Height)
            {
                continue;
            }

            for (var dx = -1; dx <= 1; dx++)
            {
                var tx = x + dx;
                if (tx < 0 || tx >= target.Width)
                {
                    continue;
                }

                var t = ((ty * target.Width) + tx) * 4;
                if (Math.Abs(target.Rgba[t] - sr) <= tolerance
                    && Math.Abs(target.Rgba[t + 1] - sg) <= tolerance
                    && Math.Abs(target.Rgba[t + 2] - sb) <= tolerance)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
