using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Skippy.UI;

/// <summary>Video thumbnails from the Windows shell (same image Explorer shows), plus MP4 duration.</summary>
public static class ShellThumbnail
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        void GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr hObject);

    const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_THUMBNAILONLY = 0x8;

    /// <summary>Gets a frozen thumbnail on a background STA thread; null if Windows has none.</summary>
    public static Task<BitmapSource?> GetAsync(string path, int width)
    {
        var tcs = new TaskCompletionSource<BitmapSource?>();
        var t = new Thread(() =>
        {
            try
            {
                var iid = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
                factory.GetImage(new SIZE { cx = width, cy = width * 9 / 16 }, SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK, out var hbmp);
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    tcs.SetResult(src);
                }
                finally { DeleteObject(hbmp); }
                Marshal.ReleaseComObject(factory);
            }
            catch { tcs.SetResult(null); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    /// <summary>Reads the duration from the MP4 'mvhd' box (Skippy writes moov at the front, so this is instant).</summary>
    public static TimeSpan? Mp4Duration(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[(int)Math.Min(fs.Length, 1 << 20)];
            fs.ReadExactly(buf);
            int i = buf.AsSpan().IndexOf("mvhd"u8);
            if (i < 0) return null;
            int p = i + 4;
            byte version = buf[p];
            p += 4;
            long timescale, duration;
            if (version == 1)
            {
                p += 16;
                timescale = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p));
                duration = (long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(p + 4));
            }
            else
            {
                p += 8;
                timescale = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p));
                duration = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p + 4));
            }
            return timescale > 0 ? TimeSpan.FromSeconds(duration / (double)timescale) : null;
        }
        catch { return null; }
    }
}
