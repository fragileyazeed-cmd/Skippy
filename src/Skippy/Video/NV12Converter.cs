using Skippy.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Skippy.Video;

/// <summary>
/// GPU colour conversion + scaling (BGRA desktop → NV12 BT.709 limited range) using the D3D11 video
/// processor, i.e. the fixed-function hardware block, not shaders competing with the game.
/// Output is letterboxed if the monitor's aspect ratio changes while the encoder keeps running.
/// </summary>
public sealed class NV12Converter : IDisposable
{
    readonly GpuDevice _gpu;
    readonly ID3D11VideoDevice _vdev;
    readonly ID3D11VideoContext1 _vctx;
    readonly int _outW, _outH, _fps;
    readonly ID3D11Texture2D[] _targets;
    readonly ID3D11VideoProcessorOutputView?[] _outViews;
    int _next;

    ID3D11VideoProcessorEnumerator? _enum;
    ID3D11VideoProcessor? _vp;
    ID3D11VideoProcessorInputView? _inView;
    ID3D11Texture2D? _inTex;
    int _inVersion = -1;

    public int OutputWidth => _outW;
    public int OutputHeight => _outH;

    public NV12Converter(GpuDevice gpu, int outW, int outH, int fps, int poolSize)
    {
        _gpu = gpu;
        _outW = outW;
        _outH = outH;
        _fps = fps;
        _vdev = gpu.Device.QueryInterface<ID3D11VideoDevice>();
        _vctx = gpu.Context.QueryInterface<ID3D11VideoContext1>();
        _targets = new ID3D11Texture2D[poolSize];
        _outViews = new ID3D11VideoProcessorOutputView?[poolSize];
        for (int i = 0; i < poolSize; i++)
        {
            _targets[i] = gpu.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)outW,
                Height = (uint)outH,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
            });
        }
    }

    /// <summary>Converts the capture's latest frame into the next NV12 texture of the pool. Caller holds <see cref="GpuDevice.Lock"/>.</summary>
    public ID3D11Texture2D? Convert(ScreenCapture capture)
    {
        var src = capture.Latest;
        if (src == null) return null;

        if (_vp == null || _inVersion != capture.Version || !ReferenceEquals(_inTex, src))
            Setup(capture);

        int i = _next;
        _next = (_next + 1) % _targets.Length;
        _outViews[i] ??= _vdev.CreateVideoProcessorOutputView(_targets[i], _enum!, new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
        });

        var streams = new[] { new VideoProcessorStream { Enable = true, InputSurface = _inView! } };
        _vctx.VideoProcessorBlt(_vp!, _outViews[i]!, 0, 1, streams).CheckError();
        return _targets[i];
    }

    void Setup(ScreenCapture capture)
    {
        ReleaseProcessor();
        int inW = capture.Width, inH = capture.Height;
        _enum = _vdev.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)_fps, 1),
            InputWidth = (uint)inW,
            InputHeight = (uint)inH,
            OutputFrameRate = new Rational((uint)_fps, 1),
            OutputWidth = (uint)_outW,
            OutputHeight = (uint)_outH,
            Usage = VideoUsage.OptimalSpeed,
        });
        _vp = _vdev.CreateVideoProcessor(_enum, 0);
        _inTex = capture.Latest;
        _inView = _vdev.CreateVideoProcessorInputView(_inTex!, _enum, new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
        });
        // output views are tied to the enumerator: rebuild them lazily
        for (int k = 0; k < _outViews.Length; k++) { _outViews[k]?.Dispose(); _outViews[k] = null; }
        _inVersion = capture.Version;

        // letterbox into the fixed encoder frame
        double scale = Math.Min((double)_outW / inW, (double)_outH / inH);
        int w = Math.Min(_outW, (int)Math.Round(inW * scale)) & ~1;
        int h = Math.Min(_outH, (int)Math.Round(inH * scale)) & ~1;
        int x = ((_outW - w) / 2) & ~1, y = ((_outH - h) / 2) & ~1;

        _vctx.VideoProcessorSetStreamFrameFormat(_vp, 0, VideoFrameFormat.Progressive);
        _vctx.VideoProcessorSetStreamColorSpace1(_vp, 0, ColorSpaceType.RgbFullG22NoneP709);
        _vctx.VideoProcessorSetOutputColorSpace1(_vp, ColorSpaceType.YcbcrStudioG22LeftP709);
        _vctx.VideoProcessorSetStreamSourceRect(_vp, 0, true, new Vortice.RawRect(0, 0, inW, inH));
        _vctx.VideoProcessorSetStreamDestRect(_vp, 0, true, new Vortice.RawRect(x, y, x + w, y + h));
        _vctx.VideoProcessorSetOutputTargetRect(_vp, true, new Vortice.RawRect(0, 0, _outW, _outH));
        _vctx.VideoProcessorSetOutputBackgroundColor(_vp, false, new VideoColor { Rgba = new VideoColorRgba { R = 0, G = 0, B = 0, A = 1 } });
        _vctx.VideoProcessorSetStreamAutoProcessingMode(_vp, 0, false);
        Log.Info($"Video processor: {inW}x{inH} -> {_outW}x{_outH} (picture {w}x{h} at {x},{y})");
    }

    void ReleaseProcessor()
    {
        _inView?.Dispose(); _inView = null;
        _vp?.Dispose(); _vp = null;
        _enum?.Dispose(); _enum = null;
        _inTex = null;
    }

    public void Dispose()
    {
        lock (_gpu.Lock)
        {
            for (int k = 0; k < _outViews.Length; k++) _outViews[k]?.Dispose();
            ReleaseProcessor();
            foreach (var t in _targets) t.Dispose();
            _vctx.Dispose();
            _vdev.Dispose();
        }
    }
}
