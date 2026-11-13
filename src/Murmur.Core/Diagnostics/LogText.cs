using System.Globalization;
using System.Text;

namespace Murmur.Core.Diagnostics;

/// <summary>
/// Turns what a caller has at hand (a path, an exception, a technical message, a worker's stderr) into the short, content-free details
/// of an <see cref="IAppLog"/> event: paths are reduced to their file names, quoted values are dropped, lines are joined and capped.
/// </summary>
public static class LogText
{
    /// <summary>The longest detail a log line keeps.</summary>
    public const int MaxDetail = 200;

    private const string Elided = "...";

    /// <summary>The last part of <paramref name="path"/> (Windows or Unix separators, a trailing separator ignored); "" for none.</summary>
    public static string FileName(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var span = path.AsSpan().TrimEnd("\\/ ");
        var cut = span.LastIndexOfAny('\\', '/');
        return (cut < 0 ? span : span[(cut + 1)..]).ToString();
    }

    /// <summary>
    /// An exception as its type and HRESULT (<c>IOException 0x80070020</c>), never its message: messages carry paths, quoted inputs
    /// and model output. A lone inner exception of an <see cref="AggregateException"/> is reported instead of the wrapper.
    /// </summary>
    public static string Error(Exception? exception)
    {
        if (exception is null)
        {
            return string.Empty;
        }

        while (exception is AggregateException { InnerExceptions.Count: 1 } aggregate)
        {
            exception = aggregate.InnerExceptions[0];
        }

        return string.Create(CultureInfo.InvariantCulture, $"{exception.GetType().Name} 0x{exception.HResult:X8}");
    }

    /// <summary>
    /// A technical message made safe for the log: rooted paths become their file names, quoted values that are not paths become
    /// <c>'...'</c>, line breaks become spaces, and the result is at most <paramref name="max"/> characters.
    /// </summary>
    public static string Scrub(string? text, int max = MaxDetail)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(text.Length, max + Elided.Length));
        var i = 0;
        while (i < text.Length && builder.Length <= max)
        {
            var c = text[i];
            if (IsOpeningQuote(text, i) && FindClosingQuote(text, i) is var close and > 0)
            {
                var inner = text.AsSpan(i + 1, close - i - 1);
                builder.Append(c);
                builder.Append(PathStart(inner, 0) ? FileName(inner.ToString()) : Elided);
                builder.Append(text[close]);
                i = close + 1;
                continue;
            }

            if (PathStart(text, i) && (i == 0 || IsBoundary(text[i - 1])))
            {
                var end = PathEnd(text, i);
                builder.Append(FileName(text[i..end]));
                i = end;
                continue;
            }

            builder.Append(c is '\r' or '\n' or '\t' ? ' ' : c);
            i++;
        }

        if (builder.Length > max)
        {
            builder.Length = max;
            builder.Append(Elided);
        }

        return builder.ToString().Trim();
    }

    /// <summary>The last <paramref name="lines"/> non-empty lines of <paramref name="text"/> (a worker's stderr), each scrubbed, joined by <c> | </c>.</summary>
    public static string Tail(string? text, int lines = 2)
    {
        if (string.IsNullOrWhiteSpace(text) || lines <= 0)
        {
            return string.Empty;
        }

        var picked = new List<string>(lines);
        var end = text.Length;
        while (end > 0 && picked.Count < lines)
        {
            var start = text.LastIndexOf('\n', end - 1);
            var line = text.AsSpan(start + 1, end - start - 1).Trim();
            if (!line.IsEmpty)
            {
                picked.Insert(0, Scrub(line.ToString(), MaxDetail / lines));
            }

            end = start < 0 ? 0 : start;
        }

        return string.Join(" | ", picked);
    }

    private static bool PathStart(ReadOnlySpan<char> text, int i)
    {
        if (i + 2 < text.Length && char.IsAsciiLetter(text[i]) && text[i + 1] == ':' && text[i + 2] is '\\' or '/')
        {
            return true; // C:\ or C:/
        }

        if (i + 1 < text.Length && text[i] == '\\' && text[i + 1] == '\\')
        {
            return true; // \\server\share
        }

        // /home/user/x, ~/x: a rooted Unix path has a second separator, so "and/or" or a lone "/" is not taken for one.
        var rest = i < text.Length && text[i] == '~' ? i + 1 : i;
        return rest < text.Length && text[rest] == '/' && rest + 1 < text.Length && text[(rest + 1)..].IndexOf('/') > 0;
    }

    private static bool PathStart(string text, int i) => PathStart(text.AsSpan(), i);

    /// <summary>
    /// Where an unquoted path ends: it may hold spaces ("C:\Users\Olena\Weekly sync\mic.wav"), so it runs to the end of the word after
    /// its last separator on the same line.
    /// </summary>
    private static int PathEnd(string text, int start)
    {
        var lineEnd = text.IndexOfAny(['\r', '\n', '\t', '"', '\'', '<', '>', '|', '(', ')'], start);
        if (lineEnd < 0)
        {
            lineEnd = text.Length;
        }

        var lastSeparator = text.AsSpan(start, lineEnd - start).LastIndexOfAny('\\', '/') + start;
        var end = lastSeparator + 1;
        while (end < lineEnd && !char.IsWhiteSpace(text[end]) && text[end] is not (',' or ';'))
        {
            end++;
        }

        // A sentence's full stop or colon after the file name stays outside it.
        while (end > lastSeparator + 1 && text[end - 1] is '.' or ':')
        {
            end--;
        }

        return end;
    }

    private static bool IsBoundary(char c) => char.IsWhiteSpace(c) || c is '(' or '[' or '=' or ':' or ',';

    private static bool IsQuote(char c) => c is '\'' or '"' or '`' or '\u2018' or '\u2019' or '\u201C' or '\u201D' or '\u00AB' or '\u00BB';

    /// <summary>A quote that opens a value: at the start or after a space or bracket (the apostrophe of "can't" does not).</summary>
    private static bool IsOpeningQuote(string text, int i) => IsQuote(text[i]) && (i == 0 || IsBoundary(text[i - 1]));

    private static int FindClosingQuote(string text, int open)
    {
        for (var i = open + 1; i < text.Length; i++)
        {
            if (IsQuote(text[i]) && (i + 1 == text.Length || !char.IsLetterOrDigit(text[i + 1])))
            {
                return i;
            }

            if (text[i] is '\r' or '\n')
            {
                return -1;
            }
        }

        return -1;
    }
}
