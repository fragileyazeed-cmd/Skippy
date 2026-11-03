using System.Runtime.InteropServices;
using Skippy.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Skippy.Video;

/// <summary>
/// Whole-monitor capture with Windows.Graphics.Capture (no hooks, no injection, works for borderless and
/// fullscreen games alike). Each new frame is copied into <see cref="Latest"/>; the encoder samples that
/// texture at a fixed 30/60 fps, so a 480 Hz desktop costs no more to encode than a 60 Hz one.
/// </summary>
public sealed unsafe class ScreenCapture : IDisposable
{
    static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    readonly GpuDevice _gpu;
    readonly IDirect3DDevice _winrtDevice;
    readonly GraphicsCaptureItem _item;
    readonly Direct3D11CaptureFramePool _pool;
    readonly GraphicsCaptureSession _session;
    SizeInt32 _poolSize;
    volatile bool _disposed;

    /// <summary>Most recent desktop image (BGRA). Access only while holding <see cref="GpuDevice.Lock"/>.</summary>
    public ID3D11Texture2D? Latest { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>Bumped whenever <see cref="Latest"/> is re-created (resolution change).</summary>
    public int Version { get; private set; }
    public long FramesArrived;

    /// <summary>The monitor went away (unplugged, disabled, ...).</summary>
    public event Action? Closed;

    public ScreenCapture(GpuDevice gpu, MonitorInfo monitor, int fps)
    {
        _gpu = gpu;
        _winrtDevice = CreateWinRtDevice(gpu.Device);
        _item = CreateItemForMonitor(monitor.Handle);
        _item.Closed += (_, _) => { if (!_disposed) Closed?.Invoke(); };
        _poolSize = _item.Size;

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);

        try { _session.IsBorderRequired = false; } catch (Exception ex) { Log.Warn($"Borderless capture not available: {ex.Message}"); }
        try { _session.IsCursorCaptureEnabled = true; } catch { }
        try
        {
            // Windows 11 24H2+: ask WGC not to deliver more than ~2x our encode rate (saves work on 240/480 Hz screens).
            _session.MinUpdateInterval = TimeSpan.FromTicks(10_000_000 / (fps * 2));
        }
        catch { }

        _session.StartCapture();
        Log.Info($"Capture started on {monitor.DisplayName} [{monitor.DeviceName}], item size {_poolSize.Width}x{_poolSize.Height}");
    }

