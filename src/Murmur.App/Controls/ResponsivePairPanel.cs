using Avalonia;
using Avalonia.Controls;

namespace Murmur.App.Controls;

/// <summary>
/// Two children side by side (the first one <see cref="LeftWidth"/> wide) while the panel is at least
/// <see cref="WrapBelow"/> wide, one above the other otherwise: the "title and explanation | content" rows of the settings
/// mockups, which wrap in the HTML.
/// </summary>
public sealed class ResponsivePairPanel : Panel
{
    public static readonly StyledProperty<double> LeftWidthProperty =
        AvaloniaProperty.Register<ResponsivePairPanel, double>(nameof(LeftWidth), 220);

    public static readonly StyledProperty<double> WrapBelowProperty =
        AvaloniaProperty.Register<ResponsivePairPanel, double>(nameof(WrapBelow), 720);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<ResponsivePairPanel, double>(nameof(ColumnSpacing));

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<ResponsivePairPanel, double>(nameof(RowSpacing));

    static ResponsivePairPanel()
    {
        AffectsMeasure<ResponsivePairPanel>(LeftWidthProperty, WrapBelowProperty, ColumnSpacingProperty, RowSpacingProperty);
    }

    public double LeftWidth
    {
        get => GetValue(LeftWidthProperty);
        set => SetValue(LeftWidthProperty, value);
    }

    public double WrapBelow
    {
        get => GetValue(WrapBelowProperty);
        set => SetValue(WrapBelowProperty, value);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? WrapBelow : availableSize.Width;
        var (first, second) = Pair();
        if (first is null)
        {
            return default;
        }

        if (second is null)
        {
            first.Measure(new Size(width, availableSize.Height));
            return first.DesiredSize;
        }

        if (width >= WrapBelow)
        {
            first.Measure(new Size(LeftWidth, availableSize.Height));
            second.Measure(new Size(Math.Max(0, width - LeftWidth - ColumnSpacing), availableSize.Height));
            return new Size(width, Math.Max(first.DesiredSize.Height, second.DesiredSize.Height));
        }

        first.Measure(new Size(width, availableSize.Height));
        second.Measure(new Size(width, availableSize.Height));
        return new Size(width, first.DesiredSize.Height + RowSpacing + second.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (first, second) = Pair();
        if (first is null)
        {
            return finalSize;
        }

        if (second is null)
        {
            first.Arrange(new Rect(finalSize));
            return finalSize;
        }

        if (finalSize.Width >= WrapBelow)
        {
            first.Arrange(new Rect(0, 0, LeftWidth, finalSize.Height));
            var left = LeftWidth + ColumnSpacing;
            second.Arrange(new Rect(left, 0, Math.Max(0, finalSize.Width - left), finalSize.Height));
        }
        else
        {
            first.Arrange(new Rect(0, 0, finalSize.Width, first.DesiredSize.Height));
            var top = first.DesiredSize.Height + RowSpacing;
            second.Arrange(new Rect(0, top, finalSize.Width, Math.Max(0, finalSize.Height - top)));
        }

        return finalSize;
    }

    private (Control? First, Control? Second) Pair()
    {
        Control? first = null;
        Control? second = null;
        foreach (var child in Children)
        {
            if (!child.IsVisible)
            {
                continue;
            }

            if (first is null)
            {
                first = child;
            }
            else
            {
                second ??= child;
            }
        }

        return (first, second);
    }
}
