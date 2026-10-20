using Murmur.App.Localization;

namespace Murmur.App.Resources;

/// <summary>Formats the placeholders of <see cref="Strings"/> with the culture of the UI language (<see cref="UiCulture"/>).</summary>
internal static class UiText
{
    public static string Format(string format, object? arg0) =>
        string.Format(UiCulture.Culture, format, arg0);

    public static string Format(string format, object? arg0, object? arg1) =>
        string.Format(UiCulture.Culture, format, arg0, arg1);

    public static string Format(string format, object? arg0, object? arg1, object? arg2) =>
        string.Format(UiCulture.Culture, format, arg0, arg1, arg2);

    public static string Format(string format, object? arg0, object? arg1, object? arg2, object? arg3) =>
        string.Format(UiCulture.Culture, format, arg0, arg1, arg2, arg3);
}
