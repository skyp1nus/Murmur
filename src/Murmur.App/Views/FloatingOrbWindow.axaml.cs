using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Murmur.App.ViewModels;
using Murmur.Core.Platform;

namespace Murmur.App.Views;

/// <summary>
/// The floating recorder orb's window (#46): borderless, transparent, topmost, not in the taskbar, not activated when it is shown,
/// holding the orb, its pill of buttons and the chip "Marked 12:41". Only Avalonia window properties are used here; what Windows
/// does beyond them (a click that activates the window, a Z-order above full-screen apps) is verified on a PC, see the PR.
/// </summary>
/// <remarks>
/// <para>The window keeps one size while the pill and the chip come and go: <see cref="ReserveSlots"/> gives the pill's column and a
/// chip row above and below the circle their full size even while nothing is shown in them. Opening the pill or the chip then only
/// redraws the content of a window that does not move or resize, so there is nothing to flicker (resizing a transparent window to the
/// left and moving it back in a second call showed a frame with the content in the wrong place, item 21 of the PR's check). The
/// transparent rest of the window must not swallow clicks over other programs: <see cref="InputRegion"/> lists the shapes that are
/// shown (with their shadow margin), and the platform style limits the window's input to them. Should the window still grow (a chip
/// wider than the reserve), it remembers where the circle is on the screen (<see cref="OrbAnchor"/>, in pixels) and after the layout
/// moves itself so that the circle is there.</para>
/// <para>A press on the circle followed by a movement of more than <see cref="FloatingOrbViewModel.DragThreshold"/> DIPs drags the
/// window; the position is saved when the button goes up. A press without that movement is a click, which opens or closes the pill.</para>
/// </remarks>
public partial class FloatingOrbWindow : Window
{
    private PixelPoint _anchor;
    private bool _hasAnchor;
    private PixelPoint _pressAnchor;
    private PixelPoint _pressPoint;
    private bool _pressing;
    private bool _placing;
    private bool _primaryKnown = true;
    private FloatingOrbViewModel? _watched;
    private Thickness _chipBelowMargin;
    private FloatingWindowRect[] _inputRegion = [];

    public FloatingOrbWindow()
    {
        InitializeComponent();
        _chipBelowMargin = Chip.Margin;
        DataContextChanged += (_, _) => Watch(DataContext as FloatingOrbViewModel);
        Closed += (_, _) => Watch(null);
        Circle.PointerPressed += OnCirclePressed;
        Circle.PointerMoved += OnCircleMoved;
        Circle.PointerReleased += OnCircleReleased;
        Circle.PointerCaptureLost += OnCircleCaptureLost;
        LayoutUpdated += (_, _) =>
        {
            KeepCircleInPlace();
            UpdateInputRegion();
        };
    }

    /// <summary>The shapes that take clicks changed (<see cref="InputRegion"/>).</summary>
    internal event EventHandler? InputRegionChanged;

    /// <summary>
    /// The parts of the window that take clicks, in pixels from its top-left: the circle, and the pill and the chip while they are shown,
    /// each with the shadow margin around it. Everything else is transparent and lets clicks through to the window below.
    /// </summary>
    internal ReadOnlySpan<FloatingWindowRect> InputRegion => _inputRegion;

    /// <summary>The width reserved for the pill (with its margin to the circle), in DIPs (for tests).</summary>
    internal double PillSlot => Root.ColumnDefinitions[0].MinWidth;

    /// <summary>The height reserved for the chip above and below the circle (with its gap), in DIPs (for tests).</summary>
    internal double ChipSlot => Root.RowDefinitions[0].MinHeight;

    /// <summary>The screens' work areas; the screens of the platform by default (tests supply fake ones).</summary>
    internal Func<IReadOnlyList<WorkArea>> WorkAreaSource { get; set; } = static () => [];

    /// <summary>Where the circle's top-left is on the virtual desktop, in pixels (for tests); meaningful once <see cref="HasAnchor"/>.</summary>
    internal PixelPoint OrbAnchor => _anchor;

    internal bool HasAnchor => _hasAnchor;

    private FloatingOrbViewModel? ViewModel => DataContext as FloatingOrbViewModel;

    protected override Size MeasureOverride(Size availableSize)
    {
        ReserveSlots();
        return base.MeasureOverride(availableSize);
    }

