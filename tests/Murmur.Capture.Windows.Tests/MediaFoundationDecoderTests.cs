using Murmur.Capture.Windows.Import;
using Murmur.Capture.Windows.Screen.Interop;
using Murmur.Core.Audio;
using Murmur.Core.Import;

namespace Murmur.Capture.Windows.Tests;

/// <summary>The rules of the import decoder, checked on any OS.</summary>
public sealed class MediaFoundationDecoderRulesTests
{
    [Fact]
    public void Extensions_AreTheFormatsOfTheIssue_LowerCaseWithTheDot()
    {
        Assert.True(new MediaFoundationDecoder().Extensions.SetEquals([".mp3", ".m4a", ".aac", ".flac", ".wma", ".ogg", ".mp4", ".mov", ".mkv", ".webm"]));
    }

    [Fact]
    public void WavFiles_StayWithTheManagedDecoder()
    {
        var composite = new CompositeMediaDecoder(new MediaFoundationDecoder(), new WavMediaDecoder());

        Assert.DoesNotContain(".wav", new MediaFoundationDecoder().Extensions);
        Assert.Contains(".wav", composite.Extensions);
        Assert.Contains(".mp3", composite.Extensions);
    }

    [Theory]
    [InlineData(unchecked((int)0xC00D36C4), typeof(NotSupportedException))] // MF_E_UNSUPPORTED_BYTESTREAM_TYPE
    [InlineData(unchecked((int)0xC00D5212), typeof(NotSupportedException))] // MF_E_TOPO_CODEC_NOT_FOUND
    [InlineData(unchecked((int)0xC00D36B4), typeof(NotSupportedException))] // MF_E_INVALIDMEDIATYPE
    [InlineData(unchecked((int)0xC00D36C3), typeof(NotSupportedException))] // MF_E_UNSUPPORTED_SCHEME
    [InlineData(unchecked((int)0xC00D36B3), typeof(InvalidDataException))] // MF_E_INVALIDSTREAMNUMBER: no audio stream
    [InlineData(unchecked((int)0x80070002), typeof(FileNotFoundException))]
    [InlineData(unchecked((int)0x80070003), typeof(FileNotFoundException))]
    [InlineData(unchecked((int)0x80070005), typeof(UnauthorizedAccessException))]
    [InlineData(unchecked((int)0x80004005), typeof(InvalidDataException))] // E_FAIL: a file Media Foundation cannot parse
    public void Failures_MapToTheImportsErrors(int hr, Type expected)
    {
        var error = MediaFoundationDecoder.ErrorFor(hr, @"C:\media\clip.ogg", "MFCreateSourceReaderFromURL");

        Assert.IsType(expected, error);
        Assert.Contains("clip.ogg", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheErrorCodes_AreTheMediaFoundationOnes()
    {
        Assert.Equal(unchecked((int)0xC00D36C4), ScreenConstants.MfEUnsupportedByteStreamType);
    }
}

/// <summary>The decoder against real files through Media Foundation. Windows only.</summary>
[Trait("Category", "Windows")]
public sealed class MediaFoundationDecoderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "murmur-mf-decoder-tests", Guid.NewGuid().ToString("N"));
    private readonly MediaFoundationDecoder _decoder = new();

    public MediaFoundationDecoderTests()
    {
        Directory.CreateDirectory(_dir);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task AStereo44kFile_IsProbedAsAudio_AndDecodedTo16kMonoPcm16()
    {
        SkipUnlessWindows();
        var path = Tone44kStereo(Path.Combine(_dir, "tone.wav"), TimeSpan.FromSeconds(3));
        var destination = Path.Combine(_dir, "system.wav");
        var reports = new List<double>();

        var probe = await _decoder.ProbeAsync(path, Ct);
        await _decoder.DecodeAudioAsync(path, destination, new SyncProgress(reports.Add), Ct);

        Assert.Equal(MediaKind.Audio, probe.Kind);
        Assert.InRange(probe.Duration, TimeSpan.FromSeconds(2.95), TimeSpan.FromSeconds(3.05));
        using var reader = WavReader.Open(destination);
        Assert.Equal((16_000, 1, 16), (reader.Layout.SampleRate, reader.Layout.Channels, reader.Layout.BitsPerSample));
        Assert.InRange(reader.Duration, TimeSpan.FromSeconds(2.95), TimeSpan.FromSeconds(3.05));
        var (_, samples) = WavReader.ReadAll(destination);
        var peak = samples.Skip(4_000).Take(32_000).Max(MathF.Abs);
        Assert.InRange(peak, 0.4f, 0.6f); // the 0.5 tone survives the down-mix and the resampling
        Assert.Equal(1, reports[^1]);
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task AVideoWithoutAudio_IsDamaged_NotAnEmptyTrack()
    {
        SkipUnlessWindows();
        var path = Path.Combine(_dir, "silent.mp4");
        TestVideo.Write(path);

        var error = await Assert.ThrowsAsync<InvalidDataException>(async () => await _decoder.ProbeAsync(path, Ct));
        Assert.Contains("audio", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileThatIsNotMedia_FailsCleanly()
    {
        SkipUnlessWindows();
        var path = Path.Combine(_dir, "notes.mp3");
        await File.WriteAllTextAsync(path, "definitely not an mp3", Ct);

        var error = await Record.ExceptionAsync(async () => await _decoder.ProbeAsync(path, Ct));

        Assert.True(error is NotSupportedException or InvalidDataException, error?.ToString());
    }

    [Fact]
    public async Task AMissingFile_IsNotFound()
    {
        SkipUnlessWindows();

        await Assert.ThrowsAsync<FileNotFoundException>(async () => await _decoder.ProbeAsync(Path.Combine(_dir, "gone.m4a"), Ct));
    }

    [Fact]
    public async Task ACancelledDecode_LeavesNoFile()
    {
        SkipUnlessWindows();
        var path = Tone44kStereo(Path.Combine(_dir, "long.wav"), TimeSpan.FromSeconds(30));
        var destination = Path.Combine(_dir, "system.wav");
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p > 0.05)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await _decoder.DecodeAudioAsync(path, destination, progress, cts.Token));

        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    private static void SkipUnlessWindows() =>
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation decoding runs only on Windows.");

    /// <summary>A 440 Hz tone of amplitude 0.5 on both channels, 44.1 kHz PCM16 (Media Foundation's WAV source reads it).</summary>
    private static string Tone44kStereo(string path, TimeSpan duration)
    {
        const int Rate = 44_100;
        var frames = (int)(duration.TotalSeconds * Rate);
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var v = 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / Rate);
            samples[2 * i] = v;
            samples[(2 * i) + 1] = v;
        }

        using var writer = new WavPcm16Writer(File.Create(path), Rate, 2);
        writer.Append(samples);
        return path;
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
