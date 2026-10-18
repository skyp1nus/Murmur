using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Murmur.Capture.Windows.Tests;

/// <summary>One video sample of a fragmented MP4: start and duration in the track's timescale units, sync or not.</summary>
internal readonly record struct Mp4Sample(long Start, long Duration, bool Sync);

/// <summary>One complete moof + mdat pair: where it starts on the timeline, how many samples, whether it opens with a sync sample.</summary>
internal readonly record struct Mp4Fragment(long Start, long Duration, int Samples, bool StartsWithSync);

/// <summary>What <see cref="Mp4Fragments.Read"/> found; times in <see cref="Timescale"/> units, laid end to end (no tfdt).</summary>
internal sealed record Mp4FragmentScan(uint Timescale, IReadOnlyList<Mp4Fragment> Fragments, IReadOnlyList<Mp4Sample> Samples, bool Truncated, long FileBytes, long RecoverableBytes)
{
    public double Seconds(long units) => Timescale == 0 ? 0 : units / (double)Timescale;

    /// <summary>The end of the last complete fragment: what a player gets back from the file (s).</summary>
    public double EndS => Samples.Count == 0 ? 0 : Seconds(Samples[^1].Start + Samples[^1].Duration);

    /// <summary>Start times of the sync samples (s).</summary>
    public IReadOnlyList<double> SyncTimesS => Samples.Where(s => s.Sync).Select(s => Seconds(s.Start)).ToList();

    /// <summary>
    /// The longest stretch of timeline without a sync sample start (s): between two sync samples, and from the last one
    /// to the end of the file.
    /// </summary>
    public double MaxSyncGapS => MaxGap(SyncTimesS);

    /// <summary>The longest stretch between fragment starts, and from the last one to the end of the file (s).</summary>
    public double MaxFragmentGapS => MaxGap(Fragments.Select(f => Seconds(f.Start)).ToList());

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"timescale {Timescale}, {Fragments.Count} fragments, {Samples.Count} samples, {SyncTimesS.Count} sync, end {EndS:F3} s, max sync gap {MaxSyncGapS:F3} s, max fragment gap {MaxFragmentGapS:F3} s, truncated {Truncated}, {RecoverableBytes} of {FileBytes} bytes in complete boxes; sync at [{string.Join(", ", SyncTimesS.Take(40).Select(t => t.ToString("F2", CultureInfo.InvariantCulture)))}]");

    private double MaxGap(IReadOnlyList<double> starts)
    {
        if (starts.Count == 0)
        {
            return EndS;
        }

        var max = starts[0];
        for (var i = 1; i < starts.Count; i++)
        {
            max = Math.Max(max, starts[i] - starts[i - 1]);
        }

        return Math.Max(max, EndS - starts[^1]);
    }
}

/// <summary>
/// Reads the sample timeline and sync flags of the (single) video track of a fragmented MP4 as Media Foundation's
/// FMPEG4 sink writes it, also from a file that was never finalized (a crash): only complete moof + mdat pairs count,
/// as a player recovers nothing of a fragment whose data is cut off. Sample flags come from trun (per sample or the
/// first-sample flags), tfhd or trex; a sample is sync when sample_is_non_sync_sample (0x10000) is clear.
/// </summary>
internal static class Mp4Fragments
{
    private const uint NonSync = 0x0001_0000;

    public static Mp4FragmentScan Read(string path)
    {
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }

        uint timescale = 0, trexDuration = 0, trexFlags = 0;
        var fragments = new List<Mp4Fragment>();
        var samples = new List<Mp4Sample>();
        long time = 0, recoverable = 0;
        var truncated = false;
        var pos = 0L;
        (int Start, int End)? pendingMoof = null;
        while (pos + 8 <= bytes.Length)
        {
            var (type, body, end) = Box(bytes, pos);
            if (end > bytes.Length || end <= pos)
            {
                truncated = true;
                break;
            }

            switch (type)
            {
                case "moov":
                    ReadMoov(bytes, (int)body, (int)end, ref timescale, ref trexDuration, ref trexFlags);
                    recoverable = end;
                    break;
                case "moof":
                    pendingMoof = ((int)body, (int)end);
                    break;
                case "mdat" when pendingMoof is { } moof:
                    var first = samples.Count;
                    ReadMoof(bytes, moof.Start, moof.End, trexDuration, trexFlags, samples, ref time);
                    if (samples.Count > first)
                    {
                        var start = samples[first].Start;
                        fragments.Add(new Mp4Fragment(start, time - start, samples.Count - first, samples[first].Sync));
                    }

                    pendingMoof = null;
                    recoverable = end;
                    break;
                default:
                    if (type is "ftyp")
                    {
                        recoverable = end;
                    }

                    break;
            }

            pos = end;
        }

