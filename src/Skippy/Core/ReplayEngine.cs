using Skippy.Audio;
using Skippy.Media;
using Skippy.Video;

namespace Skippy.Core;

/// <summary>
/// Owns the always-on capture pipeline and the RAM buffers. Applies settings live, restarts the
/// pipeline after GPU/display hiccups, and writes clips without disturbing the running buffer.
/// </summary>
public sealed class ReplayEngine : IDisposable
{
    const long Hns = 10_000_000;
    const int GopSlackSeconds = 3;

    readonly object _gate = new();
    readonly PacketBuffer _video = new();
    readonly PacketBuffer _audio = new();
    Settings _settings;
    VideoPipeline? _pipeline;
    AudioPipeline? _audioPipeline;
    System.Threading.Timer? _retry;
    bool _disposed;
    bool _forceSoftware;
    int _saving;

    /// <summary>User-facing message (shown as a toast by the UI layer).</summary>
    public event Action<string, bool>? Notify;

    public ReplayEngine(Settings settings, bool forceSoftwareEncoder = false)
    {
        _settings = settings.Clone();
        _forceSoftware = forceSoftwareEncoder;
        ApplyRetention();
        _watchdog = new System.Threading.Timer(_ => Watchdog(), null, 5000, 2000);
    }

    readonly System.Threading.Timer _watchdog;
    long _wdEncoded = -1;
    int _wdStalls;

    /// <summary>Seconds of video currently held (what a save right now would contain, up to the clip length).</summary>
    public double BufferedSeconds => Math.Min(_video.SpanHns / (double)Hns, _settings.ClipSeconds);
    public string? LastClipPath { get; private set; }
    public DateTime LastClipTime { get; private set; }
    public event Action<string>? ClipSaved;

    /// <summary>
    /// If the encoder stops producing frames (driver reset, capture silently dead) rebuild the pipeline.
    /// A static screen still produces frames (the last image is re-encoded), so a stall always means trouble.
    /// </summary>
    void Watchdog()
    {
        VideoPipeline? p;
        lock (_gate) p = _pipeline;
        if (p == null || IsPaused) { _wdEncoded = -1; _wdStalls = 0; return; }
        long enc = p.FramesEncoded;
        if (enc == _wdEncoded) _wdStalls++;
        else _wdStalls = 0;
        _wdEncoded = enc;
        if (_wdStalls >= 3)   // ~6 s without a single encoded frame
        {
            _wdStalls = 0;
            _wdEncoded = -1;
            OnPipelineProblem(p, $"Watchdog: no frames encoded for 6 s (captured {p.FramesCaptured}, encoded {enc})");
        }
    }

    /// <summary>After sleep/hibernate: devices and GPU state may be stale, so rebuild everything cleanly.</summary>
    public void OnPowerResume()
    {
        lock (_gate)
        {
            if (IsPaused || _disposed) return;
            Log.Info("Resumed from sleep: restarting capture");
            StopVideo();
            _audioPipeline?.Dispose();
            _audioPipeline = null;
            _video.Clear();
            _audio.Clear();
            StartVideo();
            StartAudio();
        }
    }

    public VideoPipeline? Pipeline => _pipeline;
    public long VideoBufferBytes => _video.LiveBytes;
    public int VideoPackets => _video.Count;
    public long AudioBufferBytes => _audio.LiveBytes;
    public AudioPipeline? Audio => _audioPipeline;
    /// <summary>Clock time of the first video frame of the most recently saved clip.</summary>
    public long LastClipStart { get; private set; }

    public bool IsPaused { get; private set; }

    public void Start()
    {
        lock (_gate)
        {
            IsPaused = false;
            StartVideo();
            StartAudio();
        }
    }

