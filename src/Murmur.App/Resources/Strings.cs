using System.Globalization;
using System.Resources;
using Murmur.App.Localization;

namespace Murmur.App.Resources;

/// <summary>
/// UI texts. The properties (one per key of <c>Strings.resx</c>) are generated at build time into
/// <c>Strings.keys.g.cs</c>; they all read through <see cref="Get"/>.
/// </summary>
/// <remarks>
/// <para>English (<c>Strings.resx</c>) is the neutral language, the default and the fallback; Ukrainian is <c>Strings.uk.resx</c>
/// (decision D-039). Both are embedded in this assembly and looked up with two <see cref="ResourceManager"/> instances instead
/// of one with satellite assemblies, because Native AOT cannot load satellite assemblies (the Ukrainian texts would never be found).</para>
/// <para>The language is <see cref="UiCulture.Language"/>: English until the app sets it once at start-up from
/// <c>AppSettings.Language</c> (decision D-032). The OS culture is never consulted.</para>
/// </remarks>
public static partial class Strings
{
    private const string BaseName = "Murmur.App.Resources.Strings";

    private static readonly ResourceManager English = new(BaseName, typeof(Strings).Assembly);

    private static readonly ResourceManager Ukrainian = new(BaseName + ".uk", typeof(Strings).Assembly);

    /// <summary>The text for <paramref name="name"/> in the UI language; English when Ukrainian has no such key.</summary>
    internal static string Get(string name)
    {
        if (string.Equals(UiCulture.Language, UiCulture.Ukrainian, StringComparison.Ordinal)
            && Ukrainian.GetString(name, CultureInfo.InvariantCulture) is { } ukrainian)
        {
            return ukrainian;
        }

        return English.GetString(name, CultureInfo.InvariantCulture) ?? name;
    }

    /// <summary>The text of <paramref name="name"/> in a given language (<c>uk</c> = Ukrainian, anything else = English); for tests and tools.</summary>
    internal static string? Get(string name, string language) =>
        string.Equals(language?.Trim(), UiCulture.Ukrainian, StringComparison.OrdinalIgnoreCase)
            ? Ukrainian.GetString(name, CultureInfo.InvariantCulture)
            : English.GetString(name, CultureInfo.InvariantCulture);
}
