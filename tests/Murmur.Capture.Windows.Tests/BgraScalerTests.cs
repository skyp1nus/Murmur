using System.Buffers.Binary;
using System.Diagnostics;
using Murmur.Capture.Windows.Screen;

namespace Murmur.Capture.Windows.Tests;

/// <summary>CPU copy / bilinear scale of the CPU readback path (pure, any OS).</summary>
public sealed class BgraScalerTests(ITestOutputHelper output)
{
    private const uint Black = 0xFF00_0000;

    [Fact]
    public void DirectCopy_CopiesTheTopLeftEncoderRegion_HonouringBothPitches()
    {
        var encoder = new PixelSize(6, 4);
        var placement = EncoderGeometry.Place(new PixelSize(7, 5), new PixelSize(7, 5), encoder);
        Assert.True(placement.DirectCopy);
        var scaler = new BgraScaler(placement, encoder);
        Assert.Equal(encoder, scaler.SourceExtent);

        var source = Picture(7, 5, pitch: (7 * 4) + 12, (x, y) => (uint)(0x0100_0000 * (x + 1)) | (uint)((y << 8) | x));
        var destination = new byte[(6 * 4 * 4) + 8];
        scaler.Convert(source.Bytes, source.Pitch, destination, destinationPitch: 6 * 4);

        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 6; x++)
            {
                Assert.Equal(source[x, y], Pixel(destination, 6 * 4, x, y));
            }
        }

        Assert.All(destination[^8..], b => Assert.Equal(0, b)); // nothing written past the frame
    }

    [Fact]
    public void Scale_UniformPicture_StaysUniform_AndLetterboxesWithOpaqueBlack()
    {
        // 4:3 content into a 16:9 encoder frame: pillarbox. Place(): scale 0.6 -> 240x180 at x = 40.
        var encoder = new PixelSize(320, 180);
        var placement = EncoderGeometry.Place(new PixelSize(400, 300), new PixelSize(400, 300), encoder);
        Assert.False(placement.DirectCopy);
        Assert.Equal(new PixelRect(40, 0, 280, 180), placement.Destination);
        var scaler = new BgraScaler(placement, encoder);

        var source = Picture(400, 300, pitch: 400 * 4, (_, _) => 0x8011_2233);
        var destination = new byte[320 * 180 * 4];
        scaler.Convert(source.Bytes, source.Pitch, destination, 320 * 4);

        for (var y = 0; y < 180; y++)
        {
            for (var x = 0; x < 320; x++)
            {
                var expected = x is >= 40 and < 280 ? 0xFF11_2233 : Black;
                Assert.Equal(expected, Pixel(destination, 320 * 4, x, y));
            }
        }
    }

    [Fact]
    public void Scale_ByExactlyOneHalf_AveragesTwoByTwoBlocks()
    {
        var encoder = new PixelSize(4, 4);
        var placement = EncoderGeometry.Place(new PixelSize(8, 8), new PixelSize(8, 8), encoder);
        Assert.Equal(new PixelRect(0, 0, 4, 4), placement.Destination);
        var scaler = new BgraScaler(placement, encoder);
        var random = new Random(1234);
        var source = Picture(8, 8, pitch: (8 * 4) + 4, (_, _) => (uint)random.Next() | 0xFF00_0000);
        var destination = new byte[4 * 4 * 4];

        scaler.Convert(source.Bytes, source.Pitch, destination, 4 * 4);

        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var actual = Pixel(destination, 16, x, y);
                for (var shift = 0; shift < 24; shift += 8)
                {
                    var sum = Channel(source[2 * x, 2 * y], shift) + Channel(source[(2 * x) + 1, 2 * y], shift)
                        + Channel(source[2 * x, (2 * y) + 1], shift) + Channel(source[(2 * x) + 1, (2 * y) + 1], shift);
                    Assert.InRange(Channel(actual, shift), (sum / 4.0) - 1, (sum / 4.0) + 1);
                }

                Assert.Equal(0xFFu, actual >> 24);
            }
        }
    }

    [Fact]
    public void Scale_TheCiDisplayGeometry_KeepsAHorizontalRampMonotonic()
    {
        // windows-latest: 1024x768 display, MaxHeight 720 -> 960x720 encoder frame (the case that needs scaling).
        var encoder = EncoderGeometry.FitEncoderSize(1024, 768, 720);
        Assert.Equal(new PixelSize(960, 720), encoder);
        var placement = EncoderGeometry.Place(new PixelSize(1024, 768), new PixelSize(1024, 768), encoder);
        Assert.Equal(new PixelRect(0, 0, 960, 720), placement.Destination);
        var scaler = new BgraScaler(placement, encoder);
        var source = Picture(1024, 768, pitch: 1024 * 4, (x, _) => 0xFF00_0000 | (uint)(x * 255 / 1023) << 16);
        var destination = new byte[960 * 720 * 4];

        scaler.Convert(source.Bytes, source.Pitch, destination, 960 * 4);

        foreach (var y in (int[])[0, 359, 719])
        {
            var previous = -1;
            for (var x = 0; x < 960; x++)
            {
                var red = (int)Channel(Pixel(destination, 960 * 4, x, y), 16);
                Assert.True(red >= previous, $"ramp decreases at ({x}, {y})");
                previous = red;
            }

            Assert.InRange(Channel(Pixel(destination, 960 * 4, 0, y), 16), 0u, 1u);
            Assert.InRange(Channel(Pixel(destination, 960 * 4, 959, y), 16), 254u, 255u);
        }
    }

    [Fact]
    public void Scale_Upwards_StaysInsideTheSource_AndBetweenItsValues()
    {
        // A capture that shrank below the encoder size mid-segment is scaled up (2x2 -> 4x4).
        var encoder = new PixelSize(4, 4);
        var placement = EncoderGeometry.Place(new PixelSize(2, 2), new PixelSize(2, 2), encoder);
        var scaler = new BgraScaler(placement, encoder);
        var source = Picture(2, 2, pitch: 8, (x, y) => x + y == 0 ? 0xFF00_0000 : 0xFFFF_FFFF);
        var destination = new byte[4 * 4 * 4];

        scaler.Convert(source.Bytes, source.Pitch, destination, 16);

        Assert.Equal(Black, Pixel(destination, 16, 0, 0)); // the corner keeps the corner value
        Assert.Equal(0xFFFF_FFFFu, Pixel(destination, 16, 3, 3));
        var middle = Pixel(destination, 16, 1, 1);
        Assert.InRange(Channel(middle, 0), 1u, 254u);
    }

    [Fact]
    public void Lerp_IsExactAtTheEnds_AndRoundsTheMiddle()
    {
        var random = new Random(42);
        for (var i = 0; i < 1000; i++)
        {
            var a = (uint)random.Next() ^ ((uint)random.Next() << 1);
            var b = (uint)random.Next() ^ ((uint)random.Next() << 1);
            Assert.Equal(a, BgraScaler.Lerp(a, b, 0));
            Assert.Equal(a, BgraScaler.Lerp(a, a, (uint)random.Next(256)));
        }

        Assert.Equal(0x8080_8080u, BgraScaler.Lerp(0x0000_0000, 0xFFFF_FFFF, 128));
        Assert.Equal(0xFEFE_FEFEu, BgraScaler.Lerp(0x0000_0000, 0xFFFF_FFFF, 255));
        Assert.Equal(0x0102_0304u, BgraScaler.Lerp(0x0204_0608, 0x0000_0000, 128));
    }

    [Theory]
    [InlineData(-0.25, 4, 0, 0, 0u)]
    [InlineData(0.0, 4, 0, 0, 0u)]
    [InlineData(0.5, 4, 0, 1, 128u)]
    [InlineData(1.25, 4, 1, 2, 64u)]
    [InlineData(2.999, 4, 3, 3, 0u)] // the weight rounds to 256: the second tap with weight 0
    [InlineData(3.0, 4, 3, 3, 0u)]
    [InlineData(7.5, 4, 3, 3, 0u)]
    [InlineData(0.7, 1, 0, 0, 0u)]
    public void Tap_ClampsToTheEdges(double coordinate, int length, int first, int second, uint weight)
    {
        BgraScaler.Tap(coordinate, length, out var a, out var b, out var w);
        Assert.Equal((first, second, weight), (a, b, w));
    }

    [Fact]
    public void Constructor_RejectsInconsistentPlacements()
    {
        var encoder = new PixelSize(8, 8);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BgraScaler(new FramePlacement(true, new PixelRect(0, 0, 4, 4), new PixelRect(0, 0, 8, 8)), encoder));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BgraScaler(new FramePlacement(false, new PixelRect(0, 0, 4, 4), new PixelRect(2, 2, 10, 8)), encoder));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BgraScaler(new FramePlacement(false, new PixelRect(0, 0, 0, 4), new PixelRect(0, 0, 8, 8)), encoder));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BgraScaler(new FramePlacement(false, new PixelRect(0, 0, 4, 4), new PixelRect(0, 0, 8, 8)), new PixelSize(0, 8)));
    }

    [Fact]
    public void Convert_RejectsBuffersThatAreTooSmall()
    {
        var encoder = new PixelSize(4, 4);
        var scaler = new BgraScaler(EncoderGeometry.Place(new PixelSize(8, 8), new PixelSize(8, 8), encoder), encoder);
        var source = new byte[8 * 8 * 4];
        Assert.Throws<ArgumentException>(() => scaler.Convert(source, 8 * 4, new byte[(4 * 4 * 4) - 1], 16));
        Assert.Throws<ArgumentException>(() => scaler.Convert(source, 8 * 4, new byte[4 * 4 * 4], 15));
        Assert.Throws<ArgumentException>(() => scaler.Convert(source.AsSpan(1), 8 * 4, new byte[4 * 4 * 4], 16));
        Assert.Throws<ArgumentException>(() => scaler.Convert(source, 31, new byte[4 * 4 * 4], 16));
        Assert.Equal((7L * 32) + 32, scaler.RequiredSourceBytes(32)); // 8 source rows are read
        Assert.Equal((3L * 20) + 16, scaler.RequiredDestinationBytes(20));
    }

    [Fact]
    public void Convert_DoesNotAllocate()
    {
        var encoder = new PixelSize(960, 720);
        var scaled = new BgraScaler(EncoderGeometry.Place(new PixelSize(1024, 768), new PixelSize(1024, 768), encoder), encoder);
        var direct = new BgraScaler(EncoderGeometry.Place(encoder, encoder, encoder), encoder);
        var source = new byte[1024 * 768 * 4];
        var destination = new byte[960 * 720 * 4];
        scaled.Convert(source, 1024 * 4, destination, 960 * 4);
        direct.Convert(source, 1024 * 4, destination, 960 * 4);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 3; i++)
        {
            scaled.Convert(source, 1024 * 4, destination, 960 * 4);
            direct.Convert(source, 1024 * 4, destination, 960 * 4);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Throughput_IsReported()
    {
        // Not a benchmark gate (shared CI machines are noisy): prints the per-frame cost of the hot path.
        var encoder = new PixelSize(1280, 720);
        var scaler = new BgraScaler(EncoderGeometry.Place(new PixelSize(1920, 1080), new PixelSize(1920, 1080), encoder), encoder);
        var source = new byte[1920 * 1080 * 4];
        Random.Shared.NextBytes(source);
        var destination = new byte[1280 * 720 * 4];
        scaler.Convert(source, 1920 * 4, destination, 1280 * 4);
        const int Frames = 10;
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < Frames; i++)
        {
            scaler.Convert(source, 1920 * 4, destination, 1280 * 4);
        }

        output.WriteLine($"1920x1080 -> 1280x720 bilinear: {stopwatch.Elapsed.TotalMilliseconds / Frames:F2} ms per frame");
    }

    private static uint Pixel(byte[] picture, int pitch, int x, int y) =>
        BinaryPrimitives.ReadUInt32LittleEndian(picture.AsSpan((y * pitch) + (x * 4), 4));

    private static uint Channel(uint pixel, int shift) => (pixel >> shift) & 0xFF;

    private static TestPicture Picture(int width, int height, int pitch, Func<int, int, uint> pixel)
    {
        var picture = new TestPicture(new byte[pitch * height], pitch);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                picture[x, y] = pixel(x, y);
            }
        }

        return picture;
    }

    private sealed record TestPicture(byte[] Bytes, int Pitch)
    {
        public uint this[int x, int y]
        {
            get => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan((y * Pitch) + (x * 4), 4));
            set => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan((y * Pitch) + (x * 4), 4), value);
        }
    }
}
