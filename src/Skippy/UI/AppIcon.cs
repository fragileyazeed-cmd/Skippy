using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Skippy.UI;

/// <summary>
/// The Skippy icon, drawn in code: a white pixel-art "S" on the violet→pink rounded tile (grey when paused).
/// The S sits on a whole-pixel grid at every size, so it stays crisp in the 16 px tray and the taskbar.
/// </summary>
public static class AppIcon
{
    // 5 x 7 pixel-font S
    static readonly string[] Glyph =
    [
        ".###.",
        "#...#",
        "#....",
        ".###.",
        "....#",
        "#...#",
        ".###.",
    ];

    public static Bitmap Draw(int size, bool paused = false)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float s = size;
        float r = Math.Max(2.5f, s * 0.2f);
        using (var tile = RoundedRect(new RectangleF(0, 0, s, s), r))
        using (var fill = new LinearGradientBrush(new PointF(0, 0), new PointF(s, s),
                   paused ? Color.FromArgb(0x8A, 0x8A, 0x96) : Color.FromArgb(0x6D, 0x4A, 0xFF),
                   paused ? Color.FromArgb(0x5A, 0x5A, 0x66) : Color.FromArgb(0xE0, 0x3E, 0x8C)))
            g.FillPath(fill, tile);

        // pixel S: whole-pixel cells, no anti-aliasing, centred
        int cell = Math.Max(1, (int)Math.Round(size * 0.72 / 7));
        int w = 5 * cell, h = 7 * cell;
        int x0 = (size - w) / 2, y0 = (size - h) / 2;
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        if (size >= 32)
        {
            // soft drop shadow one sub-cell down-right gives the pixels a bit of depth
            int off = Math.Max(1, cell / 4);
            using var shadow = new SolidBrush(Color.FromArgb(70, 30, 10, 60));
            FillGlyph(g, shadow, x0 + off, y0 + off, cell);
        }
        using (var white = new SolidBrush(paused ? Color.FromArgb(0xE6, 0xE6, 0xEA) : Color.White))
            FillGlyph(g, white, x0, y0, cell);
        return bmp;
    }

    static void FillGlyph(Graphics g, Brush b, int x0, int y0, int cell)
    {
        for (int row = 0; row < Glyph.Length; row++)
            for (int col = 0; col < Glyph[row].Length; col++)
                if (Glyph[row][col] == '#') g.FillRectangle(b, x0 + col * cell, y0 + row * cell, cell, cell);
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

    /// <summary>Dev aid: every icon size side by side on light and dark backgrounds, plus the 16 px one zoomed 6x.</summary>
    public static void WritePreview(string path)
    {
        int[] sizes = [16, 20, 24, 32, 48, 64, 128];
        int w = sizes.Sum() + 12 * sizes.Length + 16 * 6 + 24, h = 2 * 128 + 36;
        using var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.FillRectangle(Brushes.WhiteSmoke, 0, 0, w, h / 2);
        using (var dark = new SolidBrush(Color.FromArgb(0x20, 0x20, 0x20))) g.FillRectangle(dark, 0, h / 2, w, h / 2);
        for (int band = 0; band < 2; band++)
        {
            int x = 12, y0 = band * h / 2 + 12;
            foreach (var s in sizes) { using var i = Draw(s); g.DrawImageUnscaled(i, x, y0 + (128 - s)); x += s + 12; }
            using var small = Draw(16);
            g.DrawImage(small, new Rectangle(x, y0 + 32, 96, 96));
        }
        bmp.Save(path, ImageFormat.Png);
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
