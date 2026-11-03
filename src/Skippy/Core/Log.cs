using System.Text;

namespace Skippy.Core;

/// <summary>Tiny thread-safe file logger: %AppData%\Skippy\skippy.log (rotated at 2 MB).</summary>
public static class Log
{
    static readonly object Gate = new();
    static string? _path;

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Skippy");

    public static void Init()
    {
        Directory.CreateDirectory(Dir);
        _path = Path.Combine(Dir, "skippy.log");
        try
        {
            var fi = new FileInfo(_path);
            if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                File.Move(_path, Path.Combine(Dir, "skippy.old.log"), overwrite: true);
        }
        catch { }
        Info($"---- Skippy {typeof(Log).Assembly.GetName().Version} started, OS {Environment.OSVersion.Version} ----");
    }

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}: {ex}");

    static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [{Environment.CurrentManagedThreadId}] {msg}{Environment.NewLine}";
        System.Diagnostics.Debug.Write(line);
        if (_path == null) return;
        lock (Gate)
        {
            try { File.AppendAllText(_path, line, Encoding.UTF8); } catch { }
        }
    }
}
