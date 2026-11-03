using System.Runtime.InteropServices;
using Skippy.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Skippy.Media;

/// <summary>What the trim window needs to know about a clip before playing it.</summary>
public sealed record ClipInfo(int Width, int Height, double Fps, TimeSpan Duration, bool HasAudio);

/// <summary>
/// Frame-exact trimming. A video can only be cut without re-encoding on keyframes (one per second in
/// Skippy clips), so the kept range is decoded and encoded again: on the GPU when possible (hardware
/// decoder → NV12 textures → hardware encoder, nothing touches the CPU), otherwise in software. Audio is
/// cut to the exact sample. The original file is never modified.
/// </summary>
public static class Trimmer
{
    const string EditedFolderName = "EditedSkippy";
    static readonly Guid MoovBeforeMdat = new("f672e3ac-e1e1-4f10-b5ec-5f3b30828fda");   // MF_MPEG4SINK_MOOV_BEFORE_MDAT: fast start

    /// <summary>Test hook: skip the GPU path.</summary>
    public static bool ForceSoftware;

    public static string EditedFolder(string saveFolder) => Path.Combine(saveFolder, EditedFolderName);

    /// <summary>e.g. "Skippy_2026-11-03_20-15-02_0m34.0s-0m39.0s.mp4", numbered if it already exists.</summary>
    public static string OutputPath(string folder, string source, TimeSpan start, TimeSpan end)
    {
        static string T(TimeSpan t) => $"{(int)t.TotalMinutes}m{t.Seconds + t.Milliseconds / 1000.0:00.0}s".Replace(',', '.');
        string name = $"{Path.GetFileNameWithoutExtension(source)}_{T(start)}-{T(end)}";
        string path = Path.Combine(folder, name + ".mp4");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{name} ({i}).mp4");
        return path;
    }

