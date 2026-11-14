using System.Globalization;

namespace Murmur.App.Localization;

/// <summary>
/// The language of the UI texts (<c>Resources/Strings*.resx</c>), taken from <c>AppSettings.Language</c>.
/// </summary>
/// <remarks>
/// <para>Decision D-032: the language is read once, at start-up (<see cref="Apply(string?)"/>); changing it in the
/// settings takes effect on the next start. Nothing re-reads the resources while the app runs.</para>
/// <para>The language is explicit and never taken from the OS: until <see cref="Apply(string?)"/> is called it is
/// <c>en</c>, whatever <see cref="CultureInfo.CurrentUICulture"/> says. <c>en</c> (the default, also for unknown or empty
/// values, D-039) is the neutral language of the resources; <c>uk</c> is the embedded <c>Strings.uk.resources</c> of Murmur.App.dll.
/// Under invariant globalization (no ICU) a specific culture cannot be created: texts still switch, number and date
/// formatting uses the invariant culture.</para>
/// </remarks>
public static class UiCulture
{
    public const string Ukrainian = "uk";

    public const string English = "en";

    private static readonly AsyncLocal<State?> Scoped = new();

    // The one process-wide state: an immutable object swapped as a whole (Volatile.Write in Store, Volatile.Read in Current), so a
    // reader on another thread never sees the language of one Apply together with the culture of another.
    private static State s_process = new(English, Resolve(English));

    /// <summary>The UI language in effect, <c>en</c> or <c>uk</c>.</summary>
    public static string Language => Current.Language;

    /// <summary>The culture for formatting numbers and dates in the UI language.</summary>
    public static CultureInfo Culture => Current.Culture;

    /// <summary>
    /// The language and its culture in effect, read once: the two always belong together, even while another thread calls
    /// <see cref="Apply(string?)"/>. Use it when both are needed; two separate reads of <see cref="Language"/> and
    /// <see cref="Culture"/> may straddle an <see cref="Apply(string?)"/>.
    /// </summary>
    internal static (string Language, CultureInfo Culture) Snapshot
    {
        get
        {
            var state = Current;
            return (state.Language, state.Culture);
        }
    }

    private static State Current => Scoped.Value ?? Volatile.Read(ref s_process);

    /// <summary>
    /// The language for a settings value: <c>uk</c> and any <c>uk-*</c> region variant (case-insensitive, surrounding spaces
    /// ignored) = Ukrainian; anything else (<c>en</c>, <c>en-US</c>, unknown, empty, <c>null</c>) = English.
    /// </summary>
    public static string Normalize(string? language)
    {
        var value = language?.Trim();
        return value is not null
            && (string.Equals(value, Ukrainian, StringComparison.OrdinalIgnoreCase)
                || value.StartsWith(Ukrainian + "-", StringComparison.OrdinalIgnoreCase))
            ? Ukrainian
            : English;
    }

    /// <summary>The culture for a settings value (see <see cref="Normalize"/>).</summary>
    public static CultureInfo Resolve(string? language)
    {
        try
        {
            return CultureInfo.GetCultureInfo(Normalize(language));
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    /// <summary>Makes <paramref name="language"/> the UI language of the process. Call once, before any text is looked up.</summary>
    public static CultureInfo Apply(string? language) => Apply(language, Store);

    /// <summary>The seam for tests: <paramref name="store"/> receives the language and culture instead of the process-wide state.</summary>
    internal static CultureInfo Apply(string? language, Action<string, CultureInfo> store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var culture = Resolve(language);
        store(Normalize(language), culture);
        return culture;
    }

    /// <summary>
    /// Test seam: makes <paramref name="language"/> the UI language for the current async flow only (the process-wide
    /// language is untouched), so tests that run in parallel do not see each other's language. Dispose to undo.
    /// </summary>
    internal static IDisposable Override(string language) => new Scope(Normalize(language), Resolve(language));

    private static void Store(string language, CultureInfo culture) => Volatile.Write(ref s_process, new State(language, culture));

    /// <summary>An immutable language with its culture; replaced as a whole, never edited.</summary>
    private sealed class State(string language, CultureInfo culture)
    {
        public string Language { get; } = language;

        public CultureInfo Culture { get; } = culture;
    }

    private sealed class Scope : IDisposable
    {
        private readonly State? _previous;

        public Scope(string language, CultureInfo culture)
        {
            _previous = Scoped.Value;
            Scoped.Value = new State(language, culture);
        }

        public void Dispose() => Scoped.Value = _previous;
    }
}
