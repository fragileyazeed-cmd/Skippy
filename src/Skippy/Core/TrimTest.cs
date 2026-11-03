using System.Text;
using Skippy.Media;
using Vortice.MediaFoundation;

namespace Skippy.Core;

/// <summary>
/// --selftest-trim: writes a 10 s test video whose frames show their own frame number as big binary
/// blocks, trims it, decodes the result and checks that exactly the requested frames came out, in order,
/// and that the audio has the same length. With a file argument it also trims that clip and checks it.
/// </summary>
public static class TrimTest
{
    const int W = 1280, H = 720, Fps = 60, Seconds = 10, Rate = 48000;

    public static int Run(string[] args, StringBuilder r)
    {
        Trimmer.ForceSoftware = args.Contains("--software");
        r.AppendLine(Trimmer.ForceSoftware ? "mode: software" : "mode: GPU (software fallback)");
        string dir = Path.Combine(Path.GetTempPath(), "SkippyTrimTest");
        Directory.CreateDirectory(dir);
        string src = Path.Combine(dir, "numbered.mp4");
        MakeNumbered(src);
        r.AppendLine($"source: {src} ({new FileInfo(src).Length / 1024} KB)");
        bool ok = true;

        // in/out on frame boundaries, in between frames, at the very start and at the very end
        (double a, double b)[] cuts = [(3.25, 5.5), (0, 1), (4.009, 4.991), (8.5, 10)];
        foreach (var (a, b) in cuts)
        {
            string dst = Path.Combine(dir, $"cut_{a:0.000}-{b:0.000}.mp4".Replace(',', '.'));
            if (File.Exists(dst)) File.Delete(dst);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Trimmer.TrimAsync(src, dst, TimeSpan.FromSeconds(a), TimeSpan.FromSeconds(b), null, CancellationToken.None).GetAwaiter().GetResult();
            long ms = sw.ElapsedMilliseconds;
            var (numbers, audioSamples) = Decode(dst, readNumbers: true);
            int first = (int)Math.Round(a * Fps), last = (int)Math.Round(b * Fps) - 1;
            var expected = Enumerable.Range(first, last - first + 1).ToList();
            bool framesOk = numbers.SequenceEqual(expected);
            double audioSec = audioSamples / (double)Rate, videoSec = numbers.Count / (double)Fps;
            // the decoder rounds the end up to a whole 1024-sample AAC frame
            bool audioOk = audioSec >= (b - a) - 0.003 && audioSec <= (b - a) + 1024.0 / Rate + 0.003;
            r.AppendLine($"cut {a:0.000}-{b:0.000} s: {ms} ms, frames {Describe(numbers)} (want {first}..{last}, {expected.Count}) {(framesOk ? "OK" : "WRONG")}, " +
                         $"audio {audioSec:0.000} s for a {b - a:0.000} s cut (video {videoSec:0.000} s) {(audioOk ? "OK" : "WRONG")}");
            ok &= framesOk && audioOk;
        }

        // a real clip, if given: length and A/V check
        var real = args.SkipWhile(x => x != "--selftest-trim").Skip(1).FirstOrDefault(x => x.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
        if (real != null)
        {
            var info = Trimmer.Probe(real);
            double a = Math.Min(34, info.Duration.TotalSeconds / 2), b = Math.Min(a + 5, info.Duration.TotalSeconds);
            string dst = Path.Combine(dir, "real_cut.mp4");
            if (File.Exists(dst)) File.Delete(dst);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Trimmer.TrimAsync(real, dst, TimeSpan.FromSeconds(a), TimeSpan.FromSeconds(b), null, CancellationToken.None).GetAwaiter().GetResult();
            long ms = sw.ElapsedMilliseconds;
            var (frames, audioSamples) = Decode(dst, readNumbers: false);
            var outInfo = Trimmer.Probe(dst);
            double want = (b - a) * info.Fps;
            bool frOk = Math.Abs(frames.Count - want) <= 1;
            r.AppendLine($"real {Path.GetFileName(real)} {info.Width}x{info.Height}@{info.Fps:0.##}: cut {a:0.0}-{b:0.0} s in {ms} ms -> {frames.Count} frames (want ~{want:0}) {(frOk ? "OK" : "WRONG")}, " +
                         $"audio {audioSamples / (double)Rate:0.000} s, duration {outInfo.Duration.TotalSeconds:0.000} s, {new FileInfo(dst).Length / 1024} KB");
            ok &= frOk;
        }
        r.AppendLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
        return ok ? 0 : 1;
    }

    static string Describe(List<int> n) => n.Count == 0 ? "none" : $"{n[0]}..{n[^1]} ({n.Count})";

    /// <summary>Frame f: 12 blocks of 80x80 px, white where bit i of f is set; a 440 Hz tone on the audio track.</summary>
    static void MakeNumbered(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        using var wa = MediaFactory.MFCreateAttributes(2);
        wa.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Mpeg4);
        using var writer = MediaFactory.MFCreateSinkWriterFromURL(path, null, wa);

        using var vo = MediaFactory.MFCreateMediaType();
        vo.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        vo.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        vo.Set(MediaTypeAttributeKeys.AvgBitrate, 8_000_000u);
        vo.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
        MediaFactory.MFSetAttributeSize(vo, MediaTypeAttributeKeys.FrameSize, W, H);
        MediaFactory.MFSetAttributeRatio(vo, MediaTypeAttributeKeys.FrameRate, Fps, 1);
        MediaFactory.MFSetAttributeRatio(vo, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
        int v = writer.AddStream(vo);
        using var vi = MediaFactory.MFCreateMediaType();
        vi.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        vi.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        vi.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
        MediaFactory.MFSetAttributeSize(vi, MediaTypeAttributeKeys.FrameSize, W, H);
        MediaFactory.MFSetAttributeRatio(vi, MediaTypeAttributeKeys.FrameRate, Fps, 1);
        MediaFactory.MFSetAttributeRatio(vi, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
        writer.SetInputMediaType(v, vi, null);

        using var ao = MediaFactory.MFCreateMediaType();
        ao.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        ao.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
        ao.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        ao.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)Rate);
        ao.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
        ao.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 24000u);
        int a = writer.AddStream(ao);
        using var ai = MediaFactory.MFCreateMediaType();
        ai.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        ai.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
        ai.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        ai.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)Rate);
        ai.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
        ai.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 4u);
        ai.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(Rate * 4));
        writer.SetInputMediaType(a, ai, null);
        writer.BeginWriting();

        int frameBytes = W * H * 3 / 2;
        int samplesPerFrame = Rate / Fps;
        long phase = 0;
        for (int f = 0; f < Fps * Seconds; f++)
        {
            long t = f * 10_000_000L / Fps, d = 10_000_000L / Fps;
            using (var s = Sample(frameBytes, t, d, span =>
            {
                span[..(W * H)].Fill(16);
                span[(W * H)..].Fill(128);
                for (int bit = 0; bit < 12; bit++)
                {
                    if ((f >> bit & 1) == 0) continue;
                    int x0 = 40 + bit % 6 * 120, y0 = 40 + bit / 6 * 120;
                    for (int y = y0; y < y0 + 80; y++) span.Slice(y * W + x0, 80).Fill(235);
                }
            })) writer.WriteSample(v, s);

            using (var s = Sample(samplesPerFrame * 4, t, d, span =>
            {
                var pcm = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(span);
                for (int i = 0; i < samplesPerFrame; i++, phase++)
                    pcm[2 * i] = pcm[2 * i + 1] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * phase / Rate));
            })) writer.WriteSample(a, s);
        }
        writer.Finalize();
    }

    delegate void Filler(Span<byte> data);

    static unsafe IMFSample Sample(int bytes, long time, long duration, Filler fill)
    {
        var buf = MediaFactory.MFCreateMemoryBuffer(bytes);
        buf.Lock(out var p, out _, out _);
        fill(new Span<byte>((void*)p, bytes));
        buf.Unlock();
        buf.CurrentLength = bytes;
        var s = MediaFactory.MFCreateSample();
        s.AddBuffer(buf);
        buf.Dispose();
        s.SampleTime = time;
        s.SampleDuration = duration;
        return s;
    }

    /// <summary>Decodes a file in software; returns each frame's number (or just a count) and the total audio samples.</summary>
    static unsafe (List<int> frames, long audioSamples) Decode(string path, bool readNumbers)
    {
        using var ra = MediaFactory.MFCreateAttributes(1);
        ra.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, 1u);
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, ra);
        var info = Trimmer.Probe(path);
        using (var vt = MediaFactory.MFCreateMediaType())
        {
            vt.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            vt.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, vt);
        }
        int blockAlign = 4;
        if (info.HasAudio)
        {
            using var at = MediaFactory.MFCreateMediaType();
            at.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            at.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            at.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, at);
            using var cur = reader.GetCurrentMediaType(SourceReaderIndex.FirstAudioStream);
            blockAlign = (int)cur.GetUInt32(MediaTypeAttributeKeys.AudioBlockAlignment);
        }

        var frames = new List<int>();
        long audio = 0;
        int open = info.HasAudio ? 2 : 1;
        while (open > 0)
        {
            using var s = reader.ReadSample(SourceReaderIndex.AnyStream, SourceReaderControlFlag.None, out int stream, out var flags, out _);
            if ((flags & SourceReaderFlag.Error) != 0) throw new InvalidOperationException("decode error");
            if ((flags & SourceReaderFlag.EndOfStream) != 0) { open--; continue; }
            if (s == null) continue;
            using var cur = reader.GetCurrentMediaType(stream);
            using var buf = s.ConvertToContiguousBuffer();
            buf.Lock(out var p, out _, out var len);
            try
            {
                if (cur.GetGUID(MediaTypeAttributeKeys.MajorType) == MediaTypeGuids.Audio) { audio += len / blockAlign; continue; }
                if (!readNumbers) { frames.Add(frames.Count); continue; }
                int h = info.Height;
                int pitch = len * 2 / (h * 3);
                byte* y = (byte*)p;
                int n = 0;
                for (int bit = 0; bit < 12; bit++)
                {
                    int cx = 40 + bit % 6 * 120 + 40, cy = 40 + bit / 6 * 120 + 40;
                    if (y[cy * pitch + cx] > 128) n |= 1 << bit;
                }
                frames.Add(n);
            }
            finally { buf.Unlock(); }
        }
        return (frames, audio);
    }
}
