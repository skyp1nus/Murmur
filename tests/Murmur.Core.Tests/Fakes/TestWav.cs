using Murmur.Core.Audio;

namespace Murmur.Core.Tests.Fakes;

/// <summary>Writes small synthetic 16 kHz mono PCM16 WAVs.</summary>
internal static class TestWav
{
    public const int Rate = 16_000;

    public static string Write(string path, ReadOnlySpan<float> samples, int sampleRate = Rate, int channels = 1)
    {
        using var writer = new WavPcm16Writer(path, sampleRate, channels);
        writer.Append(samples);
        return path;
    }

    /// <summary>A constant-amplitude square wave (|sample| = <paramref name="amplitude"/> everywhere).</summary>
    public static string Square(string path, TimeSpan duration, float amplitude)
    {
        var samples = new float[(int)(duration.TotalSeconds * Rate)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (i & 1) == 0 ? amplitude : -amplitude;
        }

        return Write(path, samples);
    }

    public static string Silence(string path, TimeSpan duration) => Write(path, new float[(int)(duration.TotalSeconds * Rate)]);
}
