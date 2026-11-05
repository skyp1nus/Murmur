using Avalonia;
using Avalonia.Controls;

namespace Murmur.App.Views;

/// <summary>
/// The row of chapter segments between the title and the player (see <c>ChapterStripView.axaml</c>). Segments are as wide as the others;
/// when a dozen of them leave less than <see cref="CompactWidth"/> each, the strip is "compact" and the segments show the title only.
/// </summary>
public partial class ChapterStripView : UserControl
{
    /// <summary>A segment narrower than this (device-independent pixels) hides its start time; the title is the last thing to give up room.</summary>
    internal const double CompactWidth = 56;

    public ChapterStripView()
    {
        InitializeComponent();
        Strip.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty || e.Property == ItemsControl.ItemCountProperty)
            {
                UpdateCompact();
            }
        };
    }

    private void UpdateCompact()
    {
        var count = Strip.ItemCount;
        if (count == 0 || Strip.Bounds.Width <= 0)
        {
            return;
        }

        var gap = this.TryFindResource("Space.8", ActualThemeVariant, out var value) && value is double spacing ? spacing : 0;
        var segment = (Strip.Bounds.Width - (gap * (count - 1))) / count;
        Strip.Classes.Set("compact", segment < CompactWidth);
    }
}
