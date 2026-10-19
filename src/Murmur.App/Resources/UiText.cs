using System.Globalization;

namespace Murmur.App.Resources;

/// <summary>Formats the placeholders of <see cref="Strings"/> with the UI culture (the one the resources were picked for).</summary>
internal static class UiText
{
    public static string Format(string format, object? arg0) =>
        string.Format(CultureInfo.CurrentUICulture, format, arg0);

    public static string Format(string format, object? arg0, object? arg1, object? arg2) =>
        string.Format(CultureInfo.CurrentUICulture, format, arg0, arg1, arg2);

    public static string Format(string format, object? arg0, object? arg1, object? arg2, object? arg3) =>
        string.Format(CultureInfo.CurrentUICulture, format, arg0, arg1, arg2, arg3);
}
