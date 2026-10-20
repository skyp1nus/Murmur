using Murmur.App.Localization;
using Murmur.Core.Recording;
using Murmur.Core.Sessions;

namespace Murmur.App.Resources;

/// <summary>
/// Words for the problems Core stores as codes (<see cref="SessionNotes"/>): recorder warnings in <c>SessionRecord.Error</c>,
/// processing failures, statuses. Core never writes a sentence for the user; the texts live in <c>Strings*.resx</c> in both languages.
/// </summary>
/// <remarks>Free text (a technical English message, or a Ukrainian sentence an earlier version stored) and a code this version does not know are shown as they are.</remarks>
internal static class SessionNoteText
{
    /// <summary>The resource key of the text of <paramref name="code"/>, or <c>null</c> for a code the App does not know.</summary>
    internal static string? ResourceKey(string code) => code switch
    {
        SessionNotes.MicrophoneDenied => nameof(Strings.Note_MicDenied),
        SessionNotes.SystemAudioDenied => nameof(Strings.Note_SystemDenied),
        SessionNotes.MicrophoneUnavailable => nameof(Strings.Note_MicUnavailable),
        SessionNotes.SystemAudioUnavailable => nameof(Strings.Note_SystemUnavailable),
        SessionNotes.OnlySystemAudio => nameof(Strings.Note_OnlySystem),
        SessionNotes.OnlyMicrophone => nameof(Strings.Note_OnlyMic),
        SessionNotes.ScreenFramesDropped => nameof(Strings.Recording_ScreenFramesDropped),
        SessionNotes.ScreenCpuFallback => nameof(Strings.Recording_ScreenCpuFallback),
        SessionNotes.ScreenOther => nameof(Strings.Recording_ScreenProblems),
        SessionNotes.ScreenStopped => nameof(Strings.Recording_ScreenStopped),
        SessionNotes.AsrMissing => nameof(Strings.Note_AsrMissing),
        SessionNotes.IndexFailed => nameof(Strings.Note_IndexFailed),
        _ => null,
    };

    /// <summary>The text of a recorder warning in the UI language.</summary>
    public static string Describe(RecorderWarning warning, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(warning);
        return DescribeNote(new SessionNote(SessionNotes.CodeOf(warning.Code), warning.Detail, SessionNotes.CodeOf(warning.Code)), language);
    }

    /// <summary>
    /// The text of a stored problem list (<c>SessionRecord.Error</c>, a status message) in the UI language, or in
    /// <paramref name="language"/> (<c>en</c>/<c>uk</c>) when given; several notes are joined with a space. <c>null</c> for nothing.
    /// </summary>
    public static string? Describe(string? stored, string? language = null)
    {
        var notes = SessionNotes.Parse(stored);
        if (notes.Count == 0)
        {
            return null;
        }

        if (notes.Count == 1)
        {
            return DescribeNote(notes[0], language);
        }

        var parts = new string[notes.Count];
        for (var i = 0; i < notes.Count; i++)
        {
            parts[i] = DescribeNote(notes[i], language);
        }

        return string.Join(' ', parts);
    }

    /// <summary>The text of one note; free text and unknown codes as they are.</summary>
    public static string DescribeNote(SessionNote note, string? language = null)
    {
        if (note.Code is not { } code || ResourceKey(code) is not { } key)
        {
            return note.Text;
        }

        var format = language is null ? Strings.Get(key) : Strings.Get(key, language) ?? Strings.Get(key, UiCulture.English) ?? key;
        return format.Contains('{', StringComparison.Ordinal) ? UiText.Format(format, note.Detail ?? string.Empty) : format;
    }
}
