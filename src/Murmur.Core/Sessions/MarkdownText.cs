using System.Buffers;
using System.Text;

namespace Murmur.Core.Sessions;

/// <summary>
/// Escapes user and transcript text for the Markdown export so that every renderer shows it as written. Never applied to the
/// Markdown the exporter writes itself (headings, timestamps, the bullets it builds).
/// </summary>
public static class MarkdownText
{
    // Escaped wherever they stand ('&' separately, only before a character reference). '\r' and '\n' are here only to send multi-line values down the slow path.
    private static readonly SearchValues<char> Always = SearchValues.Create("\\*_`<>[]\r\n");

    /// <summary>
    /// The value as one Markdown line: line breaks collapse to single spaces (each line trimmed, empty lines dropped, as in the other
    /// formats), then <c>\ * _ ` &lt; &gt; [ ]</c> get a backslash everywhere, an <c>&amp;</c> that starts a character reference too,
    /// and the characters that make a line a block at its start (<c>#</c>, <c>- </c>, <c>+ </c>, <c>1. </c>, <c>1) </c>, <c>---</c>,
    /// <c>===</c>, <c>~~~</c>; <c>&gt;</c> is escaped everywhere) are escaped at the start of the value, which is the start of its line.
    /// Returns the same string when nothing needs escaping.
    /// </summary>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var span = text.AsSpan();
        if (span.Length == 0)
        {
            return text;
        }

        if (!char.IsWhiteSpace(span[0]) && !char.IsWhiteSpace(span[^1]) && !NeedsEscape(span))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 8);
        var first = true;
        var rest = span;
        while (!rest.IsEmpty)
        {
            var end = rest.IndexOfAny('\r', '\n');
            var line = (end < 0 ? rest : rest[..end]).Trim();
            rest = end < 0 ? default : rest[(end + 1)..];
            if (line.IsEmpty)
            {
                continue;
            }

            if (!first)
            {
                builder.Append(' ');
            }

            AppendLine(builder, line, first);
            first = false;
        }

        return builder.ToString();
    }

    /// <summary>
    /// A multi-paragraph value (the summary's overview): the same escaping applied to every line on its own, so a line that starts with
    /// <c>#</c> or <c>1.</c> in the middle of the text is escaped too; the line breaks stay (as <c>\n</c>, each line trimmed, the whole
    /// value trimmed), so the paragraphs survive.
    /// </summary>
    public static string EscapeBlock(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length + 8);
        var rest = text.AsSpan().Trim();
        var firstLine = true;
        while (true)
        {
            var end = rest.IndexOfAny('\r', '\n');
            var line = (end < 0 ? rest : rest[..end]).Trim();
            if (!firstLine)
            {
                builder.Append('\n');
            }

            if (!line.IsEmpty)
            {
                AppendLine(builder, line, atStart: true);
            }

            firstLine = false;
            if (end < 0)
            {
                break;
            }

            // "\r\n" is one break.
            rest = rest[(end + (rest[end] == '\r' && end + 1 < rest.Length && rest[end + 1] == '\n' ? 2 : 1))..];
        }

        return builder.ToString();
    }

    private static bool NeedsEscape(ReadOnlySpan<char> text)
    {
        if (text.IndexOfAny(Always) >= 0 || StartsBlock(text, out _))
        {
            return true;
        }

        for (var i = text.IndexOf('&'); i >= 0; )
        {
            if (StartsReference(text[(i + 1)..]))
            {
                return true;
            }

            var next = text[(i + 1)..].IndexOf('&');
            i = next < 0 ? -1 : i + 1 + next;
        }

        return false;
    }

    private static void AppendLine(StringBuilder builder, ReadOnlySpan<char> line, bool atStart)
    {
        // The character at 'skip' is the one that makes a block at the start; the backslash goes right before it.
        var skip = -1;
        if (atStart && StartsBlock(line, out var index))
        {
            skip = index;
        }

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (i == skip || c is '\\' or '*' or '_' or '`' or '<' or '>' or '[' or ']' || (c == '&' && StartsReference(line[(i + 1)..])))
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }
    }

    /// <summary>Whether the line, trimmed, would open a heading, list, thematic break, setext underline or fence; <paramref name="index"/> is the character to escape.</summary>
    private static bool StartsBlock(ReadOnlySpan<char> line, out int index)
    {
        index = 0;
        var first = line[0];
        var next = line.Length > 1 ? line[1] : '\0';
        switch (first)
        {
            case '#':
                return true;
            case '-':
                return next is '\0' or ' ' or '\t' or '-';
            case '+':
                return next is '\0' or ' ' or '\t';
            case '=':
                return next is '\0' or '=';
            case '~':
                return next == '~';
        }

        var digits = 0;
        while (digits < line.Length && digits < 10 && char.IsAsciiDigit(line[digits]))
        {
            digits++;
        }

        if (digits is > 0 and < 10 && digits < line.Length && line[digits] is '.' or ')'
            && (digits + 1 == line.Length || line[digits + 1] is ' ' or '\t'))
        {
            index = digits;
            return true;
        }

        return false;
    }

    /// <summary>Whether what follows an ampersand reads as a character reference (<c>&amp;amp;</c>, <c>&amp;#35;</c>, <c>&amp;#x23;</c>) that a renderer would turn into another character.</summary>
    private static bool StartsReference(ReadOnlySpan<char> after)
    {
        var limit = Math.Min(after.Length, 32);
        var semicolon = after[..limit].IndexOf(';');
        if (semicolon <= 0)
        {
            return false;
        }

        foreach (var c in after[..semicolon])
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '#')
            {
                return false;
            }
        }

        return true;
    }
}
