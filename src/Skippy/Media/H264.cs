namespace Skippy.Media;

/// <summary>Annex-B (start codes) to MP4/AVCC (4-byte length prefixes), collecting SPS/PPS on the way.</summary>
public sealed class AnnexBConverter
{
    byte[] _out = new byte[1 << 20];

    public byte[]? Sps { get; private set; }
    public byte[]? Pps { get; private set; }

    /// <summary>Converts one access unit. Returns the AVCC bytes (valid until the next call) and whether it contains an IDR slice.</summary>
    public ReadOnlySpan<byte> Convert(ReadOnlySpan<byte> annexB, out bool isIdr)
    {
        isIdr = false;
        int outLen = 0;
        foreach (var (start, len) in Nals(annexB))
        {
            var nal = annexB.Slice(start, len);
            int type = nal[0] & 0x1F;
            switch (type)
            {
                case 7: Sps = nal.ToArray(); continue;   // parameter sets go into avcC, not the samples
                case 8: Pps = nal.ToArray(); continue;
                case 9: continue;                         // access unit delimiter
                case 5: isIdr = true; break;
            }
            Ensure(outLen + 4 + len);
            _out[outLen] = (byte)(len >> 24); _out[outLen + 1] = (byte)(len >> 16);
            _out[outLen + 2] = (byte)(len >> 8); _out[outLen + 3] = (byte)len;
            nal.CopyTo(_out.AsSpan(outLen + 4));
            outLen += 4 + len;
        }
        return _out.AsSpan(0, outLen);
    }

    /// <summary>Picks SPS/PPS out of an Annex-B sequence header blob (MF_MT_MPEG_SEQUENCE_HEADER).</summary>
    public void ReadSequenceHeader(ReadOnlySpan<byte> annexB)
    {
        foreach (var (start, len) in Nals(annexB))
        {
            int type = annexB[start] & 0x1F;
            if (type == 7) Sps = annexB.Slice(start, len).ToArray();
            else if (type == 8) Pps = annexB.Slice(start, len).ToArray();
        }
    }

    void Ensure(int size)
    {
        if (size > _out.Length) Array.Resize(ref _out, Math.Max(size, _out.Length * 2));
    }

    static List<(int start, int len)> Nals(ReadOnlySpan<byte> b)
    {
        var res = new List<(int, int)>(4);
        int i = FindStart(b, 0, out int sc);
        if (i < 0)
        {
            if (b.Length > 0) res.Add((0, b.Length)); // no start codes at all: treat as one NAL
            return res;
        }
        int nalStart = i + sc;
        while (true)
        {
            int next = FindStart(b, nalStart, out int sc2);
            int end = next < 0 ? b.Length : next;
            int e = end;
            while (e > nalStart && b[e - 1] == 0) e--;   // trailing_zero_8bits
            if (e > nalStart) res.Add((nalStart, e - nalStart));
            if (next < 0) break;
            nalStart = next + sc2;
        }
        return res;
    }

    static int FindStart(ReadOnlySpan<byte> b, int from, out int scLen)
    {
        for (int i = from; i + 2 < b.Length; i++)
        {
            if (b[i] == 0 && b[i + 1] == 0)
            {
                if (b[i + 2] == 1) { scLen = 3; return i; }
                if (i + 3 < b.Length && b[i + 2] == 0 && b[i + 3] == 1) { scLen = 4; return i; }
            }
        }
        scLen = 0;
        return -1;
    }
}
