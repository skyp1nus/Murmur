using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;

namespace Murmur.App.Tests.Design;

/// <summary>
/// What the comparer reports when the layout is pixel-perfect and only the text rasteriser differs (Chromium for the reference,
/// Skia in Avalonia for the app). Text lines of the Library reference are drawn again by the app's text stack with the same
/// font, size, weight, colour and letter spacing; the best of many sub-pixel positions is the floor a perfect layout still shows.
/// The result goes to <c>calibration.md</c> next to the parity report.
/// </summary>
[Trait("Category", "DesignParity")]
public sealed class ParityCalibrationTests
{
    /// <summary>A piece of the Main artboard: the crop of the reference, what the app draws for it and where the search starts.</summary>
    private sealed record Case(string Name, int X, int Y, int Width, int Height, Func<Control> Build, double OriginX, double OriginY, int Range);

    private static Case Text(string name, string text, int x, int y, int width, int height, double size, int weight, double trackingEm, string foreground, bool mono = false)
    {
        const int Pad = 4;
        return new Case(
            name,
            x - Pad,
            y - Pad,
            width + (2 * Pad),
            height + (2 * Pad),
            () => new TextBlock
            {
                Text = text,
                FontFamily = mono ? new FontFamily("avares://Murmur.App/Assets/Fonts#Geist Mono") : new FontFamily("fonts:Inter#Inter"),
                FontSize = size,
                FontWeight = (FontWeight)weight,
                LetterSpacing = trackingEm * size,
                Foreground = new SolidColorBrush(Color.Parse(foreground)),
                TextWrapping = TextWrapping.NoWrap,
            },
            Pad,
            0,
            0);
    }

