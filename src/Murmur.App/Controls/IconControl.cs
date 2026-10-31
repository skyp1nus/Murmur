using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Murmur.App.Controls;

/// <summary>
/// A line icon drawn from a 24 x 24 geometry (the grid of the mockups' SVG icons) with a round 1.6 stroke, scaled to the
/// size of the control. It strokes with the inherited <see cref="TextElement.ForegroundProperty"/>, so inside a button it
/// follows the button's hover and selected colours without any binding.
/// </summary>
public sealed class IconControl : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<IconControl, Geometry?>(nameof(Data));

    /// <summary>Width of the line in the 24 x 24 grid; the mockups use 1.6 for most icons and 2 to 2.2 for check marks.</summary>
    public static readonly StyledProperty<double> StrokeProperty =
        AvaloniaProperty.Register<IconControl, double>(nameof(Stroke), DefaultStroke);

    /// <summary>The outline is filled with the foreground instead of stroked (the play triangle of a time chip).</summary>
    public static readonly StyledProperty<bool> IsFilledProperty =
        AvaloniaProperty.Register<IconControl, bool>(nameof(IsFilled));

    private const double Grid = 24;
    private const double DefaultStroke = 1.6;

    private Pen? _pen;
    private IBrush? _penBrush;

    static IconControl()
    {
        AffectsRender<IconControl>(DataProperty, TextElement.ForegroundProperty, StrokeProperty, IsFilledProperty);
        AffectsMeasure<IconControl>(DataProperty);
    }

    public bool IsFilled
    {
        get => GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    /// <summary>The line width in the 24 x 24 grid (default 1.6).</summary>
    public double Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>The outline, in a 24 x 24 coordinate space.</summary>
    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsNaN(Width) ? Grid : Width, double.IsNaN(Height) ? Grid : Height);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var brush = TextElement.GetForeground(this);
        if (Data is not { } geometry || brush is null)
        {
            return;
        }

        if (_pen is null || !ReferenceEquals(_penBrush, brush) || _pen.Thickness != Stroke)
        {
            _penBrush = brush;
            _pen = new Pen(brush, Stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        }

        var scale = Math.Min(Bounds.Width, Bounds.Height) / Grid;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            context.DrawGeometry(IsFilled ? brush : null, IsFilled ? null : _pen, geometry);
        }
    }
}
