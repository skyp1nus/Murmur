using System.Buffers.Binary;
using System.Text;

namespace Murmur.Scripts.Tests;

/// <summary>Writes just enough ISO BMFF to exercise the reader: boxes are built bottom-up from byte arrays.</summary>
internal static class Mp4Builder
{
    public static byte[] Bytes(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    public static byte[] U32(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        return b;
    }

    public static byte[] U64(ulong value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, value);
        return b;
    }

    public static byte[] Zeros(int count) => new byte[count];

    public static byte[] Box(string type, params byte[][] payload)
    {
        var body = Bytes(payload);
        return Bytes(U32((uint)(body.Length + 8)), Encoding.ASCII.GetBytes(type), body);
    }

    /// <summary>A box with the 64-bit size form (size field = 1).</summary>
    public static byte[] LargeBox(string type, params byte[][] payload)
    {
        var body = Bytes(payload);
        return Bytes(U32(1), Encoding.ASCII.GetBytes(type), U64((ulong)(body.Length + 16)), body);
    }

    /// <summary>The last box of a file: size 0 = "extends to the end of the file".</summary>
    public static byte[] OpenEndedBox(string type, params byte[][] payload) =>
        Bytes(U32(0), Encoding.ASCII.GetBytes(type), Bytes(payload));

    public static byte[] Ftyp() => Box("ftyp", Encoding.ASCII.GetBytes("mp42"), U32(0), Encoding.ASCII.GetBytes("isom"));

    public static byte[] FullHeader(byte version, uint flags) => [version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags];

    public static byte[] Mvhd(uint timescale, ulong duration, bool v1 = false) => v1
        ? Box("mvhd", FullHeader(1, 0), Zeros(16), U32(timescale), U64(duration), Zeros(80))
        : Box("mvhd", FullHeader(0, 0), Zeros(8), U32(timescale), U32((uint)duration), Zeros(80));

    public static byte[] Mehd(ulong fragmentDuration, bool v1 = false) => v1
        ? Box("mehd", FullHeader(1, 0), U64(fragmentDuration))
        : Box("mehd", FullHeader(0, 0), U32((uint)fragmentDuration));

    public static byte[] Trex(uint trackId, uint defaultDuration) =>
        Box("trex", FullHeader(0, 0), U32(trackId), U32(1), U32(defaultDuration), U32(0), U32(0));

    public static byte[] Mvex(params byte[][] children) => Box("mvex", children);

    /// <summary>A trak with tkhd, mdhd, hdlr and (optionally) stts/stsz in minf/stbl.</summary>
    public static byte[] Trak(
        uint trackId,
        uint timescale,
        string handler = "vide",
        bool v1 = false,
        (uint Count, uint Delta)[]? stts = null,
        uint? stszSamples = null)
    {
        var tkhd = v1
            ? Box("tkhd", FullHeader(1, 3), Zeros(16), U32(trackId), Zeros(4), U64(0), Zeros(60))
            : Box("tkhd", FullHeader(0, 3), Zeros(8), U32(trackId), Zeros(4), U32(0), Zeros(60));
        var mdhd = v1
            ? Box("mdhd", FullHeader(1, 0), Zeros(16), U32(timescale), U64(0), Zeros(4))
            : Box("mdhd", FullHeader(0, 0), Zeros(8), U32(timescale), U32(0), Zeros(4));
        var hdlr = Box("hdlr", FullHeader(0, 0), U32(0), Encoding.ASCII.GetBytes(handler), Zeros(12), [0]);

        var stbl = new List<byte[]>();
        if (stts is not null)
        {
            stbl.Add(Box("stts", FullHeader(0, 0), U32((uint)stts.Length), Bytes([.. stts.Select(e => Bytes(U32(e.Count), U32(e.Delta)))])));
        }

        if (stszSamples is { } n)
        {
            stbl.Add(Box("stsz", FullHeader(0, 0), U32(0), U32(n)));
        }

        var minf = Box("minf", Box("stbl", [.. stbl]));
        return Box("trak", tkhd, Box("mdia", mdhd, hdlr, minf));
    }

    public static byte[] Moov(params byte[][] children) => Box("moov", children);

    /// <summary>One traf: tfhd (optionally with a default duration), optional tfdt, one trun.</summary>
    public static byte[] Traf(uint trackId, uint[] sampleDurations, uint? tfhdDefaultDuration = null, bool durationsInTrun = true, bool v1 = false)
    {
        // base-data-offset-absent (0x20000) like the Media Foundation sink; 0x8 = default sample duration present.
        var tfhdFlags = 0x20000u | (tfhdDefaultDuration is null ? 0u : 0x8u);
        var tfhd = Box("tfhd", FullHeader(0, tfhdFlags), U32(trackId), tfhdDefaultDuration is { } d ? U32(d) : []);

        // data_offset (0x1) + duration (0x100) + size (0x200) + sample flags (0x400) + composition offset (0x800).
        var trunFlags = 0x1u | 0x200u | 0x400u | 0x800u | (durationsInTrun ? 0x100u : 0u);
        var samples = sampleDurations.Select(dur => durationsInTrun
            ? Bytes(U32(dur), U32(1000), U32(0x10000), U32(0))
            : Bytes(U32(1000), U32(0x10000), U32(0)));
        var trun = Box("trun", FullHeader(v1 ? (byte)1 : (byte)0, trunFlags), U32((uint)sampleDurations.Length), U32(0), Bytes([.. samples]));
        return Box("traf", tfhd, Box("tfdt", FullHeader(1, 0), U64(0)), trun);
    }

    public static byte[] Moof(uint sequence, params byte[][] trafs) => Box("moof", Box("mfhd", FullHeader(0, 0), U32(sequence)), Bytes(trafs));

    public static byte[] Mdat(int payloadBytes = 64) => Box("mdat", Zeros(payloadBytes));

    /// <summary>The shape the recorder writes: ftyp, moov(mvhd, trak, mvex), then moof+mdat pairs and an mfra.</summary>
    public static byte[] Fragmented(uint timescale, params uint[][] fragments)
    {
        var parts = new List<byte[]>
        {
            Ftyp(),
            Moov(Mvhd(timescale, 0), Trak(1, timescale), Mvex(Trex(1, 0))),
        };
        for (var i = 0; i < fragments.Length; i++)
        {
            parts.Add(Moof((uint)(i + 1), Traf(1, fragments[i])));
            parts.Add(Mdat());
        }

        parts.Add(Box("mfra", Box("mfro", FullHeader(0, 0), U32(16))));
        return Bytes([.. parts]);
    }
}
