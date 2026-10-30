using Avalonia;
using Avalonia.Controls;

namespace Murmur.App.Controls;

/// <summary>Gives its child the full width it is offered and the height (whole pixels, rounded down) that makes the shape <see cref="Ratio"/> wide per tall (16:9 for the video frame).</summary>
public sealed class AspectRatioBox : Decorator
{
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<AspectRatioBox, double>(nameof(Ratio), 16.0 / 9.0);

    static AspectRatioBox()
    {
        AffectsMeasure<AspectRatioBox>(RatioProperty);
    }

    /// <summary>Width divided by height.</summary>
    public double Ratio
    {
        get => GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var size = new Size(width, Ratio > 0 ? Math.Floor(width / Ratio) : 0);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
