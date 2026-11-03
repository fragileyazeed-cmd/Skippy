using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Skippy.Core;
using Skippy.Media;
using Image = System.Windows.Controls.Image;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using WpfBrush = System.Windows.Media.Brush;

namespace Skippy.UI;

/// <summary>
/// The trimmer: a player, a filmstrip timeline with start/end handles, frame stepping and exact time
/// fields. "Save cut" writes the chosen range as a new clip into the EditedSkippy folder.
/// </summary>
public partial class TrimWindow : Window
{
    const int StripFrames = 12;
    static TrimWindow? _open;

    readonly Settings _s;
    string _path = "";
    ClipInfo? _info;
    TimeSpan _dur, _in, _out, _pos, _frame = TimeSpan.FromSeconds(1 / 60.0);
    bool _playing, _stopAtOut, _saving, _ready;
    // MediaElement reports its position from 0 again after a seek + play, so the playhead runs on its own clock
    readonly System.Diagnostics.Stopwatch _clock = new();
    TimeSpan _playBase;
    CancellationTokenSource? _saveCts, _stripCts;
    string? _savedPath;

    enum Drag { None, Seek, In, Out }
    Drag _drag;
    double _grabOffset;

    /// <summary>Opens the trimmer on <paramref name="path"/> (reusing the window if one is open).</summary>
    public static void Open(Settings s, string path)
    {
        if (_open == null)
        {
            _open = new TrimWindow(s);
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }
        if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
        _open.Activate();
        _open.Load(path);
    }

