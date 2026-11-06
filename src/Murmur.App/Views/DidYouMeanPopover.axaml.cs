using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Murmur.App.Controls;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

/// <summary>
/// The host of "Did you mean" (see <c>DidYouMeanPopover.axaml</c>). Everything the popover does is in <see cref="DidYouMeanViewModel"/>; this
/// anchors the popup under the word that was clicked (<see cref="TranscriptTextControl.UncertainWordInvokedEvent"/> from inside
/// <see cref="Target"/>), keeps the card <see cref="GutterResource"/> away from the window's edges, moves the focus (to the first chip when
/// it opens, back to the line when it closes) and between the chips with the arrow keys. A scroll by the user closes the popover
/// (<see cref="SessionViewModel.NotifyUserScrolled"/>); while it is open "Follow along" does not move the list.
/// </summary>
public partial class DidYouMeanPopover : UserControl
{
    public static readonly StyledProperty<Control?> TargetProperty =
        AvaloniaProperty.Register<DidYouMeanPopover, Control?>(nameof(Target));

    /// <summary>The space kept between the card and the left and right edges of the window.</summary>
    private const string GutterResource = "Space.16";

    private Control? _returnFocus;
    private bool _fromKeyboard;

    public DidYouMeanPopover()
    {
        InitializeComponent();
        Card.AddHandler(KeyDownEvent, OnCardKeyDown, RoutingStrategies.Bubble);

        // The card grows when the model's chips arrive: it is kept inside the window again.
        Card.SizeChanged += (_, _) => ClampToWindow();
    }

    /// <summary>The element whose transcript lines open the popover (the transcript list).</summary>
    public Control? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    /// <summary>The popup (for tests).</summary>
    internal Avalonia.Controls.Primitives.Popup PopupControl => Popup;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TargetProperty)
        {
            return;
        }

        if (change.OldValue is Control old)
        {
            old.RemoveHandler(TranscriptTextControl.UncertainWordInvokedEvent, OnUncertainWordInvoked);
        }

        if (change.NewValue is Control target)
        {
            target.AddHandler(TranscriptTextControl.UncertainWordInvokedEvent, OnUncertainWordInvoked);
        }
    }

    private void OnUncertainWordInvoked(object? sender, UncertainWordEventArgs e)
    {
        if (e.Source is not Control line)
        {
            return;
        }

        // The view model opens the popover right after this event: by then it is anchored to the word.
        Popup.PlacementTarget = line;
        Popup.PlacementRect = e.WordBounds;
        Popup.HorizontalOffset = 0;
        _returnFocus = line;
        _fromKeyboard = e.FromKeyboard;
    }

    /// <summary>
    /// Moves the card left (or right) so it ends <see cref="GutterResource"/> inside the window: the popup itself would only stay on the
    /// screen, so near the right edge of a window the card would touch it or run past it.
    /// </summary>
    private void ClampToWindow()
    {
        if (!Popup.IsOpen || Popup.PlacementTarget is not Control target || TopLevel.GetTopLevel(target) is not { } top
            || target.TranslatePoint(Popup.PlacementRect?.TopLeft ?? default, top) is not { } anchor)
        {
            return;
        }

        var gutter = this.TryFindResource(GutterResource, out var value) && value is double space ? space : 16;
        var width = Card.Bounds.Width;
        var offset = Math.Min(0, top.ClientSize.Width - gutter - width - anchor.X);
        offset = Math.Max(offset, gutter - anchor.X);
        if (!Popup.HorizontalOffset.Equals(offset))
        {
            Popup.HorizontalOffset = offset;
        }
    }

    private void OnPopupOpened(object? sender, EventArgs e)
    {
        // The chips are in the tree after the layout pass that opens the popup. A click shows no focus ring (Pointer); Enter on the
        // line does (Tab), so the keyboard user sees where they are.
        var method = _fromKeyboard ? NavigationMethod.Tab : NavigationMethod.Pointer;
        Dispatcher.UIThread.Post(
            () =>
            {
                ClampToWindow();
                if (Popup.IsOpen && ChipButtons() is { Count: > 0 } chips)
                {
                    chips[0].Focus(method);
                }
            },
            DispatcherPriority.Input);
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        if (_returnFocus is { } line && line.IsAttachedToVisualTree())
        {
            // A line whose last uncertain word was just fixed is no tab stop any more: then its row takes the focus.
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (!line.Focus(NavigationMethod.Tab))
                    {
                        line.FindAncestorOfType<Button>()?.Focus(NavigationMethod.Tab);
                    }
                },
                DispatcherPriority.Input);
        }
    }

    /// <summary>Left / Up: the chip before; Right / Down: the chip after; Home / End: the first / the last. Enter is the button's own.</summary>
    private void OnCardKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        var chips = ChipButtons();
        if (chips.Count == 0)
        {
            return;
        }

        var current = -1;
        for (var i = 0; i < chips.Count; i++)
        {
            if (chips[i].IsFocused)
            {
                current = i;
                break;
            }
        }

        var next = e.Key switch
        {
            Key.Left or Key.Up => current <= 0 ? 0 : current - 1,
            Key.Right or Key.Down => current < 0 ? 0 : Math.Min(chips.Count - 1, current + 1),
            Key.Home => 0,
            Key.End => chips.Count - 1,
            _ => -2,
        };
        if (next == -2)
        {
            return;
        }

        chips[next].Focus(NavigationMethod.Directional);
        e.Handled = true;
    }

    /// <summary>The chip buttons in order.</summary>
    internal List<Button> ChipButtons()
    {
        var chips = new List<Button>();
        for (var i = 0; i < Chips.ItemCount; i++)
        {
            if (Chips.ContainerFromIndex(i) is { } container && container.FindDescendantOfType<Button>(includeSelf: true) is { } button)
            {
                chips.Add(button);
            }
        }

        return chips;
    }
}
