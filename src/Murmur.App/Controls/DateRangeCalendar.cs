using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Murmur.App.ViewModels;

namespace Murmur.App.Controls;

/// <summary>
/// The calendar of the Library's "Pick dates": a calendar that selects a range of days and hands it to <see cref="RangeCommand"/>
/// as a <see cref="DateRange"/> (compiled bindings cannot bind the selected-dates collection). The first tap sends that one day
/// at once, the second one the whole range (the same day again is a range of one day) and closes the flyout the calendar sits in.
/// <see cref="RangeStart"/> / <see cref="RangeEnd"/> show a range chosen elsewhere; both <c>null</c> clears the selection and closes the flyout.
/// </summary>
public sealed class DateRangeCalendar : Calendar
{
    public static readonly StyledProperty<ICommand?> RangeCommandProperty =
        AvaloniaProperty.Register<DateRangeCalendar, ICommand?>(nameof(RangeCommand));

    public static readonly StyledProperty<DateTime?> RangeStartProperty =
        AvaloniaProperty.Register<DateRangeCalendar, DateTime?>(nameof(RangeStart));

    public static readonly StyledProperty<DateTime?> RangeEndProperty =
        AvaloniaProperty.Register<DateRangeCalendar, DateTime?>(nameof(RangeEnd));

    private bool _syncing;
    private bool _firstDayPicked;
    private bool _selectionChangedInThisTap;

    public DateRangeCalendar()
    {
        SelectionMode = CalendarSelectionMode.SingleRange;
        SelectedDatesChanged += OnSelectedDatesChanged;
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override Type StyleKeyOverride => typeof(Calendar);

    /// <summary>Receives a <see cref="DateRange"/> whenever the user's selection changes to one day or a range.</summary>
    public ICommand? RangeCommand
    {
        get => GetValue(RangeCommandProperty);
        set => SetValue(RangeCommandProperty, value);
    }

    /// <summary>The first day of the range the page filters by, or <c>null</c> when it does not.</summary>
    public DateTime? RangeStart
    {
        get => GetValue(RangeStartProperty);
        set => SetValue(RangeStartProperty, value);
    }

    public DateTime? RangeEnd
    {
        get => GetValue(RangeEndProperty);
        set => SetValue(RangeEndProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RangeStartProperty || change.Property == RangeEndProperty)
        {
            ShowRange();
        }
    }

    private void ShowRange()
    {
        if (RangeStart is not { } start || RangeEnd is not { } end)
        {
            if (RangeStart is null && RangeEnd is null)
            {
                _syncing = true;
                SelectedDates.Clear();
                _syncing = false;
                _firstDayPicked = false;
                Close();
            }

            return;
        }

        if (SelectedDates.Count > 0 && SelectedDates[0].Date == start.Date && SelectedDates[^1].Date == end.Date)
        {
            return;
        }

        _syncing = true;
        SelectedDates.Clear();
        SelectedDates.AddRange(start, end);
        DisplayDate = start;
        _syncing = false;
    }

    private void OnSelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SelectedDates.Count == 0)
        {
            return;
        }

        _selectionChangedInThisTap = true;
        var first = SelectedDates[0];
        var last = SelectedDates[0];
        foreach (var date in SelectedDates)
        {
            first = date < first ? date : first;
            last = date > last ? date : last;
        }

        Send(new DateRange(first, last));
        if (SelectedDates.Count > 1)
        {
            _firstDayPicked = false;
            Close();
        }
        else
        {
            _firstDayPicked = true;
        }
    }

    /// <summary>A new tap starts: whatever an earlier one left in the flag (a selection change without a release) is forgotten.</summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e) => _selectionChangedInThisTap = false;

    /// <summary>
    /// A tap that changes nothing: the same day again after the first one is a range of that day, so the flyout closes. (A calendar
    /// raises no selection event for it.) The tap that made the first selection is skipped.
    /// </summary>
    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_selectionChangedInThisTap)
        {
            _selectionChangedInThisTap = false;
            return;
        }

        if (_firstDayPicked && e.Source is Visual source && source.FindAncestorOfType<CalendarDayButton>(includeSelf: true) is not null)
        {
            _firstDayPicked = false;
            Close();
        }
    }

    private void Send(DateRange range)
    {
        if (RangeCommand is { } command && command.CanExecute(range))
        {
            command.Execute(range);
        }
    }

    private void Close()
    {
        // The flyout's popup is the logical parent of its presenter (a native popup window or the overlay layer: same chain).
        if (this.FindLogicalAncestorOfType<Popup>() is { } popup)
        {
            popup.IsOpen = false;
        }
    }
}
