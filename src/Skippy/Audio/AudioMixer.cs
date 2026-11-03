using Skippy.Core;

namespace Skippy.Audio;

/// <summary>
/// Mixes any number of inputs on the shared <see cref="Clock"/> timeline (48 kHz stereo float).
/// Every input writes its samples at the position given by the WASAPI QPC timestamp of each packet, so
/// sources line up by time, not by arrival order. Gaps (e.g. loopback sends nothing while the PC is
/// silent) simply stay silent. Small clock drift between a device and QPC is absorbed by dropping or
/// repeating single samples (≤0.2 % rate change, inaudible); large jumps resync immediately.
/// </summary>
public sealed class AudioMixer
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    const int RingFrames = SampleRate * 4;
    const int ResyncFrames = SampleRate / 10;        // >100 ms off: jump
    const double DriftThreshold = SampleRate / 1000.0; // >1 ms average error: start trimming

    readonly List<Input> _inputs = new();
    readonly object _gate = new();

    /// <summary>Next frame the mixer will read; inputs cannot write before this.</summary>
    long _mixPos;

    public static long HnsToFrame(long hns) => (long)Math.Floor(hns * (double)SampleRate / 10_000_000);
    public static long FrameToHns(long frame) => (long)Math.Round(frame * 10_000_000.0 / SampleRate);

    public Input AddInput(string name)
    {
        var i = new Input(this, name);
        lock (_gate) _inputs.Add(i);
        return i;
    }

    public void RemoveInput(Input i)
    {
        lock (_gate) _inputs.Remove(i);
    }

    public void Reset(long frame) => Interlocked.Exchange(ref _mixPos, frame);

    /// <summary>Mixes frames [MixPos, MixPos+count) into <paramref name="dst"/> (interleaved) and advances.</summary>
    public long Mix(Span<float> dst, int count)
    {
        dst[..(count * Channels)].Clear();
        long pos = Interlocked.Read(ref _mixPos);
        Input[] inputs;
        lock (_gate) inputs = _inputs.ToArray();
        foreach (var i in inputs) i.ReadAdd(pos, count, dst);
        Interlocked.Exchange(ref _mixPos, pos + count);
        return pos;
    }

    public long MixPos => Interlocked.Read(ref _mixPos);

    /// <summary>Ring offset of a timeline frame (timeline frames can be negative right after start).</summary>
    static int Slot(long frame) => (int)(((frame % RingFrames) + RingFrames) % RingFrames) * Channels;

    public sealed class Input
    {
        readonly AudioMixer _mixer;
        readonly float[] _ring = new float[RingFrames * Channels];
        readonly object _lock = new();
        long _writePos;
        bool _started;
        double _err;

        public string Name { get; }
        public long Resyncs { get; private set; }
        public long Trimmed { get; private set; }
        public long Late { get; private set; }
        public double DriftMs => _err / SampleRate * 1000;
        public float Gain { get; set; } = 1f;

        volatile float _peak;
        /// <summary>Loudest sample since the last call (for the level meters in the UI).</summary>
        public float TakePeak() { var p = _peak; _peak = 0; return p; }

        internal Input(AudioMixer m, string name) { _mixer = m; Name = name; }

        /// <summary>Called by the capture thread for every WASAPI packet (48 kHz stereo float, or silence).</summary>
        public unsafe void Write(float* data, int frames, bool silent, bool discontinuity, long timeHns)
        {
            long expected = HnsToFrame(timeHns);
            lock (_lock)
            {
                long err = _writePos - expected;
                if (!_started || discontinuity || Math.Abs(err) > ResyncFrames)
                {
                    if (_started) Resyncs++;
                    _started = true;
                    _writePos = expected;
                    _err = 0;
                }
                else _err = _err * 0.95 + err * 0.05;

                int drop = 0, dup = 0;
                if (frames > 8)
                {
                    if (_err > DriftThreshold) { drop = 1; _err -= 1; Trimmed++; }
                    else if (_err < -DriftThreshold) { dup = 1; _err += 1; Trimmed++; }
                }

                if (!silent)
                {
                    float pk = _peak;
                    for (int i = 0; i < frames * 2; i++) { float a = Math.Abs(data[i]); if (a > pk) pk = a; }
                    _peak = pk;
                }

                long mixPos = _mixer.MixPos;
                int outFrames = frames - drop + dup;
                for (int f = 0; f < outFrames; f++)
                {
                    long pos = _writePos + f;
                    if (pos < mixPos) { Late++; continue; }               // mixer already passed this point
                    if (pos >= mixPos + RingFrames) break;                 // absurdly far ahead
                    int src = Math.Min(f, frames - 1);                     // dup repeats the last frame
                    int r = Slot(pos);
                    if (silent) { _ring[r] = 0; _ring[r + 1] = 0; }
                    else { _ring[r] = data[src * 2] * Gain; _ring[r + 1] = data[src * 2 + 1] * Gain; }
                }
                _writePos += outFrames;
            }
        }

        internal void ReadAdd(long from, int count, Span<float> dst)
        {
            lock (_lock)
            {
                for (int f = 0; f < count; f++)
                {
                    int r = Slot(from + f);
                    dst[f * 2] += _ring[r];
                    dst[f * 2 + 1] += _ring[r + 1];
                    _ring[r] = 0; _ring[r + 1] = 0;
                }
            }
        }
    }
}