    void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed) return;
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame == null) return;
            var size = frame.ContentSize;
            if (size.Width <= 0 || size.Height <= 0) return;

            lock (_gpu.Lock)
            {
                if (_disposed) return;
                if (Latest == null || size.Width != Width || size.Height != Height)
                {
                    Latest?.Dispose();
                    Latest = _gpu.Device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)size.Width,
                        Height = (uint)size.Height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                    });
                    Width = size.Width;
                    Height = size.Height;
                    Version++;
                    Log.Info($"Capture surface is now {Width}x{Height}");
                }

                // release the WinRT surface wrapper right away instead of whenever the GC finalizes it
                using var surface = frame.Surface;
                using var tex = GetTexture(surface);
                var d = tex.Description;
                uint w = Math.Min(d.Width, (uint)Width), h = Math.Min(d.Height, (uint)Height);
                _gpu.Context.CopySubresourceRegion(Latest, 0, 0, 0, 0, tex, 0, new Vortice.Mathematics.Box(0, 0, 0, (int)w, (int)h, 1));
            }
            Interlocked.Increment(ref FramesArrived);

            if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
            {
                _poolSize = size;
                sender.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Frame arrived handler failed", ex);
        }
    }

    // ---------- small preview for the settings window ----------

    ID3D11Texture2D? _mipTex, _previewStaging;
    ID3D11ShaderResourceView? _mipSrv;
    int _mipLevel, _pvW, _pvH, _mipW, _mipH;

    /// <summary>
    /// A ~320 px wide BGRA thumbnail of the latest frame. The GPU shrinks it (mipmaps, proper filtering), so
    /// only a few hundred KB are read back. Called a few times a second, and only while the window is open.
    /// </summary>
    public byte[]? GetPreview(out int width, out int height)
    {
        width = height = 0;
        lock (_gpu.Lock)
        {
            if (_disposed || Latest == null) return null;
            if (_mipTex == null || _mipW != Width || _mipH != Height)
            {
                ReleasePreview();
                int level = 0;
                while ((Width >> level) > 400) level++;
                _mipLevel = level;
                _mipW = Width; _mipH = Height;
                _pvW = Math.Max(1, Width >> level);
                _pvH = Math.Max(1, Height >> level);
                _mipTex = _gpu.Device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)Width, Height = (uint)Height, MipLevels = (uint)(level + 1), ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource, MiscFlags = ResourceOptionFlags.GenerateMips,
                });
                _mipSrv = _gpu.Device.CreateShaderResourceView(_mipTex);
                _previewStaging = _gpu.Device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)_pvW, Height = (uint)_pvH, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
                });
            }

            var ctx = _gpu.Context;
            ctx.CopySubresourceRegion(_mipTex, 0, 0, 0, 0, Latest, 0, null);
            ctx.GenerateMips(_mipSrv!);
            ctx.CopySubresourceRegion(_previewStaging!, 0, 0, 0, 0, _mipTex, (uint)_mipLevel, null);
            var map = ctx.Map(_previewStaging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            var px = new byte[_pvW * _pvH * 4];
            try
            {
                unsafe
                {
                    byte* src = (byte*)map.DataPointer;
                    fixed (byte* dst = px)
                        for (int y = 0; y < _pvH; y++)
                            Buffer.MemoryCopy(src + y * map.RowPitch, dst + y * _pvW * 4, _pvW * 4, _pvW * 4);
                }
            }
            finally { ctx.Unmap(_previewStaging!, 0); }
            width = _pvW;
            height = _pvH;
            return px;
        }
    }

    void ReleasePreview()
    {
        _mipSrv?.Dispose(); _mipSrv = null;
        _mipTex?.Dispose(); _mipTex = null;
        _previewStaging?.Dispose(); _previewStaging = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _pool.FrameArrived -= OnFrameArrived; } catch { }
        try { _session.Dispose(); } catch { }
        try { _pool.Dispose(); } catch { }
        lock (_gpu.Lock)
        {
            ReleasePreview();
            Latest?.Dispose();
            Latest = null;
        }
        try { (_winrtDevice as IDisposable)?.Dispose(); } catch { }
    }

    // ---------- WinRT / COM interop ----------

    static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var insp));
        try { return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(insp); }
        finally { Marshal.Release(insp); }
    }

    static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmon)
    {
        const string cls = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(Native.WindowsCreateString(cls, cls.Length, out var hstr));
        IntPtr factory;
        try
        {
            var iid = IID_IGraphicsCaptureItemInterop;
            Marshal.ThrowExceptionForHR(Native.RoGetActivationFactory(hstr, ref iid, out factory));
        }
        finally { Native.WindowsDeleteString(hstr); }

        try
        {
            // IGraphicsCaptureItemInterop: [3] CreateForWindow, [4] CreateForMonitor
            var vtbl = *(IntPtr**)factory;
            var createForMonitor = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[4];
            var itemIid = IID_IGraphicsCaptureItem;
            IntPtr itemPtr;
            Marshal.ThrowExceptionForHR(createForMonitor(factory, hmon, &itemIid, &itemPtr));
            try { return GraphicsCaptureItem.FromAbi(itemPtr); }
            finally { Marshal.Release(itemPtr); }
        }
        finally { Marshal.Release(factory); }
    }

    static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var unk = ((WinRT.IWinRTObject)surface).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, IID_IDirect3DDxgiInterfaceAccess, out var access));
        try
        {
            var vtbl = *(IntPtr**)access;
            var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[3];
            var iid = typeof(ID3D11Texture2D).GUID;
            IntPtr tex;
            Marshal.ThrowExceptionForHR(getInterface(access, &iid, &tex));
            return new ID3D11Texture2D(tex);
        }
        finally { Marshal.Release(access); }
    }
}
