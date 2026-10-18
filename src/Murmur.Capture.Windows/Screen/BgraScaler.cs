using System.Runtime.CompilerServices;

namespace Murmur.Capture.Windows.Screen;

/// <summary>
/// CPU copy / scale of a 32-bit BGRA picture into an encoder frame for the CPU readback path, following a
/// <see cref="FramePlacement"/>: <see cref="FramePlacement.DirectCopy"/> copies the top-left encoder-size region row by
/// row (alpha passed through: the RGB32 encoder input ignores it); otherwise the source region is scaled bilinearly
/// into the destination rectangle (written opaque) and the rest of the frame is opaque black (letterbox).
/// <para>
/// Column taps are precomputed by the constructor (built once per segment geometry); <see cref="Convert(byte*, int, byte*, int)"/>
/// is allocation-free and blends two 8-bit channels per 32-bit multiply (SWAR, 8-bit weights, rounded). A scale of
/// exactly 1/2 averages 2x2 blocks; below 1/2 bilinear sampling skips source pixels (aliasing), acceptable for the
/// software-encoder fallback this serves. Pure (any OS).
/// </para>
/// </summary>
internal sealed unsafe class BgraScaler
{
    private const uint Opaque = 0xFF00_0000;
    private const uint LaneMask = 0x00FF_00FF;
    private const uint LaneRound = 0x0080_0080;

    private readonly PixelSize _encoder;
    private readonly PixelRect _source;
    private readonly PixelRect _destination;
    private readonly ColumnTap[] _columns = [];

