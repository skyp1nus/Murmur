namespace Murmur.App.Tests.Design;

/// <summary>The screen (and the state of it) an artboard shows.</summary>
internal enum BoardScreen
{
    /// <summary>1 · Library, with the demo recordings.</summary>
    Library,

    /// <summary>2 · An active recording at 00:24:13.</summary>
    Recording,

    /// <summary>3 · "Client demo — Q4 roadmap" at 12:21.</summary>
    Session,

    /// <summary>4 · Ask AI with the two exchanges.</summary>
    Chat,

    /// <summary>5 · Settings, top of the page, full height.</summary>
    Settings,

    Onboarding1Welcome,

    /// <summary>The microphone is refused (the "allow" card), the system meter shows sound.</summary>
    Onboarding2Audio,

    /// <summary>Parakeet downloading, 0.31 of 0.67 GB.</summary>
    Onboarding3Models,

    /// <summary>Everything downloaded.</summary>
    Onboarding3ModelsReady,

    Onboarding4Preferences,

    /// <summary>Listening, 00:10, nothing transcribed yet.</summary>
    Onboarding5FirstRecording,
}

/// <summary>
/// One artboard: how to put the app into its state (screen, theme, size) and what the comparison leaves out. The size is the
/// size of <c>docs/design/reference/&lt;Name&gt;.png</c> (a test checks that).
/// </summary>
/// <param name="Name">The file name of the artboard and of its reference render, without the extension.</param>
/// <param name="Ignored">
/// Rectangles of the reference that are not compared, only for content the app cannot reproduce: the video frame
/// placeholder, waveform bars the canvas draws from a formula, and the [VERSION] / [LICENSE] / [X.X] GB placeholders.
/// </param>
/// <param name="ExcludedTexts">
/// Canvas texts that are placeholders the app replaces with real values (<c>[VERSION]</c>, <c>[X.X] GB</c>, paths with <c>[USER]</c>) or that
/// belong to the video frame: left out of the text gates, listed as excluded in the text report. Explicit, each with its reason.
/// </param>
/// <param name="MaxShapeMismatchPercent">The shape-layer mismatch of drawn pixels (total) above which the test fails; <c>null</c> = report only. Screen tasks set it.</param>
/// <param name="MinTextMatchedPercent">The share of reference texts the app must have (by text); <c>null</c> = report only.</param>
/// <param name="MaxSidebarShapePercent">The shape-layer mismatch of the sidebar strip (x &lt; 241) above which the test fails; <c>null</c> = report only. The sidebar is the shell's, so every main board has it.</param>
/// <param name="MaxTextGeometryErrors">The most matched texts allowed to be off in position, width, lines, size, weight or family; <c>null</c> = report only.</param>
internal sealed record DesignBoard(
    string Name,
    BoardScreen Screen,
    bool Dark,
    int Width,
    int Height,
    IReadOnlyList<IgnoreRect> Ignored,
    double? MaxShapeMismatchPercent = null,
    double? MinTextMatchedPercent = null,
    int? MaxTextGeometryErrors = null,
    IReadOnlyList<ExcludedText>? ExcludedTexts = null,
    double? MaxSidebarShapePercent = null)
{
    /// <summary>The page colour of the theme (docs/design/tokens.json): what the comparison treats as background.</summary>
    public (byte R, byte G, byte B) Background => ImageComparer.CanvasColor(Dark);

    /// <summary>The main screens have the 240 px sidebar of the canvas; the onboarding fills the window.</summary>
    public bool HasSidebar => Screen is BoardScreen.Library or BoardScreen.Recording or BoardScreen.Session or BoardScreen.Chat or BoardScreen.Settings;

    public bool IsOnboarding => !HasSidebar;

    /// <summary>The reference render with every glyph transparent (icons keep their colour).</summary>
    public string ShapesReferencePath => Path.Combine(RepoPaths.Root, "docs", "design", "reference", Name + ".notext.png");

    /// <summary>The rectangles that also matter without glyphs.</summary>
    public IReadOnlyList<IgnoreRect> ShapeIgnored => [.. Ignored.Where(r => !r.TextOnly)];

    public string ReferencePath => Path.Combine(RepoPaths.Root, "docs", "design", "reference", Name + ".png");

    public override string ToString() => Name;
}