        if (pendingMoof is not null || pos < bytes.Length)
        {
            truncated = true; // a moof without its mdat, or a cut-off box
        }

        return new Mp4FragmentScan(timescale, fragments, samples, truncated, bytes.Length, recoverable);
    }

    private static (string Type, long Body, long End) Box(byte[] b, long pos)
    {
        long size = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)pos));
        var type = Encoding.ASCII.GetString(b, (int)pos + 4, 4);
        var header = 8L;
        if (size == 1)
        {
            if (pos + 16 > b.Length)
            {
                return (type, pos + 16, long.MaxValue);
            }

            size = (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan((int)pos + 8));
            header = 16;
        }
        else if (size == 0)
        {
            size = b.Length - pos;
        }

        return size < header ? (type, pos + header, pos) : (type, pos + header, pos + size);
    }

    private static IEnumerable<(string Type, int Body, int End)> Children(byte[] b, int start, int end)
    {
        var p = (long)start;
        while (p + 8 <= end)
        {
            var (type, body, boxEnd) = Box(b, p);
            if (boxEnd > end || boxEnd <= p)
            {
                yield break;
            }

            yield return (type, (int)body, (int)boxEnd);
            p = boxEnd;
        }
    }

    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));

    private static void ReadMoov(byte[] b, int start, int end, ref uint timescale, ref uint trexDuration, ref uint trexFlags)
    {
        foreach (var (type, body, boxEnd) in Children(b, start, end))
        {
            switch (type)
            {
                case "trak":
                case "mdia":
                case "mvex":
                    ReadMoov(b, body, boxEnd, ref timescale, ref trexDuration, ref trexFlags);
                    break;
                case "mdhd" when timescale == 0:
                    timescale = U32(b, body + 4 + (b[body] == 1 ? 16 : 8));
                    break;
                case "trex":
                    trexDuration = U32(b, body + 12);
                    trexFlags = U32(b, body + 20);
                    break;
            }
        }
    }

    private static void ReadMoof(byte[] b, int start, int end, uint trexDuration, uint trexFlags, List<Mp4Sample> samples, ref long time)
    {
        foreach (var (type, body, boxEnd) in Children(b, start, end))
        {
            if (type != "traf")
            {
                continue;
            }

            uint duration = trexDuration, flags = trexFlags;
            foreach (var (child, childBody, _) in Children(b, body, boxEnd))
            {
                if (child == "tfhd")
                {
                    var tf = U32(b, childBody) & 0xFF_FFFF;
                    var o = childBody + 8;
                    o += (tf & 0x1) != 0 ? 8 : 0;
                    o += (tf & 0x2) != 0 ? 4 : 0;
                    if ((tf & 0x8) != 0)
                    {
                        duration = U32(b, o);
                        o += 4;
                    }

                    o += (tf & 0x10) != 0 ? 4 : 0;
                    if ((tf & 0x20) != 0)
                    {
                        flags = U32(b, o);
                    }
                }
                else if (child == "trun")
                {
                    var tr = U32(b, childBody) & 0xFF_FFFF;
                    var count = U32(b, childBody + 4);
                    var o = childBody + 8;
                    o += (tr & 0x1) != 0 ? 4 : 0;
                    uint? firstFlags = null;
                    if ((tr & 0x4) != 0)
                    {
                        firstFlags = U32(b, o);
                        o += 4;
                    }

                    for (var i = 0; i < count; i++)
                    {
                        var d = duration;
                        var f = i == 0 && firstFlags is { } ff ? ff : flags;
                        if ((tr & 0x100) != 0)
                        {
                            d = U32(b, o);
                            o += 4;
                        }

                        o += (tr & 0x200) != 0 ? 4 : 0;
                        if ((tr & 0x400) != 0)
                        {
                            f = U32(b, o);
                            o += 4;
                        }

                        o += (tr & 0x800) != 0 ? 4 : 0;
                        samples.Add(new Mp4Sample(time, d, (f & NonSync) == 0));
                        time += d;
                    }
                }
            }
        }
    }
}
