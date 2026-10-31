using Avalonia;
using Avalonia.Controls;

namespace Murmur.App.Controls;

/// <summary>
/// Children in equal columns, as many as fit at <see cref="MinColumnWidth"/> each (CSS <c>grid-template-columns: repeat(auto-fit,
/// minmax(min, 1fr))</c>): the two cards of the Library side by side on a wide window, one above the other on a narrow one. The children of a
/// row stretch to the height of the tallest.
/// </summary>
public sealed class AutoFitPanel : Panel
{
    public static readonly StyledProperty<double> MinColumnWidthProperty =
        AvaloniaProperty.Register<AutoFitPanel, double>(nameof(MinColumnWidth), 420);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<AutoFitPanel, double>(nameof(Spacing));

    static AutoFitPanel()
    {
        AffectsMeasure<AutoFitPanel>(MinColumnWidthProperty, SpacingProperty);
    }

    public double MinColumnWidth
    {
        get => GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>How many columns fit in <paramref name="width"/> for <paramref name="count"/> visible children (at least one).</summary>
    internal static int ColumnsFor(double width, double minColumnWidth, double spacing, int count)
    {
        if (count <= 1)
        {
            return 1;
        }

        var fit = (int)Math.Floor((width + spacing) / (minColumnWidth + spacing));
        return Math.Clamp(fit, 1, count);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var visible = VisibleChildren();
        if (visible.Count == 0)
        {
            return default;
        }

        var width = double.IsInfinity(availableSize.Width) ? MinColumnWidth : availableSize.Width;
        var columns = ColumnsFor(width, MinColumnWidth, Spacing, visible.Count);
        var columnWidth = (width - (Spacing * (columns - 1))) / columns;
        var height = 0.0;
        var rows = (visible.Count + columns - 1) / columns;
        for (var row = 0; row < rows; row++)
        {
            var rowHeight = 0.0;
            for (var column = 0; column < columns && (row * columns) + column < visible.Count; column++)
            {
                var child = visible[(row * columns) + column];
                child.Measure(new Size(columnWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }

            height += rowHeight;
        }

        return new Size(width, height + (Spacing * (rows - 1)));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = VisibleChildren();
        if (visible.Count == 0)
        {
            return finalSize;
        }

        var columns = ColumnsFor(finalSize.Width, MinColumnWidth, Spacing, visible.Count);
        var columnWidth = (finalSize.Width - (Spacing * (columns - 1))) / columns;
        var rows = (visible.Count + columns - 1) / columns;
        var top = 0.0;
        for (var row = 0; row < rows; row++)
        {
            var rowHeight = 0.0;
            for (var column = 0; column < columns && (row * columns) + column < visible.Count; column++)
            {
                rowHeight = Math.Max(rowHeight, visible[(row * columns) + column].DesiredSize.Height);
            }

            for (var column = 0; column < columns && (row * columns) + column < visible.Count; column++)
            {
                visible[(row * columns) + column].Arrange(new Rect(column * (columnWidth + Spacing), top, columnWidth, rowHeight));
            }

            top += rowHeight + Spacing;
        }

        return finalSize;
    }

    private List<Control> VisibleChildren()
    {
        var result = new List<Control>(Children.Count);
        foreach (var child in Children)
        {
            if (child.IsVisible)
            {
                result.Add(child);
            }
        }

        return result;
    }
}
