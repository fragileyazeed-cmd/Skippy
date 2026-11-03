using Microsoft.Win32;

namespace Skippy.Core;

/// <summary>Per-user autostart via HKCU\...\Run (no admin, no scheduled task, no service).</summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Skippy";
    public const string Arg = "--autostart";

    /// <summary>Registers (or re-points, if the folder moved) the current exe to start at login.</summary>
    public static void Ensure()
    {
        try
        {
            string exe = Environment.ProcessPath ?? Application.ExecutablePath;
            string cmd = $"\"{exe}\" {Arg}";
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (!string.Equals(k.GetValue(Name) as string, cmd, StringComparison.OrdinalIgnoreCase))
            {
                k.SetValue(Name, cmd);
                Log.Info($"Autostart registered: {cmd}");
            }
        }
        catch (Exception ex) { Log.Error("Could not register autostart", ex); }
    }
}
