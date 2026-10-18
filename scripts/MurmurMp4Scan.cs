// Compiled at run time by scripts/check-recording.ps1 (Add-Type -Path) and linked into tests/Murmur.Scripts.Tests.
// Tooling code, not part of the shipped app: plain C#, no nullable annotations.
#nullable disable
using System;
using System.Collections.Generic;
using System.IO;

public sealed class MurmurMp4ScanResult
{
    public string Error;
    public bool Truncated;
    public bool Fragmented;
    public int Fragments;
    // False when only the container header (mehd/mvhd) could be used: the frame count is then unknown.
    public bool FramesKnown;
    public long Frames;
    public double DurationS;
    // trun (sum of fragment sample durations), stts (sample table), mehd or mvhd (header fallbacks).
    public string DurationSource;
    public double MvhdDurationS;
    public uint Timescale;
    public long FileBytes;
}

// Reads the duration and sample count of an MP4 without decoding it. Fragmented MP4 (what the recorder writes: moov with
// mvex, then moof+mdat pairs): the sum of the trun sample durations of the video track over the mdhd timescale.
// Classic MP4: the stts table. Only moov and moof boxes are read into memory, mdat payloads are skipped by seeking.
public static class MurmurMp4Scan
{
    private const long MaxBoxBytes = 256L * 1024 * 1024;

    private sealed class Track
    {
        public uint Id;
        public uint Timescale;
        public string Handler = "";
        public uint TrexDuration;
        public long SttsSamples;
        public long SttsTicks;
        public long StszSamples;
        public long FragSamples;
        public long FragTicks;
    }

    private sealed class Parser
    {
        public readonly List<Track> Tracks = new List<Track>();
        public uint MovieTimescale;
        public ulong MovieDuration;
        public ulong FragmentDuration;
        public bool HaveMoov;
        public int Fragments;

        private Track _current;          // trak being parsed
        private Track _traf;             // track of the traf being parsed
        private bool _tfhdHasDuration;
        private uint _tfhdDuration;

        public void ParseMoov(byte[] b, int start, int end) { Walk(b, start, end); HaveMoov = true; }

        public void ParseMoof(byte[] b, int start, int end) { Fragments++; Walk(b, start, end); }

