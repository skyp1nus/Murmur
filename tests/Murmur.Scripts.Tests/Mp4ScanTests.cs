using static Murmur.Scripts.Tests.Mp4Builder;

namespace Murmur.Scripts.Tests;

/// <summary>The MP4 reader behind <c>scripts/check-recording.ps1 -ExpectScreen</c>.</summary>
public sealed class Mp4ScanTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "murmur-mp4scan-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private MurmurMp4ScanResult Scan(byte[] file)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(path, file);
        return MurmurMp4Scan.Scan(path);
    }

    [Fact]
    public void Fragmented_file_sums_trun_sample_durations_over_the_mdhd_timescale()
    {
        // The 12-frame, 2-fragment file the recorder produced for a 10 minute recording (timescale 30000).
        uint[] first = [1232, 597, 1167, 1012, 770, 1690, 555, 1166, 1140, 1000];
        uint[] second = [392, 1000];

        var result = Scan(Fragmented(30000, first, second));

        Assert.Null(result.Error);
        Assert.True(result.Fragmented);
        Assert.Equal(2, result.Fragments);
        Assert.True(result.FramesKnown);
        Assert.Equal(12, result.Frames);
        Assert.Equal("trun", result.DurationSource);
        Assert.Equal(11721 / 30000.0, result.DurationS, 6);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void A_thirty_fps_file_reports_its_real_length()
    {
        // 600 s at 30 fps in 1 s fragments, 1001/30000 s per frame (29.97 fps).
        var fragments = Enumerable.Range(0, 600).Select(_ => Enumerable.Repeat(1001u, 30).ToArray()).ToArray();

        var result = Scan(Fragmented(30000, fragments));

        Assert.Equal(18000, result.Frames);
        Assert.Equal(18000 * 1001 / 30000.0, result.DurationS, 6);
        Assert.Equal(600, result.Fragments);
    }

    [Fact]
    public void Samples_without_a_duration_use_the_trex_default()
    {
        var file = Bytes(
            Ftyp(),
            Moov(Mvhd(1000, 0), Trak(1, 1000), Mvex(Trex(1, 40))),
            Moof(1, Traf(1, new uint[10], durationsInTrun: false)),
            Mdat());

        var result = Scan(file);

        Assert.Equal(10, result.Frames);
        Assert.Equal(0.4, result.DurationS, 6);
    }

    [Fact]
    public void The_tfhd_default_overrides_the_trex_default()
    {
        var file = Bytes(
            Ftyp(),
            Moov(Mvhd(1000, 0), Trak(1, 1000), Mvex(Trex(1, 40))),
            Moof(1, Traf(1, new uint[10], tfhdDefaultDuration: 100, durationsInTrun: false)),
            Mdat());

        var result = Scan(file);

        Assert.Equal(1.0, result.DurationS, 6);
    }

    [Fact]
    public void Trex_before_trak_is_still_matched_by_track_id()
    {
        var file = Bytes(
            Ftyp(),
            Moov(Mvhd(1000, 0), Mvex(Trex(1, 50)), Trak(1, 1000)),
            Moof(1, Traf(1, new uint[4], durationsInTrun: false)),
            Mdat());

        var result = Scan(file);

        Assert.Null(result.Error);
        Assert.Equal(0.2, result.DurationS, 6);
    }

    [Fact]
    public void Version_one_boxes_are_read()
    {
        var file = Bytes(
            Ftyp(),
            Moov(Mvhd(90000, 90000 * 7UL, v1: true), Trak(2, 90000, v1: true), Mvex(Trex(2, 0))),
            Moof(1, Traf(2, [3000, 3000, 3000], v1: true)),
            Mdat());

        var result = Scan(file);

        Assert.Null(result.Error);
        Assert.Equal(3, result.Frames);
        Assert.Equal(9000 / 90000.0, result.DurationS, 6);
        Assert.Equal(7.0, result.MvhdDurationS, 6);
    }

    [Fact]
    public void Only_the_video_track_is_counted()
    {
        var file = Bytes(
            Ftyp(),
            Moov(Mvhd(1000, 0), Trak(1, 48000, "soun"), Trak(2, 1000), Mvex(Trex(1, 1024), Trex(2, 0))),
            Moof(1, Traf(1, [1024, 1024, 1024]), Traf(2, [100, 100])),
            Mdat());

        var result = Scan(file);

        Assert.Equal(2, result.Frames);
        Assert.Equal(0.2, result.DurationS, 6);
    }

    [Fact]
    public void Classic_mp4_uses_the_stts_table()
    {
        var file = Bytes(
            Ftyp(),
            Mdat(),
            Moov(Mvhd(1000, 5000), Trak(1, 600, stts: [(100, 20), (50, 10)], stszSamples: 150)));

        var result = Scan(file);

        Assert.False(result.Fragmented);
        Assert.True(result.FramesKnown);
        Assert.Equal(150, result.Frames);
        Assert.Equal("stts", result.DurationSource);
        Assert.Equal((100 * 20 + 50 * 10) / 600.0, result.DurationS, 6);
    }

    [Fact]
    public void A_header_without_samples_falls_back_to_mehd_and_has_unknown_frames()
    {
        var file = Bytes(Ftyp(), Moov(Mvhd(1000, 99), Trak(1, 30000), Mvex(Mehd(12000), Trex(1, 0))));

        var result = Scan(file);

        Assert.False(result.FramesKnown);
        Assert.Equal(0, result.Frames);
        Assert.Equal("mehd", result.DurationSource);
        Assert.Equal(12.0, result.DurationS, 6);
    }

    [Fact]
    public void A_header_without_samples_or_mehd_falls_back_to_mvhd()
    {
        var file = Bytes(Ftyp(), Moov(Mvhd(1000, 4500), Trak(1, 30000)));

        var result = Scan(file);

        Assert.False(result.FramesKnown);
        Assert.Equal("mvhd", result.DurationSource);
        Assert.Equal(4.5, result.DurationS, 6);
    }

    [Fact]
    public void A_file_cut_inside_the_last_mdat_keeps_every_complete_fragment()
    {
        var full = Fragmented(1000, [40, 40, 40], [40, 40], [40, 40, 40, 40]);
        // Cut 20 bytes into the last mdat payload (the mfra at the end goes away too).
        var mfra = Box("mfra", Box("mfro", FullHeader(0, 0), U32(16))).Length;
        var cut = full[..(full.Length - mfra - Mdat().Length + 20)];

        var result = Scan(cut);

        Assert.Null(result.Error);
        Assert.True(result.Truncated);
        Assert.Equal(9, result.Frames);
        Assert.Equal(0.36, result.DurationS, 6);
    }

    [Fact]
    public void A_file_cut_inside_a_moof_drops_that_fragment()
    {
        var full = Fragmented(1000, [40, 40, 40], [40, 40]);
        var secondMoof = Moof(2, Traf(1, [40, 40]));
        var cut = full[..(full.Length - Box("mfra", Box("mfro", FullHeader(0, 0), U32(16))).Length - Mdat().Length - secondMoof.Length + 30)];

        var result = Scan(cut);

        Assert.True(result.Truncated);
        Assert.Equal(3, result.Frames);
    }

    [Fact]
    public void A_file_without_moov_is_an_error()
    {
        var result = Scan(Bytes(Ftyp(), Moof(1, Traf(1, [40])), Mdat()));

        Assert.NotNull(result.Error);
        Assert.Contains("moov", result.Error);
    }

    [Fact]
    public void Large_size_and_open_ended_boxes_are_walked()
    {
        var file = Bytes(
            Ftyp(),
            Moov(Mvhd(1000, 0), Trak(1, 1000), Mvex(Trex(1, 0))),
            Moof(1, Traf(1, [40, 40])),
            LargeBox("mdat", Zeros(32)),
            Moof(2, Traf(1, [40])),
            OpenEndedBox("mdat", Zeros(16)));

        var result = Scan(file);

        Assert.Null(result.Error);
        Assert.False(result.Truncated);
        Assert.Equal(3, result.Frames);
        Assert.Equal(0.12, result.DurationS, 6);
    }

    [Fact]
    public void An_empty_file_is_an_error()
    {
        var result = Scan([]);

        Assert.NotNull(result.Error);
        Assert.Equal(0, result.FileBytes);
    }

    [Fact]
    public void A_file_that_is_not_an_mp4_is_an_error()
    {
        var result = Scan(System.Text.Encoding.ASCII.GetBytes("RIFF....WAVEfmt this is not a video file"));

        Assert.NotNull(result.Error);
        Assert.Contains("not an MP4", result.Error);
    }

    [Fact]
    public void A_missing_file_is_an_error_not_an_exception()
    {
        var result = MurmurMp4Scan.Scan(Path.Combine(_dir, "nope.mp4"));

        Assert.NotNull(result.Error);
    }
}
