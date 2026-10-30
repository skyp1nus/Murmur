using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;

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
/// <para><b>Search mark.</b> <see cref="HighlightTerm"/> marks every occurrence (any case) like the canvas's <c>&lt;mark&gt;</c>: a filled
/// rounded box with the text in <see cref="MarkForeground"/>, 3 px wider on each side. The room for the box is real: a space
/// (in a size that makes it 3 px wide) goes before and after the word in the laid-out text, so the words after it move as they do in the canvas; the
/// offsets of <see cref="WordStarts"/> and of the pointer are translated, nothing outside this control sees the extra characters.</para>
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

    public static readonly StyledProperty<string?> HighlightTermProperty =
        AvaloniaProperty.Register<TranscriptTextControl, string?>(nameof(HighlightTerm));

    public static readonly StyledProperty<IBrush?> MarkBrushProperty =
        AvaloniaProperty.Register<TranscriptTextControl, IBrush?>(nameof(MarkBrush));

    public static readonly StyledProperty<IBrush?> MarkForegroundProperty =
        AvaloniaProperty.Register<TranscriptTextControl, IBrush?>(nameof(MarkForeground));

    public static readonly StyledProperty<CornerRadius> MarkCornerRadiusProperty =
        AvaloniaProperty.Register<TranscriptTextControl, CornerRadius>(nameof(MarkCornerRadius));

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

    /// <summary>The room around a marked word is a plain space set in a smaller size, so it is exactly <see cref="Padding"/> wide whatever the font has.</summary>
    private const char Pad = ' ';

    private TextLayout? _layout;
    private Pen? _highlightPen;
    private double _layoutWidth;

    // Marked occurrences as (start, length) in Text, and the positions in the laid-out text where a Pad was inserted.
    private (int Start, int Length)[] _marks = [];
    private int[] _pads = [];

    static TranscriptTextControl()
    {
        AffectsMeasure<TranscriptTextControl>(
            TextProperty, FontFamilyProperty, FontSizeProperty, FontWeightProperty, LineHeightProperty, LetterSpacingProperty, HighlightTermProperty);
        AffectsRender<TranscriptTextControl>(
            WordStartsProperty, WordLengthsProperty, ActiveWordProperty, HighlightBrushProperty, HighlightBorderBrushProperty, HighlightCornerRadiusProperty, ForegroundProperty,
            MarkBrushProperty, MarkForegroundProperty, MarkCornerRadiusProperty);
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

    /// <summary>The text to mark (every occurrence, any case); <c>null</c> or empty = nothing is marked.</summary>
    public string? HighlightTerm
    {
        get => GetValue(HighlightTermProperty);
        set => SetValue(HighlightTermProperty, value);
    }

    /// <summary>Fill of a marked word.</summary>
    public IBrush? MarkBrush
    {
        get => GetValue(MarkBrushProperty);
        set => SetValue(MarkBrushProperty, value);
    }

    /// <summary>Colour of the text of a marked word.</summary>
    public IBrush? MarkForeground
    {
        get => GetValue(MarkForegroundProperty);
        set => SetValue(MarkForegroundProperty, value);
    }

    public CornerRadius MarkCornerRadius
    {
        get => GetValue(MarkCornerRadiusProperty);
        set => SetValue(MarkCornerRadiusProperty, value);
    }

    /// <summary>How many occurrences of <see cref="HighlightTerm"/> the laid-out text marks (for tests).</summary>
    internal int MarkCount
    {
        get
        {
            EnsureLayout(Bounds.Width > 0 ? Bounds.Width : double.PositiveInfinity);
            return _marks.Length;
        }
    }

    /// <summary>The layout the control draws at its current width (for the design parity harness).</summary>
    internal TextLayout? CurrentLayout => EnsureLayout(Bounds.Width);

    /// <summary>Where a character of <see cref="Text"/> is in <see cref="CurrentLayout"/> (the marks insert room before it).</summary>
    internal int LaidOutIndex(int position) => ToLaidOut(position);

    /// <summary>The width of the laid-out line, marks included (for tests).</summary>
    internal double MarkWidth
    {
        get
        {
            var layout = EnsureLayout(double.PositiveInfinity);
            return layout?.WidthIncludingTrailingWhitespace ?? 0;
        }
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

        var position = ToOriginal(layout.HitTestPoint(point).TextPosition);

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

            var first = ToLaidOut(starts[active]);
            var last = ToLaidOut(starts[active] + lengths[active] - 1);
            foreach (var rect in layout.HitTestTextRange(first, last - first + 1))
            {
                context.DrawRectangle(highlight, _highlightPen, rect.Inflate(new Thickness(2, 0)), radius, radius);
            }
        }

        if (MarkBrush is { } mark && _marks.Length > 0)
        {
            var markRadius = MarkCornerRadius.TopLeft;
            var markHeight = Math.Round(FontSize * 1.25);
            foreach (var (start, length) in _marks)
            {
                foreach (var rect in layout.HitTestTextRange(ToLaidOut(start), length))
                {
                    // The box of the canvas's <mark> is the content area of the font, centred in the line, plus the padding (the thin spaces).
                    var box = new Rect(rect.X - Padding, rect.Y + ((rect.Height - markHeight) / 2), rect.Width + (2 * Padding), markHeight);
                    context.DrawRectangle(mark, null, box, markRadius, markRadius);
                }
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
            || change.Property == LetterSpacingProperty || change.Property == ForegroundProperty
            || change.Property == HighlightTermProperty || change.Property == MarkForegroundProperty)
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
        var typeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight);
        var (shown, overrides) = Mark(text, typeface);
        _layout = new TextLayout(
            shown,
            typeface,
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
            LetterSpacing,
            0,
            null,
            overrides);
        return _layout;
    }

    /// <summary>Half of the room a marked word gets on each side (the canvas's <c>padding: 0 3px</c>).</summary>
    private const double Padding = 3;

    /// <summary>
    /// Finds the occurrences of <see cref="HighlightTerm"/>, puts a <see cref="Pad"/> before and after each one and gives the word the
    /// <see cref="MarkForeground"/>. Returns the text to lay out and the colour spans (<c>null</c> without marks).
    /// </summary>
    private (string Text, IReadOnlyList<ValueSpan<TextRunProperties>>? Overrides) Mark(string text, Typeface typeface)
    {
        _marks = [];
        _pads = [];
        var term = HighlightTerm;
        if (string.IsNullOrEmpty(term))
        {
            return (text, null);
        }

        List<(int, int)>? marks = null;
        for (var at = 0; at < text.Length;)
        {
            var found = text.IndexOf(term, at, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                break;
            }

            (marks ??= []).Add((found, term.Length));
            at = found + term.Length;
        }

        if (marks is null)
        {
            return (text, null);
        }

        _marks = [.. marks];
        var shown = new System.Text.StringBuilder(text.Length + (2 * marks.Count));
        var pads = new int[2 * marks.Count];
        var copied = 0;
        for (var i = 0; i < marks.Count; i++)
        {
            var (start, length) = marks[i];
            shown.Append(text, copied, start - copied);
            pads[2 * i] = shown.Length;
            shown.Append(Pad).Append(text, start, length);
            pads[(2 * i) + 1] = shown.Length;
            shown.Append(Pad);
            copied = start + length;
        }

        shown.Append(text, copied, text.Length - copied);
        _pads = pads;

        // A space of the font is a fixed share of the size: pick the size at which it (plus the letter spacing) is exactly Padding wide.
        var spaceShare = SpaceShare(typeface);
        var padSize = spaceShare > 0 ? Math.Max(1, (Padding - LetterSpacing) / spaceShare) : FontSize;
        var spans = new List<ValueSpan<TextRunProperties>>((3 * marks.Count) + 1);
        foreach (var pad in pads)
        {
            spans.Add(new ValueSpan<TextRunProperties>(pad, 1, new GenericTextRunProperties(typeface, padSize, null, Foreground)));
        }

        if (MarkForeground is { } foreground)
        {
            foreach (var (start, length) in _marks)
            {
                spans.Add(new ValueSpan<TextRunProperties>(ToLaidOut(start), length, new GenericTextRunProperties(typeface, FontSize, null, foreground)));
            }
        }

        spans.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return (shown.ToString(), spans);
    }

    /// <summary>The advance of a space as a share of the font size (the width of "a a" minus "aa" at 100 px, without letter spacing).</summary>
    private static double SpaceShare(Typeface typeface)
    {
        double Width(string text)
        {
            using var layout = new TextLayout(text, typeface, 100, Brushes.Black, TextAlignment.Left, TextWrapping.NoWrap, TextTrimming.None, null, FlowDirection.LeftToRight, double.PositiveInfinity, double.PositiveInfinity, double.NaN, 0);
            return layout.WidthIncludingTrailingWhitespace;
        }

        return (Width("a a") - Width("aa")) / 100;
    }

    /// <summary>The position in the laid-out text of a character of <see cref="Text"/> (every <see cref="Pad"/> before it counts).</summary>
    private int ToLaidOut(int position)
    {
        var inserted = 0;
        foreach (var pad in _pads)
        {
            // _pads holds laid-out positions; a pad at laid-out position p sits before the character that is at p - (pads before it).
            if (pad - inserted <= position)
            {
                inserted++;
            }
            else
            {
                break;
            }
        }

        return position + inserted;
    }

    /// <summary>The character of <see cref="Text"/> a laid-out position belongs to (a position inside a pad belongs to its word).</summary>
    private int ToOriginal(int laidOut)
    {
        var inserted = 0;
        foreach (var pad in _pads)
        {
            if (pad < laidOut)
            {
                inserted++;
            }
            else
            {
                break;
            }
        }

        return Math.Max(0, laidOut - inserted);
    }
}
