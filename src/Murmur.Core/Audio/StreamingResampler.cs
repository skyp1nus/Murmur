using System.Numerics;
using System.Runtime.InteropServices;
using Murmur.Core.Capture;

namespace Murmur.Core.Audio;

/// <summary>
/// Streaming downmix + sample-rate converter: interleaved N-channel float at any rate to mono float at
/// <see cref="OutputRate"/> (16 kHz by default).
/// </summary>
/// <remarks>
/// <para>
/// Kaiser-windowed sinc (beta 8.6, <see cref="ZeroCrossings"/> zero crossings per side) with the cutoff at
/// <see cref="CutoffRatio"/> x min(input, output rate). The input/output rate ratio L/M is tracked with exact integer
/// arithmetic, so there is no drift: output sample <c>n</c> is centred exactly on input time <c>n * M / L</c> and,
/// after <see cref="Flush"/>, exactly <c>ceil(inputFrames * L / M)</c> samples have been produced. Before the flush,
/// up to <see cref="MaxFlushSamples"/> samples (about 1 ms) are pending, waiting for future input.
/// </para>
/// <para>
/// The output depends only on the input samples, never on how they were chunked: any chunking is bit-identical to a
/// one-shot conversion. Modes: pass-through (same rate, mono), downmix only (same rate), integer decimation
/// (e.g. 48 kHz to 16 kHz, one polyphase branch), exact polyphase table (up to 1024 phases, e.g. 44.1 kHz:
/// 160 phases) and an interpolated 1024-phase table for awkward ratios. Nothing allocates after construction.
/// </para>
/// </remarks>
public sealed class StreamingResampler
{
    /// <summary>Zero crossings of the sinc on each side of the kernel centre.</summary>
    public const int ZeroCrossings = 16;

    /// <summary>Cutoff as a fraction of the lower of the two sample rates (0.45 x 16 kHz = 7.2 kHz).</summary>
    public const double CutoffRatio = 0.45;

    private const double KaiserBeta = 8.6;
    private const int MaxExactPhases = 1024;
    private const int InterpolatedPhases = 1024;
    private const int BlockFrames = 4096;

    private readonly Mode _mode;
    private readonly int _channels;
    private readonly long _up;   // L = output rate / gcd
    private readonly long _down; // M = input rate / gcd
    private readonly long _stepWhole;
    private readonly long _stepFraction;
    private readonly int _halfTaps;
    private readonly int _taps;
    private readonly float[] _table;
    private readonly float[] _history;

    private long _historyStart; // absolute input index of _history[0]
    private int _historyCount;
    private long _k;            // integer part of the input position of the next output sample
    private long _phase;        // fractional part, in 1/L units
    private bool _flushed;

    public StreamingResampler(AudioFormat input, int outputRate = 16_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(input.SampleRate, 1, nameof(input));
        ArgumentOutOfRangeException.ThrowIfLessThan(input.Channels, 1, nameof(input));
        ArgumentOutOfRangeException.ThrowIfLessThan(outputRate, 1);

        InputFormat = input;
        OutputRate = outputRate;
        _channels = input.Channels;

        var g = Gcd(input.SampleRate, outputRate);
        _up = outputRate / g;
        _down = input.SampleRate / g;
        _stepWhole = _down / _up;
        _stepFraction = _down % _up;

        if (input.SampleRate == outputRate)
        {
            _mode = _channels == 1 ? Mode.PassThrough : Mode.DownmixOnly;
            _table = [];
            _history = [];
            return;
        }

        // Cutoff in cycles per input sample; halfTaps input samples cover ZeroCrossings zero crossings of the sinc.
        var cutoff = CutoffRatio * Math.Min(input.SampleRate, outputRate) / input.SampleRate;
        _halfTaps = (int)Math.Ceiling(ZeroCrossings / (2.0 * cutoff));
        _taps = 2 * _halfTaps;

        int phases;
        if (_up == 1)
        {
            _mode = Mode.Integer;
            phases = 1;
        }
        else if (_up <= MaxExactPhases)
        {
            _mode = Mode.Exact;
            phases = (int)_up;
        }
        else
        {
            _mode = Mode.Interpolated;
            phases = InterpolatedPhases + 1; // entry [InterpolatedPhases] is the kernel for fraction 1.0
        }

        _table = new float[phases * _taps];
        var scratch = new double[_taps];
        for (var p = 0; p < phases; p++)
        {
            var fraction = _mode == Mode.Interpolated ? (double)p / InterpolatedPhases : (double)p / _up;
            FillKernel(fraction, cutoff, scratch, _table.AsSpan(p * _taps, _taps));
        }

        _history = new float[_taps + BlockFrames];
        Reset();
    }

    public AudioFormat InputFormat { get; }

    public int OutputRate { get; }

