namespace Murmur.Core.Calls;

/// <summary>
/// The friendly name of the app behind a call: the process name of the render session (with or without <c>.exe</c>, any case)
/// mapped to the name people know (<c>ms-teams</c> → Microsoft Teams, <c>msedge</c> → Microsoft Edge, …). An unknown process keeps
/// its own name, so the toast always names something.
/// </summary>
public static class CallAppNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ms-teams"] = "Microsoft Teams",
        ["teams"] = "Microsoft Teams",
        ["msteams"] = "Microsoft Teams",
        ["zoom"] = "Zoom",
        ["zoom.us"] = "Zoom",
        ["cpthost"] = "Zoom",
        ["chrome"] = "Google Chrome",
        ["msedge"] = "Microsoft Edge",
        ["msedgewebview2"] = "Microsoft Edge",
        ["firefox"] = "Firefox",
        ["brave"] = "Brave",
        ["opera"] = "Opera",
        ["vivaldi"] = "Vivaldi",
        ["arc"] = "Arc",
        ["slack"] = "Slack",
        ["discord"] = "Discord",
        ["webex"] = "Webex",
        ["webexmta"] = "Webex",
        ["ciscowebexstart"] = "Webex",
        ["atmgr"] = "Webex",
        ["skype"] = "Skype",
        ["lync"] = "Skype for Business",
        ["telegram"] = "Telegram",
        ["whatsapp"] = "WhatsApp",
        ["signal"] = "Signal",
        ["viber"] = "Viber",
        ["gotomeeting"] = "GoTo Meeting",
        ["g2mcomm"] = "GoTo Meeting",
        ["ringcentral"] = "RingCentral",
        ["facetime"] = "FaceTime",
    };

    /// <summary>The friendly name of <paramref name="processName"/>; the process name itself (without <c>.exe</c>) when it is not a known app.</summary>
    public static string FriendlyName(string processName)
    {
        ArgumentNullException.ThrowIfNull(processName);
        var name = Normalize(processName);
        return Names.TryGetValue(name, out var friendly) ? friendly : name;
    }

    /// <summary>
    /// The process name as it is compared and stored (the allowlist, <see cref="Settings.ProcessNameList"/>): trimmed, without a
    /// trailing <c>.exe</c>. The only normalisation of process names in Murmur.
    /// </summary>
    public static string Normalize(string processName)
    {
        ArgumentNullException.ThrowIfNull(processName);
        var name = processName.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4].TrimEnd() : name;
    }

    /// <summary>Whether two process names name the same app (case-insensitive, <c>.exe</c> ignored).</summary>
    public static bool SameProcess(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
}
