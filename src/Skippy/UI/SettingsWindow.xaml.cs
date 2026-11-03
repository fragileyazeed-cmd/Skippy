using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Skippy.Audio;
using Skippy.Core;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace Skippy.UI;

public sealed class ComboItem(string text, string? value)
{
    public string Text { get; } = text;
    public string? Value { get; } = value;
}

/// <summary>
/// The settings window. Every change is saved and applied immediately (no OK button); live status,
/// buffer ring and audio meters refresh while it is open, and nothing runs once it is closed.
/// </summary>
public partial class SettingsWindow : Window
{
    readonly Settings _s;
    readonly ReplayEngine _engine;
    readonly Action _changed;
    readonly Func<string?, bool> _setHotkey;
    readonly Action<bool> _setPaused;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    bool _loading = true;
    bool _capturing;
    double _micShown, _outShown;
    int _tick;
    string _statusKey = "";
    readonly Storyboard _pulse, _breathe;

    readonly Action _exitForUpdate;
    UpdateInfo? _update;
    bool _updateBusy;

    public SettingsWindow(Settings settings, ReplayEngine engine, Action changed, Func<string?, bool> setHotkey, Action<bool> setPaused, Action exitForUpdate)
    {
        _s = settings;
        _engine = engine;
        _changed = changed;
        _setHotkey = setHotkey;
        _setPaused = setPaused;
        _exitForUpdate = exitForUpdate;
        InitializeComponent();
        VersionText.Text = $"v{Updater.Current}";
        _pulse = (Storyboard)Resources["Pulse"];
        _breathe = (Storyboard)Resources["Breathe"];

        Logo.Source = ToImageSource(AppIcon.Draw(64));
        Icon = Logo.Source;

        (_s.ClipSeconds switch { 30 => Len30, 120 => Len120, 180 => Len180, _ => Len60 }).IsChecked = true;
        (_s.Fps == 30 ? Fps30 : Fps60).IsChecked = true;
        SoundSwitch.IsChecked = _s.PlaySound;
        FillAudio();
        ShowFolder();
        ShowHotkey();
        UpdateLastClip();
        _loading = false;

        SourceInitialized += (_, _) => ApplyDwmStyle();
        Loaded += (_, _) => { Animate(); BuildMonitorMap(); };
        MapFrame.SizeChanged += (_, _) => BuildMonitorMap();
        StateChanged += (_, _) => { if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; };
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => { if (_capturing) EndCapture(restore: true); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        _engine.ClipSaved += OnClipSaved;
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (_capturing) EndCapture(restore: true);
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
            _engine.ClipSaved -= OnClipSaved;
        };
    }

    // ---------------------------------------------------------------- look & feel

