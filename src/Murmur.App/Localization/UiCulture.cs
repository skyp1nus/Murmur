using System.Globalization;

namespace Murmur.App.Localization;

/// <summary>
/// Picks the language of the UI texts (<c>Resources/Strings*.resx</c>) from <c>AppSettings.Language</c>.
/// </summary>
/// <remarks>
/// <para>Decision D-032: the language is read once, at start-up; changing it in the settings takes effect on the next
/// start. Nothing re-reads the resources while the app runs.</para>
/// <para><c>uk</c> (the default, also for unknown values) is the neutral language of the resources; <c>en</c> comes from
/// the <c>en</c> satellite assembly of Murmur.App. Under invariant globalization (no ICU) a specific culture cannot be
/// created and the UI stays Ukrainian.</para>
/// </remarks>
public static class UiCulture
{
    public const string Ukrainian = "uk";

    public const string English = "en";

    /// <summary>The culture for a settings value: <c>en</c> = English, anything else = Ukrainian.</summary>
    public static CultureInfo Resolve(string? language)
    {
        var name = string.Equals(language?.Trim(), English, StringComparison.OrdinalIgnoreCase) ? English : Ukrainian;
        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    /// <summary>
    /// Makes <paramref name="language"/> the UI culture of the process: the current thread and every thread created
    /// later (view models format on pool threads as well).
    /// </summary>
    public static CultureInfo Apply(string? language) => Apply(language, SetProcessCulture);

    /// <summary>The seam for tests: <paramref name="setCulture"/> receives the resolved culture instead of the process-wide setters.</summary>
    internal static CultureInfo Apply(string? language, Action<CultureInfo> setCulture)
    {
        ArgumentNullException.ThrowIfNull(setCulture);
        var culture = Resolve(language);
        setCulture(culture);
        return culture;
    }

    private static void SetProcessCulture(CultureInfo culture)
    {
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
