using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Murmur.App.Views;

/// <summary>
/// The row of speaker chips above the transcript (see <c>SpeakerChipsView.axaml</c>). Everything the chips do is in the view models; this
/// only moves the focus: into the name box (text selected) when the popover opens, back to the chip when it closes, so the keyboard
/// path Tab, Enter, type, Enter works without the mouse.
/// </summary>
public partial class SpeakerChipsView : UserControl
{
    public SpeakerChipsView()
    {
        InitializeComponent();
    }

    private void OnPopupOpened(object? sender, System.EventArgs e)
    {
        if (sender is not Popup popup)
        {
            return;
        }

        // The box is in the tree after the layout pass that opens the popup.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (popup.IsOpen && popup.Child?.FindDescendantOfType<TextBox>() is { } box && box.Focus())
                {
                    box.SelectAll();
                }
            },
            DispatcherPriority.Input);
    }

    private void OnPopupClosed(object? sender, System.EventArgs e)
    {
        if (sender is Popup { PlacementTarget: Control chip })
        {
            Dispatcher.UIThread.Post(() => chip.Focus(), DispatcherPriority.Input);
        }
    }
}