    void Animate()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        RootShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void ApplyDwmStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1, round = 2, border = 0x00332A2E; // COLORREF 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, 20, ref dark, 4);    // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 33, ref round, 4);   // DWMWA_WINDOW_CORNER_PREFERENCE: round
        DwmSetWindowAttribute(hwnd, 34, ref border, 4);  // DWMWA_BORDER_COLOR
    }

    static ImageSource ToImageSource(System.Drawing.Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        bmp.Dispose();
        return img;
    }

    // ---------------------------------------------------------------- updates

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        UpdateButton.IsEnabled = false;
        try
        {
            if (_update == null)
            {
                SetUpdateUi("", "Checking…", null);
                _update = await Updater.CheckAsync();
                if (_update == null)
                {
                    SetUpdateUi("", "You're up to date", null);
                    await Task.Delay(3000);
                    SetUpdateUi("", "Check for updates", null);
                    return;
                }
                UpdateButton.Style = (Style)FindResource("AccentButton");
                SetUpdateUi("", $"Update to v{_update.Version}", string.IsNullOrWhiteSpace(_update.Notes) ? null : _update.Notes.Trim());
                return;
            }

            // second click: download, verify, hand over to the new version and exit
            var progress = new Progress<double>(p => UpdateText.Text = $"Downloading {p * 100:0}%");
            SetUpdateUi("", "Downloading…", null);
            await Updater.DownloadAndStartInstallAsync(_update, progress);
            SetUpdateUi("", "Restarting…", null);
            await Task.Delay(300);
            _exitForUpdate();
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            UpdateButton.Style = (Style)FindResource("Ghost");
            _update = null;
            SetUpdateUi("", "Couldn't update — retry", ex.Message);
        }
        finally
        {
            _updateBusy = false;
            UpdateButton.IsEnabled = true;
        }
    }

    void SetUpdateUi(string glyph, string text, string? tooltip)
    {
        UpdateIcon.Text = glyph;
        UpdateText.Text = text;
        UpdateButton.ToolTip = tooltip;
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------------------------------------------------------- live status

    void OnTick()
    {
        // audio meters every tick (25 fps), the rest a few times a second
        UpdateMeter(_engine.Audio?.TakeLevel(true) ?? -1, ref _micShown, MicMeter, MicTrack, MicState, _s.MicrophoneId == AudioDevices.None);
        UpdateMeter(_engine.Audio?.TakeLevel(false) ?? -1, ref _outShown, OutMeter, OutTrack, OutState, false);
        if (_tick++ % 6 != 0) return;
        UpdateStatus();
        UpdateRing();
        if (_tick % 150 == 1) UpdateLastClip();   // keeps "x min ago" fresh
    }

    static void UpdateMeter(float peak, ref double shown, FrameworkElement meter, FrameworkElement track, TextBlock state, bool off)
    {
        if (peak < 0)
        {
            state.Text = off ? "off" : "not available";
            state.Foreground = (WpfBrush)Application.Current.Resources[off ? "Text3" : "Warn"];
            shown = 0;
            meter.Width = 0;
            return;
        }
        state.Text = "";
        double db = 20 * Math.Log10(Math.Max(peak, 1e-5));
        double level = Math.Clamp((db + 54) / 54, 0, 1);
        shown = Math.Max(level, shown - 0.035);   // fast attack, smooth fall
        meter.Width = Math.Max(0, track.ActualWidth * shown);
    }

    void UpdateStatus()
    {
        var p = _engine.Pipeline;
        string key, text;
        WpfBrush dot;
        if (_engine.IsPaused) { key = "paused"; text = "Paused"; dot = (WpfBrush)FindResource("Text3"); }
        else if (p?.Format?.Sps.Length > 0) { key = "rec"; text = "Recording"; dot = (WpfBrush)FindResource("Good"); }
        else { key = "start"; text = "Starting…"; dot = (WpfBrush)FindResource("Warn"); }

        if (key != _statusKey)
        {
            _statusKey = key;
            StatusText.Text = text;
            StatusDot.Fill = dot;
            StatusPill.ToolTip = key == "paused" ? "Click to resume buffering" : "Click to pause (stops capturing completely)";
            if (key == "rec") _pulse.Begin(this, true); else { _pulse.Stop(this); StatusDot.Opacity = 1; }
        }

        if (_engine.IsPaused) EngineInfo.Text = "Paused — nothing is being captured";
        else if (p?.Format is { } f)
        {
            string enc = f.EncoderName.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD hardware encoder"
                : f.EncoderName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA NVENC"
                : f.EncoderName.Contains("Intel", StringComparison.OrdinalIgnoreCase) || f.EncoderName.Contains("Quick", StringComparison.OrdinalIgnoreCase) ? "Intel Quick Sync"
                : f.EncoderName.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ? "software encoder" : f.EncoderName;
            EngineInfo.Text = $"Screen {p.Monitor.Number}  ·  {f.Width}×{f.Height}  ·  {f.Fps} fps  ·  {enc}";
        }
        else EngineInfo.Text = "Starting capture…";
    }

    void UpdateRing()
    {
        double secs = _engine.IsPaused ? 0 : _engine.BufferedSeconds;
        double frac = Math.Clamp(secs / _s.ClipSeconds, 0, 1);
        int whole = (int)Math.Round(secs);
        RingText.Text = $"{whole / 60}:{whole % 60:00}";

        const double size = 104, r = (size - 7) / 2;
        var c = new WpfPoint(size / 2, size / 2);
        if (frac >= 0.999) { RingArc.Data = new EllipseGeometry(c, r, r); return; }
        if (frac <= 0.001) { RingArc.Data = null; return; }
        double a = frac * 2 * Math.PI;
        var start = new WpfPoint(c.X, c.Y - r);
        var end = new WpfPoint(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new WpfSize(r, r), 0, frac > 0.5, SweepDirection.Clockwise, true));
        RingArc.Data = new PathGeometry([fig]);
    }

    void StatusPill_Click(object sender, MouseButtonEventArgs e)
    {
        _setPaused(!_engine.IsPaused);
        _statusKey = "";
        UpdateStatus();
    }

    // ---------------------------------------------------------------- clip / fps / sound

    void Length_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _s.ClipSeconds = int.Parse((string)((RadioButton)sender).Tag);
        ShowHotkey();
        _changed();
    }

    void Fps_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _s.Fps = int.Parse((string)((RadioButton)sender).Tag);
        _changed();
    }

    void Sound_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _s.PlaySound = SoundSwitch.IsChecked == true;
        _changed();
        if (_s.PlaySound) Chime.Play();   // let people hear what they just enabled
    }

    // ---------------------------------------------------------------- audio devices

    void Audio_DropDownOpened(object? sender, EventArgs e)
    {
        bool was = _loading;
        _loading = true;
        FillAudio();
        _loading = was;
    }

    void FillAudio()
    {
        Fill(MicBox, DataFlow.Capture, _s.MicrophoneId, allowNone: true);
        Fill(OutBox, DataFlow.Render, _s.OutputDeviceId, allowNone: false);
    }

    static void Fill(ComboBox box, DataFlow flow, string? current, bool allowNone)
    {
        var devices = AudioDevices.List(flow);
        string? defName = null;
        try
        {
            using var e = new MMDeviceEnumerator();
            if (e.TryGetDefaultAudioEndpoint(flow, Role.Console, out var d) && d != null) using (d) defName = d.FriendlyName;
        }
        catch { }
        var items = new List<ComboItem> { new(defName != null ? $"Windows default  ·  {defName}" : "Windows default", null) };
        if (allowNone) items.Add(new ComboItem("None — don't record a microphone", AudioDevices.None));
        items.AddRange(devices.Select(d => new ComboItem(d.Name, d.Id)));
        if (!string.IsNullOrEmpty(current) && current != AudioDevices.None && devices.All(d => d.Id != current))
            items.Add(new ComboItem("Selected device (not connected right now)", current));
        box.ItemsSource = items;
        box.SelectedItem = items.FirstOrDefault(i => i.Value == current) ?? items[0];
    }

    void Mic_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MicBox.SelectedItem is not ComboItem i) return;
        _s.MicrophoneId = i.Value;
        _changed();
    }

    void Out_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || OutBox.SelectedItem is not ComboItem i) return;
        _s.OutputDeviceId = i.Value;
        _changed();
    }

    // ---------------------------------------------------------------- monitors

    void OnDisplaysChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(BuildMonitorMap);

    void BuildMonitorMap()
    {
        MonitorMap.Children.Clear();
        var all = Monitors.GetAll();
        if (all.Count == 0) return;
        var selected = Monitors.Find(_s.MonitorDevice) ?? all.First(m => m.IsPrimary);

        double W = MapFrame.ActualWidth - 2, H = MapFrame.ActualHeight - 2;
        if (W <= 0 || H <= 0) return;
        int minX = all.Min(m => m.X), minY = all.Min(m => m.Y);
        int maxX = all.Max(m => m.X + m.Width), maxY = all.Max(m => m.Y + m.Height);
        double scale = Math.Min((W - 32) / (maxX - minX), (H - 28) / (maxY - minY));
        double offX = (W - (maxX - minX) * scale) / 2, offY = (H - (maxY - minY) * scale) / 2;

        foreach (var m in all)
        {
            bool sel = m.DeviceName == selected.DeviceName;
            var tile = new Border
            {
                Width = Math.Max(20, m.Width * scale - 6),
                Height = Math.Max(14, m.Height * scale - 6),
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(sel ? 2 : 1),
                BorderBrush = sel ? (WpfBrush)FindResource("Accent") : (WpfBrush)FindResource("StrokeHi"),
                Background = sel
                    ? new LinearGradientBrush(WpfColor.FromRgb(0x2E, 0x22, 0x5A), WpfColor.FromRgb(0x3A, 0x1C, 0x40), 45)
                    : new SolidColorBrush(WpfColor.FromRgb(0x1D, 0x19, 0x24)),
                Cursor = Cursors.Hand,
                ToolTip = $"{m.DisplayName}\n{m.DeviceName}",
                Child = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = m.Number.ToString(), FontSize = 20, FontWeight = FontWeights.SemiBold, FontFamily = (System.Windows.Media.FontFamily)FindResource("DisplayFont"),
                                        Foreground = sel ? System.Windows.Media.Brushes.White : (WpfBrush)FindResource("Text2"), HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = $"{m.Width}×{m.Height}", FontSize = 10.5, Foreground = (WpfBrush)FindResource(sel ? "Text2" : "Text3"), HorizontalAlignment = HorizontalAlignment.Center },
                    },
                },
            };
            if (sel) tile.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = WpfColor.FromRgb(0x8A, 0x5C, 0xFF), BlurRadius = 16, ShadowDepth = 0, Opacity = 0.55 };
            else
            {
                tile.MouseEnter += (_, _) => tile.BorderBrush = (WpfBrush)FindResource("Text3");
                tile.MouseLeave += (_, _) => tile.BorderBrush = (WpfBrush)FindResource("StrokeHi");
            }
            var mon = m;
            tile.MouseLeftButtonUp += (_, _) => SelectMonitor(mon);
            Canvas.SetLeft(tile, offX + (m.X - minX) * scale + 3);
            Canvas.SetTop(tile, offY + (m.Y - minY) * scale + 3);
            MonitorMap.Children.Add(tile);
        }

        bool missing = _s.MonitorDevice != null && Monitors.Find(_s.MonitorDevice) == null;
        MonitorInfo.Text = missing
            ? $"Your chosen screen isn't connected — recording Screen {selected.Number} until it's back"
            : $"Recording Screen {selected.Number}  ·  {selected.Width}×{selected.Height}{(selected.IsPrimary ? "  ·  primary" : "")}";
    }

    void SelectMonitor(MonitorInfo m)
    {
        var cur = Monitors.Find(_s.MonitorDevice) ?? Monitors.Primary();
        if (cur?.DeviceName == m.DeviceName && _s.MonitorDevice != null) return;
        _s.MonitorDevice = m.DeviceName;
        _changed();
        BuildMonitorMap();
    }

    // ---------------------------------------------------------------- hotkey

    void ShowHotkey()
    {
        Keycaps.Children.Clear();
        var parts = _s.Hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) Keycaps.Children.Add(new TextBlock { Text = "+", FontSize = 18, Foreground = (WpfBrush)FindResource("Text3"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 2) });
            Keycaps.Children.Add(new Border
            {
                MinWidth = 46,
                Padding = new Thickness(13, 6, 13, 7),
                CornerRadius = new CornerRadius(9),
                Background = new LinearGradientBrush(WpfColor.FromRgb(0x36, 0x2F, 0x45), WpfColor.FromRgb(0x2A, 0x24, 0x37), 90),
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0x4A, 0x42, 0x5C)),
                BorderThickness = new Thickness(1, 1, 1, 3),
                Child = new TextBlock { Text = parts[i], FontSize = 19, FontWeight = FontWeights.SemiBold, FontFamily = (System.Windows.Media.FontFamily)FindResource("DisplayFont"), HorizontalAlignment = HorizontalAlignment.Center },
            });
        }
        HeroSub.Text = _s.ClipSeconds switch
        {
            30 => "saves the last 30 seconds",
            60 => "saves the last minute",
            _ => $"saves the last {_s.ClipSeconds / 60} minutes",
        };
    }

    void KeysArea_Click(object sender, MouseButtonEventArgs e)
    {
        if (_capturing) return;
        _capturing = true;
        _setHotkey(null);   // the registered hotkey would swallow the keys we want to record
        Keycaps.Visibility = Visibility.Hidden;
        CaptureText.Visibility = Visibility.Visible;
        _breathe.Begin(this, true);
        SetHint("Hold Ctrl / Alt / Shift and press a key, or press an F-key.  Esc cancels.", false);
        Keyboard.Focus(this);
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;   // keep Alt / F10 from reaching the window (system menu)
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (key == Key.Escape) { EndCapture(restore: true); return; }

        var mods = Keyboard.Modifiers;
        bool fKey = key >= Key.F1 && key <= Key.F24;
        if (!fKey && (mods & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            SetHint("Use Ctrl or Alt with that key (or pick an F-key) so it doesn't fire while typing.", true);
            return;
        }
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(((System.Windows.Forms.Keys)KeyInterop.VirtualKeyFromKey(key)).ToString());
        string text = string.Join("+", parts);

        if (_setHotkey(text))
        {
            _s.Hotkey = text;
            _changed();
            EndCapture(restore: false);
            SetHint("Saved. Press it anywhere, even in a fullscreen game.", false);
        }
        else
        {
            _setHotkey(null);
            SetHint($"{text} is already taken by another app. Try a different combo.", true);
        }
    }

    void EndCapture(bool restore)
    {
        _capturing = false;
        _breathe.Stop(this);
        CaptureText.Visibility = Visibility.Collapsed;
        Keycaps.Visibility = Visibility.Visible;
        if (restore)
        {
            _setHotkey(_s.Hotkey);
            SetHint("Click the keys to change the shortcut", false);
        }
        ShowHotkey();
    }

    void SetHint(string text, bool error)
    {
        HotkeyHint.Text = text;
        HotkeyHint.Foreground = error ? (WpfBrush)FindResource("Bad") : (WpfBrush)FindResource("Text3");
    }

    // ---------------------------------------------------------------- folder / last clip

    void ShowFolder()
    {
        FolderName.Text = Path.GetFileName(_s.SaveFolder.TrimEnd('\\')) is { Length: > 0 } n ? n : _s.SaveFolder;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        FolderPath.Text = _s.SaveFolder.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + _s.SaveFolder[home.Length..] : _s.SaveFolder;
        FolderPath.ToolTip = _s.SaveFolder;
    }

    void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Where should Skippy save clips?", InitialDirectory = Directory.Exists(_s.SaveFolder) ? _s.SaveFolder : null };
        if (dlg.ShowDialog(this) != true) return;
        _s.SaveFolder = dlg.FolderName;
        ShowFolder();
        UpdateLastClip();
        _changed();
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e) => Folders.Open(_s.SaveFolder);

    void OnClipSaved(string path) => Dispatcher.BeginInvoke(UpdateLastClip);

    string? _lastClip;

    void UpdateLastClip()
    {
        _lastClip = _engine.LastClipPath;
        if (_lastClip == null)
        {
            try
            {
                _lastClip = Directory.Exists(_s.SaveFolder)
                    ? new DirectoryInfo(_s.SaveFolder).GetFiles("Skippy_*.mp4").OrderByDescending(f => f.LastWriteTime).FirstOrDefault()?.FullName
                    : null;
            }
            catch { }
        }
        if (_lastClip == null || !File.Exists(_lastClip))
        {
            LastClip.Text = "none yet — press the shortcut to save one";
            ShowClip.Visibility = Visibility.Collapsed;
            return;
        }
        var t = File.GetLastWriteTime(_lastClip);
        var ago = DateTime.Now - t;
        string when = ago.TotalSeconds < 60 ? "just now"
            : ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes} min ago"
            : ago.TotalHours < 24 ? $"{(int)ago.TotalHours} h ago"
            : t.ToString("d MMM");
        LastClip.Text = $"{Path.GetFileName(_lastClip)}  ·  {when}";
        ShowClip.Visibility = Visibility.Visible;
    }

    void ShowClip_Click(object sender, RoutedEventArgs e)
    {
        if (_lastClip == null || !File.Exists(_lastClip)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{_lastClip}\"") { UseShellExecute = true });
    }
}
