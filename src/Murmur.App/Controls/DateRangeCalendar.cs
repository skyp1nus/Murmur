using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Murmur.App.ViewModels;

namespace Murmur.App.Controls;

/// <summary>
/// The calendar of the Library's "Pick dates": a calendar that selects a range of days and hands it to <see cref="RangeCommand"/>
/// as a <see cref="DateRange"/> (compiled bindings cannot bind the selected-dates collection). The first click sends that one
/// day at once, the second one the whole range and closes the flyout the calendar sits in.
/// </summary>
public sealed class DateRangeCalendar : Calendar
{
    public static readonly StyledProperty<ICommand?> RangeCommandProperty =
        AvaloniaProperty.Register<DateRangeCalendar, ICommand?>(nameof(RangeCommand));

    public DateRangeCalendar()
    {
        SelectionMode = CalendarSelectionMode.SingleRange;
        SelectedDatesChanged += OnSelectedDatesChanged;
    }

    protected override Type StyleKeyOverride => typeof(Calendar);

    /// <summary>Receives a <see cref="DateRange"/> whenever the selection changes to one or two or more days.</summary>
    public ICommand? RangeCommand
    {
        get => GetValue(RangeCommandProperty);
        set => SetValue(RangeCommandProperty, value);
    }

    private void OnSelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        var dates = SelectedDates;
        if (dates.Count == 0)
        {
            return;
        }

        var first = dates[0];
        var last = dates[0];
        foreach (var date in dates)
        {
            first = date < first ? date : first;
            last = date > last ? date : last;
        }

        var range = new DateRange(first, last);
        if (RangeCommand is { } command && command.CanExecute(range))
        {
            command.Execute(range);
        }

        if (dates.Count > 1 && this.FindAncestorOfType<PopupRoot>()?.Parent is Popup popup)
        {
            popup.IsOpen = false;
        }
    }
}
