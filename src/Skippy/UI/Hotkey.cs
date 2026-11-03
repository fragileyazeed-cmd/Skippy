using Skippy.Core;

namespace Skippy.UI;

/// <summary>Global hotkey via RegisterHotKey (a plain Win32 API: no keyboard hooks, nothing anti-cheat cares about).</summary>
public sealed class Hotkey : NativeWindow, IDisposable
{
    const int Id = 0x5C1F;
    bool _registered;

    public event Action? Pressed;

    public Hotkey() => CreateHandle(new CreateParams { Caption = "Skippy hotkey" });

    /// <summary>Registers e.g. "Alt+F10". Returns false if the text is invalid or another app owns the combination.</summary>
    public bool Register(string text)
    {
        Unregister();
        if (!TryParse(text, out uint mods, out Keys key)) { Log.Warn($"Invalid hotkey '{text}'"); return false; }
        _registered = Native.RegisterHotKey(Handle, Id, mods | Native.MOD_NOREPEAT, (uint)key);
        if (!_registered) Log.Warn($"RegisterHotKey({text}) failed, error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        else Log.Info($"Hotkey {text} registered");
        return _registered;
    }

    public void Unregister()
    {
        if (_registered) Native.UnregisterHotKey(Handle, Id);
        _registered = false;
    }

    public static bool TryParse(string text, out uint mods, out Keys key)
    {
        mods = 0; key = Keys.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "alt": mods |= Native.MOD_ALT; break;
                case "ctrl": case "control": mods |= Native.MOD_CONTROL; break;
                case "shift": mods |= Native.MOD_SHIFT; break;
                case "win": mods |= Native.MOD_WIN; break;
                default:
                    if (key != Keys.None || !Enum.TryParse(raw, true, out key)) return false;
                    break;
            }
        }
        return key != Keys.None;
    }

    public static string Format(Keys keyData)
    {
        var parts = new List<string>();
        if (keyData.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (keyData.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (keyData.HasFlag(Keys.Shift)) parts.Add("Shift");
        parts.Add((keyData & Keys.KeyCode).ToString());
        return string.Join("+", parts);
    }

    /// <summary>"Alt+F10" stays as is; kept for the tray tooltip (max 63 chars).</summary>
    public static string FormatShort(string text) => text.Length > 16 ? text[..16] : text;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && (int)m.WParam == Id) Pressed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }
}
