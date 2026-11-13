using System.Buffers.Binary;
using System.Text;
using Murmur.Core.Import;

namespace Murmur.Core.Tests.Import;

/// <summary>The MP4 edit-list reader on handcrafted files (box layouts as ffmpeg and Media Foundation write them).</summary>
public sealed class Mp4EditListTests
{
    [Fact]
    public void TheOwnersLateAudioSample_StartsTheSoundAfterItsEmptyEdit()
    {
        // The layout of ffmpeg's `-itsoffset 1 ... -c:a aac late-audio.mp4`: moov after mdat, video track first (its
        // B-frame trim), then the audio with an empty edit of 976 ms (mvhd timescale 1000) and media from 0.
        var file = LateAudioSample();

        var start = Read(file, out var problem);

        Assert.Null(problem);
        Assert.Equal(new Mp4AudioStart(TimeSpan.FromMilliseconds(976), TimeSpan.Zero), start);
        Assert.False(start.IsNone);
    }

    [Fact]
    public void AStartTrim_IsInTheMediaTimescale()
    {
        var file = Mp4(
            Box("moov",
                Mvhd(600),
                Track("soun", 48_000, Elst(0, (300, -1), (1200, 1024)))));

        var start = Read(file, out var problem);

        Assert.Null(problem);
        Assert.Equal(TimeSpan.FromMilliseconds(500), start.Delay);
        Assert.Equal(TimeSpan.FromTicks(1024 * TimeSpan.TicksPerSecond / 48_000), start.Trim);
    }

    [Fact]
    public void SeveralLeadingEmptyEdits_AreSummed()
    {
        var file = Mp4(Box("moov", Mvhd(1000), Track("soun", 44_100, Elst(0, (250, -1), (750, -1), (2000, 0)))));

        Assert.Equal(TimeSpan.FromSeconds(1), Read(file, out _).Delay);
    }

    [Fact]
    public void Version1Boxes_AreRead()
    {
        var file = Mp4(
            Box("moov",
                Mvhd(90_000, version: 1),
                Track("soun", 44_100, Elst(1, (135_000, -1), (180_000, 2112)), mdhdVersion: 1)));

        var start = Read(file, out var problem);

        Assert.Null(problem);
        Assert.Equal(TimeSpan.FromSeconds(1.5), start.Delay);
        Assert.Equal(TimeSpan.FromTicks(2112 * TimeSpan.TicksPerSecond / 44_100), start.Trim);
    }

    [Fact]
    public void Version1EditDurations_BeyondThirtyTwoBits_AreRead()
    {
        // 2^32 + 4 units at 1 MHz: 4294.967300 s, more than a 32-bit duration can hold.
        var file = Mp4(Box("moov", Mvhd(1_000_000), Track("soun", 44_100, Elst(1, ((1L << 32) + 4, -1), (1, 0)))));

        Assert.Equal(TimeSpan.FromTicks(((1L << 32) + 4) * 10), Read(file, out _).Delay);
    }

    [Fact]
    public void SixtyFourBitBoxSizes_AreFollowed()
    {
        var file = Concat(
            Box("ftyp", Ascii("isom"), new byte[4]),
            Box64("mdat", new byte[100]),
            Box64("moov",
                Mvhd(1000),
                Box64("trak",
                    Box64("edts", Elst(0, (1000, -1), (2000, 0))),
                    Box64("mdia", Mdhd(44_100), Hdlr("soun")))));

        var start = Read(file, out var problem);

        Assert.Null(problem);
        Assert.Equal(TimeSpan.FromSeconds(1), start.Delay);
    }

    [Fact]
    public void AnMdatToTheEndOfTheFile_AfterTheMoov_IsFine()
    {
        var moov = Box("moov", Mvhd(1000), Track("soun", 44_100, Elst(0, (1000, -1), (2000, 0))));
        var mdat = Box("mdat", new byte[64]);
        BinaryPrimitives.WriteUInt32BigEndian(mdat, 0); // size 0: to the end of the file

        Assert.Equal(TimeSpan.FromSeconds(1), Read(Mp4(moov, mdat), out _).Delay);
    }

    [Fact]
    public void AnAudioTrackWithoutEdits_HasNoOffset_AndNoProblem()
    {
        var file = Mp4(Box("moov", Mvhd(1000), Track("vide", 15_360, Elst(0, (500, -1), (3000, 0))), Track("soun", 44_100, edits: null)));

        var start = Read(file, out var problem);

        Assert.Null(problem);
        Assert.True(start.IsNone);
    }

    [Fact]
    public void AnEditListThatPlaysFromTheStart_HasNoOffset()
    {
        var file = Mp4(Box("moov", Mvhd(1000), Track("soun", 44_100, Elst(0, (3000, 0)))));

        var start = Read(file, out var problem);

        Assert.Null(problem);
        Assert.Equal(Mp4AudioStart.None, start);
    }

    [Fact]
    public void AFileWithoutAudio_HasNoOffset_AndNoProblem()
    {
        var file = Mp4(Box("moov", Mvhd(1000), Track("vide", 15_360, Elst(0, (500, -1), (3000, 0)))));

        var start = Read(file, out var problem);

        Assert.Null(problem);
        Assert.True(start.IsNone);
    }

