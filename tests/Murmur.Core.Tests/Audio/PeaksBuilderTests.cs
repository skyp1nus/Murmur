using System.Buffers.Binary;
using Murmur.Core.Audio;
using Murmur.Core.Tests.Fakes;

namespace Murmur.Core.Tests.Audio;

public sealed class PeaksBuilderTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const int Window = TestWav.Rate / PeaksFile.PeaksPerSecond; // 320 samples

    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Build_WritesHeaderAndOneBytePerWindow()
    {
        var wav = TestWav.Square(_dir.File("mic.wav"), TimeSpan.FromSeconds(2), 0.5f);
        var output = _dir.File("peaks.bin");

        var count = PeaksBuilder.Build([wav], output, Ct);

        Assert.Equal(100, count);
        var bytes = File.ReadAllBytes(output);
        Assert.Equal(PeaksFile.HeaderSize + 100, bytes.Length);
        Assert.Equal("MPK1"u8.ToArray(), bytes[..4]);
        Assert.Equal(50, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(100, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)));
        Assert.All(PeaksFile.Read(output), b => Assert.Equal(128, b));
        Assert.False(File.Exists(output + ".tmp"));
    }

    [Fact]
    public void Compute_TakesTheMaxAbsoluteSamplePerWindow()
    {
        var samples = new float[Window * 4];
        samples[10] = 0.25f;               // window 0
        samples[Window + 5] = -1f;         // window 1: negative full scale
        samples[(2 * Window) - 1] = 0.75f; // last sample of window 1 is smaller than the first
        samples[(3 * Window) + 319] = 0.1f; // window 3, last sample
        var wav = TestWav.Write(_dir.File("a.wav"), samples);

        var peaks = PeaksBuilder.Compute([wav], Ct);

        Assert.Equal([PeaksBuilder.ToByte(0.25f), PeaksBuilder.ToByte(1f), 0, PeaksBuilder.ToByte(0.1f)], peaks);
        Assert.Equal(255, peaks[1]);
    }

    [Fact]
    public void Compute_UsesTheLouderTrackAndTheLongestLength()
    {
        // mic: 1 s loud then nothing; system: 3 s quiet.
        var mic = TestWav.Square(_dir.File("mic.wav"), TimeSpan.FromSeconds(1), 0.8f);
        var system = TestWav.Square(_dir.File("system.wav"), TimeSpan.FromSeconds(3), 0.2f);

        var peaks = PeaksBuilder.Compute([mic, system], Ct);

        Assert.Equal(150, peaks.Length);
        Assert.All(peaks[..50], b => Assert.Equal(PeaksBuilder.ToByte(0.8f), b));
        Assert.All(peaks[50..], b => Assert.Equal(PeaksBuilder.ToByte(0.2f), b));
    }

    [Fact]
    public void Compute_CountsAPartialLastWindow()
    {
        var samples = new float[(Window * 2) + 7];
        samples[^1] = 0.5f;
        var wav = TestWav.Write(_dir.File("partial.wav"), samples);

        var peaks = PeaksBuilder.Compute([wav], Ct);

        Assert.Equal(3, peaks.Length);
        Assert.Equal([0, 0, 128], peaks);
    }

    [Fact]
    public void Compute_StreamsLongFilesAcrossChunks()
    {
        // 10 s of a ramp of window peaks: crosses many 256-window chunks and their boundaries.
        var samples = new float[TestWav.Rate * 10];
        for (var w = 0; w < samples.Length / Window; w++)
        {
            samples[(w * Window) + (w % Window)] = (w % 256) / 255f;
        }

        var wav = TestWav.Write(_dir.File("long.wav"), samples);

        var peaks = PeaksBuilder.Compute([wav], Ct);

        Assert.Equal(500, peaks.Length);
        for (var w = 0; w < peaks.Length; w++)
        {
            Assert.InRange(peaks[w], Math.Max(0, (w % 256) - 1), (w % 256) + 1);
        }
    }

    [Fact]
    public void Compute_HandlesOtherRatesAndStereo()
    {
        // 48 kHz stereo: window = 960 frames; the right channel carries the peak.
        var frames = 960 * 2;
        var samples = new float[frames * 2];
        samples[(1000 * 2) + 1] = -0.5f; // frame 1000 (window 1), right channel
        var wav = TestWav.Write(_dir.File("stereo.wav"), samples, sampleRate: 48_000, channels: 2);

        var peaks = PeaksBuilder.Compute([wav], Ct);

        Assert.Equal([0, 128], peaks);
    }

    [Fact]
    public void Compute_EmptyTrackGivesNoPeaks()
    {
        var wav = TestWav.Write(_dir.File("empty.wav"), []);

        Assert.Empty(PeaksBuilder.Compute([wav], Ct));
    }

    [Fact]
    public void Compute_RejectsANonWav()
    {
        var path = _dir.File("junk.wav");
        File.WriteAllBytes(path, new byte[100]);

        Assert.Throws<InvalidDataException>(() => PeaksBuilder.Compute([path], Ct));
    }

    [Fact]
    public void Build_ReplacesAnExistingFile()
    {
        var output = _dir.File("peaks.bin");
        PeaksBuilder.Build([TestWav.Square(_dir.File("a.wav"), TimeSpan.FromSeconds(1), 1f)], output, Ct);
        PeaksBuilder.Build([TestWav.Silence(_dir.File("b.wav"), TimeSpan.FromSeconds(0.5))], output, Ct);

        Assert.Equal(new byte[25], PeaksFile.Read(output));
    }

    [Fact]
    public void Read_RejectsBadFiles()
    {
        var bad = _dir.File("bad.bin");
        File.WriteAllBytes(bad, "NOPE\x32\0\0\0\0\0\0\0"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => PeaksFile.Read(bad));

        var truncated = _dir.File("truncated.bin");
        using (var stream = File.Create(truncated))
        {
            PeaksFile.Write(stream, [1, 2, 3]);
        }

        File.WriteAllBytes(truncated, File.ReadAllBytes(truncated)[..^1]);
        Assert.Throws<InvalidDataException>(() => PeaksFile.Read(truncated));

        var tiny = _dir.File("tiny.bin");
        File.WriteAllBytes(tiny, "MPK1"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => PeaksFile.Read(tiny));
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f, 255)]
    [InlineData(2f, 255)]
    [InlineData(0.5f, 128)]
    [InlineData(1f / 255, 1)]
    public void ToByte_ScalesAndClamps(float peak, byte expected)
    {
        Assert.Equal(expected, PeaksBuilder.ToByte(peak));
    }

    [Fact]
    public void Compute_Cancels()
    {
        var wav = TestWav.Square(_dir.File("a.wav"), TimeSpan.FromSeconds(1), 0.5f);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => PeaksBuilder.Compute([wav], cts.Token));
    }
}
