using System.Buffers.Binary;

namespace Murmur.Core.Import;

/// <summary>
/// Where the first audio track of an MP4/MOV starts on the movie timeline, from its edit list (<c>edts/elst</c>).
/// </summary>
/// <param name="Delay">
/// The leading empty edits (<c>media_time = -1</c>) summed: how long the movie runs before the sound starts. An MP4
/// whose audio starts after its picture has one (ffmpeg's <c>-itsoffset</c>, a sink writer's late first sample).
/// </param>
/// <param name="Trim">
/// The <c>media_time</c> of the first non-empty edit: how much of the track's own start is cut away (encoder priming,
/// a trimmed clip). Zero when the edit starts at the beginning of the media.
/// </param>
public readonly record struct Mp4AudioStart(TimeSpan Delay, TimeSpan Trim)
{
    /// <summary>No edit list, or one that changes nothing.</summary>
    public static Mp4AudioStart None => default;

    /// <summary>True when the edit list changes nothing.</summary>
    public bool IsNone => Delay == TimeSpan.Zero && Trim == TimeSpan.Zero;
}

/// <summary>
/// Reads the edit list of the first audio track of an ISO base media file (MP4, M4A, MOV) in managed code:
/// <c>moov</c> → <c>trak</c> (the first with <c>mdia/hdlr</c> = <c>soun</c>) → <c>edts/elst</c>, timescales from
/// <c>mvhd</c> (segment durations) and <c>mdhd</c> (media times).
/// </summary>
/// <remarks>
/// <para>Media Foundation's MPEG-4 source does not apply edit lists to the audio it decodes, so the import decoder uses
/// this to put the sound where the file says it starts.</para>
/// <para><b>Format.</b> 32- and 64-bit box sizes (<c>size == 1</c>), a size of 0 (to the end of the parent), <c>elst</c>
/// versions 0 and 1, <c>mvhd</c>/<c>mdhd</c> versions 0 and 1, <c>moov</c> before or after <c>mdat</c>.</para>
/// <para><b>Bounded.</b> Only box headers and the few fields needed are read (stack buffers, no allocation per box);
/// boxes are skipped by seeking. At most <see cref="MaxEdits"/> leading edits and <see cref="MaxBoxesPerLevel"/> boxes per
/// parent are looked at. A damaged track other than the audio one is skipped.</para>
/// <para><b>Never throws</b> on a malformed, truncated or unreadable file: the result is <see cref="Mp4AudioStart.None"/>
/// and the problem is reported in words.</para>
/// </remarks>
public static class Mp4EditList
{
    /// <summary>Edits looked at before giving up (real files have one or two).</summary>
    public const int MaxEdits = 1024;

    /// <summary>Boxes looked at in one parent (the file, <c>moov</c>, a <c>trak</c>...) before the file is taken as damaged.</summary>
    public const int MaxBoxesPerLevel = 4096;

    /// <summary><c>media_rate</c> 1.0 (16.16 fixed point: <c>media_rate_integer</c> 1, <c>media_rate_fraction</c> 0).</summary>
    private const int NormalRate = 0x0001_0000;

    private const uint Moov = 0x6D6F6F76; // 'moov'
    private const uint Mvhd = 0x6D766864; // 'mvhd'
    private const uint Trak = 0x7472616B; // 'trak'
    private const uint Mdia = 0x6D646961; // 'mdia'
    private const uint Mdhd = 0x6D646864; // 'mdhd'
    private const uint Hdlr = 0x68646C72; // 'hdlr'
    private const uint Edts = 0x65647473; // 'edts'
    private const uint Elst = 0x656C7374; // 'elst'
    private const uint Soun = 0x736F756E; // 'soun'

    /// <summary>An edit list longer than this is taken as damage, not as a real offset.</summary>
    private static readonly TimeSpan MaxOffset = TimeSpan.FromDays(1);

    /// <summary>True for the extensions of ISO base media files (<c>.mp4</c>, <c>.m4a</c>, <c>.mov</c>; any case).</summary>
    public static bool AppliesTo(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var extension = Path.GetExtension(path.AsSpan());
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The start of the first audio track of the file at <paramref name="path"/>.</summary>
    /// <param name="problem">Why the file could not be read (damaged, truncated, unreadable); null when it was read.</param>
    public static Mp4AudioStart ReadAudioStart(string path, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            // Unbuffered: only a few small reads, each at a seek.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);
            return ReadAudioStart(stream, out problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            problem = ex.Message;
            return Mp4AudioStart.None;
        }
    }

    /// <summary>The start of the first audio track of the file in <paramref name="stream"/> (readable and seekable).</summary>
    /// <param name="problem">Why the file could not be read (damaged, truncated, unreadable); null when it was read.</param>
    public static Mp4AudioStart ReadAudioStart(Stream stream, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            return Read(stream, out problem);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ObjectDisposedException)
        {
            problem = ex.Message;
            return Mp4AudioStart.None;
        }
    }