    /// <summary>Input frames consumed since construction or <see cref="Reset"/>.</summary>
    public long InputFrames { get; private set; }

    /// <summary>Output samples produced since construction or <see cref="Reset"/>.</summary>
    public long OutputSamples { get; private set; }

    /// <summary>Kernel length in input samples (0 when no rate conversion happens).</summary>
    public int Taps => _taps;

    /// <summary>Upper bound of output samples <see cref="Flush"/> produces (the samples pending for future input).</summary>
    public int MaxFlushSamples => _taps == 0 ? 0 : (int)((_halfTaps * _up + _down - 1) / _down) + 1;

    /// <summary>Upper bound of output samples one <see cref="Process"/> call over <paramref name="inputFrames"/> frames produces.</summary>
    public int MaxOutputSamples(int inputFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputFrames);
        return (int)((inputFrames * _up + _down - 1) / _down) + 1;
    }

    /// <summary>Restarts the stream: clears the history and the counters.</summary>
    public void Reset()
    {
        InputFrames = 0;
        OutputSamples = 0;
        _k = 0;
        _phase = 0;
        _flushed = false;
        if (_history.Length > 0)
        {
            // The samples before the stream start are zeros.
            _historyStart = -(_halfTaps - 1);
            _historyCount = _halfTaps - 1;
            _history.AsSpan(0, _historyCount).Clear();
        }
    }

    /// <summary>
    /// Converts <paramref name="interleaved"/> (whole frames of <see cref="InputFormat"/>) and writes the output samples
    /// that are complete. <paramref name="output"/> must hold <see cref="MaxOutputSamples"/> samples.
    /// </summary>
    /// <returns>Number of samples written to <paramref name="output"/>.</returns>
    public int Process(ReadOnlySpan<float> interleaved, Span<float> output)
    {
        if (_flushed)
        {
            throw new InvalidOperationException("The resampler was flushed; call Reset to start a new stream.");
        }

        if (interleaved.Length % _channels != 0)
        {
            throw new ArgumentException("Input length must be a multiple of the channel count.", nameof(interleaved));
        }

        var frames = interleaved.Length / _channels;
        if (output.Length < MaxOutputSamples(frames))
        {
            throw new ArgumentException("Output buffer is smaller than MaxOutputSamples(frames).", nameof(output));
        }

        switch (_mode)
        {
            case Mode.PassThrough:
                interleaved.CopyTo(output);
                InputFrames += frames;
                OutputSamples += frames;
                return frames;

            case Mode.DownmixOnly:
                Downmix(interleaved, _channels, output[..frames]);
                InputFrames += frames;
                OutputSamples += frames;
                return frames;
        }

        var written = 0;
        var offset = 0;
        while (offset < frames)
        {
            Compact();
            var count = Math.Min(_history.Length - _historyCount, frames - offset);
            Downmix(interleaved.Slice(offset * _channels, count * _channels), _channels, _history.AsSpan(_historyCount, count));
            _historyCount += count;
            offset += count;
            InputFrames += count;
            written += Produce(output[written..], long.MaxValue);
        }

        return written;
    }

    /// <summary>
    /// Ends the stream: pads with silence and emits the pending samples so that the total output is exactly
    /// <c>ceil(InputFrames * OutputRate / InputRate)</c>. <paramref name="output"/> must hold <see cref="MaxFlushSamples"/>.
    /// </summary>
    /// <returns>Number of samples written.</returns>
    public int Flush(Span<float> output)
    {
        if (_flushed)
        {
            return 0;
        }

        _flushed = true;
        if (_mode is Mode.PassThrough or Mode.DownmixOnly)
        {
            return 0;
        }

        if (output.Length < MaxFlushSamples)
        {
            throw new ArgumentException("Output buffer is smaller than MaxFlushSamples.", nameof(output));
        }

        var total = (InputFrames * _up + _down - 1) / _down;
        var written = 0;
        while (OutputSamples < total)
        {
            Compact();
            var pad = Math.Min(_history.Length - _historyCount, _taps);
            _history.AsSpan(_historyCount, pad).Clear();
            _historyCount += pad;
            written += Produce(output[written..], total);
        }

        return written;
    }

    /// <summary>Averages the channels of an interleaved buffer into <paramref name="mono"/> (frames = mono.Length).</summary>
    public static void Downmix(ReadOnlySpan<float> interleaved, int channels, Span<float> mono)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        var frames = mono.Length;
        if (interleaved.Length < frames * channels)
        {
            throw new ArgumentException("Input holds fewer frames than the destination.", nameof(interleaved));
        }

        switch (channels)
        {
            case 1:
                interleaved[..frames].CopyTo(mono);
                break;

            case 2:
                for (var f = 0; f < frames; f++)
                {
                    mono[f] = (interleaved[2 * f] + interleaved[(2 * f) + 1]) * 0.5f;
                }

                break;

            default:
                var scale = 1f / channels;
                for (var f = 0; f < frames; f++)
                {
                    var row = interleaved.Slice(f * channels, channels);
                    var sum = 0f;
                    for (var c = 0; c < row.Length; c++)
                    {
                        sum += row[c];
                    }

                    mono[f] = sum * scale;
                }

                break;
        }
    }

    /// <summary>Drops history samples that no future output sample needs.</summary>
    private void Compact()
    {
        var keepFrom = _k - _halfTaps + 1;
        var drop = (int)Math.Min(keepFrom - _historyStart, _historyCount);
        if (drop <= 0)
        {
            return;
        }

        _history.AsSpan(drop, _historyCount - drop).CopyTo(_history);
        _historyCount -= drop;
        _historyStart += drop;
    }

    /// <summary>Emits every output sample whose kernel window is fully inside the history, up to <paramref name="limit"/> in total.</summary>
    private int Produce(Span<float> output, long limit)
    {
        var end = _historyStart + _historyCount;
        var history = _history.AsSpan(0, _historyCount);
        var written = 0;

        switch (_mode)
        {
            case Mode.Integer:
            {
                var kernel = _table.AsSpan(0, _taps);
                while (_k + _halfTaps < end && OutputSamples < limit)
                {
                    var first = (int)(_k - _halfTaps + 1 - _historyStart);
                    output[written++] = Dot(history.Slice(first, _taps), kernel);
                    _k += _stepWhole;
                    OutputSamples++;
                }

                break;
            }

            case Mode.Exact:
                while (_k + _halfTaps < end && OutputSamples < limit)
                {
                    var first = (int)(_k - _halfTaps + 1 - _historyStart);
                    var kernel = _table.AsSpan((int)_phase * _taps, _taps);
                    output[written++] = Dot(history.Slice(first, _taps), kernel);
                    Advance();
                }

                break;

            case Mode.Interpolated:
                while (_k + _halfTaps < end && OutputSamples < limit)
                {
                    var first = (int)(_k - _halfTaps + 1 - _historyStart);
                    var window = history.Slice(first, _taps);
                    var position = (double)_phase * InterpolatedPhases / _up;
                    var index = (int)position;
                    var weight = (float)(position - index);
                    var a = Dot(window, _table.AsSpan(index * _taps, _taps));
                    var b = Dot(window, _table.AsSpan((index + 1) * _taps, _taps));
                    output[written++] = a + ((b - a) * weight);
                    Advance();
                }

                break;
        }

        return written;
    }

    private void Advance()
    {
        _k += _stepWhole;
        _phase += _stepFraction;
        if (_phase >= _up)
        {
            _phase -= _up;
            _k++;
        }

        OutputSamples++;
    }

    /// <summary>Dot product with a fixed summation order, so results do not depend on memory alignment.</summary>
    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var i = 0;
        var sum = 0f;
        if (Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            var va = MemoryMarshal.Cast<float, Vector<float>>(a);
            var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
            var acc = Vector<float>.Zero;
            for (var v = 0; v < va.Length; v++)
            {
                acc += va[v] * vb[v];
            }

            sum = Vector.Sum(acc);
            i = va.Length * Vector<float>.Count;
        }

        for (; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    /// <summary>Kernel for an output instant <paramref name="fraction"/> input samples after the centre tap, unity DC gain.</summary>
    private void FillKernel(double fraction, double cutoff, Span<double> scratch, Span<float> kernel)
    {
        var i0Beta = BesselI0(KaiserBeta);
        var sum = 0.0;
        for (var j = 0; j < kernel.Length; j++)
        {
            // Distance (in input samples) between tap j and the output instant.
            var d = j - _halfTaps + 1 - fraction;
            var x = 2.0 * cutoff * d;
            var sinc = Math.Abs(x) < 1e-12 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
            var r = d / _halfTaps;
            var window = Math.Abs(r) >= 1.0 ? 0.0 : BesselI0(KaiserBeta * Math.Sqrt(1.0 - (r * r))) / i0Beta;
            scratch[j] = sinc * window;
            sum += scratch[j];
        }

        for (var j = 0; j < kernel.Length; j++)
        {
            kernel[j] = (float)(scratch[j] / sum);
        }
    }

    /// <summary>Modified Bessel function of the first kind, order 0 (power series).</summary>
    private static double BesselI0(double x)
    {
        var sum = 1.0;
        var term = 1.0;
        var half = x / 2.0;
        for (var k = 1; k < 200; k++)
        {
            term *= half / k;
            var t2 = term * term;
            sum += t2;
            if (t2 < sum * 1e-17)
            {
                break;
            }
        }

        return sum;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    private enum Mode
    {
        PassThrough,
        DownmixOnly,
        Integer,
        Exact,
        Interpolated,
    }
}
