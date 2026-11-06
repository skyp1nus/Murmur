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
