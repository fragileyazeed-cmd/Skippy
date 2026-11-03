using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Skippy.Audio;
using Skippy.Core;
using Skippy.Video;
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
using Rectangle = System.Windows.Shapes.Rectangle;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;

namespace Skippy.UI;

public sealed class ComboItem(string text, string? value)
{
    public string Text { get; } = text;
    public string? Value { get; } = value;
}

/// <summary>
/// The settings window. Every change is saved and applied immediately (no OK button). While it is open it
/// shows live status: buffer dial, stat tiles, audio LED meters and a preview of the recorded screen.
/// Nothing of that runs once it is closed.
/// </summary>
public partial class SettingsWindow : Window
{
    const double RingSize = 136, RingRadius = 50;

    readonly Settings _s;
    readonly ReplayEngine _engine;
    readonly Action _changed;
    readonly Func<string?, bool> _setHotkey;
    readonly Action<bool> _setPaused;
    readonly Action _exitForUpdate;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    readonly Storyboard _pulse, _breathe, _aurora;
    readonly LedMeter _micMeter, _outMeter;
    readonly List<Line> _ticks = new();
    bool _loading = true;
    bool _capturing;
    int _tick;
    string _statusKey = "";
    UpdateInfo? _update;
    bool _updateBusy;

    // live preview of the recorded screen (inside the selected monitor tile)
    readonly ImageBrush _previewBrush = new() { Stretch = Stretch.UniformToFill };
    WriteableBitmap? _previewBmp;
    bool _previewBusy;
    bool _hasPreviewTile;

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
        _aurora = (Storyboard)Resources["Aurora"];

        // taskbar / Alt+Tab: the multi-size .ico, so Windows picks a pixel-perfect frame
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Skippy;component/Skippy.ico", UriKind.Absolute));

        Grain.Fill = MakeGrain();
        BuildTicks();
        _micMeter = new LedMeter(MicLeds);
        _outMeter = new LedMeter(OutLeds);

        (_s.ClipSeconds switch { 30 => Len30, 120 => Len120, 180 => Len180, _ => Len60 }).IsChecked = true;
        (_s.Fps == 30 ? Fps30 : Fps60).IsChecked = true;
        SoundSwitch.IsChecked = _s.PlaySound;
        AutostartSwitch.IsChecked = Autostart.IsEnabled;
        Activated += (_, _) =>
        {
            // someone may have flipped it in Task Manager meanwhile
            bool was = _loading;
            _loading = true;
            AutostartSwitch.IsChecked = Autostart.IsEnabled;
            _loading = was;
        };
        FillAudio();
        ShowFolder();
        ShowHotkey();
        UpdateClipEstimates();
        UpdateLastClip();
        _loading = false;

