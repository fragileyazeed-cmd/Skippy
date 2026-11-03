using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Skippy.UI;

/// <summary>The Skippy icon, drawn in code: a "replay" ring with a record dot on a violet tile. Grey when paused.</summary>
public static class AppIcon
{
    public static Bitmap Draw(int size, bool paused = false)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float s = size;
        float r = s * 0.22f;
        using (var tile = RoundedRect(new RectangleF(s * 0.02f, s * 0.02f, s * 0.96f, s * 0.96f), r))
        using (var fill = new LinearGradientBrush(new PointF(0, 0), new PointF(s, s),
                   paused ? Color.FromArgb(0x8A, 0x8A, 0x96) : Color.FromArgb(0x6D, 0x4A, 0xFF),
                   paused ? Color.FromArgb(0x5A, 0x5A, 0x66) : Color.FromArgb(0xE0, 0x3E, 0x8C)))
            g.FillPath(fill, tile);

        // replay ring: ~300° arc with an arrow head at its start
        float stroke = Math.Max(1.5f, s * 0.11f);
        var ring = new RectangleF(s * 0.24f, s * 0.24f, s * 0.52f, s * 0.52f);
        using (var pen = new Pen(Color.White, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(pen, ring, -60, 290);
        double a = -60 * Math.PI / 180;
        var tip = new PointF(ring.X + ring.Width / 2 + (float)Math.Cos(a) * ring.Width / 2, ring.Y + ring.Height / 2 + (float)Math.Sin(a) * ring.Height / 2);
        float h = s * 0.17f;
        g.FillPolygon(Brushes.White, new[]
        {
            new PointF(tip.X - h * 0.15f, tip.Y - h * 0.9f),
            new PointF(tip.X + h * 0.85f, tip.Y + h * 0.15f),
            new PointF(tip.X - h * 0.55f, tip.Y + h * 0.45f),
        });

        // record dot
        float d = s * 0.2f;
        using (var dot = new SolidBrush(paused ? Color.FromArgb(0xDD, 0xDD, 0xDD) : Color.White))
            g.FillEllipse(dot, s / 2 - d / 2, s / 2 - d / 2, d, d);
        return bmp;
    }

    public static Icon Create(int size, bool paused = false)
    {
        using var bmp = Draw(size, paused);
        var h = bmp.GetHicon();
        var tmp = Icon.FromHandle(h);
        var icon = (Icon)tmp.Clone();   // own the handle's pixels, then free the GDI handle
        DestroyIcon(h);
        return icon;
    }

    /// <summary>Writes a multi-size .ico (PNG frames) — used once to produce the exe icon.</summary>
    public static void WriteIco(string path)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];
        var pngs = sizes.Select(sz => { using var b = Draw(sz); using var ms = new MemoryStream(); b.Save(ms, ImageFormat.Png); return ms.ToArray(); }).ToList();
        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
    }

    public static void WritePng(string path, int size)
    {
        using var b = Draw(size);
        b.Save(path, ImageFormat.Png);
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr h);
}
