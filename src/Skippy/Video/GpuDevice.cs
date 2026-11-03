using Skippy.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Skippy.Video;

/// <summary>D3D11 device created on the GPU that drives the captured monitor (so capture and encode never cross adapters).</summary>
public sealed class GpuDevice : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public uint VendorId { get; }
    public string AdapterName { get; }

    /// <summary>Serialises all immediate-context work between the capture callback and the encode thread.</summary>
    public object Lock { get; } = new();

    public GpuDevice(IntPtr hmonitor)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? chosen = null;
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            bool match = false;
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                using (output) if (output.Description.Monitor == hmonitor) match = true;
            }
            if (match && chosen == null) chosen = adapter;
            else adapter.Dispose();
        }
        if (chosen == null)
        {
            factory.EnumAdapters1(0, out chosen).CheckError();
            Log.Warn("Monitor not found on any adapter output, using adapter 0");
        }

        using (chosen)
        {
            var desc = chosen!.Description1;
            VendorId = desc.VendorId;
            AdapterName = desc.Description;
            var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
            FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
            D3D11.D3D11CreateDevice(chosen, DriverType.Unknown, flags, levels, out ID3D11Device dev, out ID3D11DeviceContext ctx).CheckError();
            Device = dev!;
            Context = ctx!;
        }
        using (var mt = Device.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
        Log.Info($"GPU: {AdapterName} (vendor 0x{VendorId:X4})");
    }

    public void Dispose()
    {
        Context.ClearState();
        Context.Flush();
        Context.Dispose();
        Device.Dispose();
    }
}
