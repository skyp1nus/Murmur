namespace Murmur.App.Controls;

/// <summary>
/// The motion of the floating recorder orb (#46) as pure maths of the elapsed time, shared by <see cref="FloatingOrbDisc"/> (15 Hz) and
/// the benchmark: the angle of the turn (nine seconds, linear) and the ping ring (every three seconds, out for the first 60 %: grows to
/// 1.9 times and fades from 0.55 with a cubic ease-out). No allocation, no Avalonia types (the file is linked into the benchmarks).
/// </summary>
internal static class FloatingOrbMotion
{
    /// <summary>A full turn takes nine seconds.</summary>
    public const double SpinSeconds = 9;

    /// <summary>A ping goes out every three seconds.</summary>
    public const double PingSeconds = 3;

    /// <summary>The ring ends at 1.9 times the orb.</summary>
    public const double PingScale = 1.9;

    /// <summary>The ring's opacity when it starts.</summary>
    public const double PingOpacity = 0.55;

    /// <summary>The ring is gone at 60 % of the period; for the rest of it nothing moves.</summary>
    public const double PingActiveShare = 0.6;

    /// <summary>The angle in radians, in [0, 2π): linear, one turn per <see cref="SpinSeconds"/>.</summary>
    public static double Angle(double seconds)
    {
        var turns = seconds / SpinSeconds;
        return (turns - Math.Floor(turns)) * (2 * Math.PI);
    }

    /// <summary>
    /// The ring at <paramref name="seconds"/>: true with its scale and opacity while it is out, false (at rest) for the last 40 % of
    /// the period.
    /// </summary>
    public static bool Ping(double seconds, out double scale, out double opacity)
    {
        var cycle = seconds / PingSeconds;
        var progress = (cycle - Math.Floor(cycle)) / PingActiveShare;
        if (progress >= 1)
        {
            scale = 1;
            opacity = 0;
            return false;
        }

        var left = 1 - progress;
        var eased = 1 - (left * left * left);
        scale = 1 + ((PingScale - 1) * eased);
        opacity = PingOpacity * (1 - eased);
        return true;
    }
}
