using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Murmur.App.ViewModels;

namespace Murmur.App.Views;

/// <summary>
/// The floating recorder orb's window (#46): borderless, transparent, topmost, not in the taskbar, not activated when it is shown,
/// holding the orb, its pill of buttons and the chip "Marked 12:41". Only Avalonia window properties are used here; what Windows
/// does beyond them (a click that activates the window, a Z-order above full-screen apps) is verified on a PC, see the PR.
/// </summary>
/// <remarks>
/// <para>The window is exactly as big as what is shown, so the transparent area never covers other programs: it grows to the left
/// when the pill opens and downwards for the chip. The circle must not move when that happens, so the window remembers where the
/// circle is on the screen (<see cref="OrbAnchor"/>, in pixels) and after every layout moves itself so that the circle is there.</para>
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
    private FloatingOrbViewModel? _watched;
    private Thickness _chipBelowMargin;

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
        LayoutUpdated += (_, _) => KeepCircleInPlace();
    }

    /// <summary>The screens' work areas; the screens of the platform by default (tests supply fake ones).</summary>
    internal Func<IReadOnlyList<WorkArea>> WorkAreaSource { get; set; } = static () => [];

    /// <summary>Where the circle's top-left is on the virtual desktop, in pixels (for tests); meaningful once <see cref="HasAnchor"/>.</summary>
    internal PixelPoint OrbAnchor => _anchor;

    internal bool HasAnchor => _hasAnchor;

    private FloatingOrbViewModel? ViewModel => DataContext as FloatingOrbViewModel;

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
        if (ViewModel?.ResolvePlacement(areas) is { } placement)
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
            return supplied;
        }

        var screens = Screens;
        var list = new List<WorkArea>(screens.ScreenCount);
        var primary = screens.Primary;
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

        IsChipAbove = above;
        Grid.SetRow(Pill, above ? 1 : 0);
        Grid.SetRow(Circle, above ? 1 : 0);
        Grid.SetRow(Chip, above ? 0 : 1);
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

        // Before the first layout: the circle is at the corner of the shadow margin.
        var margin = Root.Margin;
        return ((int)Math.Round(margin.Left * scale), (int)Math.Round(margin.Top * scale));
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
            _ = viewModel.SavePositionAsync(_anchor.X, _anchor.Y, ReadWorkAreas());
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
