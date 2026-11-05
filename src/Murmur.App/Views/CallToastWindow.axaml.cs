using Avalonia;
using Avalonia.Controls;

namespace Murmur.App.Views;

/// <summary>
/// The call toast (#44): borderless, topmost, no taskbar entry, shown without taking the focus (Avalonia properties only, no
/// platform calls). It places itself in the bottom-right corner of the primary screen's work area whenever it opens or changes
/// size, so the card stays <see cref="EdgeMargin"/> from both edges.
/// </summary>
public partial class CallToastWindow : Window
{
    /// <summary>Distance of the card from the right and bottom edges of the work area, in DIPs.</summary>
    public const double EdgeMargin = 16;

    public CallToastWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Place();
        SizeChanged += (_, _) => Place();
    }

    /// <summary>
    /// The window position (physical pixels) that puts a card of the window 16 px from the right and bottom of
    /// <paramref name="workArea"/>; <paramref name="shadowRoom"/> is the transparent room between the card and the window's edge.
    /// </summary>
    internal static PixelPoint Placement(PixelRect workArea, double scaling, Size windowSize, Thickness shadowRoom)
    {
        var right = Math.Max(0, EdgeMargin - shadowRoom.Right);
        var bottom = Math.Max(0, EdgeMargin - shadowRoom.Bottom);
        var width = (int)Math.Ceiling((windowSize.Width + right) * scaling);
        var height = (int)Math.Ceiling((windowSize.Height + bottom) * scaling);
        return new PixelPoint(Math.Max(workArea.X, workArea.Right - width), Math.Max(workArea.Y, workArea.Bottom - height));
    }

    private void Place()
    {
        if (Screens.Primary is not { } screen || Bounds.Width <= 0)
        {
            return;
        }

        var room = Card.Margin;
        Position = Placement(screen.WorkingArea, screen.Scaling, Bounds.Size, room);
    }
}
