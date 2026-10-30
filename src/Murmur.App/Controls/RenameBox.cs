using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Murmur.App.Controls;

/// <summary>
/// The text box that replaces a sidebar row while it is renamed: when it appears it takes the focus with its text selected, and when it
/// loses the focus it finishes (<see cref="Finished"/>), as Enter does.
/// </summary>
public sealed class RenameBox : TextBox
{
    /// <summary>The box lost the focus: the owner commits what was typed.</summary>
    public event EventHandler? Finished;

    protected override Type StyleKeyOverride => typeof(TextBox);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
        {
            // The box is visible only after the layout pass that shows it.
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (IsVisible && Focus())
                    {
                        SelectAll();
                    }
                },
                DispatcherPriority.Input);
        }
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        if (IsVisible)
        {
            Finished?.Invoke(this, EventArgs.Empty);
        }
    }
}
