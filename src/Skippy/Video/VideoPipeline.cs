using Skippy.Core;
using Skippy.Media;

namespace Skippy.Video;

public sealed record VideoFormat(int Width, int Height, int Fps, int Bitrate, byte[] Sps, byte[] Pps, string EncoderName);

/// <summary>
/// Monitor → NV12 → H.264 → ring buffer. A high-resolution timer samples the latest captured frame at
/// exactly 30 or 60 fps (repeating the previous image when the screen is static), stamps it on the
/// shared <see cref="Clock"/>, and hands it to the encoder.
/// </summary>
public sealed class VideoPipeline : IDisposable
{
    readonly PacketBuffer _buffer;
    readonly GpuDevice _gpu;
    readonly ScreenCapture _capture;
    readonly NV12Converter _converter;
    readonly H264Encoder _encoder;
    readonly AnnexBConverter _annexB = new();
    readonly Thread _tick;
    readonly int _fps;
    readonly long _period;
    volatile bool _stop;
    volatile bool _faulted;
    bool _gotKey;

    public MonitorInfo Monitor { get; }
    public VideoFormat? Format { get; private set; }
    public long FramesEncoded;
    public long FramesSkipped;

    /// <summary>The pipeline broke (GPU reset, encoder error) and should be re-created.</summary>
    public event Action<string>? Faulted;
    /// <summary>The captured monitor disappeared.</summary>
    public event Action? MonitorLost;

    public static (int w, int h) EncodeSize(int w, int h)
    {
        int shortSide = Math.Min(w, h);
        double s = shortSide > 1080 ? 1080.0 / shortSide : 1.0;
        return ((int)Math.Round(w * s) & ~1, (int)Math.Round(h * s) & ~1);
    }

    /// <summary>~18 Mbps for 1080p60, scaled by pixel count and frame rate, capped so a 3-minute buffer stays near 400 MB.</summary>
    public static int Bitrate(int w, int h, int fps)
    {
        double b = 18_000_000 * (w * (double)h / (1920 * 1080)) * (fps >= 60 ? 1.0 : 0.6);
        return (int)Math.Clamp(b, 3_000_000, 18_000_000);
    }

    public VideoPipeline(MonitorInfo monitor, int fps, PacketBuffer buffer, bool forceSoftware = false)
    {
        Monitor = monitor;
        _fps = fps;
        _period = 10_000_000L / fps;
        _buffer = buffer;
        try
        {
            _gpu = new GpuDevice(monitor.Handle);
            var (w, h) = EncodeSize(monitor.Width, monitor.Height);
            int bitrate = Bitrate(w, h, fps);
            _capture = new ScreenCapture(_gpu, monitor, fps);
            _capture.Closed += () => MonitorLost?.Invoke();
            _converter = new NV12Converter(_gpu, w, h, fps, poolSize: 8);
            _encoder = new H264Encoder(_gpu, w, h, fps, bitrate, OnEncoded, forceSoftware);
            _encoder.Failed += ex => Fault($"encoder failed: {ex.Message}");
            Format = new VideoFormat(w, h, fps, bitrate, [], [], _encoder.Name);
        }
        catch
        {
            DisposeParts();
            throw;
        }
        _tick = new Thread(TickLoop) { IsBackground = true, Name = "Skippy video tick", Priority = ThreadPriority.AboveNormal };
        _tick.Start();
    }

    void TickLoop()
    {
        IntPtr timer = Native.CreateWaitableTimerExW(IntPtr.Zero, null, Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Native.TIMER_ALL_ACCESS);
        try
        {
            long start = Clock.Now;
            long k = 0;
            while (!_stop)
            {
                long target = start + k * 10_000_000L / _fps;
                long wait = target - Clock.Now;
                if (wait > 0)
                {
                    if (timer != IntPtr.Zero)
                    {
                        long due = -wait;
                        Native.SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
                        Native.WaitForSingleObject(timer, 1000);
                    }
                    else Thread.Sleep((int)Math.Max(1, wait / 10_000));
                }
                if (_stop) break;

                long now = Clock.Now;
                if (now - target > 2 * _period)
                {
                    // fell behind (system stall): skip the missed ticks instead of bursting
                    long newK = (now - start) * _fps / 10_000_000L;
                    FramesSkipped += newK - k;
                    k = newK;
                    target = start + k * 10_000_000L / _fps;
                }

                EncodeOne(target);
                k++;
            }
        }
        catch (Exception ex)
        {
            Fault($"video tick failed: {ex.Message}");
            Log.Error("Video tick loop", ex);
        }
        finally
        {
            if (timer != IntPtr.Zero) Native.CloseHandle(timer);
        }
    }

    void EncodeOne(long pts)
    {
        if (Interlocked.Read(ref _capture.FramesArrived) == 0) return;   // nothing captured yet
        Vortice.Direct3D11.ID3D11Texture2D? nv12;
        lock (_gpu.Lock) nv12 = _converter.Convert(_capture);
        if (nv12 == null) return;
        _encoder.Submit(nv12, pts, _period);
    }

    void OnEncoded(ReadOnlySpan<byte> annexB, long pts, long duration)
    {
        var avcc = _annexB.Convert(annexB, out bool idr);
        if (_annexB.Sps == null || _annexB.Pps == null)
        {
            var sh = _encoder.GetSequenceHeader();
            if (sh != null) _annexB.ReadSequenceHeader(sh);
        }
        if (!_gotKey)
        {
            if (!idr || _annexB.Sps == null || _annexB.Pps == null) return;
            _gotKey = true;
            Format = Format! with { Sps = _annexB.Sps, Pps = _annexB.Pps };
            Log.Info($"First keyframe encoded (SPS {_annexB.Sps.Length} B, PPS {_annexB.Pps.Length} B)");
        }
        else if (idr && !_annexB.Sps!.AsSpan().SequenceEqual(Format!.Sps))
        {
            Log.Warn("Encoder changed its SPS mid-stream; clearing the buffer");
            _buffer.Clear();
            Format = Format with { Sps = _annexB.Sps, Pps = _annexB.Pps! };
        }
        if (avcc.Length == 0) return;
        _buffer.Append(avcc, pts, duration > 0 ? duration : _period, idr);
        Interlocked.Increment(ref FramesEncoded);
    }

    public long FramesDropped => _encoder?.Dropped ?? 0;
    public long FramesCaptured => Interlocked.Read(ref _capture.FramesArrived);

    void Fault(string why)
    {
        if (_faulted || _stop) return;
        _faulted = true;
        Faulted?.Invoke(why);
    }

    void DisposeParts()
    {
        _encoder?.Dispose();
        _converter?.Dispose();
        _capture?.Dispose();
        _gpu?.Dispose();
    }

    public void Dispose()
    {
        _stop = true;
        if (Thread.CurrentThread != _tick) _tick.Join(2000);
        DisposeParts();
    }
}