        SourceInitialized += (_, _) =>
        {
            ApplyDwmStyle();
            MaxHeight = SystemParameters.WorkArea.Height - 16;   // small screens get a scrollbar instead of a cut-off window
        };
        Loaded += (_, _) =>
        {
            // pixel art must be drawn at the exact device size (no resampling) to stay sharp
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Logo.Source = ToImageSource(AppIcon.Draw((int)Math.Round(Logo.Width * dpi)));
            BuildMonitorMap();
            Animate();
            _aurora.Begin(this, true);
            OnTick();
        };
        MapFrame.SizeChanged += (_, _) => BuildMonitorMap();
        StateChanged += (_, _) => { if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; };
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => { if (_capturing) EndCapture(restore: true); };
        ThumbArea.MouseEnter += (_, _) => Fade(PlayOverlay, _lastClip != null ? 1 : 0);
        ThumbArea.MouseLeave += (_, _) => Fade(PlayOverlay, 0);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        _engine.ClipSaved += OnClipSaved;
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _aurora.Stop(this);
            if (_capturing) EndCapture(restore: true);
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
            _engine.ClipSaved -= OnClipSaved;
        };
    }

    // ================================================================ look & feel

    /// <summary>Cards drop in one after another.</summary>
    void Animate()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        FrameworkElement[] cards = [HeroCard, ClipCard, ScreenCard, AudioCard, SaveCard];
        for (int i = 0; i < cards.Length; i++)
        {
            var c = cards[i];
            var shift = new TranslateTransform(0, 18);
            c.RenderTransform = shift;
            c.Opacity = 0;
            var begin = TimeSpan.FromMilliseconds(40 + i * 55);
            c.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { BeginTime = begin, EasingFunction = ease });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(420)) { BeginTime = begin, EasingFunction = ease });
        }
    }

    static void Fade(UIElement e, double to) =>
        e.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(150)));

    /// <summary>Fine film grain over the background (a tiled noise texture).</summary>
    internal static ImageBrush MakeGrain()
    {
        const int n = 128;
        var px = new byte[n * n * 4];
        var rnd = new Random(7);
        for (int i = 0; i < n * n; i++)
        {
            byte v = (byte)rnd.Next(256);
            px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = v;
            px[i * 4 + 3] = 255;
        }
        var bmp = BitmapSource.Create(n, n, 96, 96, PixelFormats.Bgra32, null, px, n * 4);
        bmp.Freeze();
        return new ImageBrush(bmp) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, n, n), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
    }

    /// <summary>60 tick marks around the buffer dial; every 5th is longer.</summary>
    void BuildTicks()
    {
        double c = RingSize / 2;
        for (int i = 0; i < 60; i++)
        {
            double a = i / 60.0 * 2 * Math.PI;
            bool major = i % 5 == 0;
            double r1 = major ? 59.5 : 61, r2 = 65;
            var l = new Line
            {
                X1 = c + r1 * Math.Sin(a), Y1 = c - r1 * Math.Cos(a),
                X2 = c + r2 * Math.Sin(a), Y2 = c - r2 * Math.Cos(a),
                StrokeThickness = major ? 2 : 1.2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Stroke = TickOff,
            };
            RingTicks.Children.Add(l);
            _ticks.Add(l);
        }
    }

    static readonly WpfBrush TickOff = Frozen(WpfColor.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    static readonly WpfBrush TickOn = Frozen(WpfColor.FromArgb(0xC0, 0xE9, 0xD5, 0xFF));

    static SolidColorBrush Frozen(WpfColor c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void ApplyDwmStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1, round = 2, border = 0x00473445; // COLORREF 0x00BBGGRR
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

    // ================================================================ updates

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

    // ================================================================ live status

    void OnTick()
    {
        // meters every tick (25 fps); preview ~6 fps; the rest 4x a second
        var audio = _engine.Audio;
        bool micOff = _s.MicrophoneId == AudioDevices.None;
        UpdateMeterState(_micMeter, audio?.TakeLevel(true) ?? -1, MicState, micOff);
        UpdateMeterState(_outMeter, audio?.TakeLevel(false) ?? -1, OutState, false);
        if (_tick % 4 == 0) RefreshPreview();
        if (_tick % 6 == 0)
        {
            UpdateStatus();
            UpdateRing();
            UpdateStats();
        }
        if (_tick % 375 == 0) UpdateLastClipMeta();   // keeps "x min ago" fresh
        _tick++;
    }

    static void UpdateMeterState(LedMeter meter, float peak, TextBlock state, bool off)
    {
        meter.Update(peak);
        if (peak < 0)
        {
            state.Text = off ? "off" : "not available";
            state.Foreground = (WpfBrush)Application.Current.Resources[off ? "Text3" : "Warn"];
        }
        else if (state.Text.Length > 0) state.Text = "";
    }

    void UpdateStatus()
    {
        var p = _engine.Pipeline;
        string key = _engine.IsPaused ? "paused" : p?.Format?.Sps.Length > 0 ? "rec" : "start";
        if (key == _statusKey) return;
        _statusKey = key;

        (string text, WpfColor dot, WpfColor txt) = key switch
        {
            "rec" => ("Recording", WpfColor.FromRgb(0x3D, 0xDC, 0x97), WpfColor.FromRgb(0xA7, 0xF3, 0xD0)),
            "paused" => ("Paused", WpfColor.FromRgb(0x8A, 0x84, 0x9C), WpfColor.FromRgb(0xA8, 0xA1, 0xB9)),
            _ => ("Starting…", WpfColor.FromRgb(0xFF, 0xB5, 0x47), WpfColor.FromRgb(0xFD, 0xE6, 0x8A)),
        };
        StatusText.Text = text;
        StatusText.Foreground = new SolidColorBrush(txt);
        StatusDot.Fill = new SolidColorBrush(dot);
        StatusGlow.Color = dot;
        StatusPill.Background = new SolidColorBrush(WpfColor.FromArgb(0x22, dot.R, dot.G, dot.B));
        StatusPill.BorderBrush = new SolidColorBrush(WpfColor.FromArgb(0x55, dot.R, dot.G, dot.B));
        StatusPill.ToolTip = key == "paused" ? "Click to resume buffering" : "Click to pause (stops capturing completely)";
        if (key == "rec") _pulse.Begin(this, true); else { _pulse.Stop(this); StatusDot.Opacity = 1; }
    }

    void UpdateRing()
    {
        double secs = _engine.IsPaused ? 0 : _engine.BufferedSeconds;
        double frac = Math.Clamp(secs / _s.ClipSeconds, 0, 1);
        int whole = (int)Math.Round(secs);
        RingText.Text = $"{whole / 60}:{whole % 60:00}";
        RingSub.Text = _engine.IsPaused ? "paused" : $"of {_s.ClipSeconds / 60}:{_s.ClipSeconds % 60:00}";

        for (int i = 0; i < _ticks.Count; i++)
        {
            var want = i / 60.0 < frac ? TickOn : TickOff;
            if (_ticks[i].Stroke != want) _ticks[i].Stroke = want;
        }

        var c = new WpfPoint(RingSize / 2, RingSize / 2);
        const double r = RingRadius;
        if (frac >= 0.999) { RingArc.Data = new EllipseGeometry(c, r, r); RingDot.Visibility = Visibility.Collapsed; return; }
        if (frac <= 0.001) { RingArc.Data = null; RingDot.Visibility = Visibility.Collapsed; return; }
        double a = frac * 2 * Math.PI;
        var start = new WpfPoint(c.X, c.Y - r);
        var end = new WpfPoint(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new WpfSize(r, r), 0, frac > 0.5, SweepDirection.Clockwise, true));
        RingArc.Data = new PathGeometry([fig]);
        Canvas.SetLeft(RingDot, end.X - RingDot.Width / 2);
        Canvas.SetTop(RingDot, end.Y - RingDot.Height / 2);
        RingDot.Visibility = Visibility.Visible;
    }

    void UpdateStats()
    {
        var p = _engine.Pipeline;
        var f = p?.Format;
        if (_engine.IsPaused || p == null || f == null)
        {
            string why = _engine.IsPaused ? "paused" : "starting…";
            StatRes.Text = StatFps.Text = StatAudio.Text = StatRam.Text = "—";
            StatResSub.Text = StatFpsSub.Text = StatAudioSub.Text = StatRamSub.Text = why;
            return;
        }
        StatRes.Text = $"{f.Width}×{f.Height}";
        StatResSub.Text = $"from Screen {p.Monitor.Number}";
        StatFps.Text = $"{f.Fps} fps";
        StatFpsSub.Text = EncoderShortName(f.EncoderName);

        var audio = _engine.Audio;
        bool mic = audio?.IsRecording(true) == true, game = audio?.IsRecording(false) == true;
        (StatAudio.Text, StatAudioSub.Text) = (mic, game) switch
        {
            (true, true) => ("Mic + game", "2 sources live"),
            (false, true) => ("Game audio", "mic off"),
            (true, false) => ("Mic only", "no game audio"),
            _ => ("No audio", "check devices"),
        };
        double mb = (_engine.VideoBufferBytes + _engine.AudioBufferBytes) / 1048576.0;
        StatRam.Text = $"{mb:0} MB";
        StatRamSub.Text = "buffer in RAM";
    }

    static string EncoderShortName(string name) =>
        name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD AMF encoder"
        : name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA NVENC"
        : name.Contains("Intel", StringComparison.OrdinalIgnoreCase) || name.Contains("Quick", StringComparison.OrdinalIgnoreCase) ? "Intel Quick Sync"
        : name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ? "software encoder" : name;

    void StatusPill_Click(object sender, MouseButtonEventArgs e)
    {
        _setPaused(!_engine.IsPaused);
        _statusKey = "";
        UpdateStatus();
        UpdateStats();
    }

    /// <summary>Pulls a small frame from the GPU (off the UI thread) into the selected monitor tile.</summary>
    async void RefreshPreview()
    {
        if (_previewBusy || !_hasPreviewTile || WindowState == WindowState.Minimized || !IsVisible) return;
        var p = _engine.Pipeline;
        if (p == null || _engine.IsPaused) return;
        _previewBusy = true;
        try
        {
            var (px, w, h) = await Task.Run(() => { var b = p.GetPreview(out int w, out int h); return (b, w, h); });
            if (px == null) return;
            if (_previewBmp == null || _previewBmp.PixelWidth != w || _previewBmp.PixelHeight != h)
                _previewBmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
            _previewBmp.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
            if (_previewBrush.ImageSource != _previewBmp) _previewBrush.ImageSource = _previewBmp;
        }
        catch (Exception ex) { Log.Warn($"Preview failed: {ex.Message}"); }
        finally { _previewBusy = false; }
    }

    // ================================================================ clip / fps / sound

    void Length_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _s.ClipSeconds = int.Parse((string)((RadioButton)sender).Tag);
        ShowHotkey();
        UpdateClipEstimates();
        UpdateRing();
        _changed();
    }

    void Fps_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _s.Fps = int.Parse((string)((RadioButton)sender).Tag);
        UpdateClipEstimates();
        _changed();
    }

    /// <summary>Worst-case size of one clip and of the RAM buffer for the chosen length / fps / screen.</summary>
    void UpdateClipEstimates()
    {
        var mon = Monitors.Find(_s.MonitorDevice) ?? Monitors.Primary();
        var (w, h) = mon != null ? VideoPipeline.EncodeSize(mon.Width, mon.Height) : (1920, 1080);
        double bytesPerSec = (VideoPipeline.Bitrate(w, h, _s.Fps) + AacEncoder.Bitrate) / 8.0;
        double clip = bytesPerSec * _s.ClipSeconds / 1048576, ram = bytesPerSec * (_s.ClipSeconds + 3) / 1048576;
        ClipSizeText.Text = $"up to {clip:0} MB per clip";
        ClipRamText.Text = $"up to {ram:0} MB RAM";
        ClipSizeText.ToolTip = "Busy games reach this; calm scenes compress far smaller.";
    }

    void Sound_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _s.PlaySound = SoundSwitch.IsChecked == true;
        _changed();
        if (_s.PlaySound) Chime.Play();   // let people hear what they just enabled
    }

    void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Autostart.SetEnabled(AutostartSwitch.IsChecked == true);
    }

    // ================================================================ audio devices

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

    // ================================================================ monitors

    void OnDisplaysChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { BuildMonitorMap(); UpdateClipEstimates(); });

    void BuildMonitorMap()
    {
        MonitorMap.Children.Clear();
        _hasPreviewTile = false;
        var all = Monitors.GetAll();
        if (all.Count == 0) return;
        var selected = Monitors.Find(_s.MonitorDevice) ?? all.First(m => m.IsPrimary);

        double W = MapFrame.ActualWidth - 2, H = MapFrame.ActualHeight - 2;
        if (W <= 0 || H <= 0) return;
        int minX = all.Min(m => m.X), minY = all.Min(m => m.Y);
        int maxX = all.Max(m => m.X + m.Width), maxY = all.Max(m => m.Y + m.Height);
        double scale = Math.Min((W - 28) / (maxX - minX), (H - 24) / (maxY - minY));
        double offX = (W - (maxX - minX) * scale) / 2, offY = (H - (maxY - minY) * scale) / 2;

        foreach (var m in all)
        {
            bool sel = m.DeviceName == selected.DeviceName;
            double tw = Math.Max(24, m.Width * scale - 8), th = Math.Max(16, m.Height * scale - 8);
            var tile = sel ? SelectedTile(m, tw, th) : OtherTile(m, tw, th);
            var mon = m;
            tile.MouseLeftButtonUp += (_, _) => SelectMonitor(mon);
            Canvas.SetLeft(tile, offX + (m.X - minX) * scale + 4);
            Canvas.SetTop(tile, offY + (m.Y - minY) * scale + 4);
            MonitorMap.Children.Add(tile);
        }

        bool missing = _s.MonitorDevice != null && Monitors.Find(_s.MonitorDevice) == null;
        MonitorInfo.Text = missing
            ? $"Your chosen screen isn't connected — recording Screen {selected.Number} until it's back"
            : $"Recording Screen {selected.Number}  ·  {selected.Width}×{selected.Height}{(selected.IsPrimary ? "  ·  primary" : "")}  ·  live preview";
    }

    /// <summary>The recorded screen: live preview, number badge, pulsing REC chip and an amber glow.</summary>
    FrameworkElement SelectedTile(MonitorInfo m, double w, double h)
    {
        var amber = (WpfBrush)FindResource("AccentAmber");
        var grid = new Grid { Width = w, Height = h, Cursor = Cursors.Hand, ToolTip = $"{m.DisplayName}\nRecording this screen" };
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = new LinearGradientBrush(WpfColor.FromRgb(0x2C, 0x20, 0x12), WpfColor.FromRgb(0x1A, 0x14, 0x1E), 45),
        });
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(9), Background = _previewBrush });
        // bottom shade for the caption
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(0, 0, 9, 9),
            Height = Math.Min(30, h * 0.35),
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new LinearGradientBrush(WpfColor.FromArgb(0, 0, 0, 0), WpfColor.FromArgb(0xC0, 0, 0, 0), 90),
            Child = new TextBlock
            {
                Text = $"{m.Width}×{m.Height}", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 5),
            },
        });
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6), Background = amber, Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(7),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = m.Number.ToString(), FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
        });
        var recDot = new Ellipse { Width = 7, Height = 7, Fill = Frozen(WpfColor.FromRgb(0xFF, 0x4D, 0x6A)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
        recDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.2, TimeSpan.FromMilliseconds(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6), Background = Frozen(WpfColor.FromArgb(0xC8, 0x0C, 0x0A, 0x10)), Padding = new Thickness(6, 2, 7, 2), Margin = new Thickness(7),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { recDot, new TextBlock { Text = "REC", FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White } },
            },
        });
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(2), BorderBrush = amber, IsHitTestVisible = false });
        grid.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = WpfColor.FromRgb(0xF5, 0x9E, 0x0B), BlurRadius = 18, ShadowDepth = 0, Opacity = 0.45 };
        _hasPreviewTile = true;
        return grid;
    }

    FrameworkElement OtherTile(MonitorInfo m, double w, double h)
    {
        var idle = (WpfBrush)FindResource("StrokeHi");
        var tile = new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), BorderBrush = idle,
            Background = new LinearGradientBrush(WpfColor.FromRgb(0x1F, 0x1B, 0x27), WpfColor.FromRgb(0x17, 0x14, 0x1D), 90),
            Cursor = Cursors.Hand, ToolTip = $"{m.DisplayName}\nClick to record this screen",
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = m.Number.ToString(), FontSize = 20, FontWeight = FontWeights.SemiBold, FontFamily = (System.Windows.Media.FontFamily)FindResource("DisplayFont"),
                                    Foreground = (WpfBrush)FindResource("Text2"), HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = $"{m.Width}×{m.Height}", FontSize = 10.5, Foreground = (WpfBrush)FindResource("Text3"), HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };
        tile.MouseEnter += (_, _) => tile.BorderBrush = (WpfBrush)FindResource("AccentAmber");
        tile.MouseLeave += (_, _) => tile.BorderBrush = idle;
        return tile;
    }

    void SelectMonitor(MonitorInfo m)
    {
        var cur = Monitors.Find(_s.MonitorDevice) ?? Monitors.Primary();
        if (cur?.DeviceName == m.DeviceName && _s.MonitorDevice != null) return;
        _s.MonitorDevice = m.DeviceName;
        _previewBrush.ImageSource = null;   // old screen's picture until the new capture starts
        _changed();
        BuildMonitorMap();
        UpdateClipEstimates();
    }

    // ================================================================ hotkey

    void ShowHotkey()
    {
        Keycaps.Children.Clear();
        var parts = _s.Hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) Keycaps.Children.Add(new TextBlock { Text = "+", FontSize = 20, Foreground = Frozen(WpfColor.FromRgb(0x8E, 0x84, 0xB0)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 9, 3) });
            Keycaps.Children.Add(new Border
            {
                MinWidth = 52,
                Padding = new Thickness(15, 7, 15, 8),
                CornerRadius = new CornerRadius(11),
                Background = new LinearGradientBrush(WpfColor.FromRgb(0x45, 0x3B, 0x63), WpfColor.FromRgb(0x2B, 0x24, 0x3E), 90),
                BorderBrush = new LinearGradientBrush(WpfColor.FromRgb(0x7C, 0x6E, 0xA8), WpfColor.FromRgb(0x1E, 0x19, 0x2C), 90),
                BorderThickness = new Thickness(1, 1, 1, 4),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = WpfColor.FromRgb(0x10, 0x06, 0x24), BlurRadius = 10, ShadowDepth = 3, Direction = 270, Opacity = 0.6 },
                Child = new TextBlock { Text = parts[i], FontSize = 21, FontWeight = FontWeights.SemiBold, FontFamily = (System.Windows.Media.FontFamily)FindResource("DisplayFont"), Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
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

    // ================================================================ folder / last clip

    void ShowFolder()
    {
        FolderName.Text = System.IO.Path.GetFileName(_s.SaveFolder.TrimEnd('\\')) is { Length: > 0 } n ? n : _s.SaveFolder;
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
    string? _thumbFor;

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
            _lastClip = null;
            LastClip.Text = "No clips yet";
            LastClipMeta.Text = $"press {_s.Hotkey} in a game to save one";
            ClipButtons.Visibility = ShowClip.Visibility = Visibility.Collapsed;
            DurationBadge.Visibility = Visibility.Collapsed;
            ClipThumb.Background = (WpfBrush)new BrushConverter().ConvertFrom("#1C1824")!;
            ThumbPlaceholder.Visibility = Visibility.Visible;
            _thumbFor = null;
            return;
        }
        LastClip.Text = System.IO.Path.GetFileNameWithoutExtension(_lastClip);
        ClipButtons.Visibility = ShowClip.Visibility = Visibility.Visible;
        UpdateLastClipMeta();
        if (_thumbFor != _lastClip) LoadThumbnail(_lastClip);
    }

    void UpdateLastClipMeta()
    {
        if (_lastClip == null || !File.Exists(_lastClip)) return;
        var fi = new FileInfo(_lastClip);
        var ago = DateTime.Now - fi.LastWriteTime;
        string when = ago.TotalSeconds < 60 ? "just now"
            : ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes} min ago"
            : ago.TotalHours < 24 ? $"{(int)ago.TotalHours} h ago"
            : fi.LastWriteTime.ToString("d MMM");
        var dur = ShellThumbnail.Mp4Duration(_lastClip);
        string len = dur is { } d ? $"{(int)d.TotalMinutes}:{d.Seconds:00}" : "";
        LastClipMeta.Text = string.Join("  ·  ", new[] { len, $"{fi.Length / 1048576.0:0} MB", when }.Where(s => s.Length > 0));
        DurationText.Text = len;
        DurationBadge.Visibility = len.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void LoadThumbnail(string path)
    {
        _thumbFor = path;
        // Windows may need a moment to generate the thumbnail of a file that was just written
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var bmp = await ShellThumbnail.GetAsync(path, 224);
            if (_thumbFor != path) return;
            if (bmp != null)
            {
                ClipThumb.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                ThumbPlaceholder.Visibility = Visibility.Collapsed;
                return;
            }
            await Task.Delay(1500);
        }
    }

    void PlayClip_Click(object sender, RoutedEventArgs e)
    {
        if (_lastClip == null || !File.Exists(_lastClip)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_lastClip) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Could not open clip: {ex.Message}"); }
    }

    void TrimClip_Click(object sender, RoutedEventArgs e)
    {
        if (_lastClip == null || !File.Exists(_lastClip)) return;
        TrimWindow.Open(_s, _lastClip);
    }

    void TrimOther_Click(object sender, RoutedEventArgs e) => TrimWindow.PickAndOpen(_s, this);

    void ShowClip_Click(object sender, RoutedEventArgs e)
    {
        if (_lastClip == null || !File.Exists(_lastClip)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{_lastClip}\"") { UseShellExecute = true });
    }

    // ================================================================ LED meter

    /// <summary>Segmented level meter: teal → lime → amber → red, with a short peak hold.</summary>
    sealed class LedMeter
    {
        const int N = 30;
        static readonly WpfBrush Off = Frozen(WpfColor.FromRgb(0x24, 0x20, 0x2C));
        static readonly WpfBrush OffDim = Frozen(WpfColor.FromRgb(0x1A, 0x17, 0x20));
        static readonly (double at, WpfColor c)[] Stops =
        [
            (0.0, WpfColor.FromRgb(0x14, 0xB8, 0xA6)),
            (0.5, WpfColor.FromRgb(0x2D, 0xD4, 0xBF)),
            (0.7, WpfColor.FromRgb(0xA3, 0xE6, 0x35)),
            (0.86, WpfColor.FromRgb(0xFB, 0xBF, 0x24)),
            (1.0, WpfColor.FromRgb(0xF4, 0x3F, 0x5E)),
        ];

        readonly Border[] _segs = new Border[N];
        readonly WpfBrush[] _on = new WpfBrush[N];
        readonly WpfBrush?[] _cur = new WpfBrush?[N];
        double _shown;
        int _peak = -1;
        long _peakAt;

        public LedMeter(UniformGrid host)
        {
            for (int i = 0; i < N; i++)
            {
                _on[i] = Frozen(ColorAt(i / (double)(N - 1)));
                _segs[i] = new Border { CornerRadius = new CornerRadius(1.5), Margin = new Thickness(1.1, 0, 1.1, 0), Background = Off };
                host.Children.Add(_segs[i]);
            }
        }

        static WpfColor ColorAt(double t)
        {
            for (int k = 1; k < Stops.Length; k++)
            {
                if (t > Stops[k].at) continue;
                var (a0, c0) = Stops[k - 1];
                var (a1, c1) = Stops[k];
                double u = (t - a0) / (a1 - a0);
                return WpfColor.FromRgb((byte)(c0.R + (c1.R - c0.R) * u), (byte)(c0.G + (c1.G - c0.G) * u), (byte)(c0.B + (c1.B - c0.B) * u));
            }
            return Stops[^1].c;
        }

        /// <summary>peak &lt; 0 means the source isn't recording (meter goes dark).</summary>
        public void Update(float peak)
        {
            if (peak < 0)
            {
                _shown = 0; _peak = -1;
                for (int i = 0; i < N; i++) Set(i, OffDim);
                return;
            }
            double db = 20 * Math.Log10(Math.Max(peak, 1e-5));
            double level = Math.Clamp((db + 54) / 54, 0, 1);
            _shown = Math.Max(level, _shown - 0.03);   // fast attack, smooth fall
            int lit = (int)Math.Round(_shown * N) - 1;
            long now = Environment.TickCount64;
            if (lit >= _peak) { _peak = lit; _peakAt = now; }
            else if (now - _peakAt > 900) _peak = Math.Max(lit, _peak - 1);   // hold, then fall
            for (int i = 0; i < N; i++)
                Set(i, i <= lit || (i == _peak && _peak > 0) ? _on[i] : Off);
        }

        void Set(int i, WpfBrush b)
        {
            if (ReferenceEquals(_cur[i], b)) return;
            _cur[i] = b;
            _segs[i].Background = b;
        }
    }
}
