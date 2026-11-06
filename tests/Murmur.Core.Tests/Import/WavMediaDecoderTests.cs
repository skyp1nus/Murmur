using Murmur.Core.Audio;
using Murmur.Core.Capture;
using Murmur.Core.Import;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Import;

public sealed class WavMediaDecoderTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDirectory _dir = new();
    private readonly WavMediaDecoder _decoder = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Extensions_AreWav()
    {
        Assert.Equal([".wav"], _decoder.Extensions);
    }

    [Fact]
    public async Task Probe_ReportsAudio_SizeAndDuration()
    {
        var path = AnyWav.Float32(_dir.File("in.wav"), 48_000, 2, new float[48_000 * 2 * 3]);

        var probe = await _decoder.ProbeAsync(path, Ct);

        Assert.Equal(path, probe.Path);
        Assert.Equal(MediaKind.Audio, probe.Kind);
        Assert.Equal(new FileInfo(path).Length, probe.SizeBytes);
        Assert.Equal(TimeSpan.FromSeconds(3), probe.Duration);
    }

    [Fact]
    public async Task Probe_NotAWav_IsInvalidData()
    {
        var path = _dir.File("fake.wav");
        await File.WriteAllTextAsync(path, "this is not audio at all", Ct);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await _decoder.ProbeAsync(path, Ct));
    }

    [Fact]
    public async Task Probe_EmptyWav_IsInvalidData()
    {
        var path = AnyWav.Float32(_dir.File("empty.wav"), 16_000, 1, []);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await _decoder.ProbeAsync(path, Ct));
    }

    [Fact]
    public async Task Probe_TruncatedFormatChunk_IsInvalidData()
    {
        var path = AnyWav.Raw(_dir.File("truncated.wav"), "RIFF"u8.ToArray(), [30, 0, 0, 0], "WAVE"u8.ToArray(), "fmt "u8.ToArray(), [16, 0, 0, 0], [1, 0, 1, 0, 0x80, 0x3E]);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await _decoder.ProbeAsync(path, Ct));
    }

    [Fact]
    public async Task Probe_ShortFormatChunk_IsInvalidData()
    {
        var path = AnyWav.Raw(_dir.File("short.wav"), "RIFF"u8.ToArray(), [24, 0, 0, 0], "WAVE"u8.ToArray(), "fmt "u8.ToArray(), [8, 0, 0, 0], new byte[8]);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await _decoder.ProbeAsync(path, Ct));
    }

    [Fact]
    public async Task Probe_NoDataChunk_IsInvalidData()
    {
        var full = AnyWav.Write(_dir.File("full.wav"), 16_000, 1, 16, isFloat: false, extensible: false, new float[100]);
        var header = (await File.ReadAllBytesAsync(full, Ct))[..36]; // RIFF header + fmt chunk, no data chunk
        var path = AnyWav.Raw(_dir.File("nodata.wav"), header);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await _decoder.ProbeAsync(path, Ct));
    }

    [Theory]
    [InlineData(8, false, false)]
    [InlineData(16, false, false)]
    [InlineData(24, false, false)]
    [InlineData(32, false, false)]
    [InlineData(32, true, false)]
    [InlineData(64, true, false)]
    [InlineData(16, false, true)]
    [InlineData(24, false, true)]
    [InlineData(32, true, true)]
    public async Task Decode_EveryPcmAndFloatEncoding_GivesTheTrackFormat(int bits, bool isFloat, bool extensible)
    {
        var samples = new float[16_000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.5f * MathF.Sin(i * 0.05f);
        }

        var path = AnyWav.Write(_dir.File($"in-{bits}-{isFloat}-{extensible}.wav"), 16_000, 1, bits, isFloat, extensible, samples);
        var destination = _dir.File("system.wav");

        var probe = await _decoder.ProbeAsync(path, Ct);
        await _decoder.DecodeAudioAsync(path, destination, null, Ct);

        Assert.Equal(TimeSpan.FromSeconds(1), probe.Duration);
        var (rate, channels, outBits, decoded) = AnyWav.Read(destination);
        Assert.Equal((16_000, 1, 16), (rate, channels, outBits));
        Assert.Equal(samples.Length, decoded.Length);
        var tolerance = bits == 8 ? 1.5f / 128 : 1.5f / 32768;
        for (var i = 0; i < samples.Length; i++)
        {
            Assert.True(Math.Abs(samples[i] - decoded[i]) <= tolerance, $"sample {i}: {samples[i]} vs {decoded[i]}");
        }
    }

    [Fact]
    public void TheBlockLoop_DoesNotAllocate_ALongerFileCostsNoMore()
    {
        var interleaved = new float[48_000 * 2 * 20];
        for (var i = 0; i < interleaved.Length; i++)
        {
            interleaved[i] = 0.3f * MathF.Sin(i * 0.01f);
        }

        var shortFile = AnyWav.Write(_dir.File("short.wav"), 48_000, 2, 16, isFloat: false, extensible: false, interleaved.AsSpan(0, 48_000 * 2));
        var longFile = AnyWav.Write(_dir.File("long.wav"), 48_000, 2, 16, isFloat: false, extensible: false, interleaved);
        var destination = _dir.File("system.wav");

        var shortBytes = Allocations.SteadyState(() => WavMediaDecoder.Decode(shortFile, destination, null, CancellationToken.None), rounds: 5);
        var longBytes = Allocations.SteadyState(() => WavMediaDecoder.Decode(longFile, destination, null, CancellationToken.None), rounds: 5);

        // 20x the blocks (about 120 more), the same per-file setup: nothing is allocated per block.
        Assert.True(longBytes - shortBytes < 1024, $"short {shortBytes} B, long {longBytes} B");
    }

    [Fact]
    public async Task Probe_CompressedWav_IsNotSupported()
    {
        var path = AnyWav.Adpcm(_dir.File("adpcm.wav"));

        await Assert.ThrowsAsync<NotSupportedException>(async () => await _decoder.ProbeAsync(path, Ct));
    }

    [Fact]
    public async Task Decode_TrackFormatWav_KeepsEverySample_WithinOnePcm16Step()
    {
        var samples = new float[16_000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = MathF.Sin(i * 0.05f) * 0.5f;
        }

        var path = TestWav.Write(_dir.File("in.wav"), samples);
        var destination = _dir.File("system.wav");

        await _decoder.DecodeAudioAsync(path, destination, null, Ct);

        var (rate, channels, bits, decoded) = AnyWav.Read(destination);
        var (_, expected) = WavReader.ReadAll(path);
        Assert.Equal((AudioFormat.Asr.SampleRate, 1, 16), (rate, channels, bits));
        Assert.Equal(expected.Length, decoded.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(Math.Abs(expected[i] - decoded[i]) <= 1.5f / 32768, $"sample {i}: {expected[i]} vs {decoded[i]}");
        }
    }

    [Fact]
    public async Task Decode_StereoFloat48k_IsDownMixedAndResampledTo16kMonoPcm16()
    {
        // 2 s of a 440 Hz tone: left = +tone, right = +tone (down-mix keeps it), at 48 kHz.
        const int Rate = 48_000;
        var frames = Rate * 2;
        var interleaved = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var x = 0.6f * MathF.Sin(2 * MathF.PI * 440 * i / Rate);
            interleaved[2 * i] = x;
            interleaved[(2 * i) + 1] = x;
        }

        var path = AnyWav.Float32(_dir.File("stereo.wav"), Rate, 2, interleaved);
        var destination = _dir.File("system.wav");
        var reports = new List<double>();

        await _decoder.DecodeAudioAsync(path, destination, new SyncProgress(reports.Add), Ct);

        var (rate, channels, bits, decoded) = AnyWav.Read(destination);
        Assert.Equal((16_000, 1, 16), (rate, channels, bits));
        Assert.Equal(32_000, decoded.Length); // ceil(frames * 16k / 48k)

        // Away from the edges the output is the same tone at 16 kHz.
        var maxError = 0.0;
        for (var n = 1_000; n < decoded.Length - 1_000; n++)
        {
            var expected = 0.6 * Math.Sin(2 * Math.PI * 440 * n / 16_000.0);
            maxError = Math.Max(maxError, Math.Abs(decoded[n] - expected));
        }

        Assert.True(maxError < 0.01, $"max error {maxError}");
        Assert.Equal(1, reports[^1]);
        Assert.Equal(reports.Order(), reports);
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task Decode_OppositeChannels_DownMixToSilence()
    {
        var interleaved = new float[16_000 * 2];
        for (var i = 0; i < 16_000; i++)
        {
            interleaved[2 * i] = 0.5f;
            interleaved[(2 * i) + 1] = -0.5f;
        }

        var path = AnyWav.Float32(_dir.File("phase.wav"), 16_000, 2, interleaved);
        var destination = _dir.File("system.wav");

        await _decoder.DecodeAudioAsync(path, destination, null, Ct);

        var (_, channels, _, decoded) = AnyWav.Read(destination);
        Assert.Equal(1, channels);
        Assert.Equal(16_000, decoded.Length);
        Assert.All(decoded, s => Assert.Equal(0f, s));
    }

    [Fact]
    public async Task Decode_Cancelled_LeavesNoFile()
    {
        var path = AnyWav.Float32(_dir.File("long.wav"), 44_100, 2, new float[44_100 * 2 * 10]);
        var destination = _dir.File("system.wav");
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p > 0.1)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await _decoder.DecodeAudioAsync(path, destination, progress, cts.Token));

        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task Decode_ReplacesAnEarlierTrack()
    {
        var destination = TestWav.Square(_dir.File("system.wav"), TimeSpan.FromSeconds(3), 0.9f);
        var path = TestWav.Silence(_dir.File("in.wav"), TimeSpan.FromSeconds(1));

        await _decoder.DecodeAudioAsync(path, destination, null, Ct);

        var (_, _, _, decoded) = AnyWav.Read(destination);
        Assert.Equal(16_000, decoded.Length);
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
