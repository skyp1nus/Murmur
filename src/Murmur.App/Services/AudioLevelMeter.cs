using Murmur.App.Controls;
using Murmur.Core.Capture;

namespace Murmur.App.Services;

/// <summary>
/// Measures the peak level of one audio source for a level meter. <see cref="Write"/> runs on the capture thread (a hot
/// path: no allocation, no locks, no logging, no LINQ); it only keeps the loudest sample since the last read in a field.
/// The UI polls <see cref="Level"/> on its own timer.
/// </summary>
public sealed class AudioLevelMeter : IAudioSink, ILevelSource
{
    /// <summary>Quieter than this reads as silence (dB full scale).</summary>
    private const float FloorDb = -60f;

    private float _peak;

    /// <inheritdoc />
    public void Write(ReadOnlySpan<float> interleaved)
    {
        var loudest = 0f;
        foreach (var sample in interleaved)
        {
            var magnitude = MathF.Abs(sample);
            if (magnitude > loudest)
            {
                loudest = magnitude;
            }
        }

        // One writer (the capture thread); the reader swaps the field with 0, so a lost update only drops one old peak.
        if (loudest > Volatile.Read(ref _peak))
        {
            Volatile.Write(ref _peak, loudest);
        }
    }

    /// <inheritdoc />
    public void WriteSilence(int frames)
    {
    }

    /// <summary>
    /// The loudest sample since the previous read on a −60 dB .. 0 dB scale mapped to [0, 1] (speech sits around 0.5), and
    /// the start of the next measuring window. Read by one consumer only.
    /// </summary>
    public float Level => ToDisplay(Interlocked.Exchange(ref _peak, 0f));

    /// <summary>Linear peak amplitude to the meter scale; 0 for silence and for anything that is not a number.</summary>
    internal static float ToDisplay(float peak)
    {
        if (!(peak > 0f))
        {
            return 0f;
        }

        var db = 20f * MathF.Log10(Math.Min(peak, 1f));
        return Math.Clamp((db - FloorDb) / -FloorDb, 0f, 1f);
    }
}
