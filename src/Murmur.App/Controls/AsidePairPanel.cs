using Avalonia;
using Avalonia.Controls;

namespace Murmur.App.Controls;

/// <summary>
/// The page layout of the Recording canvas (a wrapping flex row): the first child takes the room that is left, the second one
/// is <see cref="SideWidth"/> wide at its right, <see cref="ColumnSpacing"/> apart, while the panel is at least
/// <see cref="WrapBelow"/> wide; below that the side child moves under the first one, <see cref="RowSpacing"/> apart.
/// </summary>
public sealed class AsidePairPanel : Panel
{
    public static readonly StyledProperty<double> SideWidthProperty =
        AvaloniaProperty.Register<AsidePairPanel, double>(nameof(SideWidth), 320);

    public static readonly StyledProperty<double> WrapBelowProperty =
        AvaloniaProperty.Register<AsidePairPanel, double>(nameof(WrapBelow), 872);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<AsidePairPanel, double>(nameof(ColumnSpacing));

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<AsidePairPanel, double>(nameof(RowSpacing));

    static AsidePairPanel()
    {
        AffectsMeasure<AsidePairPanel>(SideWidthProperty, WrapBelowProperty, ColumnSpacingProperty, RowSpacingProperty);
    }

    public double SideWidth
    {
        get => GetValue(SideWidthProperty);
        set => SetValue(SideWidthProperty, value);
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
        var (main, side) = Pair();
        if (main is null)
        {
            return default;
        }

        if (side is null)
        {
            main.Measure(new Size(width, availableSize.Height));
            return new Size(width, main.DesiredSize.Height);
        }

        if (width >= WrapBelow)
        {
            side.Measure(new Size(SideWidth, availableSize.Height));
            main.Measure(new Size(Math.Max(0, width - SideWidth - ColumnSpacing), availableSize.Height));
            return new Size(width, Math.Max(main.DesiredSize.Height, side.DesiredSize.Height));
        }

        main.Measure(new Size(width, availableSize.Height));
        side.Measure(new Size(width, availableSize.Height));
        return new Size(width, main.DesiredSize.Height + RowSpacing + side.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (main, side) = Pair();
        if (main is null)
        {
            return finalSize;
        }

        if (side is null)
        {
            main.Arrange(new Rect(0, 0, finalSize.Width, main.DesiredSize.Height));
            return finalSize;
        }

        if (finalSize.Width >= WrapBelow)
        {
            var mainWidth = Math.Max(0, finalSize.Width - SideWidth - ColumnSpacing);
            main.Arrange(new Rect(0, 0, mainWidth, main.DesiredSize.Height));
            side.Arrange(new Rect(mainWidth + ColumnSpacing, 0, SideWidth, side.DesiredSize.Height));
        }
        else
        {
            main.Arrange(new Rect(0, 0, finalSize.Width, main.DesiredSize.Height));
            side.Arrange(new Rect(0, main.DesiredSize.Height + RowSpacing, finalSize.Width, side.DesiredSize.Height));
        }

        return finalSize;
    }

    private (Control? Main, Control? Side) Pair()
    {
        Control? main = null;
        Control? side = null;
        foreach (var child in Children)
        {
            if (!child.IsVisible)
            {
                continue;
            }

            if (main is null)
            {
                main = child;
            }
            else
            {
                side ??= child;
            }
        }

        return (main, side);
    }
}
