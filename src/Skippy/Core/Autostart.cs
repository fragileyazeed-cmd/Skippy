using Microsoft.Win32;

namespace Skippy.Core;

/// <summary>
/// Per-user autostart via HKCU\...\Run (no admin, no scheduled task, no service), plus the matching
/// StartupApproved entry that Task Manager's "Startup apps" list and its enable/disable switch use.
/// On/off lives in StartupApproved, so Task Manager, the installer checkbox and Skippy's own toggle all agree.
/// </summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string Name = "Skippy";
    public const string Arg = "--autostart";

    static string Command => $"\"{Environment.ProcessPath ?? Application.ExecutablePath}\" {Arg}";

    /// <summary>Registers (or re-points, if the folder moved) the current exe. Never re-enables it if the user turned it off.</summary>
    public static void Ensure()
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (!string.Equals(k.GetValue(Name) as string, Command, StringComparison.OrdinalIgnoreCase))
                {
                    k.SetValue(Name, Command);
                    Log.Info($"Autostart registered: {Command}");
                }
            }
            using var a = Registry.CurrentUser.CreateSubKey(ApprovedKey);
            if (a.GetValue(Name) == null) a.SetValue(Name, Flag(enabled: true), RegistryValueKind.Binary);
        }
        catch (Exception ex) { Log.Error("Could not register autostart", ex); }
    }

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                if (k?.GetValue(Name) == null) return false;
                using var a = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                // first byte: even (02/06) = enabled, odd (03/07) = disabled by the user
                return a?.GetValue(Name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
            }
            catch { return true; }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) k.SetValue(Name, Command);
            using var a = Registry.CurrentUser.CreateSubKey(ApprovedKey);
            a.SetValue(Name, Flag(enabled), RegistryValueKind.Binary);
            Log.Info($"Start with Windows: {(enabled ? "on" : "off")}");
        }
        catch (Exception ex) { Log.Error("Could not change autostart", ex); }
    }

    /// <summary>Same layout Task Manager writes: 4-byte state + FILETIME of when it was disabled.</summary>
    static byte[] Flag(bool enabled)
    {
        var b = new byte[12];
        b[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(b, 4);
        return b;
    }
}