    /// <summary>File picker in the clips folder (newest clip pre-selected), then the trimmer.</summary>
    public static void PickAndOpen(Settings s, Window? owner)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick a clip to trim",
            Filter = "Video clips (*.mp4)|*.mp4",
            InitialDirectory = Directory.Exists(s.SaveFolder) ? s.SaveFolder : null,
        };
        try
        {
            var newest = Directory.Exists(s.SaveFolder)
                ? new DirectoryInfo(s.SaveFolder).GetFiles("*.mp4").OrderByDescending(f => f.LastWriteTime).FirstOrDefault() : null;
            if (newest != null) dlg.FileName = newest.Name;
        }
        catch { }
        bool ok = owner != null ? dlg.ShowDialog(owner) == true : dlg.ShowDialog() == true;
        if (ok) Open(s, dlg.FileName);
    }

    TrimWindow(Settings settings)
    {
        _s = settings;
        InitializeComponent();
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Skippy;component/Skippy.ico", UriKind.Absolute));
        Grain.Fill = SettingsWindow.MakeGrain();
        string edited = Trimmer.EditedFolder(_s.SaveFolder);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        DestLink.Content = edited.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + edited[home.Length..] : edited;
        DestLink.ToolTip = edited;
        for (int i = 0; i < StripFrames; i++) Strip.Children.Add(new Image { Stretch = Stretch.UniformToFill, Opacity = 0 });

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1, round = 2, border = 0x006B3A9C;   // COLORREF 0x00BBGGRR
            DwmSetWindowAttribute(hwnd, 20, ref dark, 4);
            DwmSetWindowAttribute(hwnd, 33, ref round, 4);
            DwmSetWindowAttribute(hwnd, 34, ref border, 4);
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            // never open taller than the screen
            var wa = SystemParameters.WorkArea;
            if (Height > wa.Height - 16) Height = wa.Height - 16;
            if (Width > wa.Width - 16) Width = wa.Width - 16;
        };
        Loaded += (_, _) =>
        {
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            var shift = new TranslateTransform(0, 16);
            Panel.RenderTransform = shift;
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        };
        StateChanged += (_, _) =>
        {
            bool max = WindowState == WindowState.Maximized;
            MaxButton.Content = max ? "" : "";
            MaxButton.ToolTip = max ? "Restore" : "Maximize";
        };
        Track.SizeChanged += (_, _) => Layout();
        TimelineHost.MouseLeftButtonDown += Timeline_Down;
        TimelineHost.MouseMove += Timeline_Move;
        TimelineHost.MouseLeftButtonUp += Timeline_Up;
        TimelineHost.MouseWheel += (_, e) => { Step(e.Delta > 0 ? 1 : -1, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; };
        VideoArea.MouseWheel += (_, e) => { Step(e.Delta > 0 ? 1 : -1, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; };
        PreviewKeyDown += OnKey;
        CompositionTarget.Rendering += OnRender;
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRender;
            _saveCts?.Cancel();
            _stripCts?.Cancel();
            Player.Stop();
            Player.Close();
        };
    }

    // ================================================================ loading

    async void Load(string path)
    {
        if (_saving) return;   // finish the current save first
        Pause();
        _ready = false;
        _path = path;
        _savedPath = null;
        FileTitle.Text = System.IO.Path.GetFileName(path);
        ErrorText.Visibility = Visibility.Collapsed;
        DoneRow.Visibility = Visibility.Collapsed;
        DestRow.Visibility = Visibility.Visible;
        InfoText.Text = "opening…";
        foreach (Image img in Strip.Children) { img.Source = null; img.Opacity = 0; }
        StripLoading.Visibility = Visibility.Visible;

        try { _info = await Task.Run(() => Trimmer.Probe(path)); }
        catch (Exception ex)
        {
            Log.Warn($"Trim: cannot read {path}: {ex.Message}");
            ShowError("Skippy can't read this file. Is it an MP4 video?");
            return;
        }
        if (_path != path) return;
        _frame = TimeSpan.FromSeconds(1 / Math.Clamp(_info.Fps, 1, 240));
        double mb = new FileInfo(path).Length / 1048576.0;
        InfoText.Text = $"{_info.Width}×{_info.Height}  ·  {Math.Round(_info.Fps):0} fps  ·  {mb:0} MB";

        Player.Source = new Uri(path);
        Player.IsMuted = MuteToggle.IsChecked == true;
        Player.Pause();   // opens the file and shows the first frame
        LoadStrip(path);
    }

    void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
        BigPlay.Visibility = Visibility.Collapsed;
        InfoText.Text = "";
    }

    void Player_Opened(object sender, RoutedEventArgs e)
    {
        _dur = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : _info?.Duration ?? TimeSpan.Zero;
        if (_dur <= TimeSpan.Zero) { ShowError("This clip seems to be empty."); return; }
        _in = TimeSpan.Zero;
        _out = _dur;
        _ready = true;
        TotalText.Text = " / " + Fmt(_dur);
        InfoText.Text = $"{_info!.Width}×{_info.Height}  ·  {Math.Round(_info.Fps):0} fps  ·  {Fmt(_dur, false)}  ·  {new FileInfo(_path).Length / 1048576.0:0} MB";
        BigPlay.Visibility = Visibility.Visible;
        Seek(TimeSpan.Zero);
        Layout();
        SelectionChanged();
    }

    void Player_Failed(object? sender, ExceptionRoutedEventArgs e)
    {
        Log.Warn($"Trim: player failed on {_path}: {e.ErrorException?.Message}");
        ShowError("Windows can't play this file.");
    }

    void Player_Ended(object sender, RoutedEventArgs e)
    {
        if (LoopToggle.IsChecked == true && _stopAtOut) { Seek(_in); _playBase = _in; _clock.Restart(); Player.Play(); return; }
        Pause();
        Seek(_dur - _frame);
    }

    async void LoadStrip(string path)
    {
        _stripCts?.Cancel();
        var cts = _stripCts = new CancellationTokenSource();
        double aspect = _info!.Height > 0 ? _info.Width / (double)_info.Height : 16 / 9.0;
        int h = 96, w = (int)Math.Round(h * aspect / 2) * 2;
        try
        {
            await Task.Run(() => Trimmer.Filmstrip(path, StripFrames, w, h, (i, px) =>
            {
                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, px, w * 4);
                bmp.Freeze();
                Dispatcher.BeginInvoke(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    var img = (Image)Strip.Children[i];
                    img.Source = bmp;
                    img.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
                    StripLoading.Visibility = Visibility.Collapsed;
                });
            }, cts.Token), cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Trim: filmstrip failed: {ex.Message}");
            StripLoading.Text = "";
        }
        catch (OperationCanceledException) { }
    }

    // ================================================================ playback

    void OnRender(object? sender, EventArgs e)
    {
        if (!_playing) return;
        var p = Clamp(_playBase + _clock.Elapsed, TimeSpan.Zero, _dur);
        if (_stopAtOut && p >= _out)
        {
            if (LoopToggle.IsChecked == true) { Seek(_in); _playBase = _in; _clock.Restart(); return; }
            Pause();
            Seek(_out - _frame);   // park on the last frame of the cut
            return;
        }
        _pos = p;
        UpdatePlayhead();
    }

    void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();
    void Video_Click(object sender, MouseButtonEventArgs e) => TogglePlay();

    void TogglePlay()
    {
        if (!_ready || _saving) return;
        if (_playing) { Pause(); return; }
        // parked at the end of the cut (or of the clip): start the cut again
        bool atCutEnd = Math.Abs((_pos - _out).TotalSeconds) <= 1.5 * _frame.TotalSeconds;
        if (atCutEnd || _pos >= _dur - _frame || (LoopToggle.IsChecked == true && (_pos < _in || _pos >= _out))) Seek(_in);
        _stopAtOut = _pos >= _in && _pos < _out;
        Player.Play();
        _playBase = _pos;
        _clock.Restart();
        _playing = true;
        PlayGlyph.Text = "";
        PlayGlyph.Margin = new Thickness(0);
        BigPlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
    }

    void Pause()
    {
        if (!_playing) return;
        Player.Pause();
        _playing = false;
        _clock.Stop();
        // land the picture exactly on the playhead
        Seek(Snap(Clamp(_playBase + _clock.Elapsed, TimeSpan.Zero, _dur)));
        PlayGlyph.Text = "";
        PlayGlyph.Margin = new Thickness(3, 0, 0, 0);
        BigPlay.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        UpdatePlayhead();
    }

    void Seek(TimeSpan t)
    {
        if (!_ready) return;
        _pos = Clamp(t, TimeSpan.Zero, _dur);
        Player.Position = _pos;
        UpdatePlayhead();
    }

    TimeSpan Snap(TimeSpan t) => TimeSpan.FromTicks((long)(Math.Round(t.Ticks / (double)_frame.Ticks) * _frame.Ticks));

    void Step(int dir, bool second)
    {
        if (!_ready || _saving) return;
        Pause();
        Seek(second ? _pos + TimeSpan.FromSeconds(dir) : Snap(_pos) + dir * _frame);
    }

    void StepBack_Click(object sender, RoutedEventArgs e) => Step(-1, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    void StepForward_Click(object sender, RoutedEventArgs e) => Step(1, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    void GoIn_Click(object sender, RoutedEventArgs e) { Pause(); Seek(_in); }
    void GoOut_Click(object sender, RoutedEventArgs e) { Pause(); Seek(_out - _frame); }
    void Mute_Changed(object sender, RoutedEventArgs e)
    {
        Player.IsMuted = MuteToggle.IsChecked == true;
        MuteGlyph.Text = Player.IsMuted ? "" : "";
    }

    // ================================================================ start / end

    void SetIn_Click(object sender, RoutedEventArgs e) => SetIn(Snap(_pos));
    void SetOut_Click(object sender, RoutedEventArgs e) => SetOut(Snap(_pos) + _frame);   // the current frame is the last one kept

    void SetIn(TimeSpan t)
    {
        if (!_ready) return;
        _in = Clamp(t, TimeSpan.Zero, _dur - _frame);
        if (_out < _in + _frame) _out = _dur;
        SelectionChanged();
    }

    void SetOut(TimeSpan t)
    {
        if (!_ready) return;
        _out = Clamp(t, _frame, _dur);
        if (_in > _out - _frame) _in = TimeSpan.Zero;
        SelectionChanged();
    }

    void SelectionChanged()
    {
        if (!InBox.IsKeyboardFocused) InBox.Text = Fmt(_in);
        if (!OutBox.IsKeyboardFocused) OutBox.Text = Fmt(_out);
        double len = (_out - _in).TotalSeconds;
        LengthText.Text = len >= 60 ? $"{(int)(len / 60)}:{len % 60:00.00}".Replace(',', '.') : $"{len:0.00} s".Replace(',', '.');
        if (_info != null)
        {
            double mb = Trimmer.EstimateBytes(_path, _info, len) / 1048576.0;
            SizeText.Text = mb < 10 ? $"≈ {mb:0.0} MB" : $"≈ {mb:0} MB";
        }
        if (DoneRow.Visibility == Visibility.Visible && !_saving)
        {
            DoneRow.Visibility = Visibility.Collapsed;
            DestRow.Visibility = Visibility.Visible;
        }
        Layout();
    }

    void TimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Commit((TextBox)sender); Keyboard.ClearFocus(); FocusManager.SetFocusedElement(this, this); e.Handled = true; }
        else if (e.Key == Key.Escape) { ((TextBox)sender).Text = Fmt(sender == InBox ? _in : _out); Keyboard.ClearFocus(); e.Handled = true; }
    }

    void TimeBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => Commit((TextBox)sender);

    void Commit(TextBox box)
    {
        if (!_ready) return;
        if (TryParse(box.Text, out var t))
        {
            if (box == InBox) { SetIn(t); Pause(); Seek(_in); }
            else { SetOut(t); Pause(); Seek(_out - _frame); }
        }
        box.Text = Fmt(box == InBox ? _in : _out);
    }

    /// <summary>Accepts "34", "34.5", "0:34.50", "1:02" (comma works as decimal point too).</summary>
    static bool TryParse(string text, out TimeSpan t)
    {
        t = TimeSpan.Zero;
        var parts = text.Trim().Replace(',', '.').Split(':');
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return false;
            total = total * 60 + v;
        }
        t = TimeSpan.FromSeconds(total);
        return parts.Length is >= 1 and <= 3;
    }

    static string Fmt(TimeSpan t, bool hundredths = true)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        int m = (int)t.TotalMinutes;
        double s = t.TotalSeconds - m * 60;
        if (!hundredths) return $"{m}:{(int)s:00}";
        // never round up to 60.00
        s = Math.Floor(s * 100) / 100;
        return $"{m}:{s.ToString("00.00", CultureInfo.InvariantCulture)}";
    }

    static TimeSpan Clamp(TimeSpan t, TimeSpan lo, TimeSpan hi) => t < lo ? lo : t > hi ? hi : t;

    // ================================================================ timeline

    const double StripTop = 22, StripHeight = 72;

    double X(TimeSpan t) => _dur > TimeSpan.Zero ? t.TotalSeconds / _dur.TotalSeconds * Track.ActualWidth : 0;
    TimeSpan TimeAt(double x) => Track.ActualWidth > 0 ? TimeSpan.FromSeconds(Math.Clamp(x / Track.ActualWidth, 0, 1) * _dur.TotalSeconds) : TimeSpan.Zero;

    void Layout()
    {
        double w = Track.ActualWidth;
        if (w <= 0) return;
        bool show = _ready;
        foreach (UIElement el in new UIElement[] { DimLeft, DimRight, SelFrame, InHandle, OutHandle, Playhead })
            el.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) { Ruler.Children.Clear(); return; }

        double xi = X(_in), xo = X(_out);
        Place(DimLeft, 0, StripTop + 1, xi, StripHeight - 2);
        Place(DimRight, xo, StripTop + 1, w - xo, StripHeight - 2);
        DimLeft.RadiusX = DimLeft.RadiusY = DimRight.RadiusX = DimRight.RadiusY = 9;
        Place(SelFrame, xi, StripTop, Math.Max(0, xo - xi), StripHeight);
        Place(InHandle, xi - InHandle.Width, StripTop, InHandle.Width, StripHeight);
        Place(OutHandle, xo, StripTop, OutHandle.Width, StripHeight);
        PlayheadLine.Height = StripHeight + 10;
        Canvas.SetTop(PlayheadLine, StripTop - 4);
        UpdatePlayhead();
        BuildRuler(w);
    }

    static void Place(FrameworkElement e, double x, double y, double w, double h)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        e.Width = Math.Max(0, w);
        e.Height = Math.Max(0, h);
    }

    void UpdatePlayhead()
    {
        if (!_ready) return;
        Canvas.SetLeft(Playhead, X(_pos));
        NowText.Text = Fmt(_pos);
        bool partial = _in > TimeSpan.Zero || _out < _dur;
        double inCut = partial && _pos >= _in && _pos < _out ? 1 : 0;
        if (InCutBadge.Tag is not double shown || shown != inCut)
        {
            InCutBadge.Tag = inCut;
            InCutBadge.BeginAnimation(OpacityProperty, new DoubleAnimation(inCut, TimeSpan.FromMilliseconds(160)));
        }
    }

    static readonly WpfBrush RulerText = Frozen(0x6C, 0x66, 0x80);
    static readonly WpfBrush RulerTick = Frozen(0x3A, 0x34, 0x47);

    static WpfBrush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    void BuildRuler(double w)
    {
        Ruler.Children.Clear();
        double secs = _dur.TotalSeconds;
        if (secs <= 0) return;
        double[] steps = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300];
        double step = steps.FirstOrDefault(s => s / secs * w >= 64, 600);
        for (double t = 0; t <= secs + 1e-6; t += step)
        {
            double x = t / secs * w;
            Ruler.Children.Add(new System.Windows.Shapes.Rectangle { Width = 1, Height = 5, Fill = RulerTick });
            Canvas.SetLeft(Ruler.Children[^1], x);
            Canvas.SetTop(Ruler.Children[^1], 13);
            if (x > w - 24) continue;   // the last label would hang over the edge
            var label = new TextBlock { Text = step < 1 ? $"{(int)(t / 60)}:{t % 60:00.0}" : $"{(int)(t / 60)}:{(int)(t % 60):00}", FontSize = 10, Foreground = RulerText };
            Ruler.Children.Add(label);
            Canvas.SetLeft(label, x + 3);
            Canvas.SetTop(label, -1);
        }
    }

    void Timeline_Down(object sender, MouseButtonEventArgs e)
    {
        if (!_ready || _saving) return;
        double x = e.GetPosition(Track).X;
        var src = e.OriginalSource as DependencyObject;
        if (src != null && (InHandle == src || InHandle.IsAncestorOf(src))) { _drag = Drag.In; _grabOffset = X(_in) - x; }
        else if (src != null && (OutHandle == src || OutHandle.IsAncestorOf(src))) { _drag = Drag.Out; _grabOffset = X(_out) - x; }
        else { _drag = Drag.Seek; _grabOffset = 0; }
        Pause();
        TimelineHost.CaptureMouse();
        Timeline_Move(sender, e);
        e.Handled = true;
    }

    void Timeline_Move(object sender, MouseEventArgs e)
    {
        if (_drag == Drag.None || e.LeftButton != MouseButtonState.Pressed) return;
        var t = Snap(TimeAt(e.GetPosition(Track).X + _grabOffset));
        switch (_drag)
        {
            case Drag.Seek: Seek(t); break;
            case Drag.In:
                _in = Clamp(t, TimeSpan.Zero, _out - _frame);
                SelectionChanged();
                Seek(_in);
                break;
            case Drag.Out:
                _out = Clamp(t, _in + _frame, _dur);
                SelectionChanged();
                Seek(_out - _frame);
                break;
        }
    }

    void Timeline_Up(object sender, MouseButtonEventArgs e)
    {
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        TimelineHost.ReleaseMouseCapture();
    }

    // ================================================================ keyboard

    void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;   // typing a time
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: Step(-1, shift); break;
            case Key.Right: Step(1, shift); break;
            case Key.Home: Pause(); Seek(_in); break;
            case Key.End: Pause(); Seek(_out - _frame); break;
            case Key.I: SetIn(Snap(_pos)); break;
            case Key.O: SetOut(Snap(_pos) + _frame); break;
            case Key.L: LoopToggle.IsChecked = LoopToggle.IsChecked != true; break;
            case Key.M: MuteToggle.IsChecked = MuteToggle.IsChecked != true; break;
            case Key.S when ctrl: Save(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ================================================================ saving

    void Save_Click(object sender, RoutedEventArgs e) => Save();

    async void Save()
    {
        if (!_ready || _saving || _out - _in < _frame) return;
        Pause();
        _saving = true;
        SaveBtn.IsEnabled = false;
        SaveIdle.Visibility = Visibility.Collapsed;
        SaveBusy.Visibility = Visibility.Visible;
        SaveBusy.Text = "Saving…";
        ProgressTrack.Visibility = Visibility.Visible;
        ProgressFill.Width = 0;
        DoneRow.Visibility = Visibility.Collapsed;
        DestRow.Visibility = Visibility.Visible;

        string dest = Trimmer.OutputPath(Trimmer.EditedFolder(_s.SaveFolder), _path, _in, _out);
        var cts = _saveCts = new CancellationTokenSource();
        var progress = new Progress<double>(p =>
        {
            SaveBusy.Text = $"Saving… {p * 100:0}%";
            ProgressFill.Width = ProgressTrack.ActualWidth * p;
        });
        try
        {
            await Trimmer.TrimAsync(_path, dest, _in, _out, progress, cts.Token);
            _savedPath = dest;
            DoneText.Text = $"{System.IO.Path.GetFileName(dest)}  ·  {new FileInfo(dest).Length / 1048576.0:0.0} MB";
            DestRow.Visibility = Visibility.Collapsed;
            DoneRow.Visibility = Visibility.Visible;
            DoneRow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Trim failed", ex);
            Toast.Show($"Could not save the cut: {ex.GetBaseException().Message}", true);
        }
        finally
        {
            _saving = false;
            SaveBtn.IsEnabled = true;
            SaveIdle.Visibility = Visibility.Visible;
            SaveBusy.Visibility = Visibility.Collapsed;
            ProgressTrack.Visibility = Visibility.Collapsed;
        }
    }

    void PlaySaved_Click(object sender, RoutedEventArgs e)
    {
        if (_savedPath == null || !File.Exists(_savedPath)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_savedPath) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Could not open clip: {ex.Message}"); }
    }

    void ShowSaved_Click(object sender, RoutedEventArgs e)
    {
        if (_savedPath == null || !File.Exists(_savedPath)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{_savedPath}\"") { UseShellExecute = true });
    }

    void OpenEdited_Click(object sender, RoutedEventArgs e) => Folders.Open(Trimmer.EditedFolder(_s.SaveFolder));
    void OpenOther_Click(object sender, RoutedEventArgs e) => PickAndOpen(_s, this);

    // ================================================================ window chrome

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MINMAXINFO { public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    /// <summary>A borderless window would cover the taskbar when maximized: limit it to the work area.</summary>
    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        var mon = MonitorFromWindow(hwnd, 2);
        var mi = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return IntPtr.Zero;
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.MaxPosition = new POINT { X = mi.Work.Left - mi.Monitor.Left, Y = mi.Work.Top - mi.Monitor.Top };
        mmi.MaxSize = new POINT { X = mi.Work.Right - mi.Work.Left, Y = mi.Work.Bottom - mi.Work.Top };
        Marshal.StructureToPtr(mmi, lParam, true);
        return IntPtr.Zero;
    }
}