    /// <summary>
    /// Reserves the full size of the pill and of the chip in the grid whether they are shown or not, so the window's size never depends
    /// on them. Their content is measured even while hidden (a control measures itself unless it is hidden itself; only the pill's and
    /// the chip's own borders are hidden).
    /// </summary>
    private void ReserveSlots()
    {
        var pill = OuterWidth(Pill);
        var chip = OuterWidth(Chip);
        var circle = double.IsNaN(Circle.Width) ? 0 : Circle.Width + Circle.Margin.Left + Circle.Margin.Right;

        // The chip spans both columns: the pill's column also covers a chip wider than the circle.
        var column = Math.Max(pill, chip - circle);
        var chipHeight = double.IsNaN(Chip.Height) ? 0 : Chip.Height;
        var row = chipHeight + _chipBelowMargin.Top + _chipBelowMargin.Bottom;

        var columns = Root.ColumnDefinitions[0];
        if (columns.MinWidth != column)
        {
            columns.MinWidth = column;
        }

        foreach (var index in (ReadOnlySpan<int>)[0, 2])
        {
            var definition = Root.RowDefinitions[index];
            if (definition.MinHeight != row)
            {
                definition.MinHeight = row;
            }
        }
    }

    /// <summary>The width of a border with its content, padding, border and margin, measured even while the border is hidden.</summary>
    private static double OuterWidth(Border border)
    {
        var content = 0.0;
        if (border.Child is { } child)
        {
            child.Measure(Size.Infinity);
            content = child.DesiredSize.Width;
        }

        var width = double.IsNaN(border.Width) ? content + border.Padding.Left + border.Padding.Right + border.BorderThickness.Left + border.BorderThickness.Right : border.Width;
        return width + border.Margin.Left + border.Margin.Right;
    }

    /// <summary>Recomputes <see cref="InputRegion"/> from what is shown now; raises <see cref="InputRegionChanged"/> when it changed.</summary>
    private void UpdateInputRegion()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var scale = RenderScaling;
        var margin = Root.Margin;
        var window = new Rect(Bounds.Size);
        Span<FloatingWindowRect> parts = stackalloc FloatingWindowRect[3];
        var count = 0;
        foreach (var shape in (ReadOnlySpan<Control>)[Circle, Pill, Chip])
        {
            // The shapes are children of Root, which is a child of the window: their bounds add up without any transform.
            if (shape.IsVisible)
            {
                var bounds = new Rect(Root.Bounds.Position + shape.Bounds.Position, shape.Bounds.Size);
                var part = FloatingOrbInputRegion.ToPixels(bounds, margin, window, scale);
                if (part.Width > 0 && part.Height > 0)
                {
                    parts[count++] = part;
                }
            }
        }

        if (parts[..count].SequenceEqual(_inputRegion))
        {
            return;
        }

