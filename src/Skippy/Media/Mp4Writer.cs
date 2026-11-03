using System.Buffers.Binary;
using System.Text;
using Skippy.Core;

namespace Skippy.Media;

public sealed class Mp4Track
{
    public required bool IsVideo { get; init; }
    public required int Timescale { get; init; }
    /// <summary>Samples in decode order.</summary>
    public required Packet[] Samples { get; init; }
    /// <summary>Sample start times in <see cref="Timescale"/> units, relative to the start of the movie.</summary>
    public required long[] Times { get; init; }
    /// <summary>Duration of the last sample (others are derived from time deltas).</summary>
    public required long LastDuration { get; init; }

    // video
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[]? Sps { get; init; }
    public byte[]? Pps { get; init; }

    // audio
    public int Channels { get; init; }
    public int SampleRate { get; init; }
    public byte[]? AudioSpecificConfig { get; init; }
    public int Bitrate { get; init; }

    internal long Duration => Samples.Length == 0 ? 0 : Times[^1] - Times[0] + LastDuration;
    internal long StartOffset => Samples.Length == 0 ? 0 : Times[0];
}

/// <summary>
/// Minimal progressive MP4 muxer (moov before mdat, "fast start"). Samples are written straight from
/// the ring buffer; nothing is re-encoded. Tracks are interleaved in half-second chunks.
/// </summary>
public static class Mp4Writer
{
    const int MovieTimescale = 1000;

    sealed record ChunkPlan(int Track, int First, int Count) { public long Offset; }

    public static void Write(string path, IReadOnlyList<Mp4Track> tracks)
    {
        var chunks = PlanChunks(tracks);
        long mdatPayload = 0;
        foreach (var c in chunks)
            for (int i = 0; i < c.Count; i++) mdatPayload += tracks[c.Track].Samples[c.First + i].Length;

        byte[] ftyp = BuildFtyp();
        bool use64 = mdatPayload > uint.MaxValue - 64 * 1024 * 1024;
        // moov size does not depend on offset values, so build once to measure, then again with real offsets.
        int moovSize = BuildMoov(tracks, chunks, use64).Length;
        bool bigMdat = mdatPayload + 8 > uint.MaxValue;
        long dataStart = ftyp.Length + moovSize + (bigMdat ? 16 : 8);
        long pos = dataStart;
        foreach (var c in chunks)
        {
            c.Offset = pos;
            for (int i = 0; i < c.Count; i++) pos += tracks[c.Track].Samples[c.First + i].Length;
        }
        byte[] moov = BuildMoov(tracks, chunks, use64);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
        fs.Write(ftyp);
        fs.Write(moov);
        Span<byte> hdr = stackalloc byte[16];
        if (bigMdat)
        {
            BinaryPrimitives.WriteUInt32BigEndian(hdr, 1);
            Encoding.ASCII.GetBytes("mdat", hdr[4..]);
            BinaryPrimitives.WriteUInt64BigEndian(hdr[8..], (ulong)(mdatPayload + 16));
            fs.Write(hdr);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(hdr, (uint)(mdatPayload + 8));
            Encoding.ASCII.GetBytes("mdat", hdr[4..]);
            fs.Write(hdr[..8]);
        }
        foreach (var c in chunks)
            for (int i = 0; i < c.Count; i++) fs.Write(tracks[c.Track].Samples[c.First + i].Data);
    }

    static List<ChunkPlan> PlanChunks(IReadOnlyList<Mp4Track> tracks)
    {
        var res = new List<ChunkPlan>();
        var idx = new int[tracks.Count];
        double window = 0;
        while (true)
        {
            bool any = false;
            window += 0.5;
            for (int t = 0; t < tracks.Count; t++)
            {
                var tr = tracks[t];
                int first = idx[t];
                while (idx[t] < tr.Samples.Length && tr.Times[idx[t]] / (double)tr.Timescale < window) idx[t]++;
                if (idx[t] > first) res.Add(new ChunkPlan(t, first, idx[t] - first));
                if (idx[t] < tr.Samples.Length) any = true;
            }
            if (!any) break;
        }
        return res;
    }

    static byte[] BuildFtyp()
    {
        var w = new BoxWriter();
        w.Begin("ftyp");
        w.Fourcc("isom"); w.U32(0x200);
        w.Fourcc("isom"); w.Fourcc("iso2"); w.Fourcc("avc1"); w.Fourcc("mp41");
        w.End();
        return w.ToArray();
    }

