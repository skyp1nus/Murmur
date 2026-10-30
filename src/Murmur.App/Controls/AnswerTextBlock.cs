using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Murmur.App.ViewModels;

namespace Murmur.App.Controls;

/// <summary>
/// A paragraph of an answer: its text with the time chips in the middle of the lines (<see cref="AnswerPart"/>s), as the canvas draws
/// them. The text is <see cref="Run"/>s and the chips are controls embedded in the line (<see cref="InlineUIContainer"/>), made from
/// <see cref="ChipTemplate"/> with the part's chip as the content.
/// </summary>
public sealed class AnswerTextBlock : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<AnswerPart>?> PartsProperty =
        AvaloniaProperty.Register<AnswerTextBlock, IReadOnlyList<AnswerPart>?>(nameof(Parts));

    public static readonly StyledProperty<IDataTemplate?> ChipTemplateProperty =
        AvaloniaProperty.Register<AnswerTextBlock, IDataTemplate?>(nameof(ChipTemplate));

    public IReadOnlyList<AnswerPart>? Parts
    {
        get => GetValue(PartsProperty);
        set => SetValue(PartsProperty, value);
    }

    /// <summary>How a time chip looks; its data context is the <see cref="CitationChipViewModel"/>.</summary>
    public IDataTemplate? ChipTemplate
    {
        get => GetValue(ChipTemplateProperty);
        set => SetValue(ChipTemplateProperty, value);
    }

    /// <summary>It is a text block: the styles of <see cref="TextBlock"/> apply.</summary>
    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PartsProperty || change.Property == ChipTemplateProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var inlines = Inlines ??= new InlineCollection();
        inlines.Clear();
        if (Parts is not { } parts)
        {
            return;
        }

        foreach (var part in parts)
        {
            switch (part.Kind)
            {
                case AnswerPartKind.Text:
                    inlines.Add(new Run(part.Text));
                    break;
                case AnswerPartKind.LineBreak:
                    inlines.Add(new LineBreak());
                    break;
                case AnswerPartKind.Chip when part.Chip is { } chip:
                    inlines.Add(new InlineUIContainer(new ContentControl { Content = chip, ContentTemplate = ChipTemplate, ClipToBounds = false }));
                    break;
            }
        }

        inlines.Add(new Run("\u200B"));
    }
}
