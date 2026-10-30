using BenchmarkDotNet.Attributes;
using Murmur.Capture.Windows.Playback;

namespace Murmur.Benchmarks;

/// <summary>
/// The time stretcher of the Windows player runs inside the WASAPI render callback: one second of output (the work of
/// ~20 callbacks of 50 ms) must cost a tiny fraction of a second and allocate nothing.
/// </summary>
[MemoryDiagnoser]
public class WsolaStretcherBenchmarks
{
    private const int Rate = 16_000;

    private WsolaStretcher _stretcher = null!;
    private short[] _block = null!;

    [Params(0.75, 1.0, 1.5, 2.0)]
    public double Speed { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // 60 s of a voiced-speech stand-in: a 120 Hz fundamental with harmonics and a slow amplitude wobble.
        var samples = new short[60 * Rate];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / Rate;
            var sum = Math.Sin(2 * Math.PI * 120 * t) + (0.6 * Math.Sin(2 * Math.PI * 240 * t)) + (0.4 * Math.Sin(2 * Math.PI * 360 * t));
            samples[i] = (short)(6_000 * sum * (0.7 + (0.3 * Math.Sin(2 * Math.PI * 3 * t))));
        }

        _stretcher = new WsolaStretcher(new MemorySource(samples), Rate);
        _block = new short[800]; // a 50 ms endpoint buffer
        _stretcher.SetSpeed(Speed);
    }

    /// <summary>One second of output (20 callbacks of 50 ms).</summary>
    [Benchmark]
    public int RenderOneSecond()
    {
        var total = 0;
        for (var i = 0; i < 20; i++)
        {
            var written = _stretcher.Render(_block);
            if (written == 0)
            {
                _stretcher.Seek(0);
                written = _stretcher.Render(_block);
            }

            total += written;
        }

        return total;
    }

    private sealed class MemorySource(short[] samples) : IPcmSource
    {
        public long TotalFrames => samples.Length;

        public long Position { get; private set; }

        public int Render(Span<short> destination)
        {
            var count = (int)Math.Min(destination.Length, samples.Length - Position);
            samples.AsSpan((int)Position, count).CopyTo(destination);
            Position += count;
            return count;
        }

        public void Seek(long frame) => Position = Math.Clamp(frame, 0, samples.Length);

        public void Rewind(long frame) => Seek(frame);

        public int ChangeVersion => 0;
    }
}
