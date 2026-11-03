using System.Media;

namespace Skippy.UI;

/// <summary>Short two-note "saved" chime, synthesised once and played with the plain Windows sound API.</summary>
public static class Chime
{
    static readonly Lazy<SoundPlayer> Player = new(() =>
    {
        var p = new SoundPlayer(new MemoryStream(Make()));
        p.Load();
        return p;
    });

    public static void Play()
    {
        try { Player.Value.Play(); } catch (Exception ex) { Core.Log.Warn($"Chime failed: {ex.Message}"); }
    }

    static byte[] Make()
    {
        const int rate = 48_000;
        (double freq, double start, double len)[] notes = [(880, 0.0, 0.12), (1318.5, 0.09, 0.22)];
        int n = (int)(rate * 0.33);
        var pcm = new double[n];
        foreach (var (f, st, len) in notes)
        {
            int s0 = (int)(st * rate), sl = (int)(len * rate);
            for (int i = 0; i < sl && s0 + i < n; i++)
            {
                double t = i / (double)rate;
                double env = Math.Min(1, i / (rate * 0.005)) * Math.Exp(-t * 14);   // quick attack, bell-like decay
                pcm[s0 + i] += Math.Sin(2 * Math.PI * f * t) * env * 0.35;
            }
        }
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(n * 2);
        foreach (var v in pcm) w.Write((short)(Math.Clamp(v, -1, 1) * 32767));
        return ms.ToArray();
    }
}
