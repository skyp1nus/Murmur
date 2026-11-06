using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

/// <summary>
/// The keyboard shortcuts of the recording (#61) in one place: which key runs which command, and when the keys are left alone. The
/// <see cref="SessionView"/> listens on its window while it is on screen and asks this class.
/// </summary>
/// <remarks>
/// Space play/pause, J / L seek 5 s back / forward, Up / Down the previous / next line, M a bookmark at the playhead, F follow along, Esc
/// closes the open popover and, when none is open, goes back to the library. Nothing runs with Ctrl, Alt or the Windows key held (Shift is
/// allowed), while a text box has the focus (the search box, a note, a name, the chat), or while the focus is in something that has its own
/// keys: a popup, the export dialog, the "Did you mean" popover. Those mark themselves with <see cref="OwnsKeysProperty"/>.
/// </remarks>
public static class SessionShortcuts
{
    /// <summary>
    /// <c>true</c> on a control (it is inherited by everything inside) that handles keys itself, such as a dialog or a popover: the shortcuts
    /// stay out of it.
    /// </summary>
    public static readonly AttachedProperty<bool> OwnsKeysProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("OwnsKeys", typeof(SessionShortcuts), defaultValue: false, inherits: true);

    public static bool GetOwnsKeys(Control control) => control.GetValue(OwnsKeysProperty);

    public static void SetOwnsKeys(Control control, bool value) => control.SetValue(OwnsKeysProperty, value);

    /// <summary>The element of the window that has the keyboard focus.</summary>
    internal static IInputElement? FocusedElement(Visual view) => TopLevel.GetTopLevel(view)?.FocusManager?.GetFocusedElement();

    /// <summary>
    /// The shortcuts must not run for this key press: a modifier other than Shift is held, a text box has the focus
    /// (<paramref name="textBoxesBlock"/>), or the focus is in a popup or in something that owns its keys.
    /// </summary>
    internal static bool IsBlocked(KeyEventArgs e, Visual view, bool textBoxesBlock)
    {
        if ((e.KeyModifiers & ~KeyModifiers.Shift) != KeyModifiers.None)
        {
            return true;
        }

        var focused = FocusedElement(view);
        if (textBoxesBlock && (e.Source is TextBox || focused is TextBox))
        {
            return true;
        }

        return OwnsOrLeavesWindow(focused, view) || OwnsOrLeavesWindow(e.Source as IInputElement, view);
    }

    /// <summary>The element is in another top level (a popup) or inside something marked <see cref="OwnsKeysProperty"/>.</summary>
    private static bool OwnsOrLeavesWindow(IInputElement? element, Visual view) =>
        element is Visual visual && (TopLevel.GetTopLevel(visual) != TopLevel.GetTopLevel(view) || (visual is Control control && GetOwnsKeys(control)));

    /// <summary>Runs the shortcut of <paramref name="key"/>; <c>false</c> when the key is none of them.</summary>
    internal static bool Run(SessionViewModel viewModel, Key key)
    {
        switch (key)
        {
            case Key.Space:
                viewModel.PlayPauseCommand.Execute(null);
                return true;
            case Key.J:
                viewModel.SeekBackCommand.Execute(null);
                return true;
            case Key.L:
                viewModel.SeekForwardCommand.Execute(null);
                return true;
            case Key.Up:
                viewModel.PreviousLineCommand.Execute(null);
                return true;
            case Key.Down:
                viewModel.NextLineCommand.Execute(null);
                return true;
            case Key.M:
                viewModel.AddBookmarkAtPlayheadCommand.Execute(null);
                return true;
            case Key.F:
                viewModel.ToggleFollowAlongCommand.Execute(null);
                return true;
            case Key.Escape:
                if (!viewModel.CloseOpenPopover())
                {
                    viewModel.BackCommand.Execute(null);
                }

                return true;
            default:
                return false;
        }
    }
}
