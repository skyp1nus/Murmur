using Murmur.App.Resources;

namespace Murmur.App.ViewModels;

/// <summary>The names of the transcript languages (the 25 European languages of Parakeet v3) in the UI language.</summary>
internal static class LanguageNames
{
    /// <summary>"English" for <c>en</c> (also <c>en-US</c>); the upper-case code when the language has no name here; <c>null</c> for none.</summary>
    public static string? Of(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var value = code.Trim();
        var dash = value.IndexOfAny(['-', '_']);
        var language = (dash > 0 ? value[..dash] : value).ToLowerInvariant();
        return language switch
        {
            "bg" => Strings.Lang_bg,
            "hr" => Strings.Lang_hr,
            "cs" => Strings.Lang_cs,
            "da" => Strings.Lang_da,
            "nl" => Strings.Lang_nl,
            "en" => Strings.Lang_en,
            "et" => Strings.Lang_et,
            "fi" => Strings.Lang_fi,
            "fr" => Strings.Lang_fr,
            "de" => Strings.Lang_de,
            "el" => Strings.Lang_el,
            "hu" => Strings.Lang_hu,
            "it" => Strings.Lang_it,
            "lv" => Strings.Lang_lv,
            "lt" => Strings.Lang_lt,
            "mt" => Strings.Lang_mt,
            "pl" => Strings.Lang_pl,
            "pt" => Strings.Lang_pt,
            "ro" => Strings.Lang_ro,
            "sk" => Strings.Lang_sk,
            "sl" => Strings.Lang_sl,
            "es" => Strings.Lang_es,
            "sv" => Strings.Lang_sv,
            "ru" => Strings.Lang_ru,
            "uk" => Strings.Lang_uk,
            _ => language.ToUpperInvariant(),
        };
    }
}
