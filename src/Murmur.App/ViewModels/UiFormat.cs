using System.Globalization;

namespace Murmur.App.ViewModels;

/// <summary>Text formatting shared by the screens. Hand-written so it works with invariant globalization.</summary>
internal static class UiFormat
{
    private static readonly string[] MonthsGenitive =
    [
        "січня", "лютого", "березня", "квітня", "травня", "червня",
        "липня", "серпня", "вересня", "жовтня", "листопада", "грудня",
    ];

    /// <summary><c>HH:MM:SS</c>; hours keep growing past 99 and never wrap.</summary>
    public static string Duration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        var hours = (long)value.TotalHours;
        return string.Create(CultureInfo.InvariantCulture, $"{hours:00}:{value.Minutes:00}:{value.Seconds:00}");
    }

    /// <summary>"Сьогодні, 14:30", "Вчора, 14:30", "18 жовтня, 14:30" or "18 жовтня 2025, 14:30".</summary>
    public static string Date(DateTimeOffset startedAt, TimeProvider time)
    {
        var local = TimeZoneInfo.ConvertTime(startedAt, time.LocalTimeZone);
        var today = time.GetLocalNow().Date;
        var clock = string.Create(CultureInfo.InvariantCulture, $"{local.Hour:00}:{local.Minute:00}");

        if (local.Date == today)
        {
            return "Сьогодні, " + clock;
        }

        if (local.Date == today.AddDays(-1))
        {
            return "Вчора, " + clock;
        }

        var month = MonthsGenitive[local.Month - 1];
        return local.Year == today.Year
            ? string.Create(CultureInfo.InvariantCulture, $"{local.Day} {month}, {clock}")
            : string.Create(CultureInfo.InvariantCulture, $"{local.Day} {month} {local.Year}, {clock}");
    }
}