        _inputRegion = parts[..count].ToArray();
        InputRegionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Shows the window with the circle at its saved spot (the nearest visible one if that is off every screen, the corner of the
    /// primary work area if nothing was saved). Does nothing when it is already shown.
    /// </summary>
    public void ShowOrb()
    {
        if (IsVisible)
        {
            return;
        }

        var areas = ReadWorkAreas();

        // Where the orb was last put in this run wins over the settings (a save may still be pending), and goes through the same
        // check as a saved spot: the screens may have changed since.
        var current = _hasAnchor ? FloatingOrbPlacement.ToSaved(_anchor.X, _anchor.Y, areas) : null;
        if (ViewModel?.ResolvePlacement(areas, current, _primaryKnown) is { } placement)
        {
            _anchor = new PixelPoint(placement.X, placement.Y);
            _hasAnchor = true;
            var (offsetX, offsetY) = CircleOffset();
            Position = new PixelPoint(_anchor.X - offsetX, _anchor.Y - offsetY);
        }

        Show();
    }

    /// <summary>Hides the window (the orb is not needed now); it keeps its place for the next time.</summary>
    public void HideOrb()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    /// <summary>The work areas of the screens, the primary one first.</summary>
    internal IReadOnlyList<WorkArea> ReadWorkAreas()
    {
        var supplied = WorkAreaSource();
        if (supplied.Count > 0)
        {
            _primaryKnown = true;
            return supplied;
        }

        var screens = Screens;
        var list = new List<WorkArea>(screens.ScreenCount);
        var primary = screens.Primary;

        // Without a primary screen the scale of the saved numbers is unknown: nothing saved is used or written, the default spot is kept.
        _primaryKnown = primary is not null;
        if (primary is not null)
        {
            list.Add(ToWorkArea(primary));
        }

        foreach (var screen in screens.All)
        {
            if (!ReferenceEquals(screen, primary))
            {
                list.Add(ToWorkArea(screen));
            }
        }

        return list;
    }

    private static WorkArea ToWorkArea(Screen screen)
    {
        var area = screen.WorkingArea;
        return new WorkArea(area.X, area.Y, area.Width, area.Height, screen.Scaling);
    }

    /// <summary>The chip is under the circle (the mockup), or above it when there is no room below (the orb sits at the bottom of the screen).</summary>
    internal bool IsChipAbove { get; private set; }

    private void Watch(FloatingOrbViewModel? viewModel)
    {
        if (ReferenceEquals(_watched, viewModel))
        {
            return;
        }

        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnViewModelChanged;
        }

        _watched = viewModel;
        if (viewModel is not null)
        {
            viewModel.PropertyChanged += OnViewModelChanged;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FloatingOrbViewModel.IsChipShown) && _watched?.IsChipShown == true)
        {
            PlaceChip();
        }
    }

    /// <summary>
    /// Puts the chip under the circle, or above it when the work area ends before the chip would. The circle stays where it is either way
    /// (<see cref="KeepCircleInPlace"/>).
    /// </summary>
    private void PlaceChip()
    {
        var chipHeight = double.IsNaN(Chip.Height) ? 0 : Chip.Height;
        var need = (_chipBelowMargin.Top + chipHeight) * RenderScaling;
        var room = double.MaxValue;
        if (_hasAnchor)
        {
            foreach (var area in ReadWorkAreas())
            {
                var half = FloatingOrbPlacement.OrbSize * area.Scaling / 2;
                if (area.Contains(_anchor.X + half, _anchor.Y + half))
                {
                    room = area.Bottom - (_anchor.Y + (FloatingOrbPlacement.OrbSize * area.Scaling));
                    break;
                }
            }
        }

        var above = room < need;
        if (above == IsChipAbove)
        {
            return;
        }

        // The circle and the pill stay in the middle row: the chip only changes rows, the window does not move.
        IsChipAbove = above;
        Grid.SetRow(Chip, above ? 0 : 2);
        Chip.Margin = above ? new Thickness(0, 0, 0, _chipBelowMargin.Top) : _chipBelowMargin;
    }

    /// <summary>The circle's top-left in the window, in pixels: where the window has to sit relative to the circle.</summary>
    private (int X, int Y) CircleOffset()
    {
        var scale = RenderScaling;
        if (Circle.IsArrangeValid && Circle.TranslatePoint(default, this) is { } point)
        {
            return ((int)Math.Round(point.X * scale), (int)Math.Round(point.Y * scale));
        }

        // Before the first layout: the circle is right of the pill's slot and under the upper chip slot (the middle row is as high as
        // the circle, which is taller than the pill).
        ReserveSlots();
        var margin = Root.Margin;
        return ((int)Math.Round((margin.Left + PillSlot) * scale), (int)Math.Round((margin.Top + ChipSlot) * scale));
    }

    /// <summary>The pill or the chip appeared or went away and the window changed size: move it so that the circle stays where it was.</summary>
    private void KeepCircleInPlace()
    {
        if (!_hasAnchor || _placing || !IsVisible)
        {
            return;
        }

        var (offsetX, offsetY) = CircleOffset();
        var wanted = new PixelPoint(_anchor.X - offsetX, _anchor.Y - offsetY);
        if (wanted != Position)
        {
            _placing = true;
            try
            {
                Position = wanted;
            }
            finally
            {
                _placing = false;
            }
        }
    }

    private PixelPoint ScreenPoint(PointerEventArgs e) => this.PointToScreen(e.GetPosition(this));

    private void OnCirclePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Circle).Properties.IsLeftButtonPressed || ViewModel is not { } viewModel)
        {
            return;
        }

        e.Pointer.Capture(Circle);
        _pressing = true;
        _pressPoint = ScreenPoint(e);
        _pressAnchor = _anchor;
        viewModel.PointerPressed();
        e.Handled = true;
    }

    private void OnCircleMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressing || ViewModel is not { } viewModel)
        {
            return;
        }

        var now = ScreenPoint(e);
        var deltaX = now.X - _pressPoint.X;
        var deltaY = now.Y - _pressPoint.Y;
        var scale = RenderScaling;
        if (!viewModel.PointerMoved(deltaX / scale, deltaY / scale))
        {
            return;
        }

        _anchor = new PixelPoint(_pressAnchor.X + deltaX, _pressAnchor.Y + deltaY);
        _hasAnchor = true;
        KeepCircleInPlace();
        e.Handled = true;
    }

    private void OnCircleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressing || ViewModel is not { } viewModel)
        {
            return;
        }

        _pressing = false;
        e.Pointer.Capture(null);
        if (viewModel.PointerReleased())
        {
            // Dragged: remember where the circle was left. A failed save is not worth interrupting a call for.
            var areas = ReadWorkAreas();
            if (_primaryKnown)
            {
                _ = viewModel.SavePositionAsync(_anchor.X, _anchor.Y, areas);
            }
        }

        e.Handled = true;
    }

    private void OnCircleCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_pressing)
        {
            _pressing = false;
            ViewModel?.PointerCancelled();
        }
    }
}
