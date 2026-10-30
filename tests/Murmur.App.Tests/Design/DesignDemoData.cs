using Murmur.Core.Capture;
using Murmur.Core.Models;
using Murmur.Core.Sessions;
using Murmur.Core.Transcription;

namespace Murmur.App.Tests.Design;

/// <summary>One recording of the Library artboard.</summary>
internal sealed record DemoRecording(
    string Title,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    string Language,
    bool Microphone,
    bool SystemAudio,
    bool Screen,
    string? Snippet,
    int? TranscribingPercent = null)
{
    /// <summary>The language code the transcript stores ("English" in the canvas is <c>en</c>).</summary>
    public string LanguageCode => Language == "English" ? "en" : Language;
}

/// <summary>One line of the transcript of the Session artboard.</summary>
internal sealed record DemoLine(Speaker Speaker, int StartSeconds, string Text);

/// <summary>One question of the Chat artboard, its answer (the app has no inline chips: the text is without them) and the sources.</summary>
internal sealed record DemoExchange(string Question, string Answer, IReadOnlyList<DemoSource> Sources);

/// <summary>A source of an answer: the recording, the moment, the label of the chip and the quote.</summary>
internal sealed record DemoSource(string Recording, int AtSeconds, string Label, string Quote);

/// <summary>
/// Everything the artboards of <c>docs/design/source</c> show, as data: the demo content of the parity harness. Later tasks
/// extend it when the app learns to show more of it (<see cref="NotRepresented"/> lists what it cannot show yet).
/// </summary>
/// <remarks>
/// The canvas "today" is Oct 18 (the title of the recording screen is "Untitled — Oct 18, 01:12"); the harness clock is
/// <see cref="UiHarness.Now"/> (2026-10-18 15:00 UTC) and its time zone is UTC, so the wall-clock times below are the ones shown.
/// </remarks>
internal static class DesignDemoData
{
    private static readonly DateTimeOffset Today = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);

    // ---- Library (Main) -------------------------------------------------------------------------------------------------

    public static IReadOnlyList<DemoRecording> Recordings { get; } =
    [
        new("Client demo — Q4 roadmap", Today.AddHours(10).AddMinutes(30), Span(47, 12), "English", true, true, true,
            "…PDF export moves to next sprint: first we finish meeting summaries and action items."),
        new("Voice note", Today.AddHours(9).AddMinutes(5), Span(3, 18), "English", true, false, false,
            "Idea: draw the waveform from cached peaks instead of computing it on every open."),
        new("Weekly team sync", Today.AddHours(8), Span(58, 40), "English", true, true, false, null, TranscribingPercent: 64),
        new("Interview — Senior .NET", Today.AddDays(-1).AddHours(16), Span(62, 15), "English", true, true, false,
            "…I'd start with a lock-free ring buffer and keep the capture callback allocation-free."),
        new("QA team sync", Today.AddDays(-1).AddHours(11), Span(34, 9), "English", true, true, true,
            "…we'll leave Windows 11 ARM testing for later and do x64 first."),

        // The canvas says "Mon, 14:00"; the app prints a date ("12 October, 14:00"), see NotRepresented.
        new("Sprint retro", Today.AddDays(-6).AddHours(14), Span(41, 27), "English", true, true, false,
            "…what went well: the AOT build in CI no longer breaks after dependency updates."),
    ];

    public static IReadOnlyList<string> SidebarRecent { get; } = ["Client demo — Q4 roadmap", "Voice note", "Interview — Senior .NET"];

    public static IReadOnlyList<string> SidebarChats { get; } = ["PDF export", "Action items this week"];

    public static string AskLibraryPlaceholder => "What did we decide about PDF export?";

    public static IReadOnlyList<string> AskLibrarySuggestions { get; } = ["My action items this week", "Who mentioned the budget?", "Summarize yesterday"];

    // ---- Recording ------------------------------------------------------------------------------------------------------

    public static string RecordingTitle => "Untitled — Oct 18, 01:12";

    public static TimeSpan RecordingElapsed { get; } = new(0, 24, 13);

    /// <summary>Peak levels of the two tracks: −18 dB and −24 dB.</summary>
    public static (float Microphone, float SystemAudio) RecordingLevels { get; } = (DbToPeak(-18), DbToPeak(-24));

    /// <summary>
    /// The 26 ink bars of a track of the canvas (<c>bars(seed, level)</c> of Recording.dc.html: heights 4..32 px of a sine mix), as
    /// linear peaks the level history takes; the canvas draws them from a formula, the board ignores the strip, the demo only keeps it from being flat.
    /// </summary>
    public static IReadOnlyList<float> RecordingBars(double seed, double level)
    {
        var peaks = new List<float>();
        for (var i = 0; i < 26; i++)
        {
            var v = Math.Abs((Math.Sin((i * 0.9) + seed) * 0.6) + (Math.Sin((i * 2.3) + (seed * 2)) * 0.4));
            var height = Math.Max(4, Math.Round(v * 32 * level));
            var normalized = Math.Clamp((height - 4) / 28, 0, 1);
            peaks.Add((float)Math.Pow(10, ((normalized * 60) - 60) / 20));
        }

        return peaks;
    }

    // ---- Session --------------------------------------------------------------------------------------------------------

    public static string SessionTitle => Recordings[0].Title;

    /// <summary>The current moment of the session board: the 12:21 line.</summary>
    public static TimeSpan SessionPosition { get; } = new(0, 12, 21);

    public static string SessionSearchHit => "timestamps";

    public static IReadOnlyList<DemoLine> Transcript { get; } =
    [
        new(Speaker.Me, 712, "Okay, a quick run through the roadmap. Recording and transcription are stable, and we're finishing library search now."),
        new(Speaker.Others, 728, "Does search only match exact words, or can I ask in my own words?"),
        new(Speaker.Me, 741, "Both: full-text plus semantic. Answers come back with timestamps, so one click takes you to the exact moment."),
        new(Speaker.Others, 760, "Great. What about PDF export? We need it for our reports."),
        new(Speaker.Me, 772, "PDF export moves to next sprint — first we finish meeting summaries and action items."),
        new(Speaker.Others, 795, "Okay, then let's lock in the demo for next week."),
        new(Speaker.Me, 804, "Deal. I'll send the invite today."),
    ];

    public static string SessionSummaryOverview =>
        "Walked the client through the roadmap: recording and transcription are ready, library search is in progress. PDF export moved to next sprint; the demo is set for next week.";

    /// <summary>The waveform of the session as cached peaks (50 per second): the 96 bars of the canvas, stretched over the recording.</summary>
    public static byte[] SessionPeaks(TimeSpan duration)
    {
        var count = (int)(duration.TotalSeconds * 50);
        var peaks = new byte[count];
        for (var i = 0; i < count; i++)
        {
            var bar = Math.Min(95, (int)((long)i * 96 / count));
            var v = Math.Abs((Math.Sin(bar * 0.37) * 0.55) + (Math.Sin((bar * 1.7) + 1) * 0.3) + (Math.Sin(bar * 0.11) * 0.15));
            var height = Math.Max(4, Math.Round(v * 52));
            peaks[i] = (byte)Math.Clamp(height / 52 * 255, 0, 255);
        }

        return peaks;
    }

    // ---- Chat -----------------------------------------------------------------------------------------------------------

    public static IReadOnlyList<DemoExchange> Chat { get; } =
    [
        new(
            "What did we decide about PDF export?",
            "PDF export was moved to next sprint — meeting summaries and action items come first. The client said they need PDFs for their reports.\n\n"
            + "In the sprint retro, the team also agreed to review the report template with the designer before starting.",
            [
                new("Client demo", 772, "12:52", "“PDF export moves to next sprint…”"),
                new("Client demo", 760, "12:40", "“What about PDF export? We need it for our reports.”"),
                new("Sprint retro", 1865, "Retro · 31:05", "“…let's show the report template to the designer first.”"),
            ]),
        new(
            "And who's sending the demo invite?",
            "You are — you promised to send it the same day.",
            [new("Client demo", 804, "13:24", "“Deal. I'll send the invite today.”")]),
    ];

    public static string ChatModel => "Gemma 4 E4B · local";

    // ---- Settings -------------------------------------------------------------------------------------------------------

    /// <summary>The catalog of the Models card. The llama.cpp runtime is left out: the canvas has no row for it.</summary>
    public static IReadOnlyList<ModelInfo> ModelCatalog { get; } =
    [
        new(Ids.Asr, ModelKind.Asr, "Parakeet TDT v3 · int8", 670_000_000, Required: true),
        new(Ids.Vad, ModelKind.Vad, "Silero VAD + diarization", 640_000, Required: true),
        new(Ids.ChatSmall, ModelKind.Chat, "Gemma 4 E4B", 5_000_000_000, Required: false),
        new(Ids.ChatLarge, ModelKind.Chat, "Gemma 4 26B A4B", 17_000_000_000, Required: false),
        new(Ids.Embedding, ModelKind.Embedding, "EmbeddingGemma", 330_000_000, Required: false),
    ];

    /// <summary>The share of Gemma 4 E4B that is downloaded on the settings board.</summary>
    public const int DownloadingPercent = 42;

    /// <summary>The share of the Parakeet download on the onboarding board (0.31 of 0.67 GB).</summary>
    public const long OnboardingParakeetDone = 310_000_000;

    public static AudioDevice Microphone { get; } = new("mic-usb", "Microphone (USB Audio Device)", AudioDeviceKind.Microphone, IsDefault: true);

    public static AudioDevice Speakers { get; } = new("out-realtek", "Speakers (Realtek Audio)", AudioDeviceKind.SystemOutput, IsDefault: true);

    public static DisplayInfo Display { get; } = new("display-1", "Entire display", 1920, 1080, IsPrimary: true);

    public const string StoragePath = @"C:\Users\[USER]\Murmur";

    public const string Version = "[VERSION]";

    public const string RecordHotkey = "Ctrl+Shift+R";

    public const string ToggleScreenHotkey = "Ctrl+Shift+S";

    public const string ShowAppHotkey = "Ctrl+Shift+M";

    // ---- Onboarding -----------------------------------------------------------------------------------------------------

    /// <summary>The listening clock of the first-recording board.</summary>
    public const string OnboardingClock = "00:10";

    public const long OnboardingFreeBytes = 250_000_000_000;

    // ---- What the app cannot show yet -----------------------------------------------------------------------------------

    /// <summary>
    /// Content of the artboards that the app has no way to show yet (screen, feature). The harness leaves it out; the screen
    /// tasks add the feature and then the data here.
    /// </summary>
    public static IReadOnlyList<(string Screen, string Feature)> NotRepresented { get; } =
    [
        ("Ask AI", "Sidebar 'Chats' list: provided by the chat screen through SidebarViewModel.SetChats (the harness fills it with the canvas chats until p41-chat does)"),
        ("Library", "The two visually hidden form labels of the canvas ('Question for your library', 'Search recordings') are accessibility names in the app, not text; the language of a recording that is still transcribing comes from a transcript Core only stores when it is done"),
        ("Recording", "The strip of the Tracks bars is drawn from live levels, the canvas draws it from a formula (ignored rectangles); the 'future' bars are light stubs, not a formula"),
        ("Session", "The empty search box together with the hit 'timestamps' (the app marks a hit only for a typed query: the harness marks the rows directly); the Summary tab shows 'Decisions' where the app says 'Key points' (DEVIATIONS.md); the video frame (ignored)"),
        ("Chat", "Scope chips (Whole library / Client demo), model badge ('Gemma 4 E4B · local'), inline timestamp chips inside the answer text, Sources cards with quotes, copy/retry actions, the chats list"),
        ("Settings", "The segmented control says CPU / Vulkan / CUDA (D-042: llama.cpp has no DirectML backend); the screen option names the display and its size, not the encoder line; [VERSION] and [LICENSE] placeholders; the Preferences section (theme, languages, recording defaults, the introduction) follows About and is not on the canvas"),
        ("Onboarding", "Placeholders [X.X] GB, [XX] GB and [N] GB free are replaced by the real sizes"),
    ];

    private static TimeSpan Span(int minutes, int seconds) => new(0, minutes, seconds);

    private static float DbToPeak(double db) => (float)Math.Pow(10, db / 20);

    /// <summary>The ids of the fake model catalog.</summary>
    public static class Ids
    {
        public const string Asr = "asr";
        public const string Vad = "vad";
        public const string ChatSmall = "chat-small";
        public const string ChatLarge = "chat-large";
        public const string Embedding = "embedding";
    }
}
