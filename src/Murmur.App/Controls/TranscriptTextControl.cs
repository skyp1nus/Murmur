using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Utilities;

namespace Murmur.App.Controls;

/// <summary>
/// The text of one transcript segment, drawn as a single text layout. One control per segment instead of one per
/// word keeps a 10 000-word transcript light: the virtualising list realises only the visible segments, and each
/// of them is one <see cref="TextLayout"/>.
/// </summary>
/// <remarks>
/// <para><b>Highlight (follow along).</b> <see cref="ActiveWord"/> is an index into <see cref="WordStarts"/>/<see cref="WordLengths"/>
/// (character offsets inside <see cref="Text"/>). The word gets a filled rounded chip behind it (<see cref="HighlightBrush"/>, an
/// achromatic token) with its text in <see cref="HighlightForeground"/>, and the text after it is set in <see cref="UpcomingForeground"/>,
/// like the active line of Idea4Transcript.dc.html. The chip is drawn <see cref="HighlightPadding"/> beyond the word on each side and
/// the colours change no metrics, so the text never reflows when the highlight moves (unlike the search mark below, which makes
/// real room). Only this control repaints when the highlight moves.</para>
/// <para><b>Search mark.</b> <see cref="HighlightTerm"/> marks every occurrence (any case) like the canvas's <c>&lt;mark&gt;</c>: a filled
/// rounded box with the text in <see cref="MarkForeground"/>, 3 px wider on each side. The room for the box is real: a space
/// (in a size that makes it 3 px wide) goes before and after the word in the laid-out text, so the words after it move as they do in the canvas; the
/// offsets of <see cref="WordStarts"/> and of the pointer are translated, nothing outside this control sees the extra characters.</para>
/// <para><b>Uncertain words (#58).</b> <see cref="UncertainWords"/> flags the words the recogniser doubted. Each gets a dotted
/// underline (<see cref="UnderlineBrush"/>, <see cref="UnderlineThickness"/> thick, <see cref="UnderlineOffset"/> under the baseline, below
/// the letters and digits of the word, not its punctuation) and its text in <see cref="UncertainForeground"/>, like the word of
/// "Idea 4 / Low-confidence fix". Neither changes a metric. The underline segments are worked out once per layout and kept, so a frame
/// only draws them. While the line is coloured for "Follow along" its colours win, and the word under the chip has no underline.</para>
/// <para><b>Click.</b> With a mouse a press is hit-tested against the layout, mapped to the nearest word and passed to
/// <see cref="Command"/> as its parameter (the word index; 0 when the segment has no word timings). A press on an uncertain word
/// raises <see cref="UncertainWordInvokedEvent"/> (with the word's bounds, for the popover) and runs <see cref="UncertainCommand"/> instead. Touch and pen act on the tap instead of the press (<see cref="TapGesture"/>): the press only remembers the word,
/// and the release acts if the contact stayed within half the platform's tap size (<see cref="FallbackTapSlop"/> without one) and
/// the scroll did not take it over (capture lost, a scroll gesture, a second contact). The pointer is not captured for touch.</para>
/// <para><b>Keyboard.</b> The control takes the focus (Tab) only while it has uncertain words: Left and Right move between them (a 1.5 px
/// ring in <see cref="FocusBrush"/> shows which, when the focus came from the keyboard), Enter or Space opens it like a click.</para>
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

    public static readonly StyledProperty<IBrush?> HighlightForegroundProperty =
        AvaloniaProperty.Register<TranscriptTextControl, IBrush?>(nameof(HighlightForeground));

    public static readonly StyledProperty<IBrush?> UpcomingForegroundProperty =
        AvaloniaProperty.Register<TranscriptTextControl, IBrush?>(nameof(UpcomingForeground));

    public static readonly StyledProperty<double> HighlightPaddingProperty =
        AvaloniaProperty.Register<TranscriptTextControl, double>(nameof(HighlightPadding), 2);

    public static readonly StyledProperty<IBrush?> HighlightBorderBrushProperty =
        AvaloniaProperty.Register<TranscriptTextControl, IBrush?>(nameof(HighlightBorderBrush));

    public static readonly StyledProperty<CornerRadius> HighlightCornerRadiusProperty =
        AvaloniaProperty.Register<TranscriptTextControl, CornerRadius>(nameof(HighlightCornerRadius));

    public static readonly StyledProperty<bool[]?> UncertainWordsProperty =
        AvaloniaProperty.Register<TranscriptTextControl, bool[]?>(nameof(UncertainWords));

    public static readonly StyledProperty<IBrush?> UnderlineBrushProperty =
        AvaloniaProperty.Register<TranscriptTextControl, IBrush?>(nameof(UnderlineBrush));

    public static readonly StyledProperty<double> UnderlineThicknessProperty =
        AvaloniaProperty.Register<TranscriptTextControl, double>(nameof(UnderlineThickness), 1);

    public static readonly StyledProperty<double> UnderlineOffsetProperty =
        AvaloniaProperty.Register<TranscriptTextControl, double>(nameof(UnderlineOffset), 2);

    public static readonly StyledProperty<IBrush?> UncertainForegroundProperty =
        AvaloniaProperty.Register<TranscriptTextControl, IBrush?>(nameof(UncertainForeground));

    public static readonly StyledProperty<ICommand?> UncertainCommandProperty =
        AvaloniaProperty.Register<TranscriptTextControl, ICommand?>(nameof(UncertainCommand));

    public static readonly StyledProperty<IBrush?> FocusBrushProperty =
        AvaloniaProperty.Register<TranscriptTextControl, IBrush?>(nameof(FocusBrush));

    public static readonly StyledProperty<double> FocusThicknessProperty =
        AvaloniaProperty.Register<TranscriptTextControl, double>(nameof(FocusThickness), 1.5);

    /// <summary>A click (or Enter) on an uncertain word, raised before <see cref="UncertainCommand"/> runs; bubbles, so the popover host can anchor itself.</summary>
    public static readonly RoutedEvent<UncertainWordEventArgs> UncertainWordInvokedEvent =
        RoutedEvent.Register<TranscriptTextControl, UncertainWordEventArgs>(nameof(UncertainWordInvoked), RoutingStrategies.Bubble);

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<TranscriptTextControl>();

    public static readonly StyledProperty<FontFamily> FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner<TranscriptTextControl>();

    public static readonly StyledProperty<double> FontSizeProperty = TextElement.FontSizeProperty.AddOwner<TranscriptTextControl>();

    public static readonly StyledProperty<FontWeight> FontWeightProperty = TextElement.FontWeightProperty.AddOwner<TranscriptTextControl>();

    public static readonly StyledProperty<double> LineHeightProperty = TextBlock.LineHeightProperty.AddOwner<TranscriptTextControl>();

    public static readonly StyledProperty<double> LetterSpacingProperty = TextBlock.LetterSpacingProperty.AddOwner<TranscriptTextControl>();

    /// <summary>The room around a marked word is a no-break space (so a hit inside a word can never wrap there) set in a smaller size, so it is exactly <see cref="Padding"/> wide whatever the font has.</summary>
    private const char Pad = '\u00A0';

    private TextLayout? _layout;

    // What a word change rebuilds with: the run properties of the two colours are cached (they only depend on the brush, the typeface and
    // the size) and one span list is reused, so the rebuild allocates the layout and nothing else of its own.
    private GenericTextRunProperties? _highlightRun;
    private GenericTextRunProperties? _upcomingRun;
    private readonly List<ValueSpan<TextRunProperties>> _activeSpans = [];
    private Pen? _highlightPen;
    private double _layoutWidth;

    // Marked occurrences as (start, length) in Text, and the positions in the laid-out text where a Pad was inserted.
    private (int Start, int Length)[] _marks = [];
    private int[] _pads = [];

    // The dotted underlines of the uncertain words: worked out for _underlineLayout and kept until the layout or the words change.
    private UnderlineSegment[] _underlines = [];
    private int _underlineCount;
    private TextLayout? _underlineLayout;

    // The round dots of those underlines as one geometry, for _dotsLayout without the word under the chip (_dotsSkip): a frame draws it
    // with one call; it is rebuilt only when the layout, the chipped word or the thickness changes.
    private StreamGeometry? _dots;
    private TextLayout? _dotsLayout;
    private int _dotsSkip = -1;
    private double _dotsThickness;

    // The keyboard ring around _ringWord in _ringLayout.
    private Rect? _ring;
    private TextLayout? _ringLayout;
    private int _ringWord = -1;
    private double _ringThickness;
    private Pen? _focusPen;
    private GenericTextRunProperties? _uncertainRun;

    // The uncertain word the keyboard is on, and whether the focus came from the keyboard (only then the ring is drawn).
    private int _focusedWord = -1;
    private bool _focusVisible;

    static TranscriptTextControl()
    {
        AffectsMeasure<TranscriptTextControl>(
            TextProperty, FontFamilyProperty, FontSizeProperty, FontWeightProperty, LineHeightProperty, LetterSpacingProperty, HighlightTermProperty);
        AffectsRender<TranscriptTextControl>(
            WordStartsProperty, WordLengthsProperty, ActiveWordProperty, HighlightBrushProperty, HighlightBorderBrushProperty, HighlightCornerRadiusProperty, HighlightPaddingProperty, ForegroundProperty,
            MarkBrushProperty, MarkForegroundProperty, MarkCornerRadiusProperty,
            UncertainWordsProperty, UnderlineBrushProperty, UnderlineThicknessProperty, UnderlineOffsetProperty, FocusBrushProperty, FocusThicknessProperty);
    }

    /// <summary>The tap slop (DIPs) when the platform does not report a tap size; roughly the Windows drag threshold of a touch screen.</summary>
    internal const double FallbackTapSlop = 8;

    private TapGesture _tap;

    public TranscriptTextControl()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = false;
        AddHandler(ScrollGestureEvent, OnScrollGesture, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, handledEventsToo: true);
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

    /// <summary>Colour of the text of the highlighted word (on the chip); <c>null</c> = the text keeps <see cref="Foreground"/>.</summary>
    public IBrush? HighlightForeground
    {
        get => GetValue(HighlightForegroundProperty);
        set => SetValue(HighlightForegroundProperty, value);
    }

    /// <summary>Colour of the text after the highlighted word (what is still to be said); <c>null</c> = it keeps <see cref="Foreground"/>.</summary>
    public IBrush? UpcomingForeground
    {
        get => GetValue(UpcomingForegroundProperty);
        set => SetValue(UpcomingForegroundProperty, value);
    }

    /// <summary>How far the chip of the highlighted word reaches beyond the word on each side; it overlaps the spaces and never moves the text.</summary>
    public double HighlightPadding
    {
        get => GetValue(HighlightPaddingProperty);
        set => SetValue(HighlightPaddingProperty, value);
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

    /// <summary>Per word of <see cref="WordStarts"/>: the recogniser doubted it (<c>null</c> = none is uncertain).</summary>
    public bool[]? UncertainWords
    {
        get => GetValue(UncertainWordsProperty);
        set => SetValue(UncertainWordsProperty, value);
    }

    /// <summary>Colour of the dotted underline of an uncertain word; <c>null</c> = no underline.</summary>
    public IBrush? UnderlineBrush
    {
        get => GetValue(UnderlineBrushProperty);
        set => SetValue(UnderlineBrushProperty, value);
    }

    /// <summary>Thickness of the underline, which is also the size of its round dots.</summary>
    public double UnderlineThickness
    {
        get => GetValue(UnderlineThicknessProperty);
        set => SetValue(UnderlineThicknessProperty, value);
    }

    /// <summary>How far under the baseline the underline starts (CSS <c>text-underline-offset</c>).</summary>
    public double UnderlineOffset
    {
        get => GetValue(UnderlineOffsetProperty);
        set => SetValue(UnderlineOffsetProperty, value);
    }

    /// <summary>Colour of the text of an uncertain word; <c>null</c> = it keeps <see cref="Foreground"/>.</summary>
    public IBrush? UncertainForeground
    {
        get => GetValue(UncertainForegroundProperty);
        set => SetValue(UncertainForegroundProperty, value);
    }

    /// <summary>Executed with the index of the uncertain word that was clicked (or opened with Enter); <c>null</c> = a click on it seeks like any other.</summary>
    public ICommand? UncertainCommand
    {
        get => GetValue(UncertainCommandProperty);
        set => SetValue(UncertainCommandProperty, value);
    }

    /// <summary>The ring around the uncertain word the keyboard is on.</summary>
    public IBrush? FocusBrush
    {
        get => GetValue(FocusBrushProperty);
        set => SetValue(FocusBrushProperty, value);
    }

    public double FocusThickness
    {
        get => GetValue(FocusThicknessProperty);
        set => SetValue(FocusThicknessProperty, value);
    }

    /// <summary>See <see cref="UncertainWordInvokedEvent"/>.</summary>
    public event EventHandler<UncertainWordEventArgs>? UncertainWordInvoked
    {
        add => AddHandler(UncertainWordInvokedEvent, value);
        remove => RemoveHandler(UncertainWordInvokedEvent, value);
    }

    /// <summary>The uncertain word the keyboard is on, or -1 (for tests).</summary>
    internal int FocusedWord => _focusedWord;

    /// <summary>How many underline pieces the current layout draws (a word split over two lines has two; for tests).</summary>
    internal int UnderlineCount
    {
        get
        {
            EnsureUnderlines(EnsureLayout(Bounds.Width));
            return _underlineCount;
        }
    }

    /// <summary>The underline pieces as (word, left, right, y) in this control (for tests).</summary>
    internal IEnumerable<(int Word, double Left, double Right, double Y)> Underlines
    {
        get
        {
            EnsureUnderlines(EnsureLayout(Bounds.Width));
            for (var i = 0; i < _underlineCount; i++)
            {
                var u = _underlines[i];
                yield return (u.Word, u.Left, u.Right, u.Y);
            }
        }
    }

    /// <summary>The cached dots of the underlines (the same instance until the layout, the chipped word or the words change; for tests).</summary>
    internal object? UnderlineStore => _dots;

    /// <summary>The cached keyboard ring (for tests).</summary>
    internal Rect? FocusRingRect => _ring;

    /// <summary>The bounds of a word in this control (its first line when it wraps), or <c>null</c> when it is out of range.</summary>
    internal Rect? WordBounds(int word)
    {
        var layout = EnsureLayout(Bounds.Width);
        var starts = WordStarts;
        var lengths = WordLengths;
        if (layout is null || starts is null || lengths is null || word < 0 || word >= starts.Length || word >= lengths.Length || lengths[word] <= 0)
        {
            return null;
        }

        var first = ToLaidOut(starts[word]);
        var last = ToLaidOut(starts[word] + lengths[word] - 1);
        foreach (var rect in layout.HitTestTextRange(first, last - first + 1))
        {
            return rect;
        }

        return null;
    }

    /// <summary>The word is flagged as uncertain.</summary>
    internal bool IsUncertain(int word) => UncertainWords is { } flags && word >= 0 && word < flags.Length && flags[word];

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

            // The chip is the content area of the font (like the search mark), centred in the line and wider than the word by the padding.
            var chipHeight = Math.Round(FontSize * 1.25);
            var chipPadding = HighlightPadding;
            var first = ToLaidOut(starts[active]);
            var last = ToLaidOut(starts[active] + lengths[active] - 1);
            foreach (var rect in layout.HitTestTextRange(first, last - first + 1))
            {
                var chip = new Rect(rect.X - chipPadding, rect.Y + ((rect.Height - chipHeight) / 2), rect.Width + (2 * chipPadding), chipHeight);
                context.DrawRectangle(highlight, _highlightPen, chip, radius, radius);
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

        DrawUncertain(context, layout, starts, lengths, active);
        layout.Draw(context, default);
    }

    /// <summary>The dotted underlines (one cached geometry: nothing of the control's is allocated per frame) and the keyboard ring.</summary>
    private void DrawUncertain(DrawingContext context, TextLayout layout, int[]? starts, int[]? lengths, int active)
    {
        if (UncertainWords is null)
        {
            return;
        }

        // The chip of "Follow along" wins on the word it is on.
        var chipped = HighlightBrush is not null && starts is not null && lengths is not null && active >= 0 && active < starts.Length && lengths[active] > 0 ? active : -1;
        if (UnderlineBrush is { } brush && EnsureDots(layout, chipped) is { } dots)
        {
            context.DrawGeometry(brush, null, dots);
        }

        if (_focusVisible && IsFocused && FocusBrush is { } focus && IsUncertain(_focusedWord) && FocusRing(layout) is { } ring)
        {
            if (_focusPen is null || !ReferenceEquals(_focusPen.Brush, focus) || _focusPen.Thickness != FocusThickness)
            {
                _focusPen = new Pen(focus, FocusThickness);
            }

            var radius = MarkCornerRadius.TopLeft;
            context.DrawRectangle(null, _focusPen, ring, radius, radius);
        }
    }

    /// <summary>The ring around the focused uncertain word, worked out once per layout and word (a frame only draws it).</summary>
    private Rect? FocusRing(TextLayout layout)
    {
        if (ReferenceEquals(_ringLayout, layout) && _ringWord == _focusedWord && _ringThickness.Equals(FocusThickness))
        {
            return _ring;
        }

        _ringLayout = layout;
        _ringWord = _focusedWord;
        _ringThickness = FocusThickness;
        _ring = null;
        if (WordBounds(_focusedWord) is { } word)
        {
            var padding = HighlightPadding;
            var height = Math.Round(FontSize * 1.25);
            _ring = new Rect(word.X - padding, word.Y + ((word.Height - height) / 2), word.Width + (2 * padding), height).Inflate(FocusThickness / 2);
        }

        return _ring;
    }

    /// <summary>
    /// The dots of every underline piece except those of <paramref name="skip"/> (the word under the chip): round, as wide as
    /// <see cref="UnderlineThickness"/>, one every two widths (CSS <c>dotted</c>). Kept until the layout, the skipped word or the thickness
    /// changes; <c>null</c> when there is nothing to draw.
    /// </summary>
    private StreamGeometry? EnsureDots(TextLayout layout, int skip)
    {
        EnsureUnderlines(layout);
        var thickness = UnderlineThickness;
        if (ReferenceEquals(_dotsLayout, layout) && _dotsSkip == skip && _dotsThickness.Equals(thickness))
        {
            return _dots;
        }

        _dotsLayout = layout;
        _dotsSkip = skip;
        _dotsThickness = thickness;
        _dots = null;
        if (thickness <= 0)
        {
            return null;
        }

        var radius = thickness / 2;
        var size = new Size(radius, radius);
        StreamGeometry? geometry = null;
        StreamGeometryContext? figures = null;
        for (var i = 0; i < _underlineCount; i++)
        {
            var u = _underlines[i];
            if (u.Word == skip)
            {
                continue;
            }

            for (var x = u.Left + radius; x + radius <= u.Right + 0.01; x += 2 * thickness)
            {
                if (figures is null)
                {
                    geometry = new StreamGeometry();
                    figures = geometry.Open();
                }

                figures.BeginFigure(new Point(x + radius, u.Y), true);
                figures.ArcTo(new Point(x - radius, u.Y), size, 0, false, SweepDirection.Clockwise);
                figures.ArcTo(new Point(x + radius, u.Y), size, 0, false, SweepDirection.Clockwise);
                figures.EndFigure(true);
            }
        }

        figures?.Dispose();
        _dots = geometry;
        return _dots;
    }

    /// <summary>Works the underline pieces out for <paramref name="layout"/>, once: under the letters and digits of each uncertain word, on every line it spans.</summary>
    private void EnsureUnderlines(TextLayout? layout)
    {
        if (layout is null)
        {
            _underlineCount = 0;
            _underlineLayout = null;
            return;
        }

        if (ReferenceEquals(layout, _underlineLayout))
        {
            return;
        }

        _underlineLayout = layout;
        _underlineCount = 0;
        var flags = UncertainWords;
        var starts = WordStarts;
        var lengths = WordLengths;
        var text = Text;
        if (flags is null || starts is null || lengths is null || text is null)
        {
            return;
        }

        var offset = UnderlineOffset + (UnderlineThickness / 2);
        var count = Math.Min(flags.Length, Math.Min(starts.Length, lengths.Length));
        for (var w = 0; w < count; w++)
        {
            if (!flags[w] || !Core(text, starts[w], lengths[w], out var from, out var length))
            {
                continue;
            }

            // The end is mapped on its own: a search mark inside the word puts room (pads) between its letters.
            var first = ToLaidOut(from);
            var end = ToLaidOut(from + length - 1) + 1;
            foreach (var rect in layout.HitTestTextRange(first, end - first))
            {
                if (rect.Width <= 0)
                {
                    continue;
                }

                if (_underlineCount == _underlines.Length)
                {
                    Array.Resize(ref _underlines, Math.Max(4, _underlines.Length * 2));
                }

                _underlines[_underlineCount++] = new UnderlineSegment(w, rect.X, rect.Right, BaselineOf(layout, rect.Y) + offset);
            }
        }
    }

    /// <summary>The baseline of the line whose top is <paramref name="top"/> (as HitTestTextRange reports it).</summary>
    private static double BaselineOf(TextLayout layout, double top)
    {
        var y = 0.0;
        TextLine? last = null;
        foreach (var line in layout.TextLines)
        {
            if (top < y + (line.Height / 2))
            {
                return y + line.Baseline;
            }

            last = line;
            y += line.Height;
        }

        return last is null ? top : y - last.Height + last.Baseline;
    }

    /// <summary>The letters and digits of a word, without the punctuation around it ("parquet?" → "parquet"); the whole word when it has none.</summary>
    private static bool Core(string text, int start, int length, out int from, out int coreLength)
    {
        from = start;
        coreLength = 0;
        if (length <= 0 || start < 0 || start + length > text.Length)
        {
            return false;
        }

        var end = start + length;
        var first = start;
        while (first < end && !char.IsLetterOrDigit(text[first]))
        {
            first++;
        }

        var last = end;
        while (last > first && !char.IsLetterOrDigit(text[last - 1]))
        {
            last--;
        }

        if (first == last)
        {
            first = start;
            last = end;
        }

        from = first;
        coreLength = last - first;
        return true;
    }

    /// <summary>Opens the uncertain word: tells the popover host where it is, then runs <see cref="UncertainCommand"/>.</summary>
    private bool InvokeUncertain(int word, bool fromKeyboard)
    {
        if (!IsUncertain(word) || UncertainCommand is not { } command || !command.CanExecute(word))
        {
            return false;
        }

        _focusedWord = word;
        RaiseEvent(new UncertainWordEventArgs(UncertainWordInvokedEvent, this, word, WordBounds(word) ?? new Rect(Bounds.Size), fromKeyboard));
        command.Execute(word);
        return true;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _focusVisible = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional;
        if (!IsUncertain(_focusedWord))
        {
            _focusedWord = NextUncertain(-1, 1);
        }

        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || UncertainWords is null || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Left or Key.Right:
                var next = NextUncertain(_focusedWord, e.Key == Key.Right ? 1 : -1);
                if (next >= 0)
                {
                    _focusedWord = next;
                    _focusVisible = true;
                    InvalidateVisual();
                    e.Handled = true;
                }

                break;
            case Key.Enter or Key.Space:
                if (!IsUncertain(_focusedWord))
                {
                    _focusedWord = NextUncertain(-1, 1);
                }

                e.Handled = InvokeUncertain(_focusedWord, fromKeyboard: true);
                break;
        }
    }

    /// <summary>The next uncertain word after <paramref name="from"/> in <paramref name="direction"/> (+1 / -1), or -1.</summary>
    private int NextUncertain(int from, int direction)
    {
        if (UncertainWords is not { } flags)
        {
            return -1;
        }

        for (var w = from + direction; w >= 0 && w < flags.Length; w += direction)
        {
            if (flags[w])
            {
                return w;
            }
        }

        return -1;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var word = WordAt(e.GetPosition(this));
        if (e.Pointer.Type is PointerType.Touch or PointerType.Pen)
        {
            // Touch and pen report the left button as pressed, and every drag-to-scroll starts with a press: only remember it.
            // The pointer is not captured and the event is not handled, so the parent ScrollViewer can take the gesture over.
            _tap.Press(e.Pointer.Id, e.GetPosition(this), word);
            return;
        }

        e.Handled = Activate(word);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_tap.IsActive)
        {
            _tap.Move(e.Pointer.Id, e.GetPosition(this), TapSlop());
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_tap.IsActive)
        {
            return;
        }

        var word = _tap.Release(e.Pointer.Id, e.GetPosition(this), TapSlop());
        if (word >= 0)
        {
            e.Handled = Activate(word);
        }
    }

    /// <summary>The scroll (a <see cref="ScrollViewer"/> taking the capture) or anything else took the pointer: it is no tap.</summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _tap.Cancel();
    }

    private void OnScrollGesture(object? sender, ScrollGestureEventArgs e) => _tap.Cancel();

    /// <summary>
    /// How far, in DIPs, a touch or pen contact may stray from the press and still be a tap: half the platform's tap rectangle
    /// (<see cref="IPlatformSettings.GetTapSize"/>), or <see cref="FallbackTapSlop"/> when the platform tells nothing.
    /// </summary>
    private double TapSlop()
    {
        var size = Application.Current?.PlatformSettings?.GetTapSize(PointerType.Touch).Width ?? 0;
        return size > 0 ? size / 2 : FallbackTapSlop;
    }

    /// <summary>Opens the uncertain word under the pointer, or else seeks to the word. Returns whether anything ran.</summary>
    private bool Activate(int word)
    {
        if (InvokeUncertain(word, fromKeyboard: false))
        {
            _focusVisible = false;
            return true;
        }

        if (word < 0 || Command is not { } command || !command.CanExecute(word))
        {
            return false;
        }

        command.Execute(word);
        return true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == FontFamilyProperty || change.Property == FontSizeProperty
            || change.Property == FontWeightProperty || change.Property == LineHeightProperty
            || change.Property == LetterSpacingProperty || change.Property == ForegroundProperty
            || change.Property == HighlightTermProperty || change.Property == MarkForegroundProperty
            || change.Property == HighlightForegroundProperty || change.Property == UpcomingForegroundProperty
            || change.Property == UncertainForegroundProperty
            || (change.Property == UncertainWordsProperty && UncertainForeground is not null)
            || ((change.Property == ActiveWordProperty || change.Property == WordStartsProperty || change.Property == WordLengthsProperty) && ColoursActiveLine))
        {
            _layout?.Dispose();
            _layout = null;
        }

        if (change.Property == UncertainWordsProperty || change.Property == WordStartsProperty || change.Property == WordLengthsProperty
            || change.Property == UnderlineOffsetProperty || change.Property == UnderlineThicknessProperty)
        {
            // The underlines are worked out again for the next frame; the control is a tab stop only while it has an uncertain word.
            _underlineLayout = null;
            _dotsLayout = null;
            Focusable = HasUncertain(UncertainWords);
            if (!IsUncertain(_focusedWord))
            {
                _focusedWord = -1;
            }
        }
    }

    private static bool HasUncertain(bool[]? flags)
    {
        if (flags is null)
        {
            return false;
        }

        foreach (var flag in flags)
        {
            if (flag)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The active word or the rest of the line has a colour of its own, so the colours of the layout depend on <see cref="ActiveWord"/>.</summary>
    private bool ColoursActiveLine => HighlightForeground is not null || UpcomingForeground is not null;

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
        var (shown, spans) = MarkTerm(text, typeface);
        spans = IsActiveLineColoured ? ColourActiveLine(shown.Length, spans, typeface) : ColourUncertain(text, spans, typeface);
        return (shown, spans);
    }

    /// <summary>The colours of "Follow along" are on this line (they win over the colour of the uncertain words).</summary>
    private bool IsActiveLineColoured =>
        ColoursActiveLine && WordStarts is { } starts && WordLengths is { } lengths && ActiveWord >= 0 && ActiveWord < starts.Length && ActiveWord < lengths.Length && lengths[ActiveWord] > 0;

    /// <summary>Gives the letters of every uncertain word <see cref="UncertainForeground"/>; what the search mark colours is left to it.</summary>
    private List<ValueSpan<TextRunProperties>>? ColourUncertain(string text, List<ValueSpan<TextRunProperties>>? taken, Typeface typeface)
    {
        var flags = UncertainWords;
        var starts = WordStarts;
        var lengths = WordLengths;
        if (UncertainForeground is not { } brush || flags is null || starts is null || lengths is null)
        {
            return taken;
        }

        List<ValueSpan<TextRunProperties>>? spans = taken;
        var occupied = taken?.Count ?? 0;
        var count = Math.Min(flags.Length, Math.Min(starts.Length, lengths.Length));
        for (var w = 0; w < count; w++)
        {
            if (!flags[w] || !Core(text, starts[w], lengths[w], out var from, out var length))
            {
                continue;
            }

            if (spans is null)
            {
                spans = _activeSpans;
                spans.Clear();
            }

            var first = ToLaidOut(from);
            AddOutside(spans, occupied, first, ToLaidOut(from + length - 1) + 1 - first, Run(ref _uncertainRun, brush, typeface));
        }

        if (spans is not null && spans.Count > occupied)
        {
            spans.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        }

        return spans;
    }

    private (string Text, List<ValueSpan<TextRunProperties>>? Overrides) MarkTerm(string text, Typeface typeface)
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

    /// <summary>
    /// Colours the active word (<see cref="HighlightForeground"/>) and everything after it (<see cref="UpcomingForeground"/>). Only colours:
    /// the font, size and spacing of every run stay the same, so the lines break exactly where they do without a highlight. What the
    /// search mark and its pads already colour is left to them.
    /// </summary>
    private List<ValueSpan<TextRunProperties>>? ColourActiveLine(int length, List<ValueSpan<TextRunProperties>>? taken, Typeface typeface)
    {
        var starts = WordStarts;
        var lengths = WordLengths;
        var active = ActiveWord;
        if (!ColoursActiveLine || starts is null || lengths is null || active < 0 || active >= starts.Length || lengths[active] <= 0)
        {
            return taken;
        }

        var from = ToLaidOut(starts[active]);
        var to = ToLaidOut(starts[active] + lengths[active] - 1) + 1;
        List<ValueSpan<TextRunProperties>> spans;
        if (taken is null)
        {
            // TextLayout reads the overrides while it is built and keeps no reference to the list.
            spans = _activeSpans;
            spans.Clear();
        }
        else
        {
            spans = taken;
        }

        var occupied = spans.Count;
        if (HighlightForeground is { } highlight)
        {
            AddOutside(spans, occupied, from, to - from, Run(ref _highlightRun, highlight, typeface));
        }

        if (UpcomingForeground is { } upcoming && to < length)
        {
            AddOutside(spans, occupied, to, length - to, Run(ref _upcomingRun, upcoming, typeface));
        }

        spans.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return spans;
    }

    /// <summary>The run properties for <paramref name="brush"/> in the current font, made once and kept until the brush, the typeface or the size change.</summary>
    private TextRunProperties Run(ref GenericTextRunProperties? cache, IBrush brush, Typeface typeface)
    {
        if (cache is null || !ReferenceEquals(cache.ForegroundBrush, brush) || cache.Typeface != typeface || cache.FontRenderingEmSize != FontSize)
        {
            cache = new GenericTextRunProperties(typeface, FontSize, null, brush);
        }

        return cache;
    }

    /// <summary>Adds <paramref name="properties"/> for the range, minus the first <paramref name="occupied"/> spans of the (sorted) list.</summary>
    private static void AddOutside(List<ValueSpan<TextRunProperties>> spans, int occupied, int start, int length, TextRunProperties properties)
    {
        var at = start;
        var end = start + length;
        for (var i = 0; i < occupied && at < end; i++)
        {
            var span = spans[i];
            if (span.Start + span.Length <= at)
            {
                continue;
            }

            if (span.Start >= end)
            {
                break;
            }

            if (span.Start > at)
            {
                spans.Add(new ValueSpan<TextRunProperties>(at, span.Start - at, properties));
            }

            at = span.Start + span.Length;
        }

        if (at < end)
        {
            spans.Add(new ValueSpan<TextRunProperties>(at, end - at, properties));
        }
    }

    /// <summary>The advance of a space as a share of the font size (the width of "a" + no-break space + "a" minus "aa" at 100 px, without letter spacing).</summary>
    private static double SpaceShare(Typeface typeface)
    {
        double Width(string text)
        {
            using var layout = new TextLayout(text, typeface, 100, Brushes.Black, TextAlignment.Left, TextWrapping.NoWrap, TextTrimming.None, null, FlowDirection.LeftToRight, double.PositiveInfinity, double.PositiveInfinity, double.NaN, 0);
            return layout.WidthIncludingTrailingWhitespace;
        }

        return (Width("a\u00A0a") - Width("aa")) / 100;
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

    private readonly record struct UnderlineSegment(int Word, double Left, double Right, double Y);
}

/// <summary>An uncertain word of a <see cref="TranscriptTextControl"/> was clicked or opened with the keyboard.</summary>
public sealed class UncertainWordEventArgs(RoutedEvent routedEvent, object source, int word, Rect wordBounds, bool fromKeyboard = false) : RoutedEventArgs(routedEvent, source)
{
    /// <summary>Opened with Enter or Space (the popover then shows where the keyboard focus is), not with the pointer.</summary>
    public bool FromKeyboard { get; } = fromKeyboard;

    /// <summary>Index of the word in the segment.</summary>
    public int Word { get; } = word;

    /// <summary>Where the word is, in the coordinates of the control that raised the event (its first line when it wraps).</summary>
    public Rect WordBounds { get; } = wordBounds;
}
