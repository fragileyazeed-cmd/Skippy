using Skippy.Core;
using Vortice.MediaFoundation;

namespace Skippy.Audio;

public delegate void AacHandler(ReadOnlySpan<byte> frame);

/// <summary>Windows' built-in AAC-LC encoder MFT (48 kHz stereo, 192 kbps, raw AAC frames of 1024 samples).</summary>
public sealed class AacEncoder : IDisposable
{
    public const int Bitrate = 192_000;
    public const int FrameSamples = 1024;
    const int MFT_ENUM_FLAG_SYNCMFT = 0x1, MFT_ENUM_FLAG_SORTANDFILTER = 0x40;
    const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);

    readonly IMFTransform _mft;
    readonly AacHandler _onFrame;
    readonly int _outSize;

    /// <summary>AudioSpecificConfig for the MP4 esds box: AAC-LC, 48 kHz, 2 channels.</summary>
    public static readonly byte[] AudioSpecificConfig = [0x11, 0x90];

    public AacEncoder(AacHandler onFrame)
    {
        _onFrame = onFrame;
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Audio, GuidSubtype = AudioFormatGuids.Pcm };
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Audio, GuidSubtype = AudioFormatGuids.Aac };
        IMFActivate? act = null;
        foreach (var a in MediaFactory.MFTEnumEx(TransformCategoryGuids.AudioEncoder, MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER, input, output))
        {
            if (act == null) act = a; else a.Dispose();
        }
        if (act == null) throw new InvalidOperationException("The Windows AAC encoder is not available (N edition without the Media Feature Pack?)");
        using (act) _mft = act.ActivateObject<IMFTransform>();

        using (var o = MediaFactory.MFCreateMediaType())
        {
            o.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            o.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
            o.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            o.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)AudioMixer.SampleRate);
            o.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)AudioMixer.Channels);
            o.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(Bitrate / 8));
            o.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);                      // raw AAC frames
            o.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 0x29u);   // AAC-LC L2
            _mft.SetOutputType(0, o, 0);
        }
        using (var i = MediaFactory.MFCreateMediaType())
        {
            i.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            i.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            i.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            i.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)AudioMixer.SampleRate);
            i.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)AudioMixer.Channels);
            i.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(2 * AudioMixer.Channels));
            i.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(2 * AudioMixer.Channels * AudioMixer.SampleRate));
            _mft.SetInputType(0, i, 0);
        }
        _outSize = Math.Max(_mft.GetOutputStreamInfo(0).Size, 8192);
        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    /// <summary>Encodes interleaved stereo float PCM; every finished AAC frame is passed to the callback in order.</summary>
    public unsafe void Encode(ReadOnlySpan<float> pcm, long ptsHns)
    {
        int bytes = pcm.Length * 2;
        using var buffer = MediaFactory.MFCreateMemoryBuffer(bytes);
        buffer.Lock(out var p, out _, out _);
        var dst = new Span<short>((void*)p, pcm.Length);
        for (int i = 0; i < pcm.Length; i++)
        {
            float s = pcm[i];
            if (s > 1f) s = 1f; else if (s < -1f) s = -1f;   // hard clip after summing mic + game audio
            dst[i] = (short)(s * 32767f);
        }
        buffer.Unlock();
        buffer.CurrentLength = bytes;
        using (var sample = MediaFactory.MFCreateSample())
        {
            sample.AddBuffer(buffer);
            sample.SampleTime = ptsHns;
            sample.SampleDuration = AudioMixer.FrameToHns(pcm.Length / AudioMixer.Channels);
            _mft.ProcessInput(0, sample, 0);
        }
        while (Pull()) { }
    }

    bool Pull()
    {
        var own = MediaFactory.MFCreateSample();
        using (var b = MediaFactory.MFCreateMemoryBuffer(_outSize)) own.AddBuffer(b);
        var odb = new OutputDataBuffer { StreamID = 0, Sample = own };
        try
        {
            var hr = _mft.ProcessOutput(0, 1, ref odb, out _);
            if (hr.Code == MF_E_TRANSFORM_NEED_MORE_INPUT) return false;
            hr.CheckError();
            using var buf = own.ConvertToContiguousBuffer();
            buf.Lock(out var ptr, out _, out var len);
            try { unsafe { if (len > 0) _onFrame(new ReadOnlySpan<byte>((void*)ptr, len)); } }
            finally { buf.Unlock(); }
            return true;
        }
        finally
        {
            if (odb.Sample != null && odb.Sample.NativePointer != own.NativePointer) odb.Sample.Dispose();
            own.Dispose();
            odb.Events?.Dispose();
        }
    }

    public void Dispose()
    {
        try { _mft.Dispose(); } catch (Exception ex) { Log.Warn($"AAC encoder dispose: {ex.Message}"); }
    }
}