    static byte[] BuildMoov(IReadOnlyList<Mp4Track> tracks, List<ChunkPlan> chunks, bool co64)
    {
        long movieDuration = 0;
        foreach (var t in tracks)
            movieDuration = Math.Max(movieDuration, ToMovie(t.StartOffset + t.Duration, t.Timescale));

        var w = new BoxWriter();
        w.Begin("moov");

        w.Begin("mvhd"); w.U32(0);
        w.U32(0); w.U32(0); w.U32(MovieTimescale); w.U32((uint)movieDuration);
        w.U32(0x00010000); w.U16(0x0100); w.Zero(10);
        Matrix(w);
        w.Zero(24);
        w.U32((uint)tracks.Count + 1);
        w.End();

        for (int ti = 0; ti < tracks.Count; ti++)
        {
            var t = tracks[ti];
            uint trackId = (uint)ti + 1;
            long tkDuration = ToMovie(t.StartOffset + t.Duration, t.Timescale);

            w.Begin("trak");
            w.Begin("tkhd"); w.U32(0x00000003);
            w.U32(0); w.U32(0); w.U32(trackId); w.U32(0); w.U32((uint)tkDuration);
            w.Zero(8); w.U16(0); w.U16(t.IsVideo ? (ushort)0 : (ushort)1);
            w.U16(t.IsVideo ? (ushort)0 : (ushort)0x0100); w.U16(0);
            Matrix(w);
            w.U32(t.IsVideo ? (uint)t.Width << 16 : 0); w.U32(t.IsVideo ? (uint)t.Height << 16 : 0);
            w.End();

            if (t.StartOffset > 0)
            {
                // track starts later than the movie: empty edit, then the media from its first sample
                w.Begin("edts");
                w.Begin("elst"); w.U32(0); w.U32(2);
                w.U32((uint)ToMovie(t.StartOffset, t.Timescale)); w.U32(0xFFFFFFFF); w.U32(0x00010000);
                w.U32((uint)ToMovie(t.Duration, t.Timescale)); w.U32(0); w.U32(0x00010000);
                w.End();
                w.End();
            }

            w.Begin("mdia");
            w.Begin("mdhd"); w.U32(0); w.U32(0); w.U32(0);
            w.U32((uint)t.Timescale); w.U32((uint)t.Duration);
            w.U16(0x55C4); w.U16(0); // language "und"
            w.End();

            w.Begin("hdlr"); w.U32(0); w.U32(0); w.Fourcc(t.IsVideo ? "vide" : "soun"); w.Zero(12);
            w.Bytes(Encoding.ASCII.GetBytes(t.IsVideo ? "Skippy Video\0" : "Skippy Audio\0"));
            w.End();

            w.Begin("minf");
            if (t.IsVideo) { w.Begin("vmhd"); w.U32(1); w.Zero(8); w.End(); }
            else { w.Begin("smhd"); w.U32(0); w.U32(0); w.End(); }
            w.Begin("dinf"); w.Begin("dref"); w.U32(0); w.U32(1); w.Begin("url "); w.U32(1); w.End(); w.End(); w.End();

            w.Begin("stbl");
            w.Begin("stsd"); w.U32(0); w.U32(1);
            if (t.IsVideo) VideoEntry(w, t); else AudioEntry(w, t, trackId);
            w.End();

            // stts: run-length durations
            var stts = new List<(uint count, uint delta)>();
            for (int i = 0; i < t.Samples.Length; i++)
            {
                long d = i + 1 < t.Samples.Length ? t.Times[i + 1] - t.Times[i] : t.LastDuration;
                uint delta = (uint)Math.Max(1, d);
                if (stts.Count > 0 && stts[^1].delta == delta) stts[^1] = (stts[^1].count + 1, delta);
                else stts.Add((1, delta));
            }
            w.Begin("stts"); w.U32(0); w.U32((uint)stts.Count);
            foreach (var (c, d) in stts) { w.U32(c); w.U32(d); }
            w.End();

            if (t.IsVideo)
            {
                var keys = new List<uint>();
                for (int i = 0; i < t.Samples.Length; i++) if (t.Samples[i].Key) keys.Add((uint)i + 1);
                w.Begin("stss"); w.U32(0); w.U32((uint)keys.Count);
                foreach (var k in keys) w.U32(k);
                w.End();
            }

            var myChunks = chunks.Where(c => c.Track == ti).ToList();
            var stsc = new List<(uint first, uint count)>();
            for (int i = 0; i < myChunks.Count; i++)
                if (stsc.Count == 0 || stsc[^1].count != myChunks[i].Count) stsc.Add(((uint)i + 1, (uint)myChunks[i].Count));
            w.Begin("stsc"); w.U32(0); w.U32((uint)stsc.Count);
            foreach (var (f, c) in stsc) { w.U32(f); w.U32(c); w.U32(1); }
            w.End();

            w.Begin("stsz"); w.U32(0); w.U32(0); w.U32((uint)t.Samples.Length);
            foreach (var s in t.Samples) w.U32((uint)s.Length);
            w.End();

            if (co64)
            {
                w.Begin("co64"); w.U32(0); w.U32((uint)myChunks.Count);
                foreach (var c in myChunks) w.U64((ulong)c.Offset);
            }
            else
            {
                w.Begin("stco"); w.U32(0); w.U32((uint)myChunks.Count);
                foreach (var c in myChunks) w.U32((uint)c.Offset);
            }
            w.End();

            w.End(); // stbl
            w.End(); // minf
            w.End(); // mdia
            w.End(); // trak
        }

        w.End(); // moov
        return w.ToArray();
    }

