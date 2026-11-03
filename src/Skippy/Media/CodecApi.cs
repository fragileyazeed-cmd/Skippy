using System.Runtime.InteropServices;
using Skippy.Core;

namespace Skippy.Media;

/// <summary>Raw ICodecAPI (codecapi.h) access, which Vortice does not wrap. Failures are logged, never fatal.</summary>
public sealed unsafe class CodecApi : IDisposable
{
    static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    public static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid MaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid BPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");

    public const uint RateControl_Cbr = 0, RateControl_PeakConstrainedVbr = 1, RateControl_UnconstrainedVbr = 2;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct Variant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint ui4;
        [FieldOffset(8)] public ulong ui8;
        [FieldOffset(8)] public short boolVal;
    }

    IntPtr _ptr;

    CodecApi(IntPtr ptr) => _ptr = ptr;

    public static CodecApi? From(IntPtr unknown)
    {
        return Marshal.QueryInterface(unknown, IID_ICodecAPI, out var p) == 0 ? new CodecApi(p) : null;
    }

    public bool Set(Guid key, uint value, string name) => SetVariant(key, new Variant { vt = 19, ui4 = value }, name, value.ToString());
    public bool Set(Guid key, bool value, string name) => SetVariant(key, new Variant { vt = 11, boolVal = (short)(value ? -1 : 0) }, name, value.ToString());

    bool SetVariant(Guid key, Variant v, string name, string shown)
    {
        var vtbl = *(IntPtr**)_ptr;
        var setValue = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)vtbl[9];
        int hr = setValue(_ptr, &key, &v);
        if (hr < 0) Log.Warn($"Encoder rejected {name}={shown} (0x{hr:X8})");
        return hr >= 0;
    }

    public void Dispose()
    {
        if (_ptr != IntPtr.Zero) { Marshal.Release(_ptr); _ptr = IntPtr.Zero; }
    }
}
