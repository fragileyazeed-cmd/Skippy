using NAudio.CoreAudioApi;
using NAudio.Wave;
using Skippy.Core;

namespace Skippy.Audio;

/// <summary>
/// One WASAPI shared-mode capture stream: a microphone, or an output device in loopback mode (what you
/// hear in the headset). WASAPI converts whatever the device runs at to 48 kHz stereo float for us.
/// Runs on its own MTA thread; if the device disappears the source marks itself <see cref="Failed"/> and
/// the <see cref="AudioPipeline"/> reconnects it.
/// </summary>
public sealed class AudioSource : IDisposable
{
    readonly AudioMixer.Input _input;
    readonly bool _loopback;
    readonly Thread _thread;
    readonly ManualResetEventSlim _started = new();
    volatile bool _stop;

    public AudioDevice Device { get; }
    public volatile bool Failed;
    public string? Error { get; private set; }
    public long Packets;

    public AudioSource(AudioMixer.Input input, AudioDevice device, bool loopback)
    {
        _input = input;
        Device = device;
        _loopback = loopback;
        _thread = new Thread(Run) { IsBackground = true, Name = $"Skippy audio {(loopback ? "loopback" : "mic")}", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        _started.Wait(3000);
    }

    unsafe void Run()
    {
        MMDeviceEnumerator? enumr = null;
        MMDevice? dev = null;
        AudioClient? client = null;
        try
        {
            enumr = new MMDeviceEnumerator();
            dev = enumr.GetDevice(Device.Id);
            client = dev.CreateAudioClient();
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(AudioMixer.SampleRate, AudioMixer.Channels);
            var flags = AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
            if (_loopback) flags |= AudioClientStreamFlags.Loopback;
            client.Initialize(AudioClientShareMode.Shared, flags, 2_000_000, 0, fmt, Guid.Empty);
            var cap = client.AudioCaptureClient;
            client.Start();
            Log.Info($"Audio {(_loopback ? "loopback" : "mic")} started: {Device.Name}");
            _started.Set();

            while (!_stop)
            {
                Thread.Sleep(5);
                int next;
                while (!_stop && (next = cap.GetNextPacketSize()) > 0)
                {
                    var ptr = cap.GetBuffer(out int frames, out var bf, out _, out long qpc);
                    if (frames > 0)
                    {
                        bool silent = (bf & AudioClientBufferFlags.Silent) != 0;
                        bool disc = (bf & AudioClientBufferFlags.DataDiscontinuity) != 0;
                        long t = (bf & AudioClientBufferFlags.TimestampError) != 0 || qpc == 0
                            ? Clock.Now - AudioMixer.FrameToHns(frames)
                            : Clock.FromQpc100ns(qpc);
                        _input.Write((float*)ptr, frames, silent, disc, t);
                        Packets++;
                    }
                    cap.ReleaseBuffer(frames);
                }
            }
            try { client.Stop(); } catch { }
        }
        catch (Exception ex)
        {
            if (!_stop)
            {
                Error = ex.Message;
                Failed = true;
                Log.Warn($"Audio {(_loopback ? "loopback" : "mic")} '{Device.Name}' stopped: {ex.Message}");
            }
        }
        finally
        {
            _started.Set();
            client?.Dispose();
            dev?.Dispose();
            enumr?.Dispose();
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (Thread.CurrentThread != _thread) _thread.Join(2000);
    }
}
