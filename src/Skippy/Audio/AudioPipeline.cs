using NAudio.CoreAudioApi;
using Skippy.Core;

namespace Skippy.Audio;

/// <summary>
/// Microphone + output-device loopback → timestamp mixer → AAC → ring buffer.
/// Watches Core Audio endpoint notifications (IMMNotificationClient) and re-opens sources when devices are
/// unplugged, re-plugged or the Windows default changes. Video never waits on any of this.
/// </summary>
public sealed class AudioPipeline : IDisposable
{
    /// <summary>How far behind real time the mixer runs, so late packets still land in the right place.</summary>
    const int LatencyFrames = AudioMixer.SampleRate * 300 / 1000;
    const int NotifyAfterMs = 5000;

    /// <summary>
    /// Priming delay of the Windows AAC encoder: its first output frame starts this many samples before
    /// the first input sample. Measured with `Skippy.exe --selftest-aac`.
    /// </summary>
    public static int EncoderDelayFrames = 0;

    readonly PacketBuffer _buffer;
    readonly AudioMixer _mixer = new();
    readonly object _gate = new();
    readonly Slot _mic, _out;
    readonly Thread _mixThread;
    readonly System.Threading.Timer _watchdog;
    MMDeviceEnumerator? _enum;
    MMDeviceNotificationClient? _notify;
    System.Threading.Timer? _debounce;
    volatile bool _stop;

    long _streamStartFrame;
    long _aacFrames;

    public event Action<string, bool>? Notify;

    sealed class Slot(string label, DataFlow flow, AudioMixer.Input input)
    {
        public readonly string Label = label;
        public readonly DataFlow Flow = flow;
        public readonly AudioMixer.Input Input = input;
        public string? Setting;
        public AudioSource? Source;
        public long MissingSince;      // Environment.TickCount64 when no device was usable, 0 = fine
        public bool Notified;
        public string? LastError;
    }

    public AudioPipeline(PacketBuffer buffer, string? micSetting, string? outSetting)
    {
        _buffer = buffer;
        _mic = new Slot("microphone", DataFlow.Capture, _mixer.AddInput("mic")) { Setting = micSetting };
        _out = new Slot("headset/output audio", DataFlow.Render, _mixer.AddInput("loopback")) { Setting = outSetting };

        // Core Audio objects live on an MTA thread-pool thread, never on the UI's STA thread
        Task.Run(() =>
        {
            _enum = new MMDeviceEnumerator();
            _notify = _enum.CreateNotificationClient(useSynchronizationContext: false);
            _notify.DeviceAdded += (_, _) => ScheduleReconcile();
            _notify.DeviceRemoved += (_, _) => ScheduleReconcile();
            _notify.DeviceStateChanged += (_, _) => ScheduleReconcile();
            _notify.DefaultDeviceChanged += (_, _) => ScheduleReconcile();
        }).Wait();

        Reconcile();
        _mixThread = new Thread(MixLoop) { IsBackground = true, Name = "Skippy audio mixer", Priority = ThreadPriority.AboveNormal };
        _mixThread.Start();
        // safety net: also catches sources that died without a device notification
        _watchdog = new System.Threading.Timer(_ => Reconcile(), null, 2000, 2000);
    }

    public void Apply(string? micSetting, string? outSetting)
    {
        lock (_gate)
        {
            _mic.Setting = micSetting;
            _out.Setting = outSetting;
            _mic.Notified = _out.Notified = false;
        }
        Task.Run(Reconcile);
    }

    /// <summary>Peak level since the last call, or -1 if that source isn't recording.</summary>
    public float TakeLevel(bool mic)
    {
        var s = mic ? _mic : _out;
        float p = s.Input.TakePeak();
        return s.Source == null || s.Source.Failed ? -1 : p;
    }

    public string Describe(bool mic)
    {
        var s = mic ? _mic : _out;
        var src = s.Source;
        if (s.Setting == AudioDevices.None) return "off";
        if (src == null || src.Failed) return $"not available{(s.LastError != null ? $" ({s.LastError})" : "")}";
        return $"{src.Device.Name} — {src.Packets} packets, drift trims {s.Input.Trimmed}, resyncs {s.Input.Resyncs}, late frames {s.Input.Late}";
    }

    void ScheduleReconcile()
    {
        // notifications arrive on the audio service thread in bursts: debounce, and never block that thread
        var t = new System.Threading.Timer(_ => Reconcile(), null, 300, Timeout.Infinite);
        Interlocked.Exchange(ref _debounce, t)?.Dispose();
    }

    void Reconcile()
    {
        if (_stop || _enum == null) return;
        lock (_gate)
        {
            if (_stop) return;
            ReconcileSlot(_mic);
            ReconcileSlot(_out);
        }
    }

