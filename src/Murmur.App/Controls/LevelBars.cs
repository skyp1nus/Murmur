namespace Murmur.App.Controls;

/// <summary>How a strip of level bars turns a value into a bar.</summary>
internal enum LevelBarsShape
{
    /// <summary>
    /// The Tracks card (<see cref="LevelHistoryControl"/>): whole-pixel heights from the minimum to the maximum, centred, fully rounded
    /// ends, followed by <see cref="LevelBarsStyle.FutureCount"/> stubs of the minimum height in the second brush.
    /// </summary>
    History,

    /// <summary>
    /// The audio step's meter (<see cref="LevelMeterControl"/>): silence is an idle bar of <see cref="LevelBarsStyle.IdleHeight"/> in the
    /// second brush; a live value is at least <see cref="LevelBarsStyle.MinHeight"/> and grows with the control's height.
    /// </summary>
    Meter,
}

/// <summary>The look of a strip of level bars: how many, how far apart, how tall.</summary>
/// <param name="Shape">How a value becomes a bar.</param>
/// <param name="Count">Bars that show samples (the newest at the right end of them).</param>
/// <param name="FutureCount">Stubs after them (only <see cref="LevelBarsShape.History"/>).</param>
/// <param name="Gap">Space between two bars, px.</param>
/// <param name="MinHeight">The lowest bar (History) or the lowest live bar (Meter), px.</param>
/// <param name="MaxHeight">The highest bar (History; the meter uses its height), px.</param>
/// <param name="IdleHeight">A silent bar of the meter, px.</param>
/// <param name="SampleSeconds">How often a sample normally comes: the strip moves one slot in that time.</param>
internal readonly record struct LevelBarsStyle(
    LevelBarsShape Shape,
    int Count,
    int FutureCount,
    double Gap,
    double MinHeight,
    double MaxHeight,
    double IdleHeight,
    double SampleSeconds)
{
    /// <summary>The bars that are drawn at most in one frame: the samples, the one sliding out, the stubs.</summary>
    public int MaxRects => Count + 1 + FutureCount;
}

/// <summary>One bar of a frame: where it is, how round, which brush and how opaque.</summary>
internal readonly record struct LevelBar(double X, double Y, double Width, double Height, double Radius, bool Second, double Opacity);

/// <summary>
/// The motion of a strip of level bars that moves one slot per sample, as pure maths with no Avalonia types (it is the per-frame code
/// of the render thread, and the benchmarks link this file).
/// </summary>
/// <remarks>
/// <para>
/// The strip keeps one value more than it shows. At rest (<see cref="Position"/> 1) value <c>j</c> sits in slot <c>j - 1</c>: the
/// oldest is just off the left edge and the newest is the last bar, exactly the static frame of the controls. A new sample shifts the
/// values left, so every bar stays where it was (position 0: the newest is one slot to the right, over the first stub or past the
/// right edge), and the strip then slides one slot to the left over one sample interval, at constant speed: it scrolls continuously
/// instead of jumping. The newest bar fades and grows in as it arrives.
/// </para>
/// <para>
/// A sample that comes before the slide is over keeps the distance still to go, so the bars never jump back; a burst (many samples at
/// once: a refill, a design board) or a sample with motion off shows the new values at rest at once. A strip whose bars are all the
/// same holds still: sliding it would show nothing.
/// </para>
/// <para>Allocation-free after construction: no LINQ, no closures, no boxing.</para>
/// </remarks>
internal sealed class LevelBarsMotion
{
    // A sample less than this share of the usual interval after the last one is part of a burst: no slide.
    private const double BurstShare = 0.4;

    // The interval the slide uses follows the samples (a quarter of each new one), within half and twice the usual one.
    private const double PeriodFollow = 0.25;

    private readonly float[] _values;
    private double _lastSample = double.NaN;
    private double _period;
    private double _position = 1;
    private double _positionAt;

    public LevelBarsMotion(LevelBarsStyle style)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(style.Count, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(style.SampleSeconds, 0);
        Style = style;
        _values = new float[style.Count + 1];
        _period = style.SampleSeconds;
    }

    public LevelBarsStyle Style { get; }

    /// <summary>The values, oldest first: one more than <see cref="LevelBarsStyle.Count"/> (the first is the one sliding out).</summary>
    public ReadOnlySpan<float> Values => _values;

    /// <summary>The interval the slide takes now, s.</summary>
    public double PeriodSeconds => _period;

    /// <summary>Shows <paramref name="values"/> (oldest first; the last <see cref="LevelBarsStyle.Count"/> are used) at rest.</summary>
    public void Reset(ReadOnlySpan<float> values, double now)
    {
        Array.Clear(_values);
        var take = Math.Min(values.Length, Style.Count);
        values[^take..].CopyTo(_values.AsSpan(_values.Length - take));
        _position = 1;
        _positionAt = now;
        _lastSample = now;
    }

