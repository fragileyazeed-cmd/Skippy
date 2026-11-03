using System.Diagnostics;
using System.Text;
using Vortice.MediaFoundation;

namespace Skippy.Core;

/// <summary>
/// Headless check used during development: `Skippy.exe --selftest [seconds] [--software]`.
/// Buffers for a while, saves a clip, decodes it back with Media Foundation and writes a report to
/// %AppData%\Skippy\selftest.txt. Exit code 0 = clip saved and fully decodable.
/// </summary>
public static class SelfTest
{
    public static int Run(string[] args)
    {
        int seconds = 12;
        foreach (var a in args) if (int.TryParse(a, out var n)) seconds = n;
        bool software = args.Contains("--software");
        var report = new StringBuilder();
        void R(string s) { report.AppendLine(s); Log.Info("[selftest] " + s); }

        int code = 1;
        try
        {
            var settings = Settings.Load().Clone();
            settings.ClipSeconds = 30;
            settings.SaveFolder = Path.Combine(Log.Dir, "selftest");
            using var engine = new ReplayEngine(settings, software);
            engine.Notify += (m, e) => R($"notify: {m}");

            var proc = Process.GetCurrentProcess();
            var cpu0 = proc.TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            engine.Start();
            Thread.Sleep(seconds * 1000);
            var elapsed = sw.Elapsed.TotalSeconds;
            proc.Refresh();
            var cpu = (proc.TotalProcessorTime - cpu0).TotalSeconds;

            var p = engine.Pipeline;
            R($"monitor: {p?.Monitor.DisplayName}");
            R($"format: {p?.Format?.Width}x{p?.Format?.Height}@{p?.Format?.Fps} {p?.Format?.Bitrate / 1e6:0.#} Mbps, encoder: {p?.Format?.EncoderName}");
            R($"captured frames: {p?.FramesCaptured} ({p?.FramesCaptured / elapsed:0.0}/s), encoded: {p?.FramesEncoded} ({p?.FramesEncoded / elapsed:0.0}/s), dropped at encoder: {p?.FramesDropped}, skipped ticks: {p?.FramesSkipped}");
            R($"mic devices: {string.Join(" | ", Audio.AudioDevices.List(NAudio.CoreAudioApi.DataFlow.Capture).Select(d => d.Name))}");
            R($"output devices: {string.Join(" | ", Audio.AudioDevices.List(NAudio.CoreAudioApi.DataFlow.Render).Select(d => d.Name))}");
            R($"mic: {engine.Audio?.Describe(true)}");
            R($"output: {engine.Audio?.Describe(false)}");
            R($"audio buffer: {engine.AudioBufferBytes / 1024.0:0} KB");
            R($"buffer: {engine.VideoPackets} packets, {engine.VideoBufferBytes / 1048576.0:0.0} MB ({engine.VideoBufferBytes * 8 / 1e6 / Math.Max(1, elapsed - 1):0.0} Mbit/s avg)");
            R($"process CPU: {cpu / elapsed / Environment.ProcessorCount * 100:0.00}% of all cores ({cpu:0.00}s over {elapsed:0.0}s), private memory {proc.PrivateMemorySize64 / 1048576.0:0} MB");

            var saveSw = Stopwatch.StartNew();
            var path = engine.SaveClipAsync().GetAwaiter().GetResult();
            R($"saved: {path} in {saveSw.ElapsedMilliseconds} ms, {new FileInfo(path).Length / 1048576.0:0.00} MB");

            var (frames, firstTs, lastTs, w, h) = Decode(path);
            R($"decoded: {frames} frames, {w}x{h}, timestamps {firstTs / 1e7:0.000}s .. {lastTs / 1e7:0.000}s");
            double expectFrames = (lastTs - firstTs) / 1e7 * (p?.Format?.Fps ?? 60);
            code = frames > 0 && frames >= expectFrames * 0.95 ? 0 : 2;
            R(code == 0 ? "RESULT: PASS" : "RESULT: FAIL (fewer decoded frames than expected)");
        }
        catch (Exception ex)
        {
            R($"RESULT: FAIL {ex}");
        }
        File.WriteAllText(Path.Combine(Log.Dir, "selftest.txt"), report.ToString());
        return code;
    }

