namespace Murmur.Transcription.Audio;

// Ported from spikes/Murmur.Spike.LoopbackAsr (measured 122 dB SNR there). Used only to bring short clips
// (bench input, test fixtures) to 16 kHz; recordings already arrive at 16 kHz from the capture pipeline.

/// <summary>
/// Offline windowed-sinc (Blackman) resampler. For rational ratios with a small denominator
/// (48000/16000 = 3/1, 44100/16000 = 441/160) the filter is precomputed per polyphase branch;
/// otherwise the kernel is evaluated per output sample.
/// </summary>
internal sealed class SincResampler
{
    private const int ZeroCrossings = 16;
    private const double Rolloff = 0.95;
    private const int MaxPhases = 4096;

    private readonly int _inRate;
    private readonly int _outRate;
    private readonly long _up;   // L: output rate / gcd
    private readonly long _down; // M: input rate / gcd
    private readonly double _cutoff; // cycles per input sample
    private readonly int _halfTaps;
    private readonly float[]? _table; // [phase * taps + tap]

    public SincResampler(int inRate, int outRate)
    {
        if (inRate <= 0 || outRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inRate));
        }

        _inRate = inRate;
        _outRate = outRate;
        var g = Gcd(inRate, outRate);
        _up = outRate / g;
        _down = inRate / g;
        _cutoff = 0.5 * Math.Min(1.0, (double)outRate / inRate) * Rolloff;
        _halfTaps = (int)Math.Ceiling(ZeroCrossings / (2.0 * _cutoff));

        if (_up <= MaxPhases)
        {
            var taps = 2 * _halfTaps;
            _table = new float[_up * taps];
            for (var p = 0; p < _up; p++)
            {
                FillKernel((double)p / _up, _table.AsSpan(p * taps, taps));
            }
        }
    }

    public int Taps => 2 * _halfTaps;

    public int OutputLength(int inputLength) => (int)((long)inputLength * _outRate / _inRate);

    public float[] Process(ReadOnlySpan<float> input)
    {
        var output = new float[OutputLength(input.Length)];
        Process(input, output);
        return output;
    }

    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        var taps = 2 * _halfTaps;
        Span<float> scratch = _table is null ? new float[taps] : default;

        for (var n = 0; n < output.Length; n++)
        {
            // Input position of output sample n: t = n * M / L = k + p / L.
            var num = n * _down;
            var k = (int)(num / _up);
            var p = (int)(num % _up);

            ReadOnlySpan<float> kernel;
            if (_table is not null)
            {
                kernel = _table.AsSpan(p * taps, taps);
            }
            else
            {
                FillKernel((double)p / _up, scratch);
                kernel = scratch;
            }

            // kernel[j] weights input sample k + j - halfTaps + 1.
            var first = k - _halfTaps + 1;
            var acc = 0f;
            if (first >= 0 && first + taps <= input.Length)
            {
                var window = input.Slice(first, taps);
                for (var j = 0; j < taps; j++)
                {
                    acc += window[j] * kernel[j];
                }
            }
            else
            {
                for (var j = 0; j < taps; j++)
                {
                    var idx = first + j;
                    if ((uint)idx < (uint)input.Length)
                    {
                        acc += input[idx] * kernel[j];
                    }
                }
            }

            output[n] = acc;
        }
    }

    /// <summary>Kernel for fractional offset <paramref name="frac"/> in [0, 1), normalised to unity DC gain.</summary>
    private void FillKernel(double frac, Span<float> kernel)
    {
        var sum = 0.0;
        Span<double> tmp = kernel.Length <= 1024 ? stackalloc double[kernel.Length] : new double[kernel.Length];
        for (var j = 0; j < kernel.Length; j++)
        {
            // Distance (in input samples) between the tap and the output instant.
            var d = (j - _halfTaps + 1) - frac;
            var x = 2.0 * _cutoff * d;
            var sinc = Math.Abs(x) < 1e-12 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
            var w = d / _halfTaps; // in [-1, 1]
            var window = Math.Abs(w) >= 1.0 ? 0.0 : 0.42 + 0.5 * Math.Cos(Math.PI * w) + 0.08 * Math.Cos(2.0 * Math.PI * w);
            var v = sinc * window;
            tmp[j] = v;
            sum += v;
        }

        for (var j = 0; j < kernel.Length; j++)
        {
            kernel[j] = (float)(tmp[j] / sum);
        }
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