        private void Walk(byte[] b, int start, int end)
        {
            int p = start;
            while (p + 8 <= end)
            {
                long size = U32(b, p);
                int header = 8;
                if (size == 1)
                {
                    if (p + 16 > end) return;
                    size = (long)U64(b, p + 8);
                    header = 16;
                }
                else if (size == 0) size = end - p;
                if (size < header || p + size > end) return;
                string type = Type(b, p + 4);
                int body = p + header;
                int bodyEnd = (int)(p + size);

                switch (type)
                {
                    case "trak":
                        _current = new Track();
                        Walk(b, body, bodyEnd);
                        // mvex before trak: trex already created a placeholder for this track id.
                        for (int i = 0; i < Tracks.Count; i++)
                        {
                            if (Tracks[i].Id == _current.Id && Tracks[i].Timescale == 0)
                            {
                                _current.TrexDuration = Tracks[i].TrexDuration;
                                Tracks.RemoveAt(i);
                                break;
                            }
                        }
                        Tracks.Add(_current);
                        _current = null;
                        break;
                    case "mdia":
                    case "minf":
                    case "stbl":
                    case "mvex":
                        Walk(b, body, bodyEnd);
                        break;
                    case "traf":
                        _traf = null;
                        _tfhdHasDuration = false;
                        _tfhdDuration = 0;
                        Walk(b, body, bodyEnd);
                        _traf = null;
                        break;
                    case "mvhd":
                        if (body + 4 <= bodyEnd)
                        {
                            bool v1 = b[body] == 1;
                            int o = body + 4 + (v1 ? 16 : 8);
                            if (o + (v1 ? 12 : 8) <= bodyEnd)
                            {
                                MovieTimescale = U32(b, o);
                                MovieDuration = v1 ? U64(b, o + 4) : U32(b, o + 4);
                            }
                        }
                        break;
                    case "mehd":
                        if (body + 4 <= bodyEnd)
                        {
                            bool v1 = b[body] == 1;
                            if (body + 4 + (v1 ? 8 : 4) <= bodyEnd)
                                FragmentDuration = v1 ? U64(b, body + 4) : U32(b, body + 4);
                        }
                        break;
                    case "tkhd":
                        if (_current != null && body + 4 <= bodyEnd)
                        {
                            bool v1 = b[body] == 1;
                            int o = body + 4 + (v1 ? 16 : 8);
                            if (o + 4 <= bodyEnd) _current.Id = U32(b, o);
                        }
                        break;
                    case "mdhd":
                        if (_current != null && body + 4 <= bodyEnd)
                        {
                            bool v1 = b[body] == 1;
                            int o = body + 4 + (v1 ? 16 : 8);
                            if (o + 4 <= bodyEnd) _current.Timescale = U32(b, o);
                        }
                        break;
                    case "hdlr":
                        if (_current != null && body + 12 <= bodyEnd) _current.Handler = Type(b, body + 8);
                        break;
                    case "stts":
                        if (_current != null && body + 8 <= bodyEnd)
                        {
                            long n = U32(b, body + 4);
                            int o = body + 8;
                            for (long i = 0; i < n && o + 8 <= bodyEnd; i++, o += 8)
                            {
                                long count = U32(b, o);
                                long delta = U32(b, o + 4);
                                _current.SttsSamples += count;
                                _current.SttsTicks += count * delta;
                            }
                        }
                        break;
                    case "stsz":
                        if (_current != null && body + 12 <= bodyEnd) _current.StszSamples = U32(b, body + 8);
                        break;
                    case "trex":
                        if (body + 20 <= bodyEnd)
                        {
                            Track t = FindOrAdd(U32(b, body + 4));
                            t.TrexDuration = U32(b, body + 12);
                        }
                        break;
                    case "tfhd":
                        ParseTfhd(b, body, bodyEnd);
                        break;
                    case "trun":
                        ParseTrun(b, body, bodyEnd);
                        break;
                }
                p = bodyEnd;
            }
        }

        // Finds the track with this id (trex and tfhd refer to tracks by id), adding a placeholder when it is not known yet.
        private Track FindOrAdd(uint id)
        {
            foreach (var t in Tracks) if (t.Id == id) return t;
            var n = new Track { Id = id };
            Tracks.Add(n);
            return n;
        }

        private void ParseTfhd(byte[] b, int body, int end)
        {
            if (body + 8 > end) return;
            uint flags = ((uint)b[body + 1] << 16) | ((uint)b[body + 2] << 8) | b[body + 3];
            _traf = FindOrAdd(U32(b, body + 4));
            int o = body + 8;
            if ((flags & 0x1) != 0) o += 8;
            if ((flags & 0x2) != 0) o += 4;
            if ((flags & 0x8) != 0 && o + 4 <= end)
            {
                _tfhdHasDuration = true;
                _tfhdDuration = U32(b, o);
            }
        }

        private void ParseTrun(byte[] b, int body, int end)
        {
            if (_traf == null || body + 8 > end) return;
            uint flags = ((uint)b[body + 1] << 16) | ((uint)b[body + 2] << 8) | b[body + 3];
            long count = U32(b, body + 4);
            int o = body + 8;
            if ((flags & 0x1) != 0) o += 4;
            if ((flags & 0x4) != 0) o += 4;
            int perSample = ((flags & 0x100) != 0 ? 4 : 0) + ((flags & 0x200) != 0 ? 4 : 0)
                + ((flags & 0x400) != 0 ? 4 : 0) + ((flags & 0x800) != 0 ? 4 : 0);
            uint fallback = _tfhdHasDuration ? _tfhdDuration : _traf.TrexDuration;
            for (long i = 0; i < count; i++)
            {
                if (o + perSample > end) return;
                long dur = (flags & 0x100) != 0 ? U32(b, o) : fallback;
                _traf.FragSamples++;
                _traf.FragTicks += dur;
                o += perSample;
            }
        }
    }

