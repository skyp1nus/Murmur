using System.Runtime.InteropServices;

namespace Murmur.Capture.Windows.Audio;

/// <summary>
/// Converts interleaved device samples to interleaved 32-bit float in [-1, 1). Hot path: no allocations,
/// no branches per sample beyond the loop. Assumes little-endian sample bytes (every Windows target).
/// </summary>
internal static class SampleConverter
{
    private const float Pcm16Scale = 1f / 32768f;
    private const float Pcm24Scale = 1f / 8388608f;
    private const float Pcm32Scale = 1f / 2147483648f;

    public static int BytesPerSample(SampleType type) => type switch
    {
        SampleType.Float32 => 4,
        SampleType.Pcm16 => 2,
        SampleType.Pcm24 => 3,
        SampleType.Pcm32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>
    /// Converts <c>destination.Length</c> samples from <paramref name="source"/>, which must hold exactly
    /// <c>destination.Length * BytesPerSample(type)</c> bytes.
    /// </summary>
    public static void ToFloat(ReadOnlySpan<byte> source, SampleType type, Span<float> destination)
    {
        if (source.Length != destination.Length * BytesPerSample(type))
        {
            ThrowLengthMismatch();
        }

        switch (type)
        {
            case SampleType.Float32:
                MemoryMarshal.Cast<byte, float>(source).CopyTo(destination);
                break;

            case SampleType.Pcm16:
            {
                var src = MemoryMarshal.Cast<byte, short>(source);
                for (var i = 0; i < src.Length; i++)
                {
                    destination[i] = src[i] * Pcm16Scale;
                }

                break;
            }

            case SampleType.Pcm24:
            {
                for (int i = 0, b = 0; i < destination.Length; i++, b += 3)
                {
                    // Little-endian 3-byte sample; the top byte is sign-extended through sbyte.
                    var value = source[b] | (source[b + 1] << 8) | ((sbyte)source[b + 2] << 16);
                    destination[i] = value * Pcm24Scale;
                }

                break;
            }

            case SampleType.Pcm32:
            {
                // Valid bits are left-justified in the container (WAVEFORMATEXTENSIBLE), so 24-in-32 scales the same way.
                var src = MemoryMarshal.Cast<byte, int>(source);
                for (var i = 0; i < src.Length; i++)
                {
                    destination[i] = src[i] * Pcm32Scale;
                }

                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    private static void ThrowLengthMismatch() =>
        throw new ArgumentException("Source byte count does not match destination sample count.");
}
