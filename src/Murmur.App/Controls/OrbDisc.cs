using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace Murmur.App.Controls;

/// <summary>
/// The still orb of the canvas (wordmark, "Recording" pill, the New recording card, the answer avatar): a disc painted with the
/// stacked radial gradients of <c>orbGradient</c> in <c>docs/design/tokens.json</c>, read from the generated <c>Orb.*</c> resources
/// (colour, centre and fade-out of each layer, and the lavender base). It never animates; the breathing orb of the recording
/// screen is <see cref="OrbControl"/>.
/// </summary>
public sealed class OrbDisc : Control
{
    public static readonly StyledProperty<bool> GlowProperty =
        AvaloniaProperty.Register<OrbDisc, bool>(nameof(Glow));

    /// <summary>Painted bottom to top (CSS lists the top layer first): the order of the layers in tokens.json, reversed.</summary>
    internal static readonly string[] LayersTopFirst = ["Cream", "Violet", "Ember", "Pink"];

    private const double GlowBlur = 60;
    private const byte GlowAlpha = 64;

    private IBrush? _base;
    private IBrush[]? _layers;
    private BoxShadows _halo;

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
        if (_base is null || _layers is null)
        {
            return;
        }

        var rect = new Rect(Bounds.Size);
        var radius = Math.Min(rect.Width, rect.Height) / 2;
        var centre = rect.Center;
        if (Glow && ActualThemeVariant == ThemeVariant.Dark)
        {
            context.DrawRectangle(_base, null, new RoundedRect(new Rect(centre.X - radius, centre.Y - radius, radius * 2, radius * 2), radius), _halo);
        }
        else
        {
            context.DrawEllipse(_base, null, centre, radius, radius);
        }

        // The base first, then the layers from the bottom one to the cream highlight on top.
        for (var i = _layers.Length - 1; i >= 0; i--)
        {
            context.DrawEllipse(_layers[i], null, centre, radius, radius);
        }
    }

    private void Rebuild()
    {
        if (!this.TryFindResource("Orb.Base", ActualThemeVariant, out var baseValue) || baseValue is not Color baseColour)
        {
            _base = null;
            return;
        }

        var layers = new IBrush[LayersTopFirst.Length];
        for (var i = 0; i < layers.Length; i++)
        {
            var name = LayersTopFirst[i];
            if (!Lookup("Orb." + name, out Color colour) || !Lookup("Orb." + name + "X", out double x)
                || !Lookup("Orb." + name + "Y", out double y) || !Lookup("Orb." + name + "Stop", out double stop))
            {
                _base = null;
                return;
            }

            layers[i] = Layer(colour, x, y, stop);
        }

        _layers = layers;
        _base = new ImmutableSolidColorBrush(baseColour);
        if (Lookup("Orb.Violet", out Color violet))
        {
            _halo = new BoxShadows(new BoxShadow { Blur = GlowBlur, Color = Color.FromArgb(GlowAlpha, violet.R, violet.G, violet.B) });
        }

        InvalidateVisual();
    }

    private bool Lookup<T>(string key, out T value)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var found) && found is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>"circle at X% Y%" has the farthest-corner radius: the distance from the centre to the far corner, in box widths.</summary>
    private static IBrush Layer(Color colour, double x, double y, double stop)
    {
        var dx = Math.Max(x, 1 - x);
        var dy = Math.Max(y, 1 - y);
        var farthest = Math.Sqrt((dx * dx) + (dy * dy));
        return new RadialGradientBrush
        {
            Center = new RelativePoint(x, y, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(x, y, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(farthest, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(farthest, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(colour, 0),
                new GradientStop(Color.FromArgb(0, colour.R, colour.G, colour.B), stop),
            },
        }.ToImmutable();
    }
}