    public static MurmurMp4ScanResult Scan(string path)
    {
        var r = new MurmurMp4ScanResult();
        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
            {
                long len = fs.Length;
                r.FileBytes = len;
                if (len < 8) { r.Error = "file shorter than a box header (" + len + " bytes)"; return r; }

                var parser = new Parser();
                var head = new byte[16];
                long pos = 0;
                bool first = true;
                while (pos + 8 <= len)
                {
                    fs.Position = pos;
                    if (!ReadFully(fs, head, 0, 8)) break;
                    long size = U32(head, 0);
                    string type = Type(head, 4);
                    int header = 8;
                    if (first && type != "ftyp" && type != "moov" && type != "styp") { r.Error = "not an MP4 (first box is '" + type + "')"; return r; }
                    first = false;
                    if (size == 1)
                    {
                        if (!ReadFully(fs, head, 8, 8)) break;
                        size = (long)U64(head, 8);
                        header = 16;
                    }
                    else if (size == 0) size = len - pos;
                    if (size < header) { r.Error = "invalid box size at offset " + pos; return r; }

                    // A box that runs past the end of the file (crash mid-write): keep what was read before it.
                    if (pos + size > len) { r.Truncated = true; break; }
                    if (type == "moov" || type == "moof")
                    {
                        if (size > MaxBoxBytes) { r.Error = "'" + type + "' box too large (" + size + " bytes)"; return r; }
                        var body = new byte[size - header];
                        fs.Position = pos + header;
                        if (!ReadFully(fs, body, 0, body.Length)) { r.Truncated = true; break; }
                        if (type == "moov") parser.ParseMoov(body, 0, body.Length);
                        else parser.ParseMoof(body, 0, body.Length);
                    }
                    pos += size;
                }

                if (!parser.HaveMoov) { if (r.Error == null) r.Error = "no moov box"; return r; }
                if (parser.Tracks.Count == 0) { r.Error = "no tracks in moov"; return r; }

                Track video = null;
                foreach (var t in parser.Tracks) if (t.Handler == "vide") { video = t; break; }
                if (video == null)
                {
                    foreach (var t in parser.Tracks) if (t.Timescale != 0) { video = t; break; }
                }
                if (video == null || video.Timescale == 0) { r.Error = "no media timescale (mdhd) for the video track"; return r; }

                r.Fragments = parser.Fragments;
                r.Fragmented = parser.Fragments > 0;
                r.Timescale = video.Timescale;
                if (parser.MovieTimescale != 0) r.MvhdDurationS = (double)parser.MovieDuration / parser.MovieTimescale;

                long samples = video.FragSamples + (video.SttsSamples > 0 ? video.SttsSamples : video.StszSamples);
                if (video.FragSamples > 0 || video.SttsSamples > 0)
                {
                    r.FramesKnown = true;
                    r.Frames = samples;
                    r.DurationS = (double)(video.FragTicks + video.SttsTicks) / video.Timescale;
                    r.DurationSource = video.FragSamples > 0 ? "trun" : "stts";
                }
                else if (parser.FragmentDuration > 0 && parser.MovieTimescale != 0)
                {
                    r.DurationS = (double)parser.FragmentDuration / parser.MovieTimescale;
                    r.DurationSource = "mehd";
                }
                else
                {
                    r.DurationS = r.MvhdDurationS;
                    r.DurationSource = "mvhd";
                }
            }
        }
        catch (Exception ex)
        {
            r.Error = ex.GetType().Name + ": " + ex.Message;
        }
        return r;
    }

    private static bool ReadFully(Stream s, byte[] buf, int offset, int count)
    {
        int got = 0;
        while (got < count)
        {
            int n = s.Read(buf, offset + got, count - got);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    private static uint U32(byte[] b, int o) { return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3]; }

    private static ulong U64(byte[] b, int o) { return ((ulong)U32(b, o) << 32) | U32(b, o + 4); }

    private static string Type(byte[] b, int o) { return new string(new[] { (char)b[o], (char)b[o + 1], (char)b[o + 2], (char)b[o + 3] }); }
}
