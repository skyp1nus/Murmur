using Murmur.Capture.Windows.Import;
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
    [InlineData(unchecked((int)0xC00D36B9), typeof(InvalidDataException))] // MF_E_NO_MORE_TYPES: no audio type to read
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
    public void AnEmptyEditTheReaderIgnored_IsApplied_WithItsStartTrim()
    {
        // ffmpeg's late-audio.mp4: the reader's first audio timestamp is 0, the edit list says the sound starts at 0.976 s.
        var edit = new Mp4AudioStart(TimeSpan.FromMilliseconds(976), TimeSpan.FromMilliseconds(23));

        Assert.Equal(edit, MediaFoundationDecoder.EditToApply(edit, firstTimestamp: 0));
        Assert.Equal(edit, MediaFoundationDecoder.EditToApply(edit, firstTimestamp: TimeSpan.FromMilliseconds(5).Ticks));
    }

    [Fact]
    public void AnEmptyEditTheReaderHonoured_IsNotAppliedTwice()
    {
        var edit = new Mp4AudioStart(TimeSpan.FromSeconds(1), TimeSpan.Zero);

        Assert.Null(MediaFoundationDecoder.EditToApply(edit, firstTimestamp: TimeSpan.FromSeconds(1).Ticks));
        Assert.Null(MediaFoundationDecoder.EditToApply(edit, firstTimestamp: TimeSpan.FromMilliseconds(990).Ticks));
    }

    [Fact]
    public void NoEmptyEdit_OrOneWithinCodecJitter_IsNotApplied()
    {
        Assert.Null(MediaFoundationDecoder.EditToApply(Mp4AudioStart.None, firstTimestamp: 0));
        Assert.Null(MediaFoundationDecoder.EditToApply(new Mp4AudioStart(TimeSpan.Zero, TimeSpan.FromMilliseconds(23)), firstTimestamp: 0));
        Assert.Null(MediaFoundationDecoder.EditToApply(new Mp4AudioStart(TimeSpan.FromMilliseconds(10), TimeSpan.Zero), firstTimestamp: 0));
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

    [Fact]
    public async Task AnAacM4a_IsAudio_AndDecodesTo16kMonoPcm16()
    {
        SkipUnlessWindows();
        var path = Path.Combine(_dir, "memo.m4a");
        TestMedia.WriteRequired(path, TestMedia.Aac, video: false, TimeSpan.FromSeconds(3));

        var probe = await _decoder.ProbeAsync(path, Ct);
        var destination = Path.Combine(_dir, "system.wav");
        await _decoder.DecodeAudioAsync(path, destination, null, Ct);

        Assert.Equal(MediaKind.Audio, probe.Kind);
        AssertNear(TimeSpan.FromSeconds(3), probe.Duration, "probe duration");
        AssertTone(destination, TimeSpan.FromSeconds(3), toneFrom: TimeSpan.Zero);
    }

    [Fact]
    public async Task AnH264AacMp4_IsVideo_WithItsDuration_AndItsAudioDecodesTo16kMonoPcm16()
    {
        SkipUnlessWindows();
        var path = Path.Combine(_dir, "meeting.mp4");
        TestMedia.WriteRequired(path, TestMedia.Aac, video: true, TimeSpan.FromSeconds(3));

        var probe = await _decoder.ProbeAsync(path, Ct);
        var destination = Path.Combine(_dir, "system.wav");
        await _decoder.DecodeAudioAsync(path, destination, null, Ct);

        Assert.Equal(MediaKind.Video, probe.Kind);
        Assert.Equal(new FileInfo(path).Length, probe.SizeBytes);
        AssertNear(TimeSpan.FromSeconds(3), probe.Duration, "probe duration");
        AssertTone(destination, TimeSpan.FromSeconds(3), toneFrom: TimeSpan.Zero);
    }

    [Fact]
    public async Task AnMp4WhoseAudioStartsLate_IsPaddedWithSilence_SoTheTrackStaysInStepWithTheVideo()
    {
        SkipUnlessWindows();
        var path = Path.Combine(_dir, "late.mp4");
        TestMedia.WriteRequired(path, TestMedia.Aac, video: true, TimeSpan.FromSeconds(3), audioStart: TimeSpan.FromSeconds(1));
        var destination = Path.Combine(_dir, "system.wav");

        // The sink writer stamps the audio at 1 s but may store another offset (or rebase it): expect what the file says.
        var stored = Mp4EditList.ReadAudioStart(path, out var problem);
        Assert.Null(problem);
        TestContext.Current.TestOutputHelper?.WriteLine($"late.mp4 audio edit: delay {stored.Delay}, trim {stored.Trim}");
        var probe = await _decoder.ProbeAsync(path, Ct);
        await _decoder.DecodeAudioAsync(path, destination, null, Ct);

        // Silence for the stored delay, then the 2 s of tone that were written.
        AssertTone(destination, stored.Delay + TimeSpan.FromSeconds(2), toneFrom: stored.Delay);
        AssertNear(TimeSpan.FromSeconds(3), probe.Duration, "probe duration");
        if (stored.Delay > TimeSpan.FromMilliseconds(500))
        {
            // The offset survived in the file, so the track is as long as the picture.
            using var reader = WavReader.Open(destination);
            AssertNear(probe.Duration, reader.Duration, "decoded duration against the probe");
        }
    }

    [Fact]
    public async Task AnMp3_IsAudio_AndDecodes()
    {
        SkipUnlessWindows();
        var path = Path.Combine(_dir, "podcast.mp3");
        TestMedia.WriteOrSkip(path, TestMedia.Mp3, video: false, TimeSpan.FromSeconds(3));

        var probe = await _decoder.ProbeAsync(path, Ct);
        var destination = Path.Combine(_dir, "system.wav");
        await _decoder.DecodeAudioAsync(path, destination, null, Ct);

        Assert.Equal(MediaKind.Audio, probe.Kind);
        AssertNear(TimeSpan.FromSeconds(3), probe.Duration, "probe duration");
        AssertTone(destination, TimeSpan.FromSeconds(3), toneFrom: TimeSpan.Zero);
    }

    [Fact]
    public async Task AFlac_IsAudio_AndDecodes()
    {
        SkipUnlessWindows();
        var path = Path.Combine(_dir, "lecture.flac");
        TestMedia.WriteOrSkip(path, TestMedia.Flac, video: false, TimeSpan.FromSeconds(3));

        var probe = await _decoder.ProbeAsync(path, Ct);
        var destination = Path.Combine(_dir, "system.wav");
        await _decoder.DecodeAudioAsync(path, destination, null, Ct);

        Assert.Equal(MediaKind.Audio, probe.Kind);
        AssertNear(TimeSpan.FromSeconds(3), probe.Duration, "probe duration");
        AssertTone(destination, TimeSpan.FromSeconds(3), toneFrom: TimeSpan.Zero);
    }

    /// <summary>Encoders add priming and padding (AAC: about 46 ms), so lengths are compared with this tolerance.</summary>
    private static void AssertNear(TimeSpan expected, TimeSpan actual, string what) =>
        Assert.True((actual - expected).Duration() <= TimeSpan.FromMilliseconds(200), $"{what}: expected about {expected}, got {actual}");

    /// <summary>
    /// The decoded track is the recorder's format, about <paramref name="duration"/> long, silent before
    /// <paramref name="toneFrom"/> and the test tone (down-mixed, still about <see cref="TestMedia.ToneAmplitude"/>) after it.
    /// </summary>
    private static void AssertTone(string wav, TimeSpan duration, TimeSpan toneFrom)
    {
        using (var reader = WavReader.Open(wav))
        {
            Assert.Equal((16_000, 1, 16), (reader.Layout.SampleRate, reader.Layout.Channels, reader.Layout.BitsPerSample));
            AssertNear(duration, reader.Duration, "decoded duration");
        }

        var (_, samples) = WavReader.ReadAll(wav);
        const int Rate = 16_000;
        float Peak(double from, double to) =>
            samples.Skip((int)(from * Rate)).Take((int)((to - from) * Rate)).DefaultIfEmpty(0f).Max(MathF.Abs);

        var start = toneFrom.TotalSeconds;
        if (start > 0)
        {
            Assert.True(Peak(0, start - 0.2) < 0.02f, $"expected silence before {start} s, peak {Peak(0, start - 0.2)}");
        }

        var tone = Peak(start + 0.2, duration.TotalSeconds - 0.2);
        Assert.InRange(tone, 0.35f, 0.65f);
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
