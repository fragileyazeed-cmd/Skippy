namespace Skippy.Core;

public sealed record MonitorInfo(IntPtr Handle, string DeviceName, int X, int Y, int Width, int Height, bool IsPrimary)
{
    public int Number { get; init; }
    public string DisplayName => $"Screen {Number} ({Width}×{Height}){(IsPrimary ? " – primary" : "")}";
    public override string ToString() => DisplayName;
}

public static class Monitors
{
    /// <summary>All connected monitors, primary first, then left-to-right / top-to-bottom, numbered Screen 1..N.</summary>
    public static List<MonitorInfo> GetAll()
    {
        var list = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var mi = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(h, ref mi))
            {
                var r = mi.rcMonitor;
                list.Add(new MonitorInfo(h, mi.szDevice, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                    (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }, IntPtr.Zero);

        return list
            .OrderByDescending(m => m.IsPrimary).ThenBy(m => m.X).ThenBy(m => m.Y)
            .Select((m, i) => m with { Number = i + 1 })
            .ToList();
    }

    /// <summary>Find the monitor by device name; null if it is not connected.</summary>
    public static MonitorInfo? Find(string? deviceName)
    {
        var all = GetAll();
        return deviceName == null ? null : all.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
    }

    public static MonitorInfo? Primary() => GetAll().FirstOrDefault(m => m.IsPrimary) ?? GetAll().FirstOrDefault();
}
