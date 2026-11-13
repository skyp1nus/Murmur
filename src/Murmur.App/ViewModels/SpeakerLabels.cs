using System.Text;
using Murmur.App.Resources;
using Murmur.Core.Transcription;

namespace Murmur.App.ViewModels;

/// <summary>The words for a speaker: the name the user chose, or "You" / "Others" in the UI language.</summary>
internal static class SpeakerLabels
{
    /// <summary>The longest name the store keeps; the name box stops there.</summary>
    public const int MaxNameLength = 40;

    public static string Default(Speaker speaker) => speaker == Speaker.Me ? Strings.Segment_Me : Strings.Segment_Others;

    public static string Of(Speaker speaker, string? name) => string.IsNullOrWhiteSpace(name) ? Default(speaker) : name;

    // The labels the index writes into a chunk (TranscriptChunker.MeLabel / OthersLabel in Murmur.Intelligence, which the UI library does not
    // reference): they are the model's words, fixed in Ukrainian whatever the UI language is. Escaped, as no Cyrillic literal belongs in the UI library; pinned to the chunker by a test.
    internal const string ModelMeLabel = "\u042F"; // Я
    internal const string ModelOthersLabel = "\u0406\u043D\u0448\u0456"; // Інші

    /// <summary>
    /// Shows the text of a chunk in the UI language: a line that starts with the model label (<c>Я: </c> / <c>Інші: </c>, as the chunker writes
    /// it for a speaker without a name) gets the UI word instead. Only the start of a line counts, so a snippet that begins in the middle of
    /// a line, a name the user gave the speaker and the same word inside a sentence stay as they are. The text that is indexed or sent to the
    /// model is never changed: this is for what the user reads.
    /// </summary>
    public static string Localize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder? result = null;
        var copied = 0;
        var lineStart = 0;
        while (lineStart < text.Length)
        {
            var label = LabelAt(text, lineStart);
            if (label.Length > 0)
            {
                var shown = label[0] == ModelMeLabel[0] ? Strings.Segment_Me : Strings.Segment_Others;
                if (!string.Equals(shown, label, StringComparison.Ordinal))
                {
                    result ??= new StringBuilder(text.Length + 16);
                    result.Append(text, copied, lineStart - copied).Append(shown);
                    copied = lineStart + label.Length;
                }
            }

            var newline = text.IndexOf('\n', lineStart);
            if (newline < 0)
            {
                break;
            }

            lineStart = newline + 1;
        }

        return result is null ? text : result.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>The model label the line at <paramref name="start"/> begins with (followed by <c>: </c>), or empty.</summary>
    private static string LabelAt(string text, int start)
    {
        var line = text.AsSpan(start);
        if (line.StartsWith(ModelMeLabel + ": ", StringComparison.Ordinal))
        {
            return ModelMeLabel;
        }

        return line.StartsWith(ModelOthersLabel + ": ", StringComparison.Ordinal) ? ModelOthersLabel : string.Empty;
    }

    /// <summary>The first letter of the label, upper case (a surrogate pair stays whole).</summary>
    public static string Initial(string label)
    {
        if (label.Length == 0)
        {
            return string.Empty;
        }

        var length = char.IsHighSurrogate(label[0]) && label.Length > 1 ? 2 : 1;
        return label[..length].ToUpperInvariant();
    }
}
