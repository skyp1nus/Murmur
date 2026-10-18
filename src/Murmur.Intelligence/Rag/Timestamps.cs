using System.Globalization;

namespace Murmur.Intelligence.Rag;

/// <summary>Timeline labels used in prompts and citations: <c>12:34</c> below an hour, <c>1:02:03</c> above.</summary>
public static class Timestamps
{
    public static string Format(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        var totalSeconds = (long)time.TotalSeconds;
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds / 60 % 60;
        var seconds = totalSeconds % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:D2}:{seconds:D2}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes:D2}:{seconds:D2}");
    }

    /// <summary><c>[12:00–12:45]</c>.</summary>
    public static string FormatRange(TimeSpan start, TimeSpan end) => "[" + Format(start) + "–" + Format(end) + "]";
}
