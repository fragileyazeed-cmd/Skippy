using System.Runtime.InteropServices;

namespace Skippy.UI;

/// <summary>Dark, rounded tray menu matching the settings window.</summary>
public static class TrayMenu
{
    static readonly Color Bg = Color.FromArgb(0x1E, 0x1A, 0x25);
    static readonly Color Hover = Color.FromArgb(0x2E, 0x28, 0x3A);
    static readonly Color Line = Color.FromArgb(0x33, 0x2D, 0x3E);
    static readonly Color Text = Color.FromArgb(0xF4, 0xF1, 0xFA);
    static readonly Color Dim = Color.FromArgb(0x6C, 0x66, 0x80);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static ContextMenuStrip Create()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new Renderer(),
            ShowImageMargin = false,
            BackColor = Bg,
            ForeColor = Text,
            Font = new Font("Segoe UI", 10f),
            Padding = new Padding(4, 6, 4, 6),
        };
        menu.Opening += (_, _) =>
        {
            // Windows 11 rounded corners + dark frame for the popup window
            int round = 2, dark = 1;
            DwmSetWindowAttribute(menu.Handle, 33, ref round, 4);
            DwmSetWindowAttribute(menu.Handle, 20, ref dark, 4);
        };
        return menu;
    }

    public static ToolStripMenuItem Item(string text, EventHandler onClick, bool bold = false)
    {
        var i = new ToolStripMenuItem(text, null, onClick) { Padding = new Padding(10, 7, 28, 7), ForeColor = Text };
        if (bold) i.Font = new Font("Segoe UI Semibold", 10f);
        return i;
    }

    sealed class Renderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Bg);

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var p = new Pen(Line);
            e.Graphics.DrawRectangle(p, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var r = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = Rounded(r, 6);
            using var b = new SolidBrush(Hover);
            e.Graphics.FillPath(b, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Dim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var p = new Pen(Line);
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
        }

        static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle r, int rad)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
