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

    private const double Grid = 24;
    private const double StrokeWidth = 1.6;

    private Pen? _pen;
    private IBrush? _penBrush;

    static IconControl()
    {
        AffectsRender<IconControl>(DataProperty, TextElement.ForegroundProperty);
        AffectsMeasure<IconControl>(DataProperty);
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

        if (_pen is null || !ReferenceEquals(_penBrush, brush))
        {
            _penBrush = brush;
            _pen = new Pen(brush, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        }

        var scale = Math.Min(Bounds.Width, Bounds.Height) / Grid;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            context.DrawGeometry(null, _pen, geometry);
        }
    }
}
