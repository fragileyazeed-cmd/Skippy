using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.MediaFoundation;

namespace Skippy.Core;

/// <summary>
/// `--selftest-fullscreen`: opens a real DXGI exclusive-fullscreen swap chain at 1920x1080 on the primary
/// monitor (which forces a display mode change), shows solid red/green/blue/white, leaves fullscreen
/// (mode restored), then checks the saved clip contains those colours. Covers exclusive fullscreen,
/// resolution changes mid-buffer and that the pipeline survives both.
/// </summary>
public static class FullscreenTest
{
    // expected luma of solid colours in BT.709 limited range
    static readonly (string name, Color4 color, int y)[] Colors =
    [
        ("red", new Color4(1, 0, 0, 1), 63),
        ("green", new Color4(0, 1, 0, 1), 173),
        ("blue", new Color4(0, 0, 1, 1), 32),
        ("white", new Color4(1, 1, 1, 1), 235),
    ];

    public static int Run(StringBuilder report)
    {
        var settings = Settings.Load().Clone();
        settings.ClipSeconds = 30;
        settings.MonitorDevice = null;
        settings.SaveFolder = Path.Combine(Log.Dir, "selftest");
        using var engine = new ReplayEngine(settings);
        engine.Notify += (m, e) => report.AppendLine($"notify: {m}");
        engine.Start();
        Thread.Sleep(4000);
        var before = engine.Pipeline;

        var marks = new List<(string name, int y, long t)>();
        string? fsError = null;
        var t = new Thread(() =>
        {
            try { ShowFullscreen(marks, report); }
            catch (Exception ex) { fsError = ex.Message; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (fsError != null) report.AppendLine($"fullscreen window error: {fsError}");
        Thread.Sleep(4000);

        report.AppendLine($"pipeline restarted during test: {(engine.Pipeline != before ? "yes" : "no")}");
        report.AppendLine($"captured {engine.Pipeline?.FramesCaptured}, encoded {engine.Pipeline?.FramesEncoded}, dropped {engine.Pipeline?.FramesDropped}");
        var path = engine.SaveClipAsync().GetAwaiter().GetResult();
        long t0 = engine.LastClipStart;
        report.AppendLine($"saved {path}");

        var frames = Luma(path, 400, 680);
        int pass = 0;
        foreach (var (name, y, mt) in marks)
        {
            double s = (mt - t0) / 1e7;
            var ys = frames.Where(f => f.t >= s + 0.5 && f.t <= s + 1.5).Select(f => f.y).OrderBy(v => v).ToList();
            double median = ys.Count > 0 ? ys[ys.Count / 2] : double.NaN;
            bool ok = Math.Abs(median - y) <= 15;
            if (ok) pass++;
            report.AppendLine($"{name,-6} at {s,6:0.00}s: {ys.Count} frames, median luma {median:0} (expected {y}) {(ok ? "OK" : "WRONG")}");
        }
        bool result = marks.Count == Colors.Length && pass == Colors.Length;
        report.AppendLine(result ? "RESULT: PASS" : "RESULT: FAIL");
        return result ? 0 : 1;
    }

    static void ShowFullscreen(List<(string, int, long)> marks, StringBuilder report)
    {
        var mon = Monitors.Primary()!;
        using var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(mon.X, mon.Y, mon.Width, mon.Height),
            TopMost = true,
            Text = "Skippy fullscreen test",
            BackColor = System.Drawing.Color.Black,
        };
        form.Show();
        form.Activate();
        Application.DoEvents();

        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0], out ID3D11Device dev, out ID3D11DeviceContext ctx).CheckError();
        using (dev)
        using (ctx)
        using (var dxgiDev = dev.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgiDev.GetAdapter())
        using (var factory = adapter.GetParent<IDXGIFactory2>())
        {
            var desc = new SwapChainDescription1
            {
                Width = 1920, Height = 1080, Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput, BufferCount = 2, SwapEffect = SwapEffect.FlipDiscard, Scaling = Scaling.Stretch,
            };
            var fs = new SwapChainFullscreenDescription { RefreshRate = new Rational(60, 1), Windowed = true };
            using var sc = factory.CreateSwapChainForHwnd(dev, form.Handle, desc, fs, null);
            var r = sc.SetFullscreenState(true, null);
            sc.GetFullscreenState(out var isFs, out _);
            report.AppendLine($"exclusive fullscreen: SetFullscreenState={r}, fullscreen={isFs}");
            sc.ResizeBuffers(2, 1920, 1080, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
            Thread.Sleep(1500);   // let the monitor finish the mode switch

            foreach (var (name, color, y) in Colors)
            {
                marks.Add((name, y, Clock.Now));
                var end = DateTime.Now.AddSeconds(2);
                while (DateTime.Now < end)
                {
                    using (var bb = sc.GetBuffer<ID3D11Texture2D>(0))
                    using (var rtv = dev.CreateRenderTargetView(bb))
                        ctx.ClearRenderTargetView(rtv, color);
                    sc.Present(1, PresentFlags.None);
                    Application.DoEvents();
                }
            }
            sc.SetFullscreenState(false, null);
        }
        form.Close();
    }

    static List<(double t, double y)> Luma(string path, int from, int to)
    {
        var res = new List<(double, double)>();
        using var attrs = MediaFactory.MFCreateAttributes(1);
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, attrs);
        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
        using (var mt = MediaFactory.MFCreateMediaType())
        {
            mt.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            mt.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, mt);
        }
        uint w;
        using (var cur = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
            MediaFactory.MFGetAttributeSize(cur, MediaTypeAttributeKeys.FrameSize, out w, out _);
        while (true)
        {
            using var s = reader.ReadSample(SourceReaderIndex.FirstVideoStream, 0, out _, out var flags, out var ts);
            if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
            if (s == null) continue;
            using var buf = s.ConvertToContiguousBuffer();
            buf.Lock(out var p, out _, out _);
            long sum = 0; int n = 0;
            unsafe
            {
                byte* yp = (byte*)p;
                for (int r = from; r < to; r += 4)
                    for (int c = from; c < to; c += 4) { sum += yp[r * (int)w + c]; n++; }
            }
            buf.Unlock();
            res.Add((ts / 1e7, sum / (double)Math.Max(1, n)));
        }
        return res;
    }
}
