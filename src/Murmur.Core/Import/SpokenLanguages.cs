namespace Murmur.Core.Import;

/// <summary>The spoken languages Parakeet TDT v3 transcribes: code (ISO 639-1) and the language's own name (autonym).</summary>
/// <remarks>
/// The names are the same in every UI language (a language picker lists each language in its own language), so they live here and not
/// in the App's resources. The three Cyrillic autonyms are written with escapes: Core holds no Cyrillic literals (its UI words are codes
/// the App translates); these are data, the names of the languages themselves.
/// </remarks>
public static class SpokenLanguages
{
    private static readonly (string Code, string NativeName)[] Languages =
    [
        ("bg", "\u0411\u044A\u043B\u0433\u0430\u0440\u0441\u043A\u0438"),
        ("cs", "Čeština"),
        ("da", "Dansk"),
        ("de", "Deutsch"),
        ("el", "Ελληνικά"),
        ("en", "English"),
        ("es", "Español"),
        ("et", "Eesti"),
        ("fi", "Suomi"),
        ("fr", "Français"),
        ("hr", "Hrvatski"),
        ("hu", "Magyar"),
        ("it", "Italiano"),
        ("lt", "Lietuvių"),
        ("lv", "Latviešu"),
        ("mt", "Malti"),
        ("nl", "Nederlands"),
        ("pl", "Polski"),
        ("pt", "Português"),
        ("ro", "Română"),
        ("ru", "\u0420\u0443\u0441\u0441\u043A\u0438\u0439"),
        ("sk", "Slovenčina"),
        ("sl", "Slovenščina"),
        ("sv", "Svenska"),
        ("uk", "\u0423\u043A\u0440\u0430\u0457\u043D\u0441\u044C\u043A\u0430"),
    ];

    /// <summary>The 25 languages, by code.</summary>
    public static IReadOnlyList<(string Code, string NativeName)> All { get; } = Array.AsReadOnly(Languages);
}
