namespace Murmur.App.Controls;

/// <summary>
/// The recent peak levels of one track, for the bars of the Tracks card: a fixed number of past samples, the newest last.
/// Values are normalised heights in [0, 1] on a −60 dB .. 0 dB scale, so quiet speech still draws a visible bar.
/// </summary>
/// <remarks>Single writer (the UI thread, <see cref="Push"/>); <see cref="Changed"/> tells the control to redraw.</remarks>
public sealed class LevelHistory
{
    /// <summary>Past samples, drawn in ink (the canvas: 26 of 36 bars).</summary>
    public const int PastCount = 26;

    /// <summary>Slots after the newest sample, drawn as light stubs: the part of the strip that has not happened yet (10 of 36 bars).</summary>
    public const int FutureCount = 10;

    /// <summary>Peak levels below this (dB) draw the minimum bar.</summary>
    public const double FloorDb = -60;

    private readonly float[] _values = new float[PastCount];

    /// <summary>Raised after <see cref="Push"/> or <see cref="Clear"/>.</summary>
    public event Action? Changed;

    /// <summary>The past samples, oldest first, as heights in [0, 1].</summary>
    public ReadOnlySpan<float> Values => _values;

    /// <summary>Appends the latest peak (linear, 0..1) and drops the oldest sample.</summary>
    public void Push(float peak)
    {
        _values.AsSpan(1).CopyTo(_values);
        _values[^1] = Normalize(peak);
        Changed?.Invoke();
    }

    /// <summary>Back to a flat line.</summary>
    public void Clear()
    {
        Array.Clear(_values);
        Changed?.Invoke();
    }

    /// <summary>A linear peak as a height in [0, 1]: 0 at −60 dB and below, 1 at 0 dB; NaN counts as silence.</summary>
    public static float Normalize(float peak)
    {
        if (!(peak > 0f))
        {
            return 0f;
        }

        var db = DecibelsOf(peak);
        return (float)Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);
    }

    /// <summary>The level of a linear peak in dB, not below <see cref="FloorDb"/>.</summary>
    public static double DecibelsOf(float peak) =>
        peak > 0f ? Math.Max(FloorDb, 20 * Math.Log10(Math.Min(peak, 1f))) : FloorDb;
}
