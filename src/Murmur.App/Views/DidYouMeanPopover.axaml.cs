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
/// <see cref="Target"/>), moves the focus (to the first chip when it opens, back to the line when it closes), moves it between the chips
/// with the arrow keys, and closes the popover when the list is scrolled (the word would leave it behind).
/// </summary>
public partial class DidYouMeanPopover : UserControl
{
    public static readonly StyledProperty<Control?> TargetProperty =
        AvaloniaProperty.Register<DidYouMeanPopover, Control?>(nameof(Target));

    private Control? _returnFocus;

    public DidYouMeanPopover()
    {
        InitializeComponent();
        Card.AddHandler(KeyDownEvent, OnCardKeyDown, RoutingStrategies.Bubble);
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
            if (old is ScrollViewer oldScroller)
            {
                oldScroller.ScrollChanged -= OnTargetScrolled;
            }
        }

        if (change.NewValue is Control target)
        {
            target.AddHandler(TranscriptTextControl.UncertainWordInvokedEvent, OnUncertainWordInvoked);
            if (target is ScrollViewer scroller)
            {
                scroller.ScrollChanged += OnTargetScrolled;
            }
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
        _returnFocus = line;
    }

    private void OnTargetScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (Popup.IsOpen && e.OffsetDelta != default && DataContext is SessionViewModel viewModel)
        {
            viewModel.CloseDidYouMean();
        }
    }

    private void OnPopupOpened(object? sender, EventArgs e)
    {
        // The chips are in the tree after the layout pass that opens the popup.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (Popup.IsOpen && ChipButtons() is { Count: > 0 } chips)
                {
                    chips[0].Focus(NavigationMethod.Tab);
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
