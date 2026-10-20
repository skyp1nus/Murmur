using Murmur.Core.Recording;

namespace Murmur.Core.Sessions;

/// <summary>One line of the problems stored with a session (<see cref="SessionRecord.Error"/>): a stable code with an optional detail, or legacy free text.</summary>
/// <param name="Code">The stable code (e.g. <c>rec.mic.denied</c>), or <c>null</c> when the line is free text.</param>
/// <param name="Detail">The parameter of the code (a device name, an OS message), or <c>null</c>.</param>
/// <param name="Text">The line exactly as stored; for free text this is what the UI shows as it is.</param>
public readonly record struct SessionNote(string? Code, string? Detail, string Text)
{
    /// <summary>The line is a code (the UI owns its words); otherwise it is free text from before codes existed or a technical message.</summary>
    public bool IsCode => Code is not null;
}

/// <summary>
/// The language-neutral problems of a session. Core never writes a sentence for the user: it stores <b>stable codes</b>
/// with an optional detail and the App maps each code to a text in the UI language.
/// </summary>
/// <remarks>
/// <para><b>Format.</b> <see cref="SessionRecord.Error"/> is a list of lines separated by <c>\n</c>. A line is either
/// <c>code</c> or <c>code|detail</c> (the detail is everything after the first <c>|</c>, with line breaks flattened to
/// spaces), or free text. A code starts with <c>rec.</c> (recorder) or <c>proc.</c> (processing) and has only lower-case
/// letters and dots; anything else is free text: a technical English message (<c>Writing audio failed: …</c>), or a
/// Ukrainian sentence stored before codes existed. Free text is shown as it is and never migrated.</para>
/// <para><b>Codes.</b> Recorder: <see cref="MicrophoneDenied"/>, <see cref="SystemAudioDenied"/>,
/// <see cref="MicrophoneUnavailable"/> and <see cref="SystemAudioUnavailable"/> (detail = the OS message),
/// <see cref="OnlySystemAudio"/>, <see cref="OnlyMicrophone"/>, <see cref="ScreenFramesDropped"/>,
/// <see cref="ScreenCpuFallback"/>, <see cref="ScreenOther"/>, <see cref="ScreenStopped"/>. Processing:
/// <see cref="AsrMissing"/>, <see cref="IndexFailed"/> (detail = the reason). <see cref="KnownCodes"/> lists them all.</para>
/// </remarks>
public static class SessionNotes
{
    public const string MicrophoneDenied = "rec.mic.denied";

    public const string SystemAudioDenied = "rec.system.denied";

    public const string MicrophoneUnavailable = "rec.mic.unavailable";

    public const string SystemAudioUnavailable = "rec.system.unavailable";

    public const string OnlySystemAudio = "rec.only.system";

    public const string OnlyMicrophone = "rec.only.mic";

    public const string ScreenFramesDropped = "rec.screen.dropped";

    public const string ScreenCpuFallback = "rec.screen.cpu";

    public const string ScreenOther = "rec.screen.other";

    public const string ScreenStopped = "rec.screen.stopped";

    /// <summary>The ASR model is not installed (status of a session left Recorded).</summary>
    public const string AsrMissing = "proc.asr.missing";

    /// <summary>Indexing failed after the transcript was saved; the detail is the reason.</summary>
    public const string IndexFailed = "proc.index.failed";

    private const char LineSeparator = '\n';

    private const char DetailSeparator = '|';

    /// <summary>Every code Core can store, for the App to map and for tests to enumerate.</summary>
    public static IReadOnlyList<string> KnownCodes { get; } =
    [
        MicrophoneDenied, SystemAudioDenied, MicrophoneUnavailable, SystemAudioUnavailable, OnlySystemAudio, OnlyMicrophone,
        ScreenFramesDropped, ScreenCpuFallback, ScreenOther, ScreenStopped, AsrMissing, IndexFailed,
    ];

    /// <summary>The stable code stored in the session for a recorder warning.</summary>
    public static string CodeOf(RecorderWarningCode code) => code switch
    {
        RecorderWarningCode.MicrophoneDenied => MicrophoneDenied,
        RecorderWarningCode.SystemAudioDenied => SystemAudioDenied,
        RecorderWarningCode.MicrophoneUnavailable => MicrophoneUnavailable,
        RecorderWarningCode.SystemAudioUnavailable => SystemAudioUnavailable,
        RecorderWarningCode.OnlySystemAudio => OnlySystemAudio,
        RecorderWarningCode.OnlyMicrophone => OnlyMicrophone,
        RecorderWarningCode.ScreenFramesDropped => ScreenFramesDropped,
        RecorderWarningCode.ScreenCpuFallback => ScreenCpuFallback,
        RecorderWarningCode.ScreenStopped => ScreenStopped,
        _ => ScreenOther,
    };

    /// <summary>The stored line for a code and its optional detail.</summary>
    public static string Format(string code, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        return string.IsNullOrWhiteSpace(detail) ? code : code + DetailSeparator + Flatten(detail);
    }

    /// <summary>Joins two stored texts as lines; either may be <c>null</c>.</summary>
    public static string? Join(string? first, string? second) =>
        string.IsNullOrEmpty(first) ? second : string.IsNullOrEmpty(second) ? first : first + LineSeparator + second;

    /// <summary>Splits a stored text into notes; empty or <c>null</c> gives none.</summary>
    public static IReadOnlyList<SessionNote> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }

        var notes = new List<SessionNote>();
        foreach (var raw in stored.Split(LineSeparator))
        {
            var line = raw.Trim();
            if (line.Length > 0)
            {
                notes.Add(ParseLine(line));
            }
        }

        return notes;
    }

    /// <summary>The line is the code (with or without a detail), e.g. to find a session whose indexing failed.</summary>
    public static bool Has(string? stored, string code)
    {
        foreach (var note in Parse(stored))
        {
            if (string.Equals(note.Code, code, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static SessionNote ParseLine(string line)
    {
        var bar = line.IndexOf(DetailSeparator, StringComparison.Ordinal);
        var code = bar < 0 ? line : line[..bar];
        if (!LooksLikeCode(code))
        {
            return new SessionNote(null, null, line);
        }

        var detail = bar < 0 || bar == line.Length - 1 ? null : line[(bar + 1)..];
        return new SessionNote(code, detail, line);
    }

    private static bool LooksLikeCode(string text)
    {
        if (!text.StartsWith("rec.", StringComparison.Ordinal) && !text.StartsWith("proc.", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is not ((>= 'a' and <= 'z') or '.'))
            {
                return false;
            }
        }

        return !text.EndsWith('.');
    }

    private static string Flatten(string detail) =>
        detail.AsSpan().IndexOfAny('\r', '\n') < 0
            ? detail.Trim()
            : string.Join(' ', detail.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
