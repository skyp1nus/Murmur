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

    /// <summary>
    /// The time a library row shows: "10:30" for today, yesterday and anything older than a week (the group heading has the day),
    /// "Mon, 14:00" for the days in between (they share the "This week" heading).
    /// </summary>
    public static string RowTime(DateTimeOffset startedAt, TimeProvider time)
    {
        var local = TimeZoneInfo.ConvertTime(startedAt, time.LocalTimeZone);
        var clock = string.Create(CultureInfo.InvariantCulture, $"{local.Hour:00}:{local.Minute:00}");
        return DaysAgo(local, time) is >= 2 and <= 6 ? UiText.Format(Strings.Library_RowWeekday, WeekdayName(local.DayOfWeek), clock) : clock;
    }

    /// <summary>Whole local days between the start of a recording and today (0 = today, 1 = yesterday).</summary>
    public static int DaysAgo(DateTimeOffset local, TimeProvider time) => (int)(time.GetLocalNow().Date - local.Date).TotalDays;

    /// <summary>"Mic, system audio, screen": the sources a recording has, in that order.</summary>
    public static string Sources(bool microphone, bool systemAudio, bool screen)
    {
        var parts = new List<string>(3);
        if (microphone)
        {
            parts.Add(Strings.Library_SourceMic);
        }

        if (systemAudio)
        {
            parts.Add(Strings.Library_SourceSystem);
        }

        if (screen)
        {
            parts.Add(Strings.Library_SourceScreen);
        }

        return string.Join(Strings.Library_ListSeparator, parts);
    }

    /// <summary>The heading of a day older than the last week: "Mon, Oct 5".</summary>
    public static string DayHeading(DateTimeOffset day)
    {
        return UiText.Format(Strings.Library_DayHeading, WeekdayName(day.DayOfWeek), TitleMonthName(day.Month), day.Day.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>"Oct 5": a day without the weekday (the range of the date filter).</summary>
    public static string ShortDate(DateTime day) =>
        UiText.Format(Strings.Library_ShortDate, TitleMonthName(day.Month), day.Day.ToString(CultureInfo.InvariantCulture));

    private static string WeekdayName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => Strings.Weekday_Mon,
        DayOfWeek.Tuesday => Strings.Weekday_Tue,
        DayOfWeek.Wednesday => Strings.Weekday_Wed,
        DayOfWeek.Thursday => Strings.Weekday_Thu,
        DayOfWeek.Friday => Strings.Weekday_Fri,
        DayOfWeek.Saturday => Strings.Weekday_Sat,
        _ => Strings.Weekday_Sun,
    };

    /// <summary>
    /// The title of a recording that was started without a name, like the canvas: "Untitled — Oct 18, 01:12" (English) or
    /// "Без назви — 18 жовтня, 01:12" (Ukrainian). The month comes from the resources.
    /// </summary>
    public static string DefaultTitle(DateTimeOffset startedAt, TimeProvider time)
    {
        var local = TimeZoneInfo.ConvertTime(startedAt, time.LocalTimeZone);
        var clock = string.Create(CultureInfo.InvariantCulture, $"{local.Hour:00}:{local.Minute:00}");
        return UiText.Format(Strings.Recording_DefaultTitle, local.Day.ToString(CultureInfo.InvariantCulture), TitleMonthName(local.Month), clock);
    }

    /// <summary>A byte count for the UI: GB with two decimals from 100 MB (decimal units, like the mockup's "0,67 ГБ"), else MB or KB.</summary>
    public static string Size(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        return bytes switch
        {
            >= 100_000_000 => UiText.Format(Strings.Size_Gb, bytes / 1_000_000_000d),
            >= 1_000_000 => UiText.Format(Strings.Size_Mb, bytes / 1_000_000d),
            _ => UiText.Format(Strings.Size_Kb, bytes / 1_000d),
        };
    }

    private static string TitleMonthName(int month) => month switch
    {
        1 => Strings.TitleMonth_01,
        2 => Strings.TitleMonth_02,
        3 => Strings.TitleMonth_03,
        4 => Strings.TitleMonth_04,
        5 => Strings.TitleMonth_05,
        6 => Strings.TitleMonth_06,
        7 => Strings.TitleMonth_07,
        8 => Strings.TitleMonth_08,
        9 => Strings.TitleMonth_09,
        10 => Strings.TitleMonth_10,
        11 => Strings.TitleMonth_11,
        12 => Strings.TitleMonth_12,
        _ => throw new ArgumentOutOfRangeException(nameof(month), month, "Month must be 1..12."),
    };

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