/// <summary>The table of the 22 screen artboards. <c>OnbComponents</c> and <c>OnbComponentsDark</c> are component sheets and are not rendered.</summary>
internal static class DesignBoards
{
    private static readonly ExcludedText Version = new("[VERSION]", "canvas placeholder; the app shows its real version");
    private static readonly ExcludedText License = new("[LICENSE]", "canvas placeholder; the app shows its real license", Contains: true);
    private static readonly ExcludedText UserPath = new("[USER]", "canvas placeholder; the path is the user's real folder", Contains: true);
    private static readonly ExcludedText SizeSmall = new("[X.X] GB", "canvas placeholder; the app shows the real model size");
    private static readonly ExcludedText SizeLarge = new("[XX] GB", "canvas placeholder; the app shows the real model size");
    private static readonly ExcludedText FreeSpace = new("[N] GB free", "canvas placeholder; the app shows the real free space");
    private static readonly ExcludedText MicPermissionText = new("Windows will ask once. Audio is processed on this PC only.", "documented deviation (DEVIATIONS.md): Windows has no permission prompt for desktop apps");
    private static readonly ExcludedText VideoPlaceholder = new("[Screen recording frame]", "canvas placeholder of the video frame; the app shows the video");
    private static readonly ExcludedText VideoChip = new("H.264", "label of the canvas video placeholder; the app shows the video", Contains: true);

    private const string WaveformReason = "waveform bars the canvas draws from a formula";

    public static IReadOnlyList<string> Excluded { get; } = ["OnbComponents", "OnbComponentsDark"];

    public static IReadOnlyList<DesignBoard> All { get; } = Build();

    public static DesignBoard Get(string name) => All.Single(b => b.Name == name);