    public BgraScaler(FramePlacement placement, PixelSize encoder)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(encoder.Width, nameof(encoder));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(encoder.Height, nameof(encoder));
        if (placement.Source.Left < 0 || placement.Source.Top < 0 || placement.Source.Width <= 0 || placement.Source.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(placement), placement.Source, "The source region must be a non-empty rectangle at non-negative coordinates.");
        }

        var d = placement.Destination;
        if (!placement.DirectCopy && (d.Left < 0 || d.Top < 0 || d.Width <= 0 || d.Height <= 0 || d.Right > encoder.Width || d.Bottom > encoder.Height))
        {
            throw new ArgumentOutOfRangeException(nameof(placement), d, "The destination rectangle must lie inside the encoder frame.");
        }

        Placement = placement;
        _encoder = encoder;
        _source = placement.Source;
        _destination = d;
        if (placement.DirectCopy)
        {
            if (_source.Width < encoder.Width || _source.Height < encoder.Height)
            {
                throw new ArgumentOutOfRangeException(nameof(placement), placement.Source, "A direct copy needs a source region at least as large as the encoder frame.");
            }

            return;
        }

        _columns = new ColumnTap[d.Width];
        for (var i = 0; i < d.Width; i++)
        {
            Tap(SourceCoordinate(i, _source.Width, d.Width), _source.Width, out var x0, out var x1, out var w);
            _columns[i] = new ColumnTap(_source.Left + x0, _source.Left + x1, w);
        }
    }

    public FramePlacement Placement { get; }

    /// <summary>
    /// Width and height of the source region that is read, counted from the source picture's top-left corner (the
    /// part that must be copied into the readable staging texture).
    /// </summary>
    public PixelSize SourceExtent => Placement.DirectCopy
        ? new PixelSize(_encoder.Width, _encoder.Height)
        : new PixelSize(_source.Right, _source.Bottom);

    /// <summary>Bytes the destination frame needs: <c>(height - 1) * pitch + width * 4</c>.</summary>
    public long RequiredDestinationBytes(int destinationPitch) => ((long)(_encoder.Height - 1) * destinationPitch) + ((long)_encoder.Width * 4);

    /// <summary>Bytes the source picture needs: <c>(rows - 1) * pitch + columns * 4</c> over <see cref="SourceExtent"/>.</summary>
    public long RequiredSourceBytes(int sourcePitch)
    {
        var extent = SourceExtent;
        return ((long)(extent.Height - 1) * sourcePitch) + ((long)extent.Width * 4);
    }

    /// <summary>Span overload of <see cref="Convert(byte*, int, byte*, int)"/> that validates both buffers first.</summary>
    /// <exception cref="ArgumentException">A buffer is too small or a pitch is shorter than a row.</exception>
    public void Convert(ReadOnlySpan<byte> source, int sourcePitch, Span<byte> destination, int destinationPitch)
    {
        var extent = SourceExtent;
        if (sourcePitch < extent.Width * 4 || source.Length < RequiredSourceBytes(sourcePitch))
        {
            throw new ArgumentException($"The source must hold {extent} pixels at pitch {sourcePitch}.", nameof(source));
        }

        if (destinationPitch < _encoder.Width * 4 || destination.Length < RequiredDestinationBytes(destinationPitch))
        {
            throw new ArgumentException($"The destination must hold {_encoder} pixels at pitch {destinationPitch}.", nameof(destination));
        }

        fixed (byte* s = source)
        fixed (byte* d = destination)
        {
            Convert(s, sourcePitch, d, destinationPitch);
        }
    }

    /// <summary>
    /// Writes the whole encoder frame to <paramref name="destination"/> (rows of <paramref name="destinationPitch"/>
    /// bytes, top-down) from <paramref name="source"/> (rows of <paramref name="sourcePitch"/> bytes, top-down, at least
    /// <see cref="SourceExtent"/>). Allocation-free. The caller guarantees both buffers are large enough.
    /// </summary>
    public void Convert(byte* source, int sourcePitch, byte* destination, int destinationPitch)
    {
        if (Placement.DirectCopy)
        {
            var rowBytes = _encoder.Width * 4;
            for (var y = 0; y < _encoder.Height; y++)
            {
                var from = new ReadOnlySpan<byte>(source + ((long)y * sourcePitch), rowBytes);
                from.CopyTo(new Span<byte>(destination + ((long)y * destinationPitch), rowBytes));
            }

            return;
        }

        var d = _destination;
        for (var y = 0; y < _encoder.Height; y++)
        {
            var row = (uint*)(destination + ((long)y * destinationPitch));
            if (y < d.Top || y >= d.Bottom)
            {
                new Span<uint>(row, _encoder.Width).Fill(Opaque);
                continue;
            }

            if (d.Left > 0)
            {
                new Span<uint>(row, d.Left).Fill(Opaque);
            }

            if (d.Right < _encoder.Width)
            {
                new Span<uint>(row + d.Right, _encoder.Width - d.Right).Fill(Opaque);
            }

            Tap(SourceCoordinate(y - d.Top, _source.Height, d.Height), _source.Height, out var y0, out var y1, out var wy);
            var top = (uint*)(source + ((long)(_source.Top + y0) * sourcePitch));
            var bottom = (uint*)(source + ((long)(_source.Top + y1) * sourcePitch));
            var output = row + d.Left;
            fixed (ColumnTap* columns = _columns)
            {
                var end = columns + _columns.Length;
                if (wy == 0)
                {
                    // The row falls on a source row: one horizontal blend per pixel.
                    for (var tap = columns; tap < end; tap++, output++)
                    {
                        *output = Lerp(top[tap->First], top[tap->Second], tap->Weight) | Opaque;
                    }

                    continue;
                }

                for (var tap = columns; tap < end; tap++, output++)
                {
                    var upper = Lerp(top[tap->First], top[tap->Second], tap->Weight);
                    var lower = Lerp(bottom[tap->First], bottom[tap->Second], tap->Weight);
                    *output = Lerp(upper, lower, wy) | Opaque;
                }
            }
        }
    }

    /// <summary>
    /// Per-channel <c>a + (b - a) * w / 256</c>, rounded, for two packed BGRA pixels; <paramref name="w"/> is 0..255.
    /// Each 32-bit multiply carries two 8-bit channels in 16-bit lanes (at most 255 * 256 + 128 per lane: no carry).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Lerp(uint a, uint b, uint w)
    {
        var iw = 256 - w;
        var blueRed = ((((a & LaneMask) * iw) + ((b & LaneMask) * w) + LaneRound) >> 8) & LaneMask;
        var greenAlpha = (((((a >> 8) & LaneMask) * iw) + (((b >> 8) & LaneMask) * w) + LaneRound) >> 8) & LaneMask;
        return blueRed | (greenAlpha << 8);
    }

    /// <summary>
    /// The source coordinate (in source pixels, pixel centres at integers) sampled for destination pixel
    /// <paramref name="index"/> when <paramref name="sourceLength"/> pixels map onto <paramref name="destinationLength"/>.
    /// </summary>
    internal static double SourceCoordinate(int index, int sourceLength, int destinationLength) =>
        ((index + 0.5) * sourceLength / destinationLength) - 0.5;

    /// <summary>The two source taps around <paramref name="coordinate"/> and the weight (0..255) of the second one; clamped to the edges.</summary>
    internal static void Tap(double coordinate, int length, out int first, out int second, out uint weight)
    {
        var last = length - 1;
        if (coordinate <= 0 || last == 0)
        {
            first = second = 0;
            weight = 0;
            return;
        }

        if (coordinate >= last)
        {
            first = second = last;
            weight = 0;
            return;
        }

        var floor = Math.Floor(coordinate);
        first = (int)floor;
        second = first + 1;
        var w = (int)(((coordinate - floor) * 256) + 0.5);
        if (w >= 256)
        {
            first = second;
            w = 0;
        }

        weight = (uint)w;
    }

    /// <summary>The two source columns of one destination column and the weight (0..255) of the second.</summary>
    private readonly record struct ColumnTap(int First, int Second, uint Weight);
}