    /// <summary>
    /// `--soak seconds [out.txt]`: runs the engine with a 3-minute buffer for a long time, logging memory,
    /// handles, threads and CPU every minute, then saves a full 3-minute clip and decodes it.
    /// </summary>
    public static int Soak(string[] args)
    {
        int seconds = args.Skip(1).Select(a => int.TryParse(a, out var n) ? n : 0).FirstOrDefault(n => n > 0, 1800);
        string outPath = args.FirstOrDefault(a => a.EndsWith(".txt")) ?? Path.Combine(Log.Dir, "soak.txt");
        var lines = new List<string>();
        void W(string s) { lines.Add(s); Log.Info("[soak] " + s); File.WriteAllLines(outPath, lines); }

        var settings = Settings.Load().Clone();
        settings.ClipSeconds = 180;
        settings.SaveFolder = Path.Combine(Log.Dir, "selftest");
        using var engine = new ReplayEngine(settings);
        engine.Notify += (m, e) => W($"notify: {m}");
        engine.Start();
        var proc = Process.GetCurrentProcess();
        var sw = Stopwatch.StartNew();
        var lastCpu = proc.TotalProcessorTime;
        var lastT = sw.Elapsed;
        W("min  workingMB  privateMB  managedMB  gcCommitMB  gcFragMB  nativeMB  handles  threads  videoMB  audioKB  buffered  encoded  dropped  cpu%");
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Thread.Sleep(60_000);
            proc.Refresh();
            var cpu = proc.TotalProcessorTime;
            double pct = (cpu - lastCpu).TotalSeconds / (sw.Elapsed - lastT).TotalSeconds / Environment.ProcessorCount * 100;
            lastCpu = cpu; lastT = sw.Elapsed;
            var p = engine.Pipeline;
            var gc = GC.GetGCMemoryInfo();
            double commit = gc.TotalCommittedBytes / 1048576.0, frag = gc.FragmentedBytes / 1048576.0;
            double native = proc.PrivateMemorySize64 / 1048576.0 - commit;   // everything that isn't the GC heap
            W($"{sw.Elapsed.TotalMinutes,3:0}  {proc.WorkingSet64 / 1048576.0,9:0}  {proc.PrivateMemorySize64 / 1048576.0,9:0}  {GC.GetTotalMemory(false) / 1048576.0,9:0}  {commit,10:0}  {frag,8:0}  {native,8:0}  {proc.HandleCount,7}  {proc.Threads.Count,7}  {engine.VideoBufferBytes / 1048576.0,7:0}  {engine.AudioBufferBytes / 1024.0,7:0}  {engine.BufferedSeconds,7:0}s  {p?.FramesEncoded,7}  {p?.FramesDropped,7}  {pct,5:0.00}");
        }
        var path = engine.SaveClipAsync().GetAwaiter().GetResult();
        var (frames, first, last, w, h) = Decode(path);
        W($"saved {path}: {new FileInfo(path).Length / 1048576.0:0} MB, decoded {frames} frames {w}x{h}, {first / 1e7:0.00}..{last / 1e7:0.00}s");
        W(frames > 60 * 170 * (settings.Fps / 60.0) * 0.95 ? "RESULT: PASS" : "RESULT: FAIL");
        return 0;
    }

    /// <summary>Decodes every video frame of the file (proves it is playable, not just parseable).</summary>
    internal static (int frames, long first, long last, uint w, uint h) Decode(string path)
    {
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
        uint w = 0, h = 0;
        using (var cur = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
            MediaFactory.MFGetAttributeSize(cur, MediaTypeAttributeKeys.FrameSize, out w, out h);

        int frames = 0; long first = -1, last = 0;
        while (true)
        {
            using var s = reader.ReadSample(SourceReaderIndex.FirstVideoStream, 0, out _, out var flags, out var ts);
            if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
            if ((flags & SourceReaderFlag.Error) != 0) throw new InvalidOperationException("decoder error");
            if (s == null) continue;
            frames++;
            if (first < 0) first = ts;
            last = ts;
        }
        return (frames, first, last, w, h);
    }
}
