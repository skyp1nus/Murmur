using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Murmur.App.Controls;

/// <summary>
/// The text of one transcript segment, drawn as a single text layout. One control per segment instead of one per
/// word keeps a 10 000-word transcript light: the virtualising list realises only the visible segments, and each
/// of them is one <see cref="TextLayout"/>.
/// </summary>
/// <remarks>
/// <para><b>Highlight.</b> <see cref="ActiveWord"/> is an index into <see cref="WordStarts"/>/<see cref="WordLengths"/>
/// (character offsets inside <see cref="Text"/>). The word gets a filled rounded rectangle behind it
/// (<see cref="HighlightBrush"/>, an achromatic token); only this control repaints when the highlight moves.</para>
/// <para><b>Click.</b> A press is hit-tested against the layout, mapped to the nearest word and passed to
/// <see cref="Command"/> as its parameter (the word index; 0 when the segment has no word timings).</para>
/// </remarks>
public sealed class TranscriptTextControl : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<TranscriptTextControl, string?>(nameof(Text));

    public static readonly StyledProperty<int[]?> WordStartsProperty =
        AvaloniaProperty.Register<TranscriptTextControl, int[]?>(nameof(WordStarts));

    public static readonly StyledProperty<int[]?> WordLengthsProperty =
        AvaloniaProperty.Register<TranscriptTextControl, int[]?>(nameof(WordLengths));

    public static readonly StyledProperty<int> ActiveWordProperty =
        AvaloniaProperty.Register<TranscriptTextControl, int>(nameof(ActiveWord), -1);

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<TranscriptTextControl, ICommand?>(nameof(Command));

    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<TranscriptTextControl, IBrush?>(nameof(HighlightBrush));

    public static readonly StyledProperty<IBrush?> HighlightBorderBrushProperty =
        AvaloniaProperty.Register<TranscriptTextControl, IBrush?>(nameof(HighlightBorderBrush));

    public static readonly StyledProperty<CornerRadius> HighlightCornerRadiusProperty =
        AvaloniaProperty.Register<TranscriptTextControl, CornerRadius>(nameof(HighlightCornerRadius));

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<TranscriptTextControl>();

    public static readonly StyledProperty<FontFamily> FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner<TranscriptTextControl>();

    public static readonly StyledProperty<double> FontSizeProperty = TextElement.FontSizeProperty.AddOwner<TranscriptTextControl>();

    public static readonly StyledProperty<FontWeight> FontWeightProperty = TextElement.FontWeightProperty.AddOwner<TranscriptTextControl>();

    public static readonly StyledProperty<double> LineHeightProperty = TextBlock.LineHeightProperty.AddOwner<TranscriptTextControl>();

    public static readonly StyledProperty<double> LetterSpacingProperty = TextBlock.LetterSpacingProperty.AddOwner<TranscriptTextControl>();

    private TextLayout? _layout;
    private Pen? _highlightPen;
    private double _layoutWidth;

    static TranscriptTextControl()
    {
        AffectsMeasure<TranscriptTextControl>(
            TextProperty, FontFamilyProperty, FontSizeProperty, FontWeightProperty, LineHeightProperty, LetterSpacingProperty);
        AffectsRender<TranscriptTextControl>(
            WordStartsProperty, WordLengthsProperty, ActiveWordProperty, HighlightBrushProperty, HighlightBorderBrushProperty, HighlightCornerRadiusProperty, ForegroundProperty);
    }

    public TranscriptTextControl()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = false;
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Character offset of every word inside <see cref="Text"/>.</summary>
    public int[]? WordStarts
    {
        get => GetValue(WordStartsProperty);
        set => SetValue(WordStartsProperty, value);
    }

    public int[]? WordLengths
    {
        get => GetValue(WordLengthsProperty);
        set => SetValue(WordLengthsProperty, value);
    }

    /// <summary>Index of the highlighted word, or -1.</summary>
    public int ActiveWord
    {
        get => GetValue(ActiveWordProperty);
        set => SetValue(ActiveWordProperty, value);
    }

    /// <summary>Executed with the clicked word index as parameter.</summary>
    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public IBrush? HighlightBrush
    {
        get => GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    /// <summary>Hairline around the highlighted word; <c>null</c> = none.</summary>
    public IBrush? HighlightBorderBrush
    {
        get => GetValue(HighlightBorderBrushProperty);
        set => SetValue(HighlightBorderBrushProperty, value);
    }

    public CornerRadius HighlightCornerRadius
    {
        get => GetValue(HighlightCornerRadiusProperty);
        set => SetValue(HighlightCornerRadiusProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public double LineHeight
    {
        get => GetValue(LineHeightProperty);
        set => SetValue(LineHeightProperty, value);
    }

    public double LetterSpacing
    {
        get => GetValue(LetterSpacingProperty);
        set => SetValue(LetterSpacingProperty, value);
    }

    /// <summary>Maps a point to the nearest word index; 0 when the segment has no word timings, -1 when there is no text.</summary>
    internal int WordAt(Point point)
    {
        var layout = EnsureLayout(Bounds.Width);
        if (layout is null)
        {
            return -1;
        }

        var starts = WordStarts;
        var lengths = WordLengths;
        if (starts is null || lengths is null || starts.Length == 0)
        {
            return 0;
        }

        var position = layout.HitTestPoint(point).TextPosition;

        // Last word that starts at or before the hit character (the space after a word belongs to that word).
        int low = 0, high = starts.Length - 1, found = 0;
        while (low <= high)
        {
            var mid = low + ((high - low) >> 1);
            if (starts[mid] <= position)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width;
        var layout = EnsureLayout(width);
        return layout is null ? default : new Size(double.IsInfinity(width) ? layout.Width : width, layout.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The layout is wrapped for the measured width; rebuild only if the final width differs.
        EnsureLayout(finalSize.Width);
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        // Hit-test surface for the whole row, so a click between lines still selects the nearest word.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));

        var layout = EnsureLayout(Bounds.Width);
        if (layout is null)
        {
            return;
        }

        var starts = WordStarts;
        var lengths = WordLengths;
        var active = ActiveWord;
        if (HighlightBrush is { } highlight && starts is not null && lengths is not null && active >= 0 && active < starts.Length && lengths[active] > 0)
        {
            var radius = HighlightCornerRadius.TopLeft;
            if (HighlightBorderBrush is not { } border)
            {
                _highlightPen = null;
            }
            else if (_highlightPen is null || !ReferenceEquals(_highlightPen.Brush, border))
            {
                _highlightPen = new Pen(border, 1);
            }

            foreach (var rect in layout.HitTestTextRange(starts[active], lengths[active]))
            {
                context.DrawRectangle(highlight, _highlightPen, rect.Inflate(new Thickness(2, 0)), radius, radius);
            }
        }

        layout.Draw(context, default);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var word = WordAt(e.GetPosition(this));
        if (word < 0 || Command is not { } command || !command.CanExecute(word))
        {
            return;
        }

        command.Execute(word);
        e.Handled = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == FontFamilyProperty || change.Property == FontSizeProperty
            || change.Property == FontWeightProperty || change.Property == LineHeightProperty
            || change.Property == LetterSpacingProperty || change.Property == ForegroundProperty)
        {
            _layout?.Dispose();
            _layout = null;
        }
    }

    private TextLayout? EnsureLayout(double width)
    {
        var text = Text;
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (_layout is not null && (_layoutWidth.Equals(width) || (double.IsInfinity(width) && double.IsInfinity(_layoutWidth))))
        {
            return _layout;
        }

        _layout?.Dispose();
        _layoutWidth = width;
        _layout = new TextLayout(
            text,
            new Typeface(FontFamily, FontStyle.Normal, FontWeight),
            FontSize,
            Foreground,
            TextAlignment.Left,
            TextWrapping.Wrap,
            TextTrimming.None,
            null,
            FlowDirection.LeftToRight,
            width,
            double.PositiveInfinity,
            LineHeight,
            LetterSpacing);
        return _layout;
    }
}
