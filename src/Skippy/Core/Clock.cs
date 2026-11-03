using System.Diagnostics;

namespace Skippy.Core;

/// <summary>
/// One shared timeline for every stream: 100 ns ticks since the app started, derived from QPC.
/// Video frames and audio packets are both stamped on this clock so they can be muxed by timestamp.
/// </summary>
public static class Clock
{
    static readonly long Epoch = Stopwatch.GetTimestamp();
    static readonly double ToHns = 10_000_000.0 / Stopwatch.Frequency;

    /// <summary>Now, in 100 ns units since app start.</summary>
    public static long Now => QpcToHns(Stopwatch.GetTimestamp());

    public static long QpcToHns(long qpc) => (long)((qpc - Epoch) * ToHns);

    public static long HnsToQpc(long hns) => Epoch + (long)(hns / ToHns);

    /// <summary>WASAPI reports packet times as QPC already converted to 100 ns units.</summary>
    public static long FromQpc100ns(long qpc100ns) => qpc100ns - (long)(Epoch * ToHns);
}
