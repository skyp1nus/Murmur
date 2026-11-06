using Avalonia;
using Avalonia.Controls;
using Murmur.App.Controls;

namespace Murmur.App.Views;

/// <summary>The drop overlay: fades in over the page while files are dragged over the window; no fade under reduced motion.</summary>
public partial class DropOverlayView : UserControl
{
    public DropOverlayView() => InitializeComponent();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == Motion.ReduceMotionProperty)
        {
            Root.Classes.Set("still", change.GetNewValue<bool>());
        }
    }
}
