using Skippy.Core;
using Skippy.Media;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace Skippy.Video;

public delegate void EncodedHandler(ReadOnlySpan<byte> annexB, long pts, long duration);

/// <summary>
/// H.264 encoding through Media Foundation transforms. Hardware encoders (AMD AMF, NVIDIA NVENC,
/// Intel Quick Sync) are exposed by their drivers as async MFTs and take D3D11 NV12 textures directly,
/// so frames never leave the GPU. Falls back to the Microsoft software encoder if none works.
/// </summary>
public sealed class H264Encoder : IDisposable
{
    const int MFT_ENUM_FLAG_SYNCMFT = 0x1, MFT_ENUM_FLAG_ASYNCMFT = 0x2, MFT_ENUM_FLAG_HARDWARE = 0x4, MFT_ENUM_FLAG_SORTANDFILTER = 0x40;
    const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);

    readonly GpuDevice _gpu;
    readonly int _w, _h, _fps, _bitrate;
    readonly EncodedHandler _onOutput;
    IMFTransform? _mft;
    IMFDXGIDeviceManager? _devManager;
    IMFMediaEventGenerator? _events;
    Thread? _eventThread;
    volatile bool _stopping;
    int _needInput;
    bool _providesSamples;
    int _outBufferSize;

    // software path
    ID3D11Texture2D? _staging;

    public string Name { get; private set; } = "";
    public bool IsHardware { get; private set; }
    public long Dropped;
    public event Action<Exception>? Failed;

    public H264Encoder(GpuDevice gpu, int width, int height, int fps, int bitrate, EncodedHandler onOutput, bool forceSoftware = false)
    {
        _gpu = gpu; _w = width; _h = height; _fps = fps; _bitrate = bitrate; _onOutput = onOutput;

        var candidates = new List<(IMFActivate act, string name, bool hw)>();
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };

        if (!forceSoftware)
        {
            try
            {
                var hw = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder,
                    MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER, input, output);
                string myVendor = $"VEN_{gpu.VendorId:X4}";
                var list = hw.Select(a => (act: a, name: Str(a, TransformAttributeKeys.MftFriendlyNameAttribute), vendor: Str(a, TransformAttributeKeys.MftEnumHardwareVendorIdAttribute))).ToList();
                foreach (var c in list) Log.Info($"Found hardware H.264 encoder: {c.name} ({c.vendor})");
                // encoder of the GPU that owns the monitor first
                foreach (var c in list.OrderByDescending(c => string.Equals(c.vendor, myVendor, StringComparison.OrdinalIgnoreCase)))
                    candidates.Add((c.act, c.name, true));
            }
            catch (Exception ex) { Log.Warn($"Hardware encoder enumeration failed: {ex.Message}"); }
        }
        try
        {
            var sw = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER, input, output);
            foreach (var a in sw) candidates.Add((a, Str(a, TransformAttributeKeys.MftFriendlyNameAttribute), false));
        }
        catch (Exception ex) { Log.Warn($"Software encoder enumeration failed: {ex.Message}"); }

        foreach (var (act, name, hw) in candidates)
        {
            if (_mft != null) { act.Dispose(); continue; }
            try
            {
                Init(act, hw);
                Name = string.IsNullOrEmpty(name) ? (hw ? "Hardware H.264" : "Software H.264") : name;
                IsHardware = hw;
                Log.Info($"Using encoder: {Name} ({(hw ? "hardware" : "software")}), {_w}x{_h}@{_fps}, {_bitrate / 1_000_000.0:0.#} Mbps");
            }
            catch (Exception ex)
            {
                Log.Warn($"Encoder '{name}' failed to start: {ex.Message}");
                Teardown();
            }
            finally { act.Dispose(); }
        }
        if (_mft == null) throw new InvalidOperationException("No usable H.264 encoder found on this PC.");
    }

    static string Str(IMFAttributes a, Guid key)
    {
        try { return a.GetString(key) ?? ""; } catch { return ""; }
    }

    void Init(IMFActivate act, bool hardware)
    {
        _mft = act.ActivateObject<IMFTransform>();
        var attrs = _mft.Attributes;
        bool isAsync = MediaFactory.MFGetAttributeUInt32(attrs, TransformAttributeKeys.TransformAsync, 0) != 0;
        if (isAsync) attrs.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
        if (hardware && !isAsync) throw new InvalidOperationException("expected an async hardware MFT");

        if (hardware)
        {
            _devManager = MediaFactory.MFCreateDXGIDeviceManager();
            _devManager.ResetDevice(_gpu.Device).CheckError();
            _mft.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(nuint)_devManager.NativePointer);
        }

        using (var api = CodecApi.From(_mft.NativePointer))
        {
            if (api != null)
            {
                api.Set(CodecApi.RateControlMode, CodecApi.RateControl_PeakConstrainedVbr, "RateControlMode");
                api.Set(CodecApi.MeanBitRate, (uint)_bitrate, "MeanBitRate");
                api.Set(CodecApi.MaxBitRate, (uint)(_bitrate * 3L / 2), "MaxBitRate");
                api.Set(CodecApi.GopSize, (uint)_fps, "GopSize");               // keyframe every second
                api.Set(CodecApi.BPictureCount, 0u, "BPictureCount");           // no reordering: pts == dts
                api.Set(CodecApi.LowLatencyMode, true, "LowLatencyMode");
            }
        }

        using (var outType = MediaFactory.MFCreateMediaType())
        {
            outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)_bitrate);
            MediaFactory.MFSetAttributeSize(outType, MediaTypeAttributeKeys.FrameSize, (uint)_w, (uint)_h);
            MediaFactory.MFSetAttributeRatio(outType, MediaTypeAttributeKeys.FrameRate, (uint)_fps, 1);
            MediaFactory.MFSetAttributeRatio(outType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
            outType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);       // progressive
            outType.Set(MediaTypeAttributeKeys.Mpeg2Profile, 100u);      // H.264 High
            SetColor(outType);
            _mft.SetOutputType(0, outType, 0);
        }

        IMFMediaType? inType = null;
        for (int i = 0; ; i++)
        {
            IMFMediaType t;
            try { t = _mft.GetInputAvailableType(0, i); } catch { break; }
            if (t.GetGUID(MediaTypeAttributeKeys.Subtype) == VideoFormatGuids.NV12) { inType = t; break; }
            t.Dispose();
        }
        if (inType == null)
        {
            inType = MediaFactory.MFCreateMediaType();
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        }
        using (inType)
        {
            MediaFactory.MFSetAttributeSize(inType, MediaTypeAttributeKeys.FrameSize, (uint)_w, (uint)_h);
            MediaFactory.MFSetAttributeRatio(inType, MediaTypeAttributeKeys.FrameRate, (uint)_fps, 1);
            MediaFactory.MFSetAttributeRatio(inType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
            inType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            SetColor(inType);
            _mft.SetInputType(0, inType, 0);
        }

        var info = _mft.GetOutputStreamInfo(0);
        _providesSamples = (info.Flags & (int)(OutputStreamInfoFlags.OutputStreamProvidesSamples | OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;
        _outBufferSize = Math.Max(info.Size, _w * _h * 2);

        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        if (isAsync)
        {
            _events = _mft.QueryInterface<IMFMediaEventGenerator>();
            _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "Skippy encoder events", Priority = ThreadPriority.AboveNormal };
            _eventThread.Start();
        }
        else
        {
            _staging = _gpu.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_w, Height = (uint)_h, MipLevels = 1, ArraySize = 1, Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
            });
        }
    }

    static void SetColor(IMFMediaType t)
    {
        t.Set(MediaTypeAttributeKeys.VideoPrimaries, 2u);     // BT.709
        t.Set(MediaTypeAttributeKeys.TransferFunction, 5u);   // BT.709
        t.Set(MediaTypeAttributeKeys.YuvMatrix, 1u);          // BT.709
        t.Set(MediaTypeAttributeKeys.VideoNominalRange, 2u);  // 16-235
    }

    /// <summary>Annex-B SPS/PPS from the current output type, if the encoder reports it there.</summary>
    public byte[]? GetSequenceHeader()
    {
        try
        {
            using var t = _mft!.GetOutputCurrentType(0);
            return t.GetBlobSize(MediaTypeAttributeKeys.MpegSequenceHeader, out var size).Success && size > 0
                ? t.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader) : null;
        }
        catch { return null; }
    }

    /// <summary>Feeds one NV12 frame. Returns false if the encoder was busy and the frame was dropped.</summary>
    public bool Submit(ID3D11Texture2D nv12, long pts, long duration)
    {
        var mft = _mft;
        if (mft == null || _stopping) return false;

        if (_events != null)
        {
            if (Interlocked.CompareExchange(ref _needInput, 0, 0) <= 0) { Interlocked.Increment(ref Dropped); return false; }
            Interlocked.Decrement(ref _needInput);
            using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, nv12, 0, false);
            using (var b2 = buffer.QueryInterface<IMF2DBuffer>()) buffer.CurrentLength = b2.ContiguousLength;
            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = pts;
            sample.SampleDuration = duration;
            mft.ProcessInput(0, sample, 0);
            return true;
        }

        // software: read the NV12 frame back and run the synchronous MFT inline
        using (var sample = ReadBack(nv12, pts, duration))
            mft.ProcessInput(0, sample, 0);
        while (PullOutput(mft)) { }
        return true;
    }

    unsafe IMFSample ReadBack(ID3D11Texture2D nv12, long pts, long duration)
    {
        int size = _w * _h * 3 / 2;
        var buffer = MediaFactory.MFCreateMemoryBuffer(size);
        lock (_gpu.Lock)
        {
            _gpu.Context.CopyResource(_staging!, nv12);
            var map = _gpu.Context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                buffer.Lock(out var dst, out _, out _);
                byte* d = (byte*)dst, s = (byte*)map.DataPointer;
                int pitch = (int)map.RowPitch;
                for (int y = 0; y < _h; y++) Buffer.MemoryCopy(s + y * pitch, d + y * _w, _w, _w);
                byte* uvSrc = s + pitch * _h;   // NV12 staging: UV plane follows the Y plane at the same pitch
                byte* uvDst = d + _w * _h;
                for (int y = 0; y < _h / 2; y++) Buffer.MemoryCopy(uvSrc + y * pitch, uvDst + y * _w, _w, _w);
                buffer.Unlock();
            }
            finally { _gpu.Context.Unmap(_staging!, 0); }
        }
        buffer.CurrentLength = size;
        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        buffer.Dispose();
        sample.SampleTime = pts;
        sample.SampleDuration = duration;
        return sample;
    }

    void EventLoop()
    {
        try
        {
            while (!_stopping)
            {
                using var ev = _events!.GetEvent(0);
                var type = ev.EventType;
                if (type == MediaEventTypes.TransformNeedInput) Interlocked.Increment(ref _needInput);
                else if (type == MediaEventTypes.TransformHaveOutput) PullOutput(_mft!);
                else if (type == MediaEventTypes.Error) throw new InvalidOperationException($"Encoder error event 0x{ev.Status.Code:X8}");
            }
        }
        catch (Exception ex)
        {
            if (!_stopping)
            {
                Log.Error("Encoder event loop stopped", ex);
                Failed?.Invoke(ex);
            }
        }
    }

    /// <summary>Gets one output sample; returns false when the MFT needs more input.</summary>
    bool PullOutput(IMFTransform mft)
    {
        IMFSample? own = null;
        if (!_providesSamples)
        {
            own = MediaFactory.MFCreateSample();
            using var b = MediaFactory.MFCreateMemoryBuffer(_outBufferSize);
            own.AddBuffer(b);
        }
        var odb = new OutputDataBuffer { StreamID = 0, Sample = own! };
        var hr = mft.ProcessOutput(0, 1, ref odb, out _);
        try
        {
            if (hr.Code == MF_E_TRANSFORM_NEED_MORE_INPUT) return false;
            if (hr.Code == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                using var t = mft.GetOutputAvailableType(0, 0);
                mft.SetOutputType(0, t, 0);
                Log.Info("Encoder output stream changed, renegotiated");
                return true;
            }
            hr.CheckError();
            var s = odb.Sample;
            if (s == null) return true;
            long pts = 0, dur = 0;
            try { pts = s.SampleTime; } catch { }
            try { dur = s.SampleDuration; } catch { }
            using var buf = s.ConvertToContiguousBuffer();
            buf.Lock(out var ptr, out _, out var len);
            try
            {
                unsafe { _onOutput(new ReadOnlySpan<byte>((void*)ptr, len), pts, dur); }
            }
            finally { buf.Unlock(); }
            return true;
        }
        finally
        {
            // the marshaller hands back a new wrapper even when the MFT filled our own sample: release that pointer only once
            if (odb.Sample != null && (own == null || odb.Sample.NativePointer != own.NativePointer)) odb.Sample.Dispose();
            own?.Dispose();
            odb.Events?.Dispose();
        }
    }

    void Teardown()
    {
        _stopping = true;
        if (_mft != null)
        {
            try { using var sd = _mft.QueryInterfaceOrNull<IMFShutdown>(); sd?.Shutdown(); } catch { }
        }
        _eventThread?.Join(2000);
        _eventThread = null;
        _events?.Dispose(); _events = null;
        _mft?.Dispose(); _mft = null;
        _devManager?.Dispose(); _devManager = null;
        _staging?.Dispose(); _staging = null;
        _stopping = false;
        _needInput = 0;
    }

    public void Dispose()
    {
        Teardown();
        _stopping = true;
    }
}
