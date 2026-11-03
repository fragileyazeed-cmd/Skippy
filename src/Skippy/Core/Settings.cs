using System.Text.Json;

namespace Skippy.Core;

public sealed class Settings
{
    public int ClipSeconds { get; set; } = 60;          // 30 / 60 / 120 / 180
    public int Fps { get; set; } = 60;                  // 30 / 60
    public string? MicrophoneId { get; set; }           // null = Windows default
    public string? OutputDeviceId { get; set; }         // null = Windows default
    public bool PlaySound { get; set; } = true;
    public string? MonitorDevice { get; set; }          // e.g. \\.\DISPLAY1, null = primary
    public string Hotkey { get; set; } = "Alt+F10";
    public string SaveFolder { get; set; } = DefaultSaveFolder;

    public static string DefaultSaveFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Skippy");

    static string FilePath => Path.Combine(Log.Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
                s.Sanitize();
                return s;
            }
        }
        catch (Exception ex) { Log.Error("Could not read settings, using defaults", ex); }
        var d = new Settings();
        d.Save();
        return d;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Log.Error("Could not save settings", ex); }
    }

    public Settings Clone() => (Settings)MemberwiseClone();

    void Sanitize()
    {
        if (ClipSeconds is not (30 or 60 or 120 or 180)) ClipSeconds = 60;
        if (Fps is not (30 or 60)) Fps = 60;
        if (string.IsNullOrWhiteSpace(Hotkey)) Hotkey = "Alt+F10";
        if (string.IsNullOrWhiteSpace(SaveFolder)) SaveFolder = DefaultSaveFolder;
    }
}
