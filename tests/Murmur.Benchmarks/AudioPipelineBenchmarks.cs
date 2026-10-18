using BenchmarkDotNet.Attributes;
using Murmur.Core.Audio;
using Murmur.Core.Capture;

namespace Murmur.Benchmarks;

/// <summary>
/// Hot paths of the recording pipeline, each over one 10 ms buffer of a 48 kHz stereo source
/// (960 interleaved samples in, 160 mono 16 kHz samples out). All of them must allocate 0 B.
/// </summary>
[MemoryDiagnoser]
public class AudioPipelineBenchmarks
{
    private const int Frames10ms48k = 480;

    private readonly float[] _stereo10ms = new float[Frames10ms48k * 2];
    private readonly float[] _stereo10ms44k = new float[441 * 2];
    private readonly float[] _drain = new float[Frames10ms48k * 2];
    private readonly float[] _mono10ms16k = new float[160];
    private AudioRingBuffer _ring = null!;
    private CaptureTrack _track = null!;
    private StreamingResampler _resampler48k = null!;
    private StreamingResampler _resampler44k = null!;
    private float[] _resampled = null!;
    private WavPcm16Writer _wav = null!;

    [GlobalSetup]
    public void Setup()
    {
        for (var i = 0; i < Frames10ms48k; i++)
        {
            var v = MathF.Sin(2 * MathF.PI * 440 * i / 48_000f) * 0.5f;
            _stereo10ms[2 * i] = v;
            _stereo10ms[(2 * i) + 1] = -v;
        }

        _stereo10ms.AsSpan(0, _stereo10ms44k.Length).CopyTo(_stereo10ms44k);
        _stereo10ms.AsSpan(0, _mono10ms16k.Length).CopyTo(_mono10ms16k);

        _ring = new AudioRingBuffer(1 << 18);
        _track = new CaptureTrack(new AudioFormat(48_000, 2));
        _resampler48k = new StreamingResampler(new AudioFormat(48_000, 2));
        _resampler44k = new StreamingResampler(new AudioFormat(44_100, 2));
        _resampled = new float[_resampler48k.MaxOutputSamples(Frames10ms48k)];
        _wav = new WavPcm16Writer(Stream.Null);
    }

    [GlobalCleanup]
    public void Cleanup() => _wav.Dispose();

    /// <summary>Capture thread writes 10 ms, writer thread reads it back.</summary>
    [Benchmark]
    public int RingWriteRead10ms()
    {
        _ring.Write(_stereo10ms);
        return _ring.Read(_drain);
    }

    /// <summary>The IAudioSink call a capture thread makes per buffer (peak + ring copy + counters), plus its drain.</summary>
    [Benchmark]
    public int CaptureTrackWrite10ms()
    {
        _track.Write(_stereo10ms);
        return _track.Read(_drain);
    }

    [Benchmark]
    public int Resample48kStereoTo16kMono10ms() => _resampler48k.Process(_stereo10ms, _resampled);

    [Benchmark]
    public int Resample44k1StereoTo16kMono10ms() => _resampler44k.Process(_stereo10ms44k, _resampled);

    /// <summary>PCM16 conversion + write of 10 ms of 16 kHz mono (to <see cref="Stream.Null"/>, so only our cost is measured).</summary>
    [Benchmark]
    public long WavAppend10ms()
    {
        _wav.Append(_mono10ms16k);
        return _wav.SamplesWritten;
    }
}
