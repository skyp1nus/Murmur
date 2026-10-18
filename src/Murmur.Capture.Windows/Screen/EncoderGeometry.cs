namespace Murmur.Capture.Windows.Screen;

/// <summary>A size in pixels.</summary>
internal readonly record struct PixelSize(int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public override string ToString() => $"{Width}x{Height}";
}

/// <summary>A pixel rectangle, right/bottom exclusive (the RECT / D3D11_BOX convention).</summary>
internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

/// <summary>
/// How one captured frame lands in an encoder texture.
/// <see cref="DirectCopy"/>: a plain GPU sub-rectangle copy of <see cref="Source"/> to the encoder texture's top-left
/// corner (source and encoder size match up to the 1 px even-size crop). Otherwise a GPU video-processor blit that
/// scales <see cref="Source"/> into <see cref="Destination"/> (aspect kept, centred, letterboxed with black).
/// </summary>
internal readonly record struct FramePlacement(bool DirectCopy, PixelRect Source, PixelRect Destination);

/// <summary>Pure size math of the screen encoder (allocation-free, any OS).</summary>
internal static class EncoderGeometry
{
    /// <summary>Widest frame handed to H.264 encoders (level 5.1 / most hardware encoders top out at 4096).</summary>
    public const int MaxEncoderWidth = 4096;

    /// <summary>
    /// The encoder frame size for a <paramref name="width"/> x <paramref name="height"/> capture: the capture size
    /// scaled down (never up) so the height is at most <paramref name="maxHeight"/> and the width at most
    /// <paramref name="maxWidth"/>, aspect ratio kept, both dimensions rounded down to even (4:2:0 chroma), at least 2x2.
    /// </summary>
    public static PixelSize FitEncoderSize(int width, int height, int maxHeight, int maxWidth = MaxEncoderWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHeight, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWidth, 2);

        var scale = 1.0;
        if (height > maxHeight)
        {
            scale = maxHeight / (double)height;
        }

        if (width * scale > maxWidth)
        {
            scale = maxWidth / (double)width;
        }

        if (scale >= 1.0)
        {
            return new PixelSize(EvenDown(width), EvenDown(height));
        }

        // Round the constrained dimension first, then derive the other from it, so the even-size rounding costs at
        // most 1.5 px of aspect error.
        var aspect = width / (double)height;
        if (width * scale > maxWidth - 0.5)
        {
            var w = EvenDown(maxWidth);
            return new PixelSize(w, Math.Min(EvenDown((int)Math.Round(w / aspect)), EvenDown(maxHeight)));
        }

        var h = Math.Min(EvenDown((int)Math.Round(height * scale)), EvenDown(maxHeight));
        return new PixelSize(Math.Min(EvenDown((int)Math.Round(h * aspect)), EvenDown(maxWidth)), h);
    }

    /// <summary>
    /// Places a frame whose valid content is <paramref name="content"/> inside a capture texture of
    /// <paramref name="texture"/> size into an encoder frame of <paramref name="encoder"/> size.
    /// </summary>
    public static FramePlacement Place(PixelSize content, PixelSize texture, PixelSize encoder)
    {
        var sourceWidth = Math.Clamp(content.Width, 1, Math.Max(1, texture.Width));
        var sourceHeight = Math.Clamp(content.Height, 1, Math.Max(1, texture.Height));
        var full = new PixelRect(0, 0, encoder.Width, encoder.Height);

        var extraWidth = sourceWidth - encoder.Width;
        var extraHeight = sourceHeight - encoder.Height;
        if (extraWidth is >= 0 and <= 1 && extraHeight is >= 0 and <= 1)
        {
            return new FramePlacement(true, new PixelRect(0, 0, encoder.Width, encoder.Height), full);
        }

        var scale = Math.Min(encoder.Width / (double)sourceWidth, encoder.Height / (double)sourceHeight);
        var w = Math.Clamp((int)Math.Round(sourceWidth * scale), 1, encoder.Width);
        var h = Math.Clamp((int)Math.Round(sourceHeight * scale), 1, encoder.Height);
        var left = (encoder.Width - w) / 2;
        var top = (encoder.Height - h) / 2;
        return new FramePlacement(
            false,
            new PixelRect(0, 0, sourceWidth, sourceHeight),
            new PixelRect(left, top, left + w, top + h));
    }

    /// <summary>MF_MT_FRAME_SIZE / MF_MT_FRAME_RATE / MF_MT_PIXEL_ASPECT_RATIO packing: high 32 bits, low 32 bits.</summary>
    public static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    private static int EvenDown(int value) => Math.Max(2, value & ~1);
}