    /// <summary>Stops capturing entirely (GPU, encoder and audio devices released) and drops the buffer.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (IsPaused) return;
            IsPaused = true;
            _retry?.Dispose();
            StopVideo();
            _audioPipeline?.Dispose();
            _audioPipeline = null;
            _video.Clear();
            _audio.Clear();
            Log.Info("Paused");
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!IsPaused) return;
            Log.Info("Resumed");
        }
        Start();
    }

    /// <summary>
    /// Called when monitors are added/removed/changed: if the user's chosen monitor came back (or the one we
    /// record disappeared), move capture to the right one. Resolution changes alone don't restart anything.
    /// </summary>
    public void OnDisplaysChanged()
    {
        lock (_gate)
        {
            if (IsPaused || _disposed) return;
            var want = Monitors.Find(_settings.MonitorDevice) ?? Monitors.Primary();
            var cur = _pipeline?.Monitor;
            if (want == null) return;
            if (cur == null || !string.Equals(cur.DeviceName, want.DeviceName, StringComparison.OrdinalIgnoreCase)
                || Monitors.Find(cur.DeviceName) == null)
            {
                Log.Info($"Displays changed: switching capture to {want.DisplayName}");
                StopVideo();
                _video.Clear();
                StartVideo();
            }
        }
    }

    void StartAudio()
    {
        try
        {
            _audioPipeline = new AudioPipeline(_audio, _settings.MicrophoneId, _settings.OutputDeviceId);
            _audioPipeline.Notify += (m, e) => Notify?.Invoke(m, e);
        }
        catch (Exception ex)
        {
            // clips still work without sound
            Log.Error("Audio pipeline could not start", ex);
            Notify?.Invoke($"Audio recording is unavailable: {ex.Message}", true);
        }
    }

    public void Apply(Settings s)
    {
        lock (_gate)
        {
            var old = _settings;
            _settings = s.Clone();
            ApplyRetention();
            if (IsPaused) return;   // picked up on Resume
            if (old.MicrophoneId != s.MicrophoneId || old.OutputDeviceId != s.OutputDeviceId)
                _audioPipeline?.Apply(s.MicrophoneId, s.OutputDeviceId);
            if (old.Fps != s.Fps || !string.Equals(old.MonitorDevice, s.MonitorDevice, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info("Video settings changed, restarting capture");
                StopVideo();
                _video.Clear();
                StartVideo();
            }
        }
    }

    void ApplyRetention()
    {
        _video.RetentionHns = (_settings.ClipSeconds + GopSlackSeconds) * Hns;
        _audio.RetentionHns = (_settings.ClipSeconds + GopSlackSeconds) * Hns;
    }

    void StartVideo()
    {
        if (_disposed) return;
        var mon = Monitors.Find(_settings.MonitorDevice);
        if (mon == null)
        {
            mon = Monitors.Primary();
            if (_settings.MonitorDevice != null && mon != null)
                Notify?.Invoke($"The selected monitor is not connected. Recording {mon.DisplayName} instead.", true);
        }
        if (mon == null)
        {
            Log.Warn("No monitor found; retrying");
            ScheduleRetry();
            return;
        }

        try
        {
            var p = new VideoPipeline(mon, _settings.Fps, _video, _forceSoftware);
            p.Faulted += why => OnPipelineProblem(p, $"Video pipeline fault: {why}");
            p.MonitorLost += () => OnPipelineProblem(p, $"{mon.DisplayName} was disconnected");
            _pipeline = p;
        }
        catch (Exception ex)
        {
            Log.Error("Could not start video capture", ex);
            Notify?.Invoke($"Could not start screen capture: {ex.Message}", true);
            ScheduleRetry();
        }
    }

    void StopVideo()
    {
        var p = _pipeline;
        _pipeline = null;
        p?.Dispose();
    }

    void OnPipelineProblem(VideoPipeline p, string why)
    {
        Log.Warn(why);
        // never tear the pipeline down from its own threads
        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_gate)
            {
                if (_pipeline != p) return;
                StopVideo();
                // a restart produces a new SPS/encoder session: old packets can't share the same track
                _video.Clear();
                StartVideo();
            }
        });
    }

    void ScheduleRetry()
    {
        _retry?.Dispose();
        _retry = new System.Threading.Timer(_ =>
        {
            lock (_gate) { if (_pipeline == null && !_disposed && !IsPaused) StartVideo(); }
        }, null, 3000, Timeout.Infinite);
    }

    /// <summary>Writes the last N seconds to an MP4. Runs on a worker thread; capture keeps going.</summary>
    public Task<string> SaveClipAsync()
    {
        if (IsPaused)
            return Task.FromException<string>(new InvalidOperationException("Skippy is paused. Resume it from the tray menu to buffer clips again."));
        if (Interlocked.Exchange(ref _saving, 1) == 1)
            return Task.FromException<string>(new InvalidOperationException("A clip is already being saved."));

        long pressed = Clock.Now;
        return Task.Run(async () =>
        {
            try
            {
                // the audio mixer runs ~0.3 s behind real time; give it a moment so the clip's sound reaches the keypress
                await Task.Delay(450);
                PacketBuffer.Snapshot snap, audioSnap;
                VideoFormat? fmt;
                int clipSeconds;
                string folder;
                lock (_gate)
                {
                    snap = _video.TakeSnapshot();
                    audioSnap = _audio.TakeSnapshot();
                    fmt = _pipeline?.Format;
                    clipSeconds = _settings.ClipSeconds;
                    folder = _settings.SaveFolder;
                }
                using (snap)
                using (audioSnap)
                {
                    if (fmt == null || fmt.Sps.Length == 0 || snap.Packets.Length == 0)
                        throw new InvalidOperationException("Nothing has been buffered yet.");
                    var path = WriteClip(snap.Packets, audioSnap.Packets, fmt, clipSeconds, pressed, folder, t => LastClipStart = t);
                    LastClipPath = path;
                    LastClipTime = DateTime.Now;
                    ClipSaved?.Invoke(path);
                    return path;
                }
            }
            finally { Interlocked.Exchange(ref _saving, 0); }
        });
    }

    static string WriteClip(Packet[] videoAll, Packet[] audioAll, VideoFormat fmt, int clipSeconds, long pressed, string folder, Action<long> reportStart)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // the clip ends at the keypress (frames encoded during the settle delay are left out)
        int last = videoAll.Length - 1;
        while (last > 0 && videoAll[last].Pts > pressed) last--;
        var all = videoAll[..(last + 1)];
        long end = all[^1].Pts + all[^1].Duration;
        long want = end - clipSeconds * Hns;

        int startIdx = -1;
        for (int i = 0; i < all.Length; i++)
        {
            if (!all[i].Key) continue;
            if (all[i].Pts <= want || startIdx < 0) startIdx = i;
            if (all[i].Pts > want) break;
        }
        if (startIdx < 0) throw new InvalidOperationException("No keyframe in the buffer yet.");

        var samples = all[startIdx..];
        long t0 = samples[0].Pts;
        reportStart(t0);
        const int ts = 90_000;
        var times = new long[samples.Length];
        for (int i = 0; i < samples.Length; i++)
            times[i] = (long)Math.Round((samples[i].Pts - t0) * (double)ts / Hns);

        var video = new Mp4Track
        {
            IsVideo = true,
            Timescale = ts,
            Samples = samples,
            Times = times,
            LastDuration = (long)Math.Round(samples[^1].Duration * (double)ts / Hns),
            Width = fmt.Width,
            Height = fmt.Height,
            Sps = fmt.Sps,
            Pps = fmt.Pps,
        };
        var tracks = new List<Mp4Track> { video };

        // audio: every AAC frame that starts inside the video's time span, positioned on the same clock
        long videoEnd = end;
        var aSamples = audioAll.Where(p => p.Pts >= t0 && p.Pts < videoEnd).ToArray();
        if (aSamples.Length > 0)
        {
            const int ats = AudioMixer.SampleRate;
            var aTimes = new long[aSamples.Length];
            for (int i = 0; i < aSamples.Length; i++)
                aTimes[i] = (long)Math.Round((aSamples[i].Pts - t0) * (double)ats / Hns);
            tracks.Add(new Mp4Track
            {
                IsVideo = false,
                Timescale = ats,
                Samples = aSamples,
                Times = aTimes,
                LastDuration = AacEncoder.FrameSamples,
                Channels = AudioMixer.Channels,
                SampleRate = AudioMixer.SampleRate,
                AudioSpecificConfig = AacEncoder.AudioSpecificConfig,
                Bitrate = AacEncoder.Bitrate,
            });
        }
        else Log.Warn("Clip has no audio (audio buffer empty for this time span)");

        Directory.CreateDirectory(folder);
        string name = $"Skippy_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        string path = Path.Combine(folder, name + ".mp4");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{name}_{n}.mp4");
        string tmp = path + ".part";
        try
        {
            Mp4Writer.Write(tmp, tracks);
            File.Move(tmp, path);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
        double secs = (samples[^1].Pts + samples[^1].Duration - t0) / (double)Hns;
        Log.Info($"Saved {path}: {samples.Length} frames, {aSamples.Length} audio frames, {secs:0.0}s, {new FileInfo(path).Length / 1048576.0:0.0} MB in {sw.ElapsedMilliseconds} ms");
        return path;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _watchdog.Dispose();
            _retry?.Dispose();
            StopVideo();
            _audioPipeline?.Dispose();
            _audioPipeline = null;
            _video.Clear();
            _audio.Clear();
        }
    }
}