    void ReconcileSlot(Slot s)
    {
        AudioDevice? want = null;
        try { want = AudioDevices.Resolve(_enum!, s.Flow, s.Setting); } catch (Exception ex) { s.LastError = ex.Message; }

        if (s.Setting == AudioDevices.None)
        {
            StopSource(s);
            s.MissingSince = 0;
            return;
        }

        bool healthy = s.Source != null && !s.Source.Failed && want != null && s.Source.Device.Id == want.Id;
        if (healthy)
        {
            if (s.Notified) Notify?.Invoke($"Audio is back: recording {s.Label} from {want!.Name}.", false);
            s.Notified = false;
            s.MissingSince = 0;
            return;
        }

        if (s.Source?.Failed == true) s.LastError = s.Source.Error;
        StopSource(s);
        if (want != null)
        {
            if (!string.IsNullOrEmpty(s.Setting) && want.Id != s.Setting)
                Log.Info($"Selected {s.Label} is not connected, using Windows default '{want.Name}' meanwhile");
            var src = new AudioSource(s.Input, want, loopback: s.Flow == DataFlow.Render);
            if (!src.Failed)
            {
                s.Source = src;
                if (s.Notified) Notify?.Invoke($"Audio is back: recording {s.Label} from {want.Name}.", false);
                s.Notified = false;
                s.MissingSince = 0;
                s.LastError = null;
                return;
            }
            s.LastError = src.Error;
            src.Dispose();
        }

        // nothing usable right now: keep video running, retry on the next notification / watchdog tick
        if (s.MissingSince == 0) s.MissingSince = Environment.TickCount64;
        if (!s.Notified && Environment.TickCount64 - s.MissingSince > NotifyAfterMs)
        {
            s.Notified = true;
            string why = want == null ? $"no {s.Label} device is connected" : $"{want.Name} could not be opened ({s.LastError})";
            Log.Warn($"Audio problem: {why}");
            Notify?.Invoke($"Clips have no {s.Label} sound: {why}. Skippy will reconnect automatically.", true);
        }
    }

    static void StopSource(Slot s)
    {
        s.Source?.Dispose();
        s.Source = null;
    }

    void MixLoop()
    {
        var block = new float[AacEncoder.FrameSamples * AudioMixer.Channels];
        AacEncoder? aac = null;
        int failures = 0;
        while (!_stop)
        {
            try
            {
                MixUntilStopped(block, ref aac);
            }
            catch (Exception ex)
            {
                // restart the audio stream rather than leaving clips silent for the rest of the session
                Log.Error("Audio mixer error, restarting audio stream", ex);
                aac?.Dispose(); aac = null;
                _buffer.Clear();
                if (++failures == 3) Notify?.Invoke($"Audio recording keeps failing: {ex.Message}", true);
                Thread.Sleep(1000);
            }
        }
        aac?.Dispose();
    }

    void MixUntilStopped(float[] block, ref AacEncoder? aac)
    {
        {
            while (!_stop)
            {
                if (aac == null)
                {
                    aac = new AacEncoder(OnAacFrame);
                    long start = AudioMixer.HnsToFrame(Clock.Now) - LatencyFrames;
                    _mixer.Reset(start);
                    _streamStartFrame = start;
                    _aacFrames = 0;
                }

                long target = AudioMixer.HnsToFrame(Clock.Now) - LatencyFrames;
                if (target - _mixer.MixPos > AudioMixer.SampleRate * 2)
                {
                    // the mixer was starved for seconds (system suspend?): start a fresh audio stream
                    Log.Warn("Audio mixer fell behind; restarting the audio stream");
                    aac.Dispose(); aac = null;
                    _buffer.Clear();
                    continue;
                }
                while (target - _mixer.MixPos >= AacEncoder.FrameSamples && !_stop)
                {
                    long pos = _mixer.Mix(block, AacEncoder.FrameSamples);
                    aac.Encode(block, AudioMixer.FrameToHns(pos));
                }
                Thread.Sleep(10);
            }
        }
    }

    void OnAacFrame(ReadOnlySpan<byte> frame)
    {
        // frame k covers input samples [k*1024 - delay, ...) of this stream
        long startFrame = _streamStartFrame + _aacFrames * AacEncoder.FrameSamples - EncoderDelayFrames;
        _aacFrames++;
        _buffer.Append(frame, AudioMixer.FrameToHns(startFrame), AudioMixer.FrameToHns(AacEncoder.FrameSamples), true);
    }

    public void Dispose()
    {
        _stop = true;
        _watchdog.Dispose();
        _debounce?.Dispose();
        _mixThread.Join(2000);
        lock (_gate)
        {
            StopSource(_mic);
            StopSource(_out);
        }
        Task.Run(() => { _notify?.Dispose(); _enum?.Dispose(); }).Wait(2000);
    }
}
