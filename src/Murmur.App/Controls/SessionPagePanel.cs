using Avalonia;
using Avalonia.Controls;

namespace Murmur.App.Controls;

/// <summary>
/// The page of a recording as the canvas lays it out (a wrapping flex page): three children in order, the header, the left column
/// (video and player) and the right column (tabs and text). From <see cref="WrapBelow"/> wide the two columns sit side by side,
/// <see cref="LeftBasis"/> and <see cref="RightBasis"/> wide plus half of the free room each; below that the right column goes under the left
/// one. The right column is as tall as the window leaves room for (<see cref="FillHeight"/>), so its transcript list scrolls inside it and
/// stays virtualised; it is never shorter than <see cref="MinRightHeight"/> (the page itself then scrolls).
/// </summary>
public sealed class SessionPagePanel : Panel
{
    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<SessionPagePanel, double>(nameof(ColumnSpacing));

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<SessionPagePanel, double>(nameof(RowSpacing));

    public static readonly StyledProperty<double> LeftBasisProperty =
        AvaloniaProperty.Register<SessionPagePanel, double>(nameof(LeftBasis), 480);

    public static readonly StyledProperty<double> RightBasisProperty =
        AvaloniaProperty.Register<SessionPagePanel, double>(nameof(RightBasis), 520);

    public static readonly StyledProperty<double> MinRightHeightProperty =
        AvaloniaProperty.Register<SessionPagePanel, double>(nameof(MinRightHeight), 400);

    /// <summary>The height the page may fill (the viewport of the scroll viewer minus its padding); <c>NaN</c> = as tall as the content.</summary>
    public static readonly StyledProperty<double> FillHeightProperty =
        AvaloniaProperty.Register<SessionPagePanel, double>(nameof(FillHeight), double.NaN);

    static SessionPagePanel()
    {
        AffectsMeasure<SessionPagePanel>(ColumnSpacingProperty, RowSpacingProperty, LeftBasisProperty, RightBasisProperty, MinRightHeightProperty, FillHeightProperty);
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

    public double LeftBasis
    {
        get => GetValue(LeftBasisProperty);
        set => SetValue(LeftBasisProperty, value);
    }

    public double RightBasis
    {
        get => GetValue(RightBasisProperty);
        set => SetValue(RightBasisProperty, value);
    }

    public double MinRightHeight
    {
        get => GetValue(MinRightHeightProperty);
        set => SetValue(MinRightHeightProperty, value);
    }

    public double FillHeight
    {
        get => GetValue(FillHeightProperty);
        set => SetValue(FillHeightProperty, value);
    }

    /// <summary>The columns are side by side at this width.</summary>
    private bool IsWide(double width) => width >= LeftBasis + RightBasis + ColumnSpacing;

    private (double Left, double Right) Widths(double width)
    {
        if (!IsWide(width))
        {
            return (width, width);
        }

        var free = width - ColumnSpacing - LeftBasis - RightBasis;
        return (LeftBasis + (free / 2), RightBasis + (free / 2));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (header, left, right) = Parts();
        if (header is null || left is null || right is null)
        {
            return default;
        }

        var width = double.IsInfinity(availableSize.Width) ? LeftBasis + RightBasis + ColumnSpacing : availableSize.Width;
        header.Measure(new Size(width, double.PositiveInfinity));
        var (leftWidth, rightWidth) = Widths(width);
        left.Measure(new Size(leftWidth, double.PositiveInfinity));
        var below = header.DesiredSize.Height + RowSpacing;
        var rightHeight = RightHeight(below, left.DesiredSize.Height);
        right.Measure(new Size(rightWidth, rightHeight));

        var height = IsWide(width)
            ? below + Math.Max(left.DesiredSize.Height, rightHeight)
            : below + left.DesiredSize.Height + RowSpacing + rightHeight;
        return new Size(width, height);
    }

    private double RightHeight(double above, double leftHeight)
    {
        if (double.IsNaN(FillHeight))
        {
            return Math.Max(MinRightHeight, leftHeight);
        }

        return Math.Max(MinRightHeight, FillHeight - above);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (header, left, right) = Parts();
        if (header is null || left is null || right is null)
        {
            return finalSize;
        }

        header.Arrange(new Rect(0, 0, finalSize.Width, header.DesiredSize.Height));
        var top = header.DesiredSize.Height + RowSpacing;
        var (leftWidth, rightWidth) = Widths(finalSize.Width);
        var rightHeight = RightHeight(top, left.DesiredSize.Height);
        if (IsWide(finalSize.Width))
        {
            left.Arrange(new Rect(0, top, leftWidth, left.DesiredSize.Height));
            right.Arrange(new Rect(leftWidth + ColumnSpacing, top, rightWidth, rightHeight));
        }
        else
        {
            left.Arrange(new Rect(0, top, finalSize.Width, left.DesiredSize.Height));
            right.Arrange(new Rect(0, top + left.DesiredSize.Height + RowSpacing, finalSize.Width, rightHeight));
        }

        return finalSize;
    }

    private (Control? Header, Control? Left, Control? Right) Parts()
    {
        Control? header = null;
        Control? left = null;
        Control? right = null;
        foreach (var child in Children)
        {
            if (header is null)
            {
                header = child;
            }
            else if (left is null)
            {
                left = child;
            }
            else
            {
                right ??= child;
            }
        }

        return (header, left, right);
    }
}
