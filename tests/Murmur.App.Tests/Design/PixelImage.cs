using SkiaSharp;

namespace Murmur.App.Tests.Design;

/// <summary>An opaque RGBA image in memory (row by row, no padding): what the parity comparer works on.</summary>
internal sealed class PixelImage
{
    public PixelImage(int width, int height, byte[]? rgba = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        Rgba = rgba ?? new byte[width * height * 4];
        if (Rgba.Length != width * height * 4)
        {
            throw new ArgumentException("The pixel buffer does not match the size.", nameof(rgba));
        }
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Rgba { get; }

    /// <summary>A solid image.</summary>
    public static PixelImage Filled(int width, int height, byte r, byte g, byte b)
    {
        var image = new PixelImage(width, height);
        for (var i = 0; i < image.Rgba.Length; i += 4)
        {
            image.Rgba[i] = r;
            image.Rgba[i + 1] = g;
            image.Rgba[i + 2] = b;
            image.Rgba[i + 3] = 255;
        }

        return image;
    }

    public void Fill(int x, int y, int width, int height, byte r, byte g, byte b)
    {
        for (var row = Math.Max(0, y); row < Math.Min(Height, y + height); row++)
        {
            for (var column = Math.Max(0, x); column < Math.Min(Width, x + width); column++)
            {
                Set(column, row, r, g, b);
            }
        }
    }

    public void Set(int x, int y, byte r, byte g, byte b)
    {
        var o = ((y * Width) + x) * 4;
        Rgba[o] = r;
        Rgba[o + 1] = g;
        Rgba[o + 2] = b;
        Rgba[o + 3] = 255;
    }

    /// <summary>The image moved by <paramref name="dx"/>, <paramref name="dy"/>; the vacated edge repeats the border pixels.</summary>
    public PixelImage Shifted(int dx, int dy)
    {
        var result = new PixelImage(Width, Height);
        for (var y = 0; y < Height; y++)
        {
            var sy = Math.Clamp(y - dy, 0, Height - 1);
            for (var x = 0; x < Width; x++)
            {
                var sx = Math.Clamp(x - dx, 0, Width - 1);
                Buffer.BlockCopy(Rgba, ((sy * Width) + sx) * 4, result.Rgba, ((y * Width) + x) * 4, 4);
            }
        }

        return result;
    }

    public static PixelImage LoadPng(string path)
    {
        using var decoded = SKBitmap.Decode(path) ?? throw new InvalidOperationException("Cannot decode " + path);
        return FromBitmap(decoded);
    }

    /// <summary>Copies a bitmap, flattening transparency onto white.</summary>
    public static PixelImage FromBitmap(SKBitmap bitmap)
    {
        using var converted = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(converted))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(bitmap, 0, 0);
        }

        var pixels = new byte[bitmap.Width * bitmap.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(converted.GetPixels(), pixels, 0, pixels.Length);
        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }

        return new PixelImage(bitmap.Width, bitmap.Height, pixels);
    }

    /// <summary>Copies a rendered frame: <paramref name="bgra"/> says whether the bytes are B, G, R, A (else R, G, B, A).</summary>
    public static PixelImage FromFrame(byte[] pixels, int width, int height, bool bgra)
    {
        var rgba = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            rgba[i] = bgra ? pixels[i + 2] : pixels[i];
            rgba[i + 1] = pixels[i + 1];
            rgba[i + 2] = bgra ? pixels[i] : pixels[i + 2];
            rgba[i + 3] = 255;
        }

        return new PixelImage(width, height, rgba);
    }

    public byte[] ToPng()
    {
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(Rgba, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var image = SKImage.FromPixels(info, handle.AddrOfPinnedObject(), Width * 4);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    public void SavePng(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ToPng());
    }
}
