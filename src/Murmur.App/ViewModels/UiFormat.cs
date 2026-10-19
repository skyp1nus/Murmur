using System.Globalization;
using Murmur.App.Resources;

namespace Murmur.App.ViewModels;

/// <summary>Text formatting shared by the screens. Month names come from the resources, so it works with invariant globalization.</summary>
internal static class UiFormat
{
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

    /// <summary>Time chip of a transcript segment: <c>mm:ss</c>, or <c>h:mm:ss</c> from one hour on.</summary>
    public static string Clock(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        var hours = (long)value.TotalHours;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{value.Minutes:00}:{value.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{value.Minutes:00}:{value.Seconds:00}");
    }

    /// <summary>
    /// Today / yesterday with the time, otherwise day and month (and the year when it is not the current one), all in the
    /// UI language: "Сьогодні, 14:30", "18 жовтня, 14:30", "18 жовтня 2025, 14:30" or "October 18, 2025, 14:30".
    /// </summary>
    public static string Date(DateTimeOffset startedAt, TimeProvider time)
    {
        var local = TimeZoneInfo.ConvertTime(startedAt, time.LocalTimeZone);
        var today = time.GetLocalNow().Date;
        var clock = string.Create(CultureInfo.InvariantCulture, $"{local.Hour:00}:{local.Minute:00}");

        if (local.Date == today)
        {
            return UiText.Format(Strings.Date_Today, clock);
        }

        if (local.Date == today.AddDays(-1))
        {
            return UiText.Format(Strings.Date_Yesterday, clock);
        }

        var month = MonthName(local.Month);
        var day = local.Day.ToString(CultureInfo.InvariantCulture);
        return local.Year == today.Year
            ? UiText.Format(Strings.Date_ThisYear, day, month, clock)
            : UiText.Format(Strings.Date_OtherYear, day, month, clock, local.Year.ToString(CultureInfo.InvariantCulture));
    }

    private static string MonthName(int month) => month switch
    {
        1 => Strings.Month_01,
        2 => Strings.Month_02,
        3 => Strings.Month_03,
        4 => Strings.Month_04,
        5 => Strings.Month_05,
        6 => Strings.Month_06,
        7 => Strings.Month_07,
        8 => Strings.Month_08,
        9 => Strings.Month_09,
        10 => Strings.Month_10,
        11 => Strings.Month_11,
        12 => Strings.Month_12,
        _ => throw new ArgumentOutOfRangeException(nameof(month), month, "Month must be 1..12."),
    };
}