    private static Mp4AudioStart Read(Stream file, out string? problem)
    {
        var stream = new Source(file, file.Length);
        var end = stream.Length;
        var visited = 0;
        for (var position = 0L; position + 8 <= end;)
        {
            if (++visited > MaxBoxesPerLevel)
            {
                problem = $"the file has more than {MaxBoxesPerLevel} top-level boxes before its 'moov'";
                return Mp4AudioStart.None;
            }

            if (!TryHeader(stream, position, end, out var box))
            {
                problem = $"a top-level box at {position} is damaged or runs past the end of the file";
                return Mp4AudioStart.None;
            }

            if (box.Type == Moov)
            {
                return ReadMovie(stream, box, out problem);
            }

            position = box.End;
        }

        problem = "the file has no 'moov' box";
        return Mp4AudioStart.None;
    }

    private static Mp4AudioStart ReadMovie(Source stream, Box moov, out string? problem)
    {
        uint movieScale = 0;
        var audio = default(Track);
        var found = false;
        var damagedTrak = -1L;
        var visited = 0;
        for (var position = moov.Payload; position + 8 <= moov.End;)
        {
            if (++visited > MaxBoxesPerLevel)
            {
                problem = $"the 'moov' has more than {MaxBoxesPerLevel} boxes";
                return Mp4AudioStart.None;
            }

            if (!TryHeader(stream, position, moov.End, out var box))
            {
                problem = $"a box in 'moov' at {position} is damaged";
                return Mp4AudioStart.None;
            }

            if (box.Type == Mvhd)
            {
                if (!TryTimescale(stream, box, out movieScale))
                {
                    problem = "the 'mvhd' box is damaged";
                    return Mp4AudioStart.None;
                }
            }
            else if (box.Type == Trak && !found)
            {
                if (!TryTrack(stream, box, out var track))
                {
                    // Its own size is sound, so the next track is still found; a damaged video track must not cost the
                    // audio its offset.
                    damagedTrak = damagedTrak < 0 ? position : damagedTrak;
                }
                else if (track.IsSound)
                {
                    audio = track;
                    found = true;
                }
            }

            position = box.End;
        }

        problem = null;
        if (!found)
        {
            if (damagedTrak >= 0)
            {
                problem = $"the 'trak' at {damagedTrak} is damaged and no audio track could be read";
            }

            return Mp4AudioStart.None;
        }

        if (!audio.HasEdits)
        {
            return Mp4AudioStart.None;
        }

        TimeSpan delay = TimeSpan.Zero, trim = TimeSpan.Zero;
        if (audio.EmptyUnits > 0 && !TryDuration(audio.EmptyUnits, movieScale, out delay))
        {
            problem = movieScale == 0 ? "the movie has no timescale" : "the audio edit list delays the sound by more than a day";
            return Mp4AudioStart.None;
        }

        if (audio.MediaTime > 0 && !TryDuration((ulong)audio.MediaTime, audio.MediaScale, out trim))
        {
            problem = audio.MediaScale == 0 ? "the audio track has no timescale" : "the audio edit list trims more than a day";
            return Mp4AudioStart.None;
        }

        return new Mp4AudioStart(delay, trim);
    }

    private static bool TryTrack(Source stream, Box trak, out Track track)
    {
        track = default;
        var visited = 0;
        for (var position = trak.Payload; position + 8 <= trak.End;)
        {
            if (++visited > MaxBoxesPerLevel || !TryHeader(stream, position, trak.End, out var box))
            {
                return false;
            }

            if (box.Type == Edts && !track.HasEdits)
            {
                if (!TryEdits(stream, box, ref track))
                {
                    return false;
                }
            }
            else if (box.Type == Mdia && !TryMedia(stream, box, ref track))
            {
                return false;
            }

            position = box.End;
        }

        return true;
    }

    private static bool TryMedia(Source stream, Box mdia, ref Track track)
    {
        Span<byte> field = stackalloc byte[12];
        var visited = 0;
        for (var position = mdia.Payload; position + 8 <= mdia.End;)
        {
            if (++visited > MaxBoxesPerLevel || !TryHeader(stream, position, mdia.End, out var box))
            {
                return false;
            }

            if (box.Type == Mdhd)
            {
                if (!TryTimescale(stream, box, out var scale))
                {
                    return false;
                }

                track.MediaScale = scale;
            }
            else if (box.Type == Hdlr)
            {
                // FullBox (version, flags), pre_defined, handler_type.
                if (box.End - box.Payload < 12 || !TryRead(stream, box.Payload, field))
                {
                    return false;
                }

                track.IsSound = BinaryPrimitives.ReadUInt32BigEndian(field[8..]) == Soun;
            }

            position = box.End;
        }

        return true;
    }

    private static bool TryEdits(Source stream, Box edts, ref Track track)
    {
        var visited = 0;
        for (var position = edts.Payload; position + 8 <= edts.End;)
        {
            if (++visited > MaxBoxesPerLevel || !TryHeader(stream, position, edts.End, out var box))
            {
                return false;
            }

            if (box.Type == Elst)
            {
                return TryEditList(stream, box, ref track);
            }

            position = box.End;
        }

        return true;
    }

