using System.Text;
using Skippy.Audio;
using Skippy.Media;
using Vortice.MediaFoundation;

namespace Skippy.Core;

/// <summary>Development checks for the audio path (run from a terminal, results in %AppData%\Skippy\selftest.txt).</summary>
public static class SyncTest
{
    /// <summary>
    /// `--selftest-aac`: encodes a click at a known sample, muxes it, decodes it back and reports where the
    /// click came out. The difference is the AAC encoder's priming delay that the pipeline compensates.
    /// </summary>
    public static int MeasureAacDelay(StringBuilder report)
    {
        const int clickAt = 48_000 + 300;
        var frames = new List<byte[]>();
        using (var enc = new AacEncoder(f => frames.Add(f.ToArray())))
        {
            var block = new float[AacEncoder.FrameSamples * 2];
            for (int k = 0; k < 150; k++)
            {
                Array.Clear(block);
                for (int i = 0; i < AacEncoder.FrameSamples; i++)
                {
                    int n = k * AacEncoder.FrameSamples + i;
                    if (n >= clickAt && n < clickAt + 48) block[i * 2] = block[i * 2 + 1] = (n - clickAt) % 2 == 0 ? 0.9f : -0.9f;
                }
                enc.Encode(block, AudioMixer.FrameToHns((long)k * AacEncoder.FrameSamples));
            }
        }
        var buf = new PacketBuffer { RetentionHns = long.MaxValue };
        for (int k = 0; k < frames.Count; k++) buf.Append(frames[k], AudioMixer.FrameToHns((long)k * 1024), AudioMixer.FrameToHns(1024), true);
        using var snap = buf.TakeSnapshot();
        string path = Path.Combine(Log.Dir, "selftest", "aac-delay.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Mp4Writer.Write(path, [new Mp4Track
        {
            IsVideo = false, Timescale = 48_000, Samples = snap.Packets,
            Times = Enumerable.Range(0, snap.Packets.Length).Select(i => (long)i * 1024).ToArray(),
            LastDuration = 1024, Channels = 2, SampleRate = 48_000, AudioSpecificConfig = AacEncoder.AudioSpecificConfig, Bitrate = AacEncoder.Bitrate,
        }]);

