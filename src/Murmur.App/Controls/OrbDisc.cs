using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Murmur.App.Controls;

/// <summary>
/// The still orb of the canvas (wordmark, "Recording" pill, the New recording card, the answer avatar): a disc painted with the
/// five stacked radial gradients of <c>orbGradient</c> in <c>docs/design/tokens.json</c>. Violet and Ember come from the token
/// resources; the cream highlight, the pink and the lavender base are the other three stops of the same gradient (the tokens keep
/// them only inside the <c>orbGradient</c> string). It never animates; the breathing orb of the recording screen is <see cref="OrbControl"/>.
/// </summary>
public sealed class OrbDisc : Control
{
    private static readonly Color Cream = Color.FromRgb(0xFF, 0xF1, 0xE8);
    private static readonly Color Pink = Color.FromRgb(0xF4, 0xA6, 0xC8);
    private static readonly Color Lavender = Color.FromRgb(0xD8, 0xC8, 0xF0);

    public static readonly StyledProperty<bool> GlowProperty =
        AvaloniaProperty.Register<OrbDisc, bool>(nameof(Glow));

    private Color _violetColour;
    private IBrush? _cream;
    private IBrush? _violet;
    private IBrush? _ember;
    private IBrush? _pink;
    private IBrush? _base;

    public OrbDisc()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ActualThemeVariantChanged += (_, _) => Rebuild();
    }

    /// <summary>A soft Violet halo behind the disc on the dark theme (the New recording orb has it there, the light theme none).</summary>
    public bool Glow
    {
        get => GetValue(GlowProperty);
        set => SetValue(GlowProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GlowProperty)
        {
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsNaN(Width) ? 0 : Width, double.IsNaN(Height) ? 0 : Height);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Rebuild();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_base is null)
        {
            return;
        }

        var rect = new Rect(Bounds.Size);
        var radius = Math.Min(rect.Width, rect.Height) / 2;
        var centre = rect.Center;

        // CSS paints the first gradient on top: base first, the cream highlight last.
        if (Glow && ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark)
        {
            var halo = new BoxShadows(new BoxShadow { Blur = 60, Color = Color.FromArgb(64, _violetColour.R, _violetColour.G, _violetColour.B) });
            context.DrawRectangle(_base, null, new RoundedRect(new Rect(centre.X - radius, centre.Y - radius, radius * 2, radius * 2), radius), halo);
        }
        else
        {
            context.DrawEllipse(_base, null, centre, radius, radius);
        }

        context.DrawEllipse(_pink, null, centre, radius, radius);
        context.DrawEllipse(_ember, null, centre, radius, radius);
        context.DrawEllipse(_violet, null, centre, radius, radius);
        context.DrawEllipse(_cream, null, centre, radius, radius);
    }

    private void Rebuild()
    {
        if (!this.TryFindResource("Color.AccentViolet", ActualThemeVariant, out var violetValue) || violetValue is not Color violet ||
            !this.TryFindResource("Color.AccentEmber", ActualThemeVariant, out var emberValue) || emberValue is not Color ember)
        {
            _base = null;
            return;
        }

        // "circle at X% Y%" has the farthest-corner radius: the distance from the centre to the far corner, in box widths.
        _violetColour = violet;
        _cream = Layer(Cream, 0.32, 0.26, 0.30);
        _violet = Layer(violet, 0.74, 0.70, 0.58);
        _ember = Layer(ember, 0.20, 0.80, 0.55);
        _pink = Layer(Pink, 0.72, 0.20, 0.50);
        _base = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Lavender);
        InvalidateVisual();
    }

    private static IBrush Layer(Color colour, double x, double y, double stop)
    {
        var farthest = Math.Sqrt((Math.Max(x, 1 - x) * Math.Max(x, 1 - x)) + (Math.Max(y, 1 - y) * Math.Max(y, 1 - y)));
        return new RadialGradientBrush
        {
            Center = new RelativePoint(x, y, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(x, y, RelativeUnit.Relative),
            RadiusX = RelativeScalar.Parse(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{farthest * 100}%")),
            RadiusY = RelativeScalar.Parse(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{farthest * 100}%")),
            GradientStops =
            {
                new GradientStop(colour, 0),
                new GradientStop(Color.FromArgb(0, colour.R, colour.G, colour.B), stop),
            },
        }.ToImmutable();
    }
}