    public static ClipInfo Probe(string path)
    {
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null);
        using var v = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0);
        var (w, h) = Unpack(v.GetUInt64(MediaTypeAttributeKeys.FrameSize));
        var (num, den) = Unpack(v.GetUInt64(MediaTypeAttributeKeys.FrameRate));
        bool audio = false;
        try { using var a = reader.GetNativeMediaType(SourceReaderIndex.FirstAudioStream, 0); audio = true; } catch { }
        long dur = 0;
        try
        {
            var d = reader.GetPresentationAttribute(SourceReaderIndex.MediaSource, PresentationDescriptionAttributeKeys.Duration);
            dur = (long)Convert.ToUInt64(d.Value);
        }
        catch { }
        return new ClipInfo((int)w, (int)h, den > 0 ? num / (double)den : 60, TimeSpan.FromTicks(dur), audio);
    }

    static (uint hi, uint lo) Unpack(ulong v) => ((uint)(v >> 32), (uint)v);

    /// <summary>Writes [start, end) of <paramref name="source"/> to <paramref name="dest"/>. Progress is 0..1.</summary>
    public static Task TrimAsync(string source, string dest, TimeSpan start, TimeSpan end, IProgress<double>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Run(source, dest, start, end, progress, ct, hardware: !ForceSoftware);
                Log.Info($"Trimmed {Path.GetFileName(source)} {start.TotalSeconds:0.000}-{end.TotalSeconds:0.000} s on the GPU in {sw.ElapsedMilliseconds} ms -> {dest}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"GPU trim failed ({ex.Message}), retrying in software");
                TryDelete(dest);
                sw.Restart();
                Run(source, dest, start, end, progress, ct, hardware: false);
                Log.Info($"Trimmed {Path.GetFileName(source)} in software in {sw.ElapsedMilliseconds} ms -> {dest}");
            }
        }, ct).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully) TryDelete(dest);
            t.GetAwaiter().GetResult();
        }, TaskScheduler.Default);

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    static void Run(string source, string dest, TimeSpan startTs, TimeSpan endTs, IProgress<double>? progress, CancellationToken ct, bool hardware)
    {
        var info = Probe(source);
        long start = startTs.Ticks, end = endTs.Ticks;   // TimeSpan ticks are 100 ns, the Media Foundation unit
        int w = info.Width, h = info.Height;
        var (fpsNum, fpsDen) = FpsRatio(info.Fps);
        long frameHns = (long)Math.Round(1e7 / info.Fps);

        ID3D11Device? device = null;
        IMFDXGIDeviceManager? manager = null;
        IMFSourceReader? reader = null;
        IMFSinkWriter? writer = null;
        try
        {
            if (hardware)
            {
                D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out device).CheckError();
                using (var mt = device!.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
                manager = MediaFactory.MFCreateDXGIDeviceManager();
                manager.ResetDevice(device).CheckError();
            }

            // ---- reader: decode to NV12 (GPU textures with a device manager, system memory otherwise)
            using (var ra = MediaFactory.MFCreateAttributes(3))
            {
                ra.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, 1u);
                if (manager != null)
                {
                    ra.Set(SourceReaderAttributeKeys.D3DManager, manager);
                    ra.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
                }
                reader = MediaFactory.MFCreateSourceReaderFromURL(source, ra);
            }
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
            using (var vt = VideoType(VideoFormatGuids.NV12, w, h, fpsNum, fpsDen))
                reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, vt);

            IMFMediaType? pcm = null;
            if (info.HasAudio)
            {
                reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);
                using (var at = MediaFactory.MFCreateMediaType())
                {
                    at.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                    at.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                    at.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                    reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, at);
                }
                pcm = reader.GetCurrentMediaType(SourceReaderIndex.FirstAudioStream);
            }

            // ---- writer: H.264 + AAC in a fast-start MP4
            using (var wa = MediaFactory.MFCreateAttributes(5))
            {
                wa.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Mpeg4);
                wa.Set(MoovBeforeMdat, 1u);
                wa.Set(SinkWriterAttributeKeys.DisableThrottling, 1u);
                wa.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, hardware ? 1u : 0u);
                if (manager != null) wa.Set(SinkWriterAttributeKeys.D3DManager, manager);
                writer = MediaFactory.MFCreateSinkWriterFromURL(dest, null, wa);
            }

            int bitrate = Bitrate(source, info);
            int vOut;
            using (var o = VideoType(VideoFormatGuids.H264, w, h, fpsNum, fpsDen))
            {
                o.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
                o.Set(MediaTypeAttributeKeys.Mpeg2Profile, 100u);   // High
                vOut = writer.AddStream(o);
            }
            using (var i = VideoType(VideoFormatGuids.NV12, w, h, fpsNum, fpsDen))
            using (var enc = MediaFactory.MFCreateAttributes(5))
            {
                enc.Set(CodecApi.GopSize, (uint)Math.Round(info.Fps));   // keyframe every second, like the originals
                enc.Set(CodecApi.BPictureCount, 0u);
                // without an explicit rate control some GPU encoders ignore the bitrate and come out twice as big
                enc.Set(CodecApi.RateControlMode, CodecApi.RateControl_PeakConstrainedVbr);
                enc.Set(CodecApi.MeanBitRate, (uint)bitrate);
                enc.Set(CodecApi.MaxBitRate, (uint)(bitrate * 3L / 2));
                try { writer.SetInputMediaType(vOut, i, enc); }
                catch { writer.SetInputMediaType(vOut, i, null); }       // encoder refused the extra settings
            }

            int aOut = -1, blockAlign = 4, rate = 48000;
            if (pcm != null)
            {
                rate = (int)pcm.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond);
                int channels = (int)pcm.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels);
                blockAlign = channels * 2;
                using (var o = MediaFactory.MFCreateMediaType())
                {
                    o.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                    o.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                    o.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                    o.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)rate);
                    o.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)channels);
                    o.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 24000u);   // 192 kbps
                    o.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);
                    o.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 0x29u);
                    aOut = writer.AddStream(o);
                }
                writer.SetInputMediaType(aOut, pcm, null);
                pcm.Dispose();
            }

            writer.BeginWriting();
            reader.SetCurrentPosition(Math.Max(0, start - 10_000_000));   // lands on a keyframe before the in point

            bool videoDone = false, audioDone = aOut < 0;
            long lastVideo = -1;
            int frames = 0;
            var kinds = new Dictionary<int, bool>();
            while (!videoDone || !audioDone)
            {
                ct.ThrowIfCancellationRequested();
                using var sample = reader.ReadSample(SourceReaderIndex.AnyStream, SourceReaderControlFlag.None, out int stream, out var flags, out long ts);
                if (!kinds.TryGetValue(stream, out bool isVideo)) kinds[stream] = isVideo = IsVideoStream(reader, stream);
                if ((flags & SourceReaderFlag.Error) != 0) throw new InvalidOperationException("Could not read the clip.");
                if ((flags & SourceReaderFlag.EndOfStream) != 0)
                {
                    if (isVideo) videoDone = true; else audioDone = true;
                    continue;
                }
                if (sample == null) continue;

                if (isVideo)
                {
                    if (videoDone) continue;
                    // a frame belongs to the cut when it starts within half a frame of the range
                    if (ts < start - frameHns / 2) continue;
                    if (ts >= end - frameHns / 2)
                    {
                        videoDone = true;
                        reader.SetStreamSelection(stream, false);
                        continue;
                    }
                    long t = Math.Max(0, ts - start);
                    if (t <= lastVideo) t = lastVideo + 1;
                    lastVideo = t;
                    sample.SampleTime = t;
                    sample.SampleDuration = frameHns;
                    writer.WriteSample(vOut, sample);
                    frames++;
                    progress?.Report(Math.Clamp((ts - start) / (double)(end - start), 0, 1));
                }
                else
                {
                    if (audioDone) continue;
                    if (ts >= end)
                    {
                        audioDone = true;
                        reader.SetStreamSelection(stream, false);
                        continue;
                    }
                    using var cut = CutAudio(sample, ts, start, end, rate, blockAlign);
                    if (cut != null) writer.WriteSample(aOut, cut);
                }
            }
            if (frames == 0) throw new InvalidOperationException("No video frames in the chosen range.");
            writer.Finalize();
            progress?.Report(1);
        }
        finally
        {
            writer?.Dispose();
            reader?.Dispose();
            manager?.Dispose();
            device?.Dispose();
        }
    }

    static bool IsVideoStream(IMFSourceReader reader, int stream)
    {
        using var t = reader.GetCurrentMediaType(stream);
        return t.GetGUID(MediaTypeAttributeKeys.MajorType) == MediaTypeGuids.Video;
    }

    /// <summary>The part of a PCM packet inside [start, end), re-timed to the cut; null if none of it is.</summary>
    static unsafe IMFSample? CutAudio(IMFSample sample, long ts, long start, long end, int rate, int blockAlign)
    {
        using var buf = sample.ConvertToContiguousBuffer();
        buf.Lock(out var ptr, out _, out var len);
        try
        {
            int total = len / blockAlign;
            int first = ts >= start ? 0 : (int)Math.Min(total, Math.Ceiling((start - ts) * (double)rate / 1e7));
            int last = (int)Math.Min(total, Math.Ceiling((end - ts) * (double)rate / 1e7));
            int count = last - first;
            if (count <= 0) return null;
            var outBuf = MediaFactory.MFCreateMemoryBuffer(count * blockAlign);
            outBuf.Lock(out var dst, out _, out _);
            Buffer.MemoryCopy((byte*)ptr + first * blockAlign, (void*)dst, count * blockAlign, count * blockAlign);
            outBuf.Unlock();
            outBuf.CurrentLength = count * blockAlign;
            var s = MediaFactory.MFCreateSample();
            s.AddBuffer(outBuf);
            outBuf.Dispose();
            s.SampleTime = Math.Max(0, ts + (long)Math.Round(first * 1e7 / rate) - start);
            s.SampleDuration = (long)Math.Round(count * 1e7 / rate);
            return s;
        }
        finally { buf.Unlock(); }
    }

    static IMFMediaType VideoType(Guid subtype, int w, int h, uint fpsNum, uint fpsDen)
    {
        var t = MediaFactory.MFCreateMediaType();
        t.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        t.Set(MediaTypeAttributeKeys.Subtype, subtype);
        MediaFactory.MFSetAttributeSize(t, MediaTypeAttributeKeys.FrameSize, (uint)w, (uint)h);
        MediaFactory.MFSetAttributeRatio(t, MediaTypeAttributeKeys.FrameRate, fpsNum, fpsDen);
        MediaFactory.MFSetAttributeRatio(t, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
        t.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);           // progressive
        t.Set(MediaTypeAttributeKeys.VideoPrimaries, 2u);          // BT.709, limited range, as Skippy records
        t.Set(MediaTypeAttributeKeys.TransferFunction, 5u);
        t.Set(MediaTypeAttributeKeys.YuvMatrix, 1u);
        t.Set(MediaTypeAttributeKeys.VideoNominalRange, 2u);
        return t;
    }

    static (uint num, uint den) FpsRatio(double fps)
    {
        double r = Math.Round(fps);
        return Math.Abs(fps - r) < 0.01 ? ((uint)r, 1u) : ((uint)Math.Round(fps * 1001), 1001u);
    }

    /// <summary>Roughly how big a cut of <paramref name="seconds"/> will be.</summary>
    public static long EstimateBytes(string source, ClipInfo info, double seconds) =>
        (long)((Bitrate(source, info) + (info.HasAudio ? 192_000 : 0)) / 8.0 * seconds);

    /// <summary>
    /// Evenly spaced small frames for the timeline filmstrip (BGRX, <paramref name="w"/> x <paramref name="h"/>),
    /// delivered one by one as they are decoded. Each is the keyframe nearest before its slot, which is close enough.
    /// </summary>
    public static unsafe void Filmstrip(string path, int count, int w, int h, Action<int, byte[]> onFrame, CancellationToken ct)
    {
        var info = Probe(path);
        using var ra = MediaFactory.MFCreateAttributes(1);
        ra.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, 1u);
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, ra);
        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
        using (var t = MediaFactory.MFCreateMediaType())
        {
            t.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            t.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            MediaFactory.MFSetAttributeSize(t, MediaTypeAttributeKeys.FrameSize, (uint)w, (uint)h);
            MediaFactory.MFSetAttributeRatio(t, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
            t.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(w * 4));   // top-down rows
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, t);
        }
        for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
        {
            reader.SetCurrentPosition((long)(info.Duration.Ticks * (i + 0.5) / count));
            for (int tries = 0; tries < 8; tries++)
            {
                using var s = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out _);
                if ((flags & (SourceReaderFlag.EndOfStream | SourceReaderFlag.Error)) != 0) break;
                if (s == null) continue;
                using var buf = s.ConvertToContiguousBuffer();
                buf.Lock(out var p, out _, out var len);
                try
                {
                    var px = new byte[w * h * 4];
                    int pitch = len / h;
                    for (int y = 0; y < h; y++)
                        new ReadOnlySpan<byte>((byte*)p + y * pitch, w * 4).CopyTo(px.AsSpan(y * w * 4));
                    onFrame(i, px);
                }
                finally { buf.Unlock(); }
                break;
            }
        }
    }

    /// <summary>A bit above the source's own bitrate, so the second encode loses no visible detail.</summary>
    static int Bitrate(string source, ClipInfo info)
    {
        double seconds = Math.Max(1, info.Duration.TotalSeconds);
        double avg = new FileInfo(source).Length * 8 / seconds - (info.HasAudio ? 192_000 : 0);
        return (int)Math.Clamp(avg * 1.2, 4_000_000, 30_000_000);
    }
}