    /// <summary>The leading empty edits summed (movie units) and the media time of the first edit that plays media.</summary>
    private static bool TryEditList(Source stream, Box elst, ref Track track)
    {
        Span<byte> field = stackalloc byte[20];
        if (elst.End - elst.Payload < 8 || !TryRead(stream, elst.Payload, field[..8]))
        {
            return false;
        }

        var version = field[0];
        if (version > 1)
        {
            return false;
        }

        var count = BinaryPrimitives.ReadUInt32BigEndian(field[4..]);
        var size = version == 1 ? 20 : 12;
        if ((ulong)count * (ulong)size > (ulong)(elst.End - elst.Payload - 8))
        {
            return false; // truncated
        }

        track.HasEdits = true;
        var entry = field[..size];
        ulong empty = 0;
        for (var i = 0; i < Math.Min(count, MaxEdits); i++)
        {
            if (!TryRead(stream, elst.Payload + 8 + ((long)i * size), entry))
            {
                return false;
            }

            ulong duration;
            long mediaTime;
            int rate;
            if (version == 1)
            {
                duration = BinaryPrimitives.ReadUInt64BigEndian(entry);
                mediaTime = BinaryPrimitives.ReadInt64BigEndian(entry[8..]);
                rate = BinaryPrimitives.ReadInt32BigEndian(entry[16..]);
            }
            else
            {
                duration = BinaryPrimitives.ReadUInt32BigEndian(entry);
                mediaTime = BinaryPrimitives.ReadInt32BigEndian(entry[4..]);
                rate = BinaryPrimitives.ReadInt32BigEndian(entry[8..]);
            }

            if (mediaTime != -1)
            {
                // Only an edit that plays at normal speed trims the media's start; a dwell (rate 0) or another rate
                // is not a trim.
                track.MediaTime = rate == NormalRate ? Math.Max(mediaTime, 0) : 0;
                break;
            }

            if (duration > ulong.MaxValue - empty)
            {
                return false;
            }

            empty += duration;
        }

        track.EmptyUnits = empty;
        return true;
    }

    /// <summary>The <c>timescale</c> of an <c>mvhd</c> or <c>mdhd</c> (same layout up to it, versions 0 and 1).</summary>
    private static bool TryTimescale(Source stream, Box box, out uint scale)
    {
        scale = 0;
        Span<byte> field = stackalloc byte[24];
        if (box.End - box.Payload < 4 || !TryRead(stream, box.Payload, field[..1]))
        {
            return false;
        }

        // version, flags, creation_time, modification_time (32 or 64 bits each), timescale.
        var offset = field[0] == 1 ? 20 : 12;
        if (field[0] > 1 || box.End - box.Payload < offset + 4 || !TryRead(stream, box.Payload + offset, field[..4]))
        {
            return false;
        }

        scale = BinaryPrimitives.ReadUInt32BigEndian(field);
        return true;
    }

    private static bool TryDuration(ulong units, uint scale, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (scale == 0)
        {
            return false;
        }

        var ticks = (UInt128)units * TimeSpan.TicksPerSecond / scale;
        if (ticks > (UInt128)MaxOffset.Ticks)
        {
            return false;
        }

        duration = TimeSpan.FromTicks((long)ticks);
        return true;
    }

    /// <summary>The box at <paramref name="position"/>, which must end within <paramref name="parentEnd"/>.</summary>
    private static bool TryHeader(Source stream, long position, long parentEnd, out Box box)
    {
        box = default;
        Span<byte> header = stackalloc byte[16];
        if (!TryRead(stream, position, header[..8]))
        {
            return false;
        }

        ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
        var type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        var headerSize = 8;
        if (size == 1)
        {
            // 64-bit largesize after the type.
            if (position + 16 > parentEnd || !TryRead(stream, position + 8, header[8..16]))
            {
                return false;
            }

            size = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
            headerSize = 16;
        }
        else if (size == 0)
        {
            // To the end of the parent (the file, at the top level).
            size = (ulong)(parentEnd - position);
        }

        if (size < (ulong)headerSize || size > (ulong)(parentEnd - position))
        {
            return false;
        }

        box = new Box(type, position + headerSize, position + (long)size);
        return true;
    }

    private static bool TryRead(Source stream, long position, Span<byte> destination)
    {
        if (position < 0 || position > stream.Length - destination.Length)
        {
            return false;
        }

        stream.Stream.Position = position;
        return stream.Stream.ReadAtLeast(destination, destination.Length, throwOnEndOfStream: false) == destination.Length;
    }

    /// <summary>The file and its length (read once).</summary>
    private readonly record struct Source(Stream Stream, long Length);

    /// <param name="Payload">Where the box's content starts (after its header).</param>
    /// <param name="End">Where the box ends (its first byte plus its size).</param>
    private readonly record struct Box(uint Type, long Payload, long End);

    /// <summary>What one <c>trak</c> says: its handler, media timescale and edit list.</summary>
    private struct Track
    {
        public bool IsSound;
        public bool HasEdits;
        public uint MediaScale;
        public ulong EmptyUnits;
        public long MediaTime;
    }
}