    static void VideoEntry(BoxWriter w, Mp4Track t)
    {
        var sps = t.Sps ?? throw new InvalidOperationException("No SPS from the encoder");
        var pps = t.Pps ?? throw new InvalidOperationException("No PPS from the encoder");
        w.Begin("avc1");
        w.Zero(6); w.U16(1);
        w.Zero(16);
        w.U16((ushort)t.Width); w.U16((ushort)t.Height);
        w.U32(0x00480000); w.U32(0x00480000); w.U32(0); w.U16(1);
        var name = new byte[32]; var n = Encoding.ASCII.GetBytes("Skippy H.264"); name[0] = (byte)n.Length; n.CopyTo(name, 1);
        w.Bytes(name);
        w.U16(0x18); w.U16(0xFFFF);

        w.Begin("avcC");
        w.U8(1); w.U8(sps[1]); w.U8(sps[2]); w.U8(sps[3]);
        w.U8(0xFF);                     // 4-byte NAL lengths
        w.U8(0xE1); w.U16((ushort)sps.Length); w.Bytes(sps);
        w.U8(1); w.U16((ushort)pps.Length); w.Bytes(pps);
        w.End();

        w.Begin("colr"); w.Fourcc("nclx"); w.U16(1); w.U16(1); w.U16(1); w.U8(0); w.End(); // BT.709, limited range
        w.Begin("pasp"); w.U32(1); w.U32(1); w.End();
        w.End();
    }

    static void AudioEntry(BoxWriter w, Mp4Track t, uint trackId)
    {
        var asc = t.AudioSpecificConfig ?? throw new InvalidOperationException("No AAC config");
        w.Begin("mp4a");
        w.Zero(6); w.U16(1);
        w.Zero(8);
        w.U16((ushort)t.Channels); w.U16(16); w.U16(0); w.U16(0);
        w.U32((uint)t.SampleRate << 16);

        w.Begin("esds"); w.U32(0);
        int dsi = 2 + asc.Length;
        int dcd = 2 + 13 + dsi;
        int sl = 3;
        int es = 3 + dcd + sl;
        w.U8(0x03); w.U8((byte)es); w.U16((ushort)trackId); w.U8(0);
        w.U8(0x04); w.U8((byte)(13 + dsi));
        w.U8(0x40); w.U8(0x15); w.U8(0); w.U16(0x1800);
        w.U32((uint)t.Bitrate); w.U32((uint)t.Bitrate);
        w.U8(0x05); w.U8((byte)asc.Length); w.Bytes(asc);
        w.U8(0x06); w.U8(1); w.U8(0x02);
        w.End();
        w.End();
    }

    static void Matrix(BoxWriter w)
    {
        w.U32(0x00010000); w.U32(0); w.U32(0);
        w.U32(0); w.U32(0x00010000); w.U32(0);
        w.U32(0); w.U32(0); w.U32(0x40000000);
    }

    static long ToMovie(long t, int timescale) => (long)Math.Round(t * (double)MovieTimescale / timescale);

    sealed class BoxWriter
    {
        readonly MemoryStream _ms = new();
        readonly Stack<long> _starts = new();

        public void Begin(string type) { _starts.Push(_ms.Position); U32(0); Fourcc(type); }
        public void End()
        {
            long start = _starts.Pop();
            long end = _ms.Position;
            _ms.Position = start;
            U32((uint)(end - start));
            _ms.Position = end;
        }
        public void U8(byte v) => _ms.WriteByte(v);
        public void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); _ms.Write(b); }
        public void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); _ms.Write(b); }
        public void U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); _ms.Write(b); }
        public void Fourcc(string s) => _ms.Write(Encoding.ASCII.GetBytes(s));
        public void Zero(int n) { for (int i = 0; i < n; i++) _ms.WriteByte(0); }
        public void Bytes(ReadOnlySpan<byte> b) => _ms.Write(b);
        public byte[] ToArray() => _ms.ToArray();
    }
}
