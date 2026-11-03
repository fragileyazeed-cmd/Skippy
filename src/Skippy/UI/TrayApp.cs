using System.Windows.Threading;
using Skippy.Core;

namespace Skippy.UI;

/// <summary>Tray icon + menu (Settings / Pause / Resume / Open folder / Exit), hotkey, toasts and the settings window.</summary>
public sealed class TrayApp : IDisposable
{
    public const string ShowSettingsEventName = @"Local\Skippy.ShowSettings";

    readonly Settings _settings;
    readonly ReplayEngine _engine;
    readonly NotifyIcon _tray;
    readonly Hotkey _hotkey;
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly ToolStripMenuItem _pauseItem, _resumeItem;
    readonly Icon _iconOn, _iconPaused;
    readonly EventWaitHandle _showSettings;
    readonly DispatcherTimer _displayDebounce = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    SettingsWindow? _window;
    volatile bool _exiting;

    public event Action? ExitRequested;

    public TrayApp(Settings settings, bool showSettingsNow)
    {
        _settings = settings;

        int iconSize = SystemInformation.SmallIconSize.Width;
        _iconOn = AppIcon.Create(iconSize);
        _iconPaused = AppIcon.Create(iconSize, paused: true);

        var menu = TrayMenu.Create();
        menu.Items.Add(TrayMenu.Item("Settings", (_, _) => ShowSettings(), bold: true));
        _pauseItem = TrayMenu.Item("Pause", (_, _) => SetPaused(true));
        _resumeItem = TrayMenu.Item("Resume", (_, _) => SetPaused(false));
        _resumeItem.Enabled = false;
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_resumeItem);
        menu.Items.Add(TrayMenu.Item("Open folder", (_, _) => Folders.Open(_settings.SaveFolder)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(TrayMenu.Item("Exit", (_, _) => ExitRequested?.Invoke()));

        _tray = new NotifyIcon { Icon = _iconOn, Text = "Skippy", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };
        Toast.Init(_tray);
        Autostart.Ensure();

        _engine = new ReplayEngine(settings);
        _engine.Notify += (msg, isError) => Toast.Show(msg, isError);
        _tray.Text = TrayText();

        _hotkey = new Hotkey();
        _hotkey.Pressed += SaveClip;
        if (!_hotkey.Register(_settings.Hotkey))
            Toast.Show($"The shortcut {_settings.Hotkey} is taken by another app. Pick another one in Skippy's settings.", true);

        // monitors plugged/unplugged: let things settle, then make sure the right screen is captured
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _displayDebounce.Tick += (_, _) => { _displayDebounce.Stop(); Task.Run(_engine.OnDisplaysChanged); };

        // a second launch of Skippy.exe asks this instance to show its window
        _showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
        new Thread(() =>
        {
            while (!_exiting && _showSettings.WaitOne())
                if (!_exiting) _ui.BeginInvoke(ShowSettings);
        }) { IsBackground = true, Name = "Skippy show-settings listener" }.Start();

        Task.Run(_engine.Start);   // a second or two (GPU, encoder, audio devices): keep the UI responsive
        if (showSettingsNow) _ui.BeginInvoke(ShowSettings);
    }

    void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _ui.BeginInvoke(() => { _displayDebounce.Stop(); _displayDebounce.Start(); });
    }

    void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            Task.Delay(3000).ContinueWith(_ => _engine.OnPowerResume());
    }

    string TrayText()
    {
        if (_engine.IsPaused) return "Skippy (paused)";
        string len = _settings.ClipSeconds < 60 ? $"{_settings.ClipSeconds} s" : _settings.ClipSeconds == 60 ? "minute" : $"{_settings.ClipSeconds / 60} min";
        string t = $"Skippy · {_settings.Hotkey} saves the last {len}";
        return t.Length > 63 ? t[..63] : t;
    }

    void ShowSettings()
    {
        if (_window == null)
        {
            _window = new SettingsWindow(_settings, _engine, OnSettingsChanged, text =>
            {
                if (text == null) { _hotkey.Unregister(); return true; }
                return _hotkey.Register(text);
            }, SetPaused, () => ExitRequested?.Invoke());
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }
        if (_window.WindowState == System.Windows.WindowState.Minimized) _window.WindowState = System.Windows.WindowState.Normal;
        _window.Activate();
    }

    void OnSettingsChanged()
    {
        _settings.Save();
        var copy = _settings.Clone();
        Task.Run(() => _engine.Apply(copy));   // FPS / monitor changes restart capture: don't block the window
        _tray.Text = TrayText();
    }

    void SetPaused(bool paused)
    {
        _pauseItem.Enabled = !paused;
        _resumeItem.Enabled = paused;
        _tray.Icon = paused ? _iconPaused : _iconOn;
        if (paused) _engine.Pause();
        else Task.Run(_engine.Resume);
        _tray.Text = paused ? "Skippy (paused)" : TrayText();
    }

    void SaveClip()
    {
        _engine.SaveClipAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                var msg = t.Exception!.GetBaseException().Message;
                Log.Error("Save failed", t.Exception);
                Toast.Show($"Could not save the clip: {msg}", true);
                return;
            }
            if (_settings.PlaySound) Chime.Play();
            Toast.Show($"Clip saved: {Path.GetFileName(t.Result)}", false);
        });
    }

    public void Dispose()
    {
        _exiting = true;
        _showSettings.Set();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _window?.Close();
        _hotkey.Dispose();
        _tray.Visible = false;
        _engine.Dispose();
        _tray.Dispose();
    }
}

public static class Folders
{
    public static void Open(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("Open folder failed", ex); }
    }
}
