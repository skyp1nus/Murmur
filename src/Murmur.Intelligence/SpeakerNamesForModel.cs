using Murmur.Core.Sessions;

namespace Murmur.Intelligence;

/// <summary>
/// The speaker names as the model gets them: the user types them, so they are made safe for a prompt. Quotes (<c>"</c>, <c>«</c>, <c>»</c>)
/// become apostrophes (a name cannot close the quotation the prompt puts it in) and two speakers with the same name (they would be
/// indistinguishable to the model) fall back to the default labels.
/// </summary>
internal static class SpeakerNamesForModel
{
    public static SpeakerNames ForModel(this SpeakerNames? names)
    {
        if (names is null)
        {
            return SpeakerNames.Default;
        }

        var me = Clean(names.Me);
        var others = Clean(names.Others);
        return me is not null && others is not null && string.Equals(me, others, StringComparison.OrdinalIgnoreCase)
            ? SpeakerNames.Default
            : new SpeakerNames(me, others);
    }

    private static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var text = name.Replace('"', '\'').Replace('«', '\'').Replace('»', '\'').ReplaceLineEndings(" ").Trim();
        return text.Length == 0 ? null : text;
    }
}
