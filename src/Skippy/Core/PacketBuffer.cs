namespace Skippy.Core;

/// <summary>One encoded access unit / audio frame living inside a <see cref="PacketBuffer"/> chunk.</summary>
public readonly record struct Packet(long Pts, long Duration, bool Key, PacketBuffer.Chunk Chunk, int Offset, int Length)
{
    public ReadOnlySpan<byte> Data => Chunk.Data.AsSpan(Offset, Length);
}

/// <summary>
/// Time-bounded RAM ring buffer of encoded packets. Bytes are packed into large reusable chunks
/// (no per-packet allocations, so no GC pressure), and old packets fall out once they are older
/// than the retention window. A save "pins" the buffer so chunks it is reading are not recycled
/// while the file is written, which lets the encoder keep appending without ever waiting.
/// </summary>
public sealed class PacketBuffer
{
    public sealed class Chunk(int size)
    {
        public readonly byte[] Data = new byte[size];
        public int Used;
        public int Live;
    }

    const int ChunkSize = 4 * 1024 * 1024;
    // Recycled chunks are kept rather than handed to the GC, so a running buffer stops allocating
    // altogether once it is full (no large-object-heap churn over hours).
    const int MaxFreeChunks = 16;

    readonly object _gate = new();
    readonly Queue<Packet> _packets = new();
    readonly Stack<Chunk> _free = new();
    readonly List<Chunk> _deferred = new();
    Chunk? _current;
    int _pins;
    long _liveBytes;

    public long RetentionHns { get; set; }
    public long MaxBytes { get; set; } = 700L * 1024 * 1024;

    /// <summary>Bytes held by packets currently in the buffer.</summary>
    public long LiveBytes { get { lock (_gate) return _liveBytes; } }
    public int Count { get { lock (_gate) return _packets.Count; } }

    /// <summary>Time covered by the buffered packets (100 ns units).</summary>
    public long SpanHns
    {
        get { lock (_gate) return _packets.Count == 0 ? 0 : _newestEnd - _packets.Peek().Pts; }
    }
    long _newestEnd;

    public void Append(ReadOnlySpan<byte> data, long pts, long duration, bool key)
    {
        lock (_gate)
        {
            if (_current == null || _current.Data.Length - _current.Used < data.Length)
            {
                if (_current != null && _current.Live == 0) Release(_current);
                _current = Rent(data.Length);
            }
            var c = _current;
            data.CopyTo(c.Data.AsSpan(c.Used));
            _packets.Enqueue(new Packet(pts, duration, key, c, c.Used, data.Length));
            c.Used += data.Length;
            c.Live++;
            _liveBytes += data.Length;
            _newestEnd = pts + duration;
            Evict(pts);
        }
    }

    void Evict(long newestPts)
    {
        while (_packets.Count > 0)
        {
            var p = _packets.Peek();
            bool tooOld = newestPts - p.Pts > RetentionHns;
            bool tooBig = _liveBytes > MaxBytes;
            if (!tooOld && !tooBig) break;
            _packets.Dequeue();
            _liveBytes -= p.Length;
            if (--p.Chunk.Live == 0 && p.Chunk != _current) Release(p.Chunk);
        }
    }

    Chunk Rent(int min)
    {
        if (min <= ChunkSize && _free.Count > 0) { var c = _free.Pop(); c.Used = 0; c.Live = 0; return c; }
        return new Chunk(Math.Max(ChunkSize, min));
    }

    void Release(Chunk c)
    {
        if (_pins > 0) { _deferred.Add(c); return; }
        if (c.Data.Length == ChunkSize && _free.Count < MaxFreeChunks) _free.Push(c);
    }

    public void Clear()
    {
        lock (_gate)
        {
            while (_packets.Count > 0)
            {
                var p = _packets.Dequeue();
                if (--p.Chunk.Live == 0 && p.Chunk != _current) Release(p.Chunk);
            }
            if (_current != null) { Release(_current); _current = null; }
            _liveBytes = 0;
        }
    }

    /// <summary>Copy the packet index (not the bytes) and pin the chunks until the returned handle is disposed.</summary>
    public Snapshot TakeSnapshot()
    {
        lock (_gate)
        {
            _pins++;
            return new Snapshot(this, _packets.ToArray());
        }
    }

    void Unpin()
    {
        lock (_gate)
        {
            if (--_pins > 0) return;
            foreach (var c in _deferred)
                if (c.Data.Length == ChunkSize && _free.Count < MaxFreeChunks && c != _current) _free.Push(c);
            _deferred.Clear();
        }
    }

    public sealed class Snapshot(PacketBuffer owner, Packet[] packets) : IDisposable
    {
        public Packet[] Packets { get; } = packets;
        int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Unpin(); }
    }
}