    [Fact]
    public void OnlyTheFirstAudioTrack_Counts_WhereverTheVideoIs()
    {
        var file = Mp4(
            Box("moov",
                Track("vide", 15_360, Elst(0, (2000, -1), (3000, 0))),
                Track("soun", 44_100, Elst(0, (400, -1), (2600, 0))),
                Track("soun", 48_000, Elst(0, (900, -1), (2100, 0))),
                Mvhd(1000))); // after the tracks: allowed, and the timescale still applies

        Assert.Equal(TimeSpan.FromMilliseconds(400), Read(file, out _).Delay);
    }

    [Fact]
    public void TheHandlerAfterTheEdits_IsStillFound()
    {
        var file = Mp4(
            Box("moov",
                Mvhd(1000),
                Box("trak",
                    Box("tkhd", new byte[84]),
                    Box("mdia", Box("minf", new byte[16]), Hdlr("soun"), Mdhd(44_100)),
                    Box("edts", Elst(0, (750, -1), (1000, 0))))));

        Assert.Equal(TimeSpan.FromMilliseconds(750), Read(file, out _).Delay);
    }

    [Fact]
    public void NoMoov_IsAProblem()
    {
        var file = Concat(Box("ftyp", Ascii("isom"), new byte[4]), Box("mdat", new byte[32]));

        var start = Read(file, out var problem);

        Assert.True(start.IsNone);
        Assert.Contains("moov", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void NotAnMp4_IsAProblem_NotAnException()
    {
        var wav = Encoding.ASCII.GetBytes("RIFF\x24\0\0\0WAVEfmt \x10\0\0\0\x01\0\x01\0\x80\x3e\0\0\0\x7d\0\0\x02\0\x10\0data\0\0\0\0");

        var start = Read(wav, out var problem);

        Assert.True(start.IsNone);
        Assert.NotNull(problem);
    }

    [Fact]
    public void AnEmptyFile_HasNoMoov()
    {
        var start = Read([], out var problem);

        Assert.True(start.IsNone);
        Assert.NotNull(problem);
    }

    [Fact]
    public void AnEditListShorterThanItsEntryCount_IsAProblem()
    {
        var elst = Elst(0, (1000, -1), (2000, 0));
        BinaryPrimitives.WriteUInt32BigEndian(elst.AsSpan(12), 3); // entry_count 3, only 2 present
        var file = Mp4(Box("moov", Mvhd(1000), Track("soun", 44_100, elst)));

        var start = Read(file, out var problem);

        Assert.True(start.IsNone);
        Assert.NotNull(problem);
    }

    [Fact]
    public void ABoxLargerThanItsParent_IsAProblem()
    {
        var trak = Track("soun", 44_100, Elst(0, (1000, -1), (2000, 0)));
        BinaryPrimitives.WriteUInt32BigEndian(trak, (uint)trak.Length + 1000);
        var file = Mp4(Box("moov", Mvhd(1000), trak));

        var start = Read(file, out var problem);

        Assert.True(start.IsNone);
        Assert.NotNull(problem);
    }

    [Fact]
    public void AnUnknownElstVersion_IsAProblem()
    {
        var elst = Elst(0, (1000, -1), (2000, 0));
        elst[8] = 2;

        var start = Read(Mp4(Box("moov", Mvhd(1000), Track("soun", 44_100, elst))), out var problem);

        Assert.True(start.IsNone);
        Assert.NotNull(problem);
    }

    [Fact]
    public void AMovieWithoutATimescale_IsAProblem()
    {
        var start = Read(Mp4(Box("moov", Mvhd(0), Track("soun", 44_100, Elst(0, (1000, -1), (2000, 0))))), out var problem);

        Assert.True(start.IsNone);
        Assert.NotNull(problem);
    }

    [Fact]
    public void ADelayOfMoreThanADay_IsTakenAsDamage()
    {
        var file = Mp4(Box("moov", Mvhd(1), Track("soun", 44_100, Elst(1, (long.MaxValue, -1), (1, 0)))));

        var start = Read(file, out var problem);

        Assert.True(start.IsNone);
        Assert.NotNull(problem);
    }

    [Fact]
    public void EveryTruncationAndEveryDamagedByte_IsHandledWithoutThrowing()
    {
        var file = LateAudioSample();
        for (var length = 0; length < file.Length; length++)
        {
            var start = Read(file.AsSpan(0, length).ToArray(), out var problem);

            // Cut anywhere before the end of the moov, nothing is trusted.
            Assert.True(start.IsNone, $"cut at {length}");
            Assert.NotNull(problem);
        }

        var random = new Random(66);
        for (var i = 0; i < 2000; i++)
        {
            var damaged = (byte[])file.Clone();
            damaged[random.Next(damaged.Length)] = (byte)random.Next(256);
            damaged[random.Next(damaged.Length)] = (byte)random.Next(256);

            _ = Read(damaged, out _);
        }
    }

    [Fact]
    public void ReadingAFile_AllocatesNothingPerBox()
    {
        var stream = new MemoryStream(LateAudioSample());
        _ = Mp4EditList.ReadAudioStart(stream, out _);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            _ = Mp4EditList.ReadAudioStart(stream, out _);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void AFileOnDisk_IsRead_AndAMissingOneIsAProblem()
    {
        var dir = Path.Combine(Path.GetTempPath(), "murmur-mp4-edit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "late-audio.mp4");
            File.WriteAllBytes(path, LateAudioSample());

            Assert.Equal(TimeSpan.FromMilliseconds(976), Mp4EditList.ReadAudioStart(path, out var problem).Delay);
            Assert.Null(problem);

            var missing = Mp4EditList.ReadAudioStart(Path.Combine(dir, "gone.mp4"), out problem);
            Assert.True(missing.IsNone);
            Assert.NotNull(problem);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"C:\media\call.mp4", true)]
    [InlineData("/media/voice.M4A", true)]
    [InlineData("clip.MoV", true)]
    [InlineData("podcast.mp3", false)]
    [InlineData("meeting.mkv", false)]
    [InlineData("mp4", false)]
    public void AppliesTo_TheIsoMediaExtensions(string path, bool expected) =>
        Assert.Equal(expected, Mp4EditList.AppliesTo(path));

    private static Mp4AudioStart Read(byte[] file, out string? problem) =>
        Mp4EditList.ReadAudioStart(new MemoryStream(file, writable: false), out problem);

    /// <summary>ffmpeg's late-audio.mp4 (3 s of H.264, AAC from 0.976 s), with filler where the sample tables are.</summary>
    private static byte[] LateAudioSample() => Concat(
        Box("ftyp", Ascii("isom"), new byte[4], Ascii("isomiso2avc1mp41")),
        Box("free"),
        Box("mdat", new byte[512]),
        Box("moov",
            Mvhd(1000),
            Track("vide", 15_360, Elst(0, (3000, 1024))),
            Track("soun", 44_100, Elst(0, (976, -1), (2024, 0))),
            Box("udta", new byte[90])));

    private static byte[] Mp4(params byte[][] boxes) =>
        Concat([Box("ftyp", Ascii("isom"), new byte[4]), .. boxes]);

    private static byte[] Track(string handler, uint mediaScale, byte[]? edits, byte mdhdVersion = 0)
    {
        var mdia = Box("mdia", Mdhd(mediaScale, mdhdVersion), Hdlr(handler), Box("minf", new byte[40]));
        return edits is null
            ? Box("trak", Box("tkhd", new byte[84]), mdia)
            : Box("trak", Box("tkhd", new byte[84]), Box("edts", edits), mdia);
    }

    private static byte[] Mvhd(uint scale, byte version = 0) => Box("mvhd", TimeHeader(scale, version, extra: 80));

    private static byte[] Mdhd(uint scale, byte version = 0) => Box("mdhd", TimeHeader(scale, version, extra: 4));

    /// <summary>version, flags, creation and modification time, timescale, duration, then <paramref name="extra"/> bytes.</summary>
    private static byte[] TimeHeader(uint scale, byte version, int extra)
    {
        var times = version == 1 ? 16 : 8;
        var duration = version == 1 ? 8 : 4;
        var payload = new byte[4 + times + 4 + duration + extra];
        payload[0] = version;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4 + times), scale);
        return payload;
    }

    private static byte[] Hdlr(string handler) => Box("hdlr", new byte[8], Ascii(handler), new byte[12], Ascii("Handler\0"));

    private static byte[] Elst(byte version, params (long Duration, long MediaTime)[] entries)
    {
        var size = version == 1 ? 20 : 12;
        var payload = new byte[8 + (entries.Length * size)];
        payload[0] = version;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (uint)entries.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = payload.AsSpan(8 + (i * size), size);
            if (version == 1)
            {
                BinaryPrimitives.WriteUInt64BigEndian(entry, (ulong)entries[i].Duration);
                BinaryPrimitives.WriteInt64BigEndian(entry[8..], entries[i].MediaTime);
                BinaryPrimitives.WriteInt16BigEndian(entry[16..], 1);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(entry, (uint)entries[i].Duration);
                BinaryPrimitives.WriteInt32BigEndian(entry[4..], (int)entries[i].MediaTime);
                BinaryPrimitives.WriteInt16BigEndian(entry[8..], 1);
            }
        }

        return Box("elst", payload);
    }

    private static byte[] Box(string type, params byte[][] content)
    {
        var payload = Concat(content);
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type, box.AsSpan(4));
        payload.CopyTo(box, 8);
        return box;
    }

    /// <summary>A box with a 64-bit size (<c>size == 1</c>, then the largesize).</summary>
    private static byte[] Box64(string type, params byte[][] content)
    {
        var payload = Concat(content);
        var box = new byte[16 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, 1);
        Encoding.ASCII.GetBytes(type, box.AsSpan(4));
        BinaryPrimitives.WriteUInt64BigEndian(box.AsSpan(8), (ulong)box.Length);
        payload.CopyTo(box, 16);
        return box;
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Concat(params byte[][] parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(all, offset);
            offset += part.Length;
        }

        return all;
    }
}