    // Boxes are the ink boxes of the lines in docs/design/reference/Main.png; the canvas body has letter-spacing 0.01em.
    private static readonly Case[] Cases =
    [
        Text("Heading 36 px / 300 / -0.02em", "What are we recording today?", 333, 99, 478, 35, 36, 300, -0.02, "#000000"),
        Text("Row title 15 px / 500", "Client demo — Q4 roadmap", 410, 693, 200, 14, 15, 500, 0.01, "#000000"),
        Text("Row meta 13 px / 400", "10:30 · English · Mic, system audio, screen", 410, 714, 265, 13, 13, 400, 0.01, "#777169"),
        Text("Row snippet 13 px / 400", "…PDF export moves to next sprint: first we finish meeting summaries and action items.", 410, 733, 541, 13, 13, 400, 0.01, "#777169"),
        Text("Timestamp, Geist Mono 13 px", "47:12", 1256, 712, 39, 10, 13, 400, 0.01, "#44403b", mono: true),
        new("Card corner: 20 px radius, 1 px hairline", 326, 666, 60, 50, () => new Border { Width = 300, Height = 200, CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.Parse("#ebe8e4")) }, 6, 6, 3),
        new("Black pill, left cap (44 px high)", 520, 396, 22, 52, () => new Border { Width = 143, Height = 44, CornerRadius = new CornerRadius(22), Background = Brushes.Black }, 4, 4, 3),
    ];

    [Fact]
    public async Task TheFloorOfTextRasterisation_IsMeasuredAndReported()
    {
        var reference = PixelImage.LoadPng(DesignBoards.Get("Main").ReferencePath);
        var sb = new StringBuilder();
        sb.Append("# Calibration: what a pixel-perfect layout still shows\n\n");
        sb.Append("Text lines, a card corner and a pill of `Main.png` (Chromium) drawn again by Avalonia/Skia with the same font, size, weight, colour and letter spacing, ")
          .Append("moved over the reference in steps of 0.5 px (x) and 1 px (y); the best position per line. The numbers are the symmetric mismatch ")
          .Append("of the pixels of the text box (padded by 4 px) that are not background in either image, at tolerance 5/255, plus the same at 12 and 24 for comparison.\n\n");
        sb.Append("| Text | Mismatch of drawn % (tol 5) | tol 12 | tol 24 | best offset (x, y) |\n| --- | ---: | ---: | ---: | --- |\n");
        var worst = 0.0;

        foreach (var c in Cases)
        {
            var (best5, best12, best24, offset) = await UiHarness.Session.Dispatch(() => Measure(reference, c), TestContext.Current.CancellationToken);
            worst = Math.Max(worst, best5);
            sb.Append(CultureInfo.InvariantCulture, $"| {c.Name} | {best5:0.0} | {best12:0.0} | {best24:0.0} | {offset} |\n");
        }

        Directory.CreateDirectory(ParityReport.Directory);
        File.WriteAllText(Path.Combine(ParityReport.Directory, "calibration.md"), sb.ToString());
        Assert.True(worst < 100, "The calibration text never matched the reference at all.");
    }

    private static (double Best5, double Best12, double Best24, string Offset) Measure(PixelImage reference, Case c)
    {
        var width = c.Width;
        var height = c.Height;
        var crop = Crop(reference, c.X, c.Y, width, height);
        var background = PixelAt(crop, 0, 0);

        var canvas = new Canvas { Width = width + 40, Height = height + 40, Background = new SolidColorBrush(Color.FromRgb(background.R, background.G, background.B)) };
        var control = c.Build();
        canvas.Children.Add(control);
        var window = new Window
        {
            Width = canvas.Width,
            Height = canvas.Height,
            CanResize = false,
            Content = canvas,
            RequestedThemeVariant = ThemeVariant.Light,
            Background = canvas.Background,
        };
        TextOptions.SetTextRenderingMode(window, TextRenderingMode.Antialias);
        window.Show();
        try
        {
            double best5 = 100;
            PixelImage? bestApp = null;
            double bestX = 0, bestY = 0;

            void Try(double dx, double dy)
            {
                Canvas.SetLeft(control, 20 + c.OriginX + dx);
                Canvas.SetTop(control, 20 + c.OriginY + dy);
                UiHarness.Pump(2);
                var app = Crop(Capture(window), 20, 20, width, height);
                var m5 = ImageComparer.Compare(crop, app, tolerance: 5).Drawn.Percent;
                if (m5 < best5)
                {
                    best5 = m5;
                    bestApp = app;
                    bestX = dx;
                    bestY = dy;
                }
            }

            // Coarse: whole pixels; fine: quarter pixels around the best.
            var (xRange, yFrom, yTo) = c.Range == 0 ? (5, -8, 12) : (c.Range, -c.Range, c.Range);
            for (var dy = yFrom; dy <= yTo; dy++)
            {
                for (var dx = -xRange; dx <= xRange; dx++)
                {
                    Try(dx, dy);
                }
            }

            var (cx, cy) = (bestX, bestY);
            for (var fy = -1.0; fy <= 1.0; fy += 0.25)
            {
                for (var fx = -1.0; fx <= 1.0; fx += 0.25)
                {
                    Try(cx + fx, cy + fy);
                }
            }

            var best12 = ImageComparer.Compare(crop, bestApp!, tolerance: 12).Drawn.Percent;
            var best24 = ImageComparer.Compare(crop, bestApp!, tolerance: 24).Drawn.Percent;
            var sheet = ParityReport.SideBySide(crop, bestApp!, ImageComparer.Compare(crop, bestApp!, tolerance: 5).Diff);
            sheet.SavePng(Path.Combine(ParityReport.Directory, "calibration-" + (Array.IndexOf(Cases, c) + 1) + ".png"));
            return (best5, best12, best24, $"{bestX:0.00}, {bestY:0.00}");
        }
        finally
        {
            window.Close();
        }
    }

    private static PixelImage Capture(Window window)
    {
        using var frame = window.CaptureRenderedFrame()!;
        using var buffer = frame.Lock();
        var w = buffer.Size.Width;
        var h = buffer.Size.Height;
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(buffer.Address + (y * buffer.RowBytes), pixels, y * w * 4, w * 4);
        }

        return PixelImage.FromFrame(pixels, w, h, buffer.Format == Avalonia.Platform.PixelFormat.Bgra8888);
    }

    private static PixelImage Crop(PixelImage source, int x, int y, int width, int height)
    {
        var result = new PixelImage(width, height);
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(source.Rgba, (((y + row) * source.Width) + x) * 4, result.Rgba, row * width * 4, width * 4);
        }

        return result;
    }

    private static (byte R, byte G, byte B) PixelAt(PixelImage image, int x, int y)
    {
        var o = ((y * image.Width) + x) * 4;
        return (image.Rgba[o], image.Rgba[o + 1], image.Rgba[o + 2]);
    }
}
