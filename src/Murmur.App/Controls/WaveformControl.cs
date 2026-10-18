using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Murmur.App.Controls;

/// <summary>
/// Waveform overview of a session with a playhead. Click or drag seeks.
/// </summary>
/// <remarks>
/// <para><b>One cached geometry.</b> The bars are one <see cref="StreamGeometry"/> built from the peaks
/// (<see cref="Murmur.Core.Audio.PeaksFile"/>, 50 values per second) and rebuilt only when the peaks or the size change.
/// A playhead update at 30 Hz only draws the cached geometry twice (idle colour, then the played part under a clip)
/// and one line; no geometry or pen is allocated per frame.</para>
/// <para><b>Seeking.</b> <see cref="ScrubStarted"/>, <see cref="Scrubbed"/> and <see cref="ScrubCompleted"/> carry the pointer
/// position as a fraction of the width; the owner decides what to do with it.</para>
/// <para>Colours come from styled properties set by the control style (design tokens), none are hard-coded here.</para>
/// </remarks>
public sealed class WaveformControl : Control
{
    public static readonly StyledProperty<byte[]?> PeaksProperty =
        AvaloniaProperty.Register<WaveformControl, byte[]?>(nameof(Peaks));

    /// <summary>Playhead position in [0, 1].</summary>
    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<WaveformControl, double>(nameof(Fraction));

    public static readonly StyledProperty<IBrush?> WaveBrushProperty =
        AvaloniaProperty.Register<WaveformControl, IBrush?>(nameof(WaveBrush));

    public static readonly StyledProperty<IBrush?> PlayedBrushProperty =
        AvaloniaProperty.Register<WaveformControl, IBrush?>(nameof(PlayedBrush));

    public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
        AvaloniaProperty.Register<WaveformControl, IBrush?>(nameof(PlayheadBrush));

    private const double BarWidth = 2;
    private const double BarGap = 1;
    private const double MinBarHeight = 2;

    private StreamGeometry? _geometry;
    private Size _geometrySize;
    private byte[]? _geometryPeaks;
    private Pen? _playheadPen;
    private IBrush? _playheadPenBrush;
    private bool _dragging;

    static WaveformControl()
    {
        AffectsRender<WaveformControl>(PeaksProperty, FractionProperty, WaveBrushProperty, PlayedBrushProperty, PlayheadBrushProperty);
        AffectsMeasure<WaveformControl>(PeaksProperty);
    }

    public WaveformControl()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = false;
    }

    /// <summary>Raised when a click or drag starts, with the pointer position as a fraction of the width.</summary>
    public event Action<double>? ScrubStarted;

    /// <summary>Raised while dragging.</summary>
    public event Action<double>? Scrubbed;

    /// <summary>Raised when the pointer is released.</summary>
    public event Action<double>? ScrubCompleted;

    public byte[]? Peaks
    {
        get => GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public IBrush? WaveBrush
    {
        get => GetValue(WaveBrushProperty);
        set => SetValue(WaveBrushProperty, value);
    }

    public IBrush? PlayedBrush
    {
        get => GetValue(PlayedBrushProperty);
        set => SetValue(PlayedBrushProperty, value);
    }

    public IBrush? PlayheadBrush
    {
        get => GetValue(PlayheadBrushProperty);
        set => SetValue(PlayheadBrushProperty, value);
    }

    /// <summary>How many times the geometry was rebuilt (for tests: must not grow with the playhead).</summary>
    internal int GeometryBuilds { get; private set; }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        // Hit-test surface for the whole control, also where no bar is drawn.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(size));

        var geometry = EnsureGeometry(size);
        if (WaveBrush is { } wave)
        {
            context.DrawGeometry(wave, null, geometry);
        }

        var x = Math.Clamp(Fraction, 0, 1) * size.Width;
        if (PlayedBrush is { } played && x > 0)
        {
            using (context.PushClip(new Rect(0, 0, x, size.Height)))
            {
                context.DrawGeometry(played, null, geometry);
            }
        }

        if (PlayheadBrush is { } playhead)
        {
            if (!ReferenceEquals(playhead, _playheadPenBrush))
            {
                _playheadPenBrush = playhead;
                _playheadPen = new Pen(playhead, 1);
            }

            var lineX = Math.Clamp(x, 0.5, Math.Max(0.5, size.Width - 0.5));
            context.DrawLine(_playheadPen!, new Point(lineX, 0), new Point(lineX, size.Height));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Bounds.Width <= 0)
        {
            return;
        }

        e.Pointer.Capture(this);
        _dragging = true;
        var fraction = FractionAt(e);
        ScrubStarted?.Invoke(fraction);
        Scrubbed?.Invoke(fraction);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            Scrubbed?.Invoke(FractionAt(e));
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        e.Pointer.Capture(null);
        ScrubCompleted?.Invoke(FractionAt(e));
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_dragging)
        {
            _dragging = false;
            ScrubCompleted?.Invoke(Math.Clamp(Fraction, 0, 1));
        }
    }

    private double FractionAt(PointerEventArgs e) =>
        Bounds.Width <= 0 ? 0 : Math.Clamp(e.GetPosition(this).X / Bounds.Width, 0, 1);

    private StreamGeometry EnsureGeometry(Size size)
    {
        var peaks = Peaks;
        if (_geometry is not null && _geometrySize == size && ReferenceEquals(_geometryPeaks, peaks))
        {
            return _geometry;
        }

        GeometryBuilds++;
        _geometrySize = size;
        _geometryPeaks = peaks;
        _geometry = Build(peaks, size);
        return _geometry;
    }

    /// <summary>
    /// Mirrored bars, <see cref="BarWidth"/> wide with <see cref="BarGap"/> between them. A bar shows the mean of the
    /// maximum and the average of its slice of peaks (the plain maximum turns a one-hour recording into a solid block),
    /// scaled so the loudest bar fills the control.
    /// </summary>
    private static StreamGeometry Build(byte[]? peaks, Size size)
    {
        var columns = Math.Max(1, (int)((size.Width + BarGap) / (BarWidth + BarGap)));
        var count = peaks?.Length ?? 0;
        var levels = new double[columns];
        var loudest = 0.0;
        for (var column = 0; column < columns && count > 0; column++)
        {
            var from = (int)((long)column * count / columns);
            var to = Math.Min(count, Math.Max(from + 1, (int)((long)(column + 1) * count / columns)));
            var max = 0;
            long sum = 0;
            for (var i = from; i < to; i++)
            {
                var value = peaks![i];
                max = Math.Max(max, value);
                sum += value;
            }

            var level = (max + ((double)sum / (to - from))) / 2;
            levels[column] = level;
            loudest = Math.Max(loudest, level);
        }

        var scale = loudest > 0 ? 1.0 / loudest : 0;
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        var centre = size.Height / 2;
        for (var column = 0; column < columns; column++)
        {
            var half = Math.Max(MinBarHeight, levels[column] * scale * size.Height) / 2;
            var left = column * (BarWidth + BarGap);
            context.BeginFigure(new Point(left, centre - half), true);
            context.LineTo(new Point(left + BarWidth, centre - half));
            context.LineTo(new Point(left + BarWidth, centre + half));
            context.LineTo(new Point(left, centre + half));
            context.EndFigure(true);
        }

        return geometry;
    }
}