    /// <summary>Theory data: the board names.</summary>
    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var board in All)
        {
            data.Add(board.Name);
        }

        return data;
    }

    private static IReadOnlyList<DesignBoard> Build()
    {
        var boards = new List<DesignBoard>();
        foreach (var dark in new[] { false, true })
        {
            var suffix = dark ? "Dark" : string.Empty;
            var settingsHeight = dark ? 1821 : 1819;
            var shift = dark ? 2 : 0;

            // Library: reached 0.8 % / 1.2 % shape mismatch, every text found, 2 geometry errors (x +2.3 / +2.1 px of two chips).
            boards.Add(new("Main" + suffix, BoardScreen.Library, dark, 1440, 1406, [], MaxShapeMismatchPercent: dark ? 1.5 : 1.0, MinTextMatchedPercent: 100, MaxTextGeometryErrors: 2));
            boards.Add(new(
                "Recording" + suffix,
                BoardScreen.Recording,
                dark,
                1440,
                1000,
                [
                    new IgnoreRect(1100, 173, 276, 36, "Recording: microphone " + WaveformReason),
                    new IgnoreRect(1100, 301, 276, 36, "Recording: system audio " + WaveformReason),
                ],
                // Set at integration with the shell frame: reached 0.77 % / 0.94 % shape, every text found, 1 geometry error.
                MaxShapeMismatchPercent: dark ? 1.2 : 1.0,
                MinTextMatchedPercent: 100,
                MaxTextGeometryErrors: 1));
            boards.Add(new(
                "Session" + suffix,
                BoardScreen.Session,
                dark,
                1440,
                1100,
                [
                    new IgnoreRect(273, 165, 535, 301, "Session: screen-recording frame placeholder (the app shows the video, not a placeholder)"),
                    new IgnoreRect(292, 497, 498, 66, "Session: " + WaveformReason),
                ],
                ExcludedTexts: [VideoPlaceholder, VideoChip]));
            // Ask AI: reached 3.7 % / 1.4 % shape mismatch, every text found, 1 geometry error: the placeholder of the question box, which the
            // reference measures against the centre of the whole textarea while the canvas draws it on its first line (the app does the same).
            boards.Add(new("Chat" + suffix, BoardScreen.Chat, dark, 1440, 1080, [], MaxShapeMismatchPercent: dark ? 1.5 : 3.8, MinTextMatchedPercent: 100, MaxTextGeometryErrors: 1));
            boards.Add(new(
                "Settings" + suffix,
                BoardScreen.Settings,
                dark,
                1440,
                settingsHeight,
                [
                    new IgnoreRect(822, 1611 + shift, 70, 17, "Settings: [VERSION] placeholder", TextOnly: true),
                    new IgnoreRect(853, 1638 + shift, 70, 18, "Settings: [LICENSE] placeholder", TextOnly: true),
                ],
                ExcludedTexts: [Version, License, UserPath]));
            boards.Add(new("Onb1Welcome" + suffix, BoardScreen.Onboarding1Welcome, dark, 1280, 800, [], MaxShapeMismatchPercent: 0.8, MinTextMatchedPercent: 95, MaxTextGeometryErrors: 0));
            boards.Add(new(
                "Onb2Audio" + suffix,
                BoardScreen.Onboarding2Audio,
                dark,
                1280,
                800,
                [new IgnoreRect(671, 456, 366, 26, "Onboarding 2: system audio " + WaveformReason)],
                MaxShapeMismatchPercent: 3.0,
                MinTextMatchedPercent: 95,
                MaxTextGeometryErrors: 0,
                ExcludedTexts: [MicPermissionText]));
            boards.Add(new(
                "Onb3Models" + suffix,
                BoardScreen.Onboarding3Models,
                dark,
                1280,
                800,
                [
                    new IgnoreRect(547, 497, 65, 16, "Onboarding 3: [X.X] GB placeholder", TextOnly: true),
                    new IgnoreRect(942, 497, 57, 16, "Onboarding 3: [XX] GB placeholder", TextOnly: true),
                ],
                MaxShapeMismatchPercent: 5.0,
                MinTextMatchedPercent: 95,
                MaxTextGeometryErrors: 0,
                ExcludedTexts: [SizeSmall, SizeLarge]));
            boards.Add(new(
                "Onb3ModelsReady" + suffix,
                BoardScreen.Onboarding3ModelsReady,
                dark,
                1280,
                800,
                [
                    new IgnoreRect(547, 462, 65, 16, "Onboarding 3 ready: [X.X] GB placeholder", TextOnly: true),
                    new IgnoreRect(942, 462, 57, 16, "Onboarding 3 ready: [XX] GB placeholder", TextOnly: true),
                ],
                MaxShapeMismatchPercent: 4.5,
                MinTextMatchedPercent: 95,
                MaxTextGeometryErrors: 0,
                ExcludedTexts: [SizeSmall, SizeLarge]));
            boards.Add(new(
                "Onb4Preferences" + suffix,
                BoardScreen.Onboarding4Preferences,
                dark,
                1280,
                800,
                [new IgnoreRect(792, 404, 92, 18, "Onboarding 4: [N] GB free placeholder", TextOnly: true)],
                MaxShapeMismatchPercent: 2.8,
                MinTextMatchedPercent: 95,
                MaxTextGeometryErrors: 0,
                ExcludedTexts: [FreeSpace, UserPath]));
            boards.Add(new("Onb5FirstRecording" + suffix, BoardScreen.Onboarding5FirstRecording, dark, 1280, 800, [], MaxShapeMismatchPercent: 3.5, MinTextMatchedPercent: 95, MaxTextGeometryErrors: 0));
        }

        // The sidebar is the shell's: every main board has to match it (reached 0.84 % at most), whatever its content does.
        return [.. boards.Select(b => b.HasSidebar ? b with { MaxSidebarShapePercent = SidebarShapeLimit } : b)];
    }

    private const double SidebarShapeLimit = 1.0;
}