        var pcm = DecodeAudio(path, out long firstTs);
        int found = Onsets(pcm, 0.3f).FirstOrDefault(-1);
        long foundAbs = found + AudioMixer.HnsToFrame(firstTs);
        int delay = (int)(foundAbs - clickAt);
        report.AppendLine($"AAC: {frames.Count} frames for 150 input blocks; click at sample {clickAt} decoded at {foundAbs} => encoder delay {delay} samples ({delay / 48.0:0.0} ms)");
        return delay;
    }

    /// <summary>
    /// `--selftest-sync [seconds]`: flashes a white square in the top-left corner of the primary monitor and
    /// plays a beep at the same moment every 10 s, saves a clip and measures audio-vs-video offset for each
    /// flash. The spread between first and last offset shows drift over the clip.
    /// </summary>
    public static int Run(string[] args, StringBuilder report)
    {
        int seconds = 40;
        foreach (var a in args) if (int.TryParse(a, out var n)) seconds = n;
        var mon = Monitors.Primary()!;
        var settings = Settings.Load().Clone();
        settings.ClipSeconds = seconds <= 30 ? 30 : seconds <= 60 ? 60 : seconds <= 120 ? 120 : 180;
        settings.SaveFolder = Path.Combine(Log.Dir, "selftest");
        settings.MonitorDevice = mon.DeviceName;

        using var flash = new FlashWindow(mon.X, mon.Y, 240);
        var beep = MakeBeep();
        using var player = new System.Media.SoundPlayer(new MemoryStream(beep));
        player.Load();

        using var engine = new ReplayEngine(settings);
        engine.Notify += (m, e) => report.AppendLine($"notify: {m}");
        engine.Start();
        Thread.Sleep(4000);
        var start = DateTime.Now;
        var triggers = new List<long>();   // Clock time of each flash+beep
        while ((DateTime.Now - start).TotalSeconds < seconds - 3)
        {
            flash.Set(true);
            triggers.Add(Clock.Now);
            player.Play();
            Thread.Sleep(250);
            flash.Set(false);
            Thread.Sleep(9750);
        }
        Thread.Sleep(2000);
        report.AppendLine($"mic: {engine.Audio?.Describe(true)}");
        report.AppendLine($"output: {engine.Audio?.Describe(false)}");
        var path = engine.SaveClipAsync().GetAwaiter().GetResult();
        long t0 = engine.LastClipStart;
        report.AppendLine($"saved {path}, {triggers.Count} flash+beep events, monitor {mon.DisplayName}");

        var fmt = engine.Pipeline!.Format!;
        double scale = fmt.Width / (double)mon.Width;
        var bright = VideoBrightness(path, (int)(40 * scale), (int)(200 * scale));
        var pcm = DecodeAudio(path, out long audioFirst);

        // Only look right after each trigger: anything else happening on the PC can't be mistaken for an event.
        var rows = new List<(double t, double video, double audio)>();
        report.AppendLine("event  time(s)  video latency(ms)  audio latency(ms)  audio-video(ms)");
        foreach (var trig in triggers)
        {
            double t = (trig - t0) / 1e7;
            if (t < 0) continue;
            double v = bright.Where(b => b.t >= t - 0.02 && b.t <= t + 0.4 && b.y > 128).Select(b => b.t).FirstOrDefault(double.NaN);
            double a = BeepOnset(pcm, audioFirst / 1e7, t);   // decoder timestamps are already clip-relative
            double vl = (v - t) * 1000, al = (a - t) * 1000;
            report.AppendLine($"{rows.Count + 1,5}  {t,7:0.00}  {(double.IsNaN(v) ? "   (not seen)" : $"{vl,13:0}")}  {(double.IsNaN(a) ? "   (not heard)" : $"{al,13:0}")}  {(double.IsNaN(v) || double.IsNaN(a) ? "" : $"{al - vl,11:0}")}");
            rows.Add((t, vl, al));
        }

        var heard = rows.Where(r => !double.IsNaN(r.audio)).ToList();
        var both = rows.Where(r => !double.IsNaN(r.audio) && !double.IsNaN(r.video)).ToList();
        if (heard.Count < 3)
        {
            report.AppendLine("RESULT: FAIL too few beeps found in the clip (output muted?)");
            return 2;
        }
        // drift = how the audio path's latency changes across the clip (should stay flat)
        double audioDrift = Slope(heard.Select(r => (r.t, r.audio)).ToList()) * (heard[^1].t - heard[0].t);
        report.AppendLine($"audio latency first/last: {heard[0].audio:0} / {heard[^1].audio:0} ms, trend over {heard[^1].t - heard[0].t:0} s: {audioDrift:+0;-0} ms");
        if (both.Count > 0)
            report.AppendLine($"audio minus video: mean {both.Average(r => r.audio - r.video):0} ms over {both.Count} events (includes the test beep's own playback start latency)");
        bool pass = Math.Abs(audioDrift) <= 20 && (both.Count == 0 || Math.Abs(both.Average(r => r.audio - r.video)) < 120);
        report.AppendLine(pass ? "RESULT: PASS" : "RESULT: FAIL");
        return pass ? 0 : 1;
    }

    /// <summary>`--analyze-audio file.mp4 firstTriggerSeconds`: prints 1 kHz energy around each expected beep (debug aid).</summary>
    public static void AnalyzeAudio(string path, double first, StringBuilder report)
    {
        var pcm = DecodeAudio(path, out long audioFirst);
        double start = audioFirst / 1e7;
        report.AppendLine($"audio starts at {start:0.000}s, {pcm.Length / 48000.0:0.00}s long, global onsets: {string.Join(", ", Onsets(pcm, 0.25f).Select(i => (start + i / 48000.0).ToString("0.000")))}");
        for (double t = first; t < start + pcm.Length / 48000.0; t += 10)
        {
            var sb = new StringBuilder();
            for (double s = t - 0.1; s < t + 0.6; s += 0.02)
            {
                int i = (int)((s - start) * 48000);
                double peak = 0;
                for (int k = 0; k < 960 && i + k < pcm.Length; k++) if (i + k >= 0) peak = Math.Max(peak, Math.Abs(pcm[i + k]));
                sb.Append($"{peak:0.00} ");
            }
            report.AppendLine($"t={t:0.00}: beep onset {(BeepOnset(pcm, start, t) - t) * 1000:0.0} ms | peaks per 20ms from t-0.1: {sb}");
        }
    }

    static double Slope(List<(double x, double y)> pts)
    {
        double mx = pts.Average(p => p.x), my = pts.Average(p => p.y);
        double num = pts.Sum(p => (p.x - mx) * (p.y - my)), den = pts.Sum(p => (p.x - mx) * (p.x - mx));
        return den == 0 ? 0 : num / den;
    }

    /// <summary>
    /// Start (clip seconds) of the test beep near t: first 10 ms window whose 1 kHz energy reaches a quarter of
    /// what the beep alone produces. A full-scale 1 kHz tone is rare in music/voice, so other sound doesn't fool it.
    /// The crossing happens when the beep covers half the window, hence the half-window correction.
    /// </summary>
    static double BeepOnset(float[] pcm, double audioStart, double t)
    {
        const int win = 480; // 10 ms = 10 cycles of 1 kHz
        const double beepAmp = 20000 / 32768.0;
        double full = Math.Pow(win * beepAmp / 2, 2);
        int Idx(double s) => (int)Math.Round((s - audioStart) * 48_000);
        double Energy(int i)
        {
            if (i < 0 || i + win > pcm.Length) return 0;
            double w = 2 * Math.PI * 1000 / 48_000, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
            for (int k = 0; k < win; k++) { double s0 = pcm[i + k] + c * s1 - s2; s2 = s1; s1 = s0; }
            return s1 * s1 + s2 * s2 - c * s1 * s2;
        }
        for (int i = Idx(t - 0.1); i < Idx(t + 0.5); i += 24)   // 0.5 ms steps
            if (Energy(i) > full * 0.25) return audioStart + (i + win / 2) / 48_000.0;
        return double.NaN;
    }

    static byte[] MakeBeep()
    {
        const int rate = 48_000, n = rate * 150 / 1000;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(n * 2);
        for (int i = 0; i < n; i++) w.Write((short)(Math.Sin(2 * Math.PI * 1000 * i / rate) * 20000));
        return ms.ToArray();
    }

    /// <summary>Average brightness of the test square for every frame (clip seconds, luma).</summary>
    static List<(double t, long y)> VideoBrightness(string path, int from, int to)
    {
        var res = new List<(double, long)>();
        using var attrs = MediaFactory.MFCreateAttributes(1);
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, attrs);
        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
        using (var t = MediaFactory.MFCreateMediaType())
        {
            t.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            t.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, t);
        }
        uint w, h;
        using (var cur = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
            MediaFactory.MFGetAttributeSize(cur, MediaTypeAttributeKeys.FrameSize, out w, out h);
        while (true)
        {
            using var s = reader.ReadSample(SourceReaderIndex.FirstVideoStream, 0, out _, out var flags, out var ts);
            if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
            if (s == null) continue;
            using var buf = s.ConvertToContiguousBuffer();
            buf.Lock(out var p, out _, out _);
            long sum = 0; int cnt = 0;
            unsafe
            {
                byte* y = (byte*)p;
                int stride = (int)w;
                for (int r = from; r < to; r += 2)
                    for (int c = from; c < to; c += 2) { sum += y[r * stride + c]; cnt++; }
            }
            buf.Unlock();
            res.Add((ts / 1e7, sum / Math.Max(1, cnt)));
        }
        return res;
    }

    static float[] DecodeAudio(string path, out long firstTs)
    {
        using var attrs = MediaFactory.MFCreateAttributes(1);
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, attrs);
        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);
        using (var t = MediaFactory.MFCreateMediaType())
        {
            t.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            t.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Float);
            t.Set(MediaTypeAttributeKeys.AudioNumChannels, 1u);
            t.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, 48000u);
            t.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 32u);
            t.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 4u);
            t.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 192000u);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, t);
        }
        var all = new List<float>();
        firstTs = -1;
        while (true)
        {
            using var s = reader.ReadSample(SourceReaderIndex.FirstAudioStream, 0, out _, out var flags, out var ts);
            if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
            if (s == null) continue;
            if (firstTs < 0) firstTs = ts;
            using var buf = s.ConvertToContiguousBuffer();
            buf.Lock(out var p, out _, out var len);
            unsafe { all.AddRange(new ReadOnlySpan<float>((void*)p, len / 4)); }
            buf.Unlock();
        }
        return all.ToArray();
    }

    /// <summary>Sample indices where loud sound starts after ≥200 ms of relative quiet.</summary>
    static List<int> Onsets(float[] pcm, float relThreshold)
    {
        float max = 0;
        foreach (var v in pcm) max = Math.Max(max, Math.Abs(v));
        var res = new List<int>();
        if (max < 0.01f) return res;
        float thr = max * relThreshold;
        int quiet = 0;
        for (int i = 0; i < pcm.Length; i++)
        {
            if (Math.Abs(pcm[i]) >= thr)
            {
                if (quiet >= 48_000 / 5) res.Add(i);
                quiet = 0;
            }
            else quiet++;
        }
        return res;
    }

    sealed class FlashWindow : IDisposable
    {
        Form? _form;
        readonly Thread _t;
        readonly ManualResetEventSlim _ready = new();

        public FlashWindow(int x, int y, int size)
        {
            _t = new Thread(() =>
            {
                _form = new Form
                {
                    FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false,
                    TopMost = true, BackColor = Color.Black, Bounds = new Rectangle(x, y, size, size),
                };
                _form.Shown += (_, _) => _ready.Set();
                Application.Run(_form);
            });
            _t.SetApartmentState(ApartmentState.STA);
            _t.IsBackground = true;
            _t.Start();
            _ready.Wait(5000);
        }

        public void Set(bool white) => _form?.Invoke(() =>
        {
            _form.TopMost = true;   // re-assert in case another topmost window came up
            _form.BackColor = white ? Color.White : Color.Black;
            _form.Refresh();
        });

        public void Dispose()
        {
            try { _form?.Invoke(() => _form.Close()); } catch { }
            _t.Join(2000);
        }
    }
}
