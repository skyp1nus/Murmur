using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Murmur.App.Controls;

/// <summary>
/// The flags of the bookmarks above the waveform (Idea4Transcript.dc.html, "Idea 4 / Waveform with bookmarks"): one <see cref="FlagSize"/> px flag
/// in <see cref="FlagBrush"/> for each <see cref="WaveformFlag"/>, centred over its time. It has the width of the waveform it sits on and uses the
/// waveform's own time-to-x mapping (<see cref="WaveformControl.XOf"/>), so a flag stands exactly over the bars of its moment. A click on a flag
/// raises <see cref="FlagActivated"/> (the owner seeks); the tooltip shows the time in Geist Mono and the note.
/// </summary>
/// <remarks>
/// <b>No allocation per frame.</b> The x of every flag is worked out when the flags or the width change (<see cref="FlagPositions"/>); drawing only
/// walks that array and the one cached icon geometry. The tooltip is built when the pointer moves onto another flag, not when drawing.
/// </remarks>
public sealed class BookmarkFlagsControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<WaveformFlag>?> FlagsProperty =
        AvaloniaProperty.Register<BookmarkFlagsControl, IReadOnlyList<WaveformFlag>?>(nameof(Flags));

    public static readonly StyledProperty<IBrush?> FlagBrushProperty =
        AvaloniaProperty.Register<BookmarkFlagsControl, IBrush?>(nameof(FlagBrush));

    /// <summary>The flag outline on the 24 x 24 grid of the icons (<c>Icon.Mark</c>).</summary>
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<BookmarkFlagsControl, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<double> FlagSizeProperty =
        AvaloniaProperty.Register<BookmarkFlagsControl, double>(nameof(FlagSize), 12);

    private const double Grid = 24;

    private double[] _xs = [];
    private int _hover = -1;

    static BookmarkFlagsControl()
    {
        AffectsRender<BookmarkFlagsControl>(FlagsProperty, FlagBrushProperty, IconProperty, FlagSizeProperty);
    }

    public BookmarkFlagsControl()
    {
        Focusable = false;
    }

    /// <summary>Raised when a flag is clicked, with its index in <see cref="Flags"/>.</summary>
    public event Action<int>? FlagActivated;

    public IReadOnlyList<WaveformFlag>? Flags
    {
        get => GetValue(FlagsProperty);
        set => SetValue(FlagsProperty, value);
    }

    public IBrush? FlagBrush
    {
        get => GetValue(FlagBrushProperty);
        set => SetValue(FlagBrushProperty, value);
    }

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public double FlagSize
    {
        get => GetValue(FlagSizeProperty);
        set => SetValue(FlagSizeProperty, value);
    }

    /// <summary>The x (centre) of every flag in the control, from the waveform's time-to-x mapping; recomputed when the flags or the width change.</summary>
    internal ReadOnlySpan<double> FlagPositions => _xs;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, double.IsNaN(Height) ? FlagSize : 0);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FlagsProperty || change.Property == BoundsProperty)
        {
            Recompute();
        }
    }

    private void Recompute()
    {
        var flags = Flags;
        var width = Bounds.Width;
        var count = flags?.Count ?? 0;
        if (_xs.Length != count)
        {
            _xs = new double[count];
        }

        for (var i = 0; i < count; i++)
        {
            _xs[i] = WaveformControl.XOf(flags![i].Fraction, width);
        }

        _hover = -1;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        // Hit-test surface for the whole band.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(size));
        if (Icon is not { } icon || FlagBrush is not { } brush)
        {
            return;
        }

        var flag = FlagSize;
        var scale = flag / Grid;
        var top = (size.Height - flag) / 2;
        var xs = _xs;
        for (var i = 0; i < xs.Length; i++)
        {
            // Centred over the moment; the first and the last stay inside the band instead of being cut.
            var left = Math.Clamp(xs[i] - (flag / 2), 0, Math.Max(0, size.Width - flag));
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(left, top)))
            {
                context.DrawGeometry(brush, null, icon);
            }
        }
    }

    /// <summary>The flag under <paramref name="x"/> (the nearest one within half a flag plus a margin), or -1.</summary>
    internal int HitTest(double x)
    {
        var reach = FlagSize / 2 + 2;
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < _xs.Length; i++)
        {
            var distance = Math.Abs(x - Math.Clamp(_xs[i], FlagSize / 2, Math.Max(FlagSize / 2, Bounds.Width - (FlagSize / 2))));
            if (distance <= reach && distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>The tooltip of a flag: the time in Geist Mono, then the note.</summary>
    internal Control? TipFor(int index)
    {
        if (Flags is not { } flags || (uint)index >= (uint)flags.Count)
        {
            return null;
        }

        var flag = flags[index];
        var time = new TextBlock { Text = flag.TimeText };
        if (this.TryFindResource("Font.Mono", ActualThemeVariant, out var mono) && mono is FontFamily family)
        {
            time.FontFamily = family;
        }

        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(time);
        if (flag.Note.Length > 0)
        {
            panel.Children.Add(new TextBlock { Text = flag.Note });
        }

        return panel;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var hit = HitTest(e.GetPosition(this).X);
        if (hit == _hover)
        {
            return;
        }

        _hover = hit;
        Cursor = hit >= 0 ? new Cursor(StandardCursorType.Hand) : null;
        ToolTip.SetIsOpen(this, false);
        ToolTip.SetTip(this, hit >= 0 ? TipFor(hit) : null);
        if (hit >= 0)
        {
            ToolTip.SetIsOpen(this, true);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        Cursor = null;
        ToolTip.SetIsOpen(this, false);
        ToolTip.SetTip(this, null);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var hit = HitTest(e.GetPosition(this).X);
        if (hit >= 0)
        {
            FlagActivated?.Invoke(hit);
            e.Handled = true;
        }
    }
}