    /// <summary>A new sample at <paramref name="now"/> (s); it slides in only when <paramref name="animate"/>.</summary>
    public void Push(float value, double now, bool animate)
    {
        var interval = now - _lastSample;
        var burst = double.IsNaN(interval) || interval < Style.SampleSeconds * BurstShare;
        _lastSample = now;
        var position = Position(now);

        _values.AsSpan(1).CopyTo(_values);
        _values[^1] = value;

        if (!animate || burst || AllEqual())
        {
            _position = 1;
            _positionAt = now;
            return;
        }

        if (interval <= Style.SampleSeconds * 2)
        {
            _period += (Math.Max(interval, Style.SampleSeconds / 2) - _period) * PeriodFollow;
        }

        // Everything stays where it is on screen: one slot less to the rest frame. Never more than one slot behind.
        _position = Math.Max(-1, position - 1);
        _positionAt = now;
    }

    /// <summary>Where the strip is at <paramref name="now"/>: 1 at rest, 0 right after a sample, below 0 when samples came early.</summary>
    public double Position(double now) => Math.Min(1, _position + (Math.Max(0, now - _positionAt) / _period));

    /// <summary>The strip is still sliding at <paramref name="now"/>: the render thread wants another frame.</summary>
    public bool IsMoving(double now) => Position(now) < 1;

    /// <summary>How far the newest bar has come in (0..1): a smooth step of the position.</summary>
    public static double Arrival(double position)
    {
        var t = Math.Clamp(position, 0, 1);
        return t * t * (3 - (2 * t));
    }

    /// <summary>Height of a History bar for a value: whole pixels from the minimum to the maximum.</summary>
    public static double HistoryHeight(in LevelBarsStyle style, float value) =>
        Math.Round(style.MinHeight + (Math.Clamp(value, 0f, 1f) * (style.MaxHeight - style.MinHeight)));

    /// <summary>
    /// The bars of the frame at <paramref name="position"/> in a <paramref name="width"/> × <paramref name="height"/> strip, written to
    /// <paramref name="bars"/> (at least <see cref="LevelBarsStyle.MaxRects"/> long); returns how many. <paramref name="idle"/>: every
    /// bar is a stub (a History track that is not recording). Bars wholly outside the strip are left out.
    /// </summary>
    public int Layout(Span<LevelBar> bars, double width, double height, double position, bool idle)
    {
        var style = Style;
        var slots = style.Count + (style.Shape == LevelBarsShape.History ? style.FutureCount : 0);
        if (width <= 0 || height <= 0 || slots <= 0)
        {
            return 0;
        }

        var barWidth = (width - (style.Gap * (slots - 1))) / slots;
        if (style.Shape == LevelBarsShape.Meter)
        {
            barWidth = Math.Max(1, barWidth);
        }
        else if (barWidth <= 0)
        {
            return 0;
        }

        var pitch = barWidth + style.Gap;
        var count = 0;

        // The stubs first: they hold still, and the newest bar arrives over the first of them.
        if (style.Shape == LevelBarsShape.History)
        {
            for (var i = idle ? 0 : style.Count; i < slots; i++)
            {
                bars[count++] = new LevelBar(i * pitch, (height - style.MinHeight) / 2, barWidth, style.MinHeight, barWidth / 2, Second: true, Opacity: 1);
            }

            if (idle)
            {
                return count;
            }
        }

        var shift = Math.Clamp(position, -1, 1);
        var arrival = Arrival(position);
        var last = _values.Length - 1;
        for (var j = 0; j <= last; j++)
        {
            var x = (j - shift) * pitch;
            if (x + barWidth <= 0 || x >= width)
            {
                continue;
            }

            var newest = j == last;
            var value = _values[j];
            double barHeight;
            bool second;
            double radius;
            if (style.Shape == LevelBarsShape.History)
            {
                barHeight = HistoryHeight(style, value);
                if (newest && arrival < 1)
                {
                    barHeight = style.MinHeight + ((barHeight - style.MinHeight) * arrival);
                }

                second = false;
                radius = barWidth / 2;
            }
            else
            {
                second = !(value > 0f);
                barHeight = second ? style.IdleHeight : Math.Max(style.MinHeight, value * height);
                radius = Math.Min(barWidth / 2, barHeight / 2);
            }

            // The History's newest bar arrives over a stub, so it fades in; the meter's slides in from beyond the right edge.
            var opacity = newest && style.Shape == LevelBarsShape.History ? arrival : 1;
            bars[count++] = new LevelBar(x, (height - barHeight) / 2, barWidth, barHeight, radius, second, opacity);
        }

        return count;
    }

    private bool AllEqual()
    {
        var first = _values[0];
        for (var i = 1; i < _values.Length; i++)
        {
            if (_values[i] != first)
            {
                return false;
            }
        }

        return true;
    }
}
