using System.Globalization;
using System.Text;
using Murmur.Core.Intelligence;

namespace Murmur.App.ViewModels;

/// <summary>What an <see cref="AnswerPart"/> is.</summary>
public enum AnswerPartKind
{
    /// <summary>Plain text.</summary>
    Text,

    /// <summary>A time chip (<see cref="AnswerPart.Chip"/>) in the middle of the line.</summary>
    Chip,

    /// <summary>A line break inside a paragraph.</summary>
    LineBreak,
}

/// <summary>A piece of a paragraph of an answer: text, an inline time chip or a line break.</summary>
public sealed class AnswerPart
{
    private AnswerPart(AnswerPartKind kind, string text, CitationChipViewModel? chip)
    {
        Kind = kind;
        Text = text;
        Chip = chip;
    }

    public AnswerPartKind Kind { get; }

    /// <summary>The text of a <see cref="AnswerPartKind.Text"/> part; empty otherwise.</summary>
    public string Text { get; }

    /// <summary>The chip of a <see cref="AnswerPartKind.Chip"/> part; <c>null</c> otherwise.</summary>
    public CitationChipViewModel? Chip { get; }

    internal static AnswerPart OfText(string text) => new(AnswerPartKind.Text, text, null);

    internal static AnswerPart OfChip(CitationChipViewModel chip) => new(AnswerPartKind.Chip, string.Empty, chip);

    internal static AnswerPart Break { get; } = new(AnswerPartKind.LineBreak, string.Empty, null);
}

/// <summary>One paragraph of a finished answer (the model separates them with a blank line).</summary>
public sealed class AnswerParagraph
{
    internal AnswerParagraph(IReadOnlyList<AnswerPart> parts)
    {
        Parts = parts;
    }

    public IReadOnlyList<AnswerPart> Parts { get; }
}

/// <summary>
/// Turns the text of an answer into paragraphs with inline time chips: the model marks the moments it cites as <c>[12:34]</c> (or
/// <c>[1:02:03]</c>, or a range <c>[12:00–12:45]</c> that cites its start), the same syntax <c>CitationResolver</c> resolves into
/// <see cref="Citation"/>s. A marker that matches a citation becomes a chip; one that does not stays as the plain text it was written as.
/// </summary>
internal static class AnswerText
{
    public static IReadOnlyList<AnswerParagraph> Parse(string text, Func<TimeSpan, CitationChipViewModel?> chipAt)
    {
        var paragraphs = new List<AnswerParagraph>();
        var parts = new List<AnswerPart>();
        var plain = new StringBuilder();

        void FlushText()
        {
            if (plain.Length > 0)
            {
                parts.Add(AnswerPart.OfText(plain.ToString()));
                plain.Clear();
            }
        }

        void EndParagraph()
        {
            FlushText();
            if (parts.Count > 0)
            {
                paragraphs.Add(new AnswerParagraph(parts.ToArray()));
                parts.Clear();
            }
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                // A blank line ends the paragraph.
                EndParagraph();
                continue;
            }

            if (parts.Count > 0 || plain.Length > 0)
            {
                FlushText();
                parts.Add(AnswerPart.Break);
            }

            AppendLine(line.Trim(), chipAt, plain, parts);
        }

        EndParagraph();
        return paragraphs;
    }

    private static void AppendLine(string line, Func<TimeSpan, CitationChipViewModel?> chipAt, StringBuilder plain, List<AnswerPart> parts)
    {
        var position = 0;
        while (position < line.Length)
        {
            var open = line.IndexOf('[', position);
            if (open < 0)
            {
                plain.Append(line, position, line.Length - position);
                return;
            }

            var close = line.IndexOf(']', open + 1);
            if (close < 0)
            {
                plain.Append(line, position, line.Length - position);
                return;
            }

            plain.Append(line, position, open - position);
            var marker = line.AsSpan(open + 1, close - open - 1);
            if (TryParseMarker(marker, out var at) && chipAt(at) is { } chip)
            {
                // The space the model wrote before the marker stays before the chip; none stays after it.
                if (plain.Length > 0)
                {
                    parts.Add(AnswerPart.OfText(plain.ToString()));
                    plain.Clear();
                }

                parts.Add(AnswerPart.OfChip(chip));
            }
            else
            {
                plain.Append(line, open, close - open + 1);
            }

            position = close + 1;
        }
    }

    /// <summary><c>m:ss</c>, <c>mm:ss</c> or <c>h:mm:ss</c>, optionally followed by <c>–m:ss</c> (the range's end is ignored).</summary>
    internal static bool TryParseMarker(ReadOnlySpan<char> marker, out TimeSpan at)
    {
        at = default;
        var dash = marker.IndexOfAny('–', '-');
        var start = (dash < 0 ? marker : marker[..dash]).Trim();
        if (dash >= 0 && !IsClock(marker[(dash + 1)..].Trim()))
        {
            return false;
        }

        return TryParseClock(start, out at);
    }

    private static bool IsClock(ReadOnlySpan<char> value) => TryParseClock(value, out _);

    private static bool TryParseClock(ReadOnlySpan<char> value, out TimeSpan at)
    {
        at = default;
        var first = value.IndexOf(':');
        if (first <= 0)
        {
            return false;
        }

        var rest = value[(first + 1)..];
        var second = rest.IndexOf(':');
        var hours = 0;
        ReadOnlySpan<char> minutes;
        ReadOnlySpan<char> seconds;
        if (second < 0)
        {
            minutes = value[..first];
            seconds = rest;
        }
        else
        {
            if (!TryDigits(value[..first], out hours))
            {
                return false;
            }

            minutes = rest[..second];
            seconds = rest[(second + 1)..];
        }

        if (!TryDigits(minutes, out var m) || !TryDigits(seconds, out var s) || minutes.Length > 2 || seconds.Length != 2 || s >= 60 || (second >= 0 && m >= 60))
        {
            return false;
        }

        at = new TimeSpan(hours, m, s);
        return true;
    }

    private static bool TryDigits(ReadOnlySpan<char> value, out int number)
    {
        number = 0;
        if (value.Length == 0 || value.Length > 3)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }
}
