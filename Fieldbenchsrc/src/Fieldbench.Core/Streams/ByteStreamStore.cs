namespace Fieldbench.Core.Streams;

/// <summary>
/// Stores the raw, timestamped, directional byte stream of one connection.
/// This is the single source of truth: protocol frames are never stored, they are
/// derived by a lens and can be recomputed over the full history at any time
/// (retroactive lens switching, the W1 acceptance gate).
///
/// Bounded by a byte budget; oldest chunks are evicted first (ring semantics).
/// Thread-safe: producers append from transport threads, consumers snapshot from UI.
/// </summary>
public sealed class ByteStreamStore
{
    private readonly object _gate = new();
    private readonly LinkedList<StreamChunk> _chunks = new();
    private long _nextSeq;
    private long _storedBytes;

    public ByteStreamStore(long maxBytes = 32 * 1024 * 1024)
    {
        MaxBytes = maxBytes;
    }

    /// <summary>Total byte budget; oldest chunks are evicted when exceeded.</summary>
    public long MaxBytes { get; set; }

    public long TotalTxBytes { get; private set; }

    public long TotalRxBytes { get; private set; }

    public long StoredBytes { get { lock (_gate) return _storedBytes; } }

    /// <summary>Sequence number of the first chunk still retained (older ones were evicted).</summary>
    public long FirstRetainedSeq { get; private set; }

    /// <summary>Raised after a chunk is appended. Fired on the producer thread.</summary>
    public event Action<StreamChunk>? ChunkAppended;

    /// <summary>Raised after Clear(). Fired on the caller thread.</summary>
    public event Action? Cleared;

    public StreamChunk Append(DateTime timestampUtc, StreamDirection direction, byte[] data, int clientId = 0)
    {
        StreamChunk chunk;
        lock (_gate)
        {
            chunk = new StreamChunk(_nextSeq++, timestampUtc, direction, data, clientId);
            _chunks.AddLast(chunk);
            _storedBytes += data.Length;
            if (direction == StreamDirection.Tx) TotalTxBytes += data.Length;
            else TotalRxBytes += data.Length;

            while (_storedBytes > MaxBytes && _chunks.First is { } first && _chunks.Count > 1)
            {
                _storedBytes -= first.Value.Data.Length;
                FirstRetainedSeq = first.Value.Seq + 1;
                _chunks.RemoveFirst();
            }
        }

        ChunkAppended?.Invoke(chunk);
        return chunk;
    }

    /// <summary>Stable copy of the retained history, in order. Used for retroactive lens re-parse and export.</summary>
    public IReadOnlyList<StreamChunk> Snapshot()
    {
        lock (_gate)
        {
            return _chunks.ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _chunks.Clear();
            _storedBytes = 0;
            TotalTxBytes = 0;
            TotalRxBytes = 0;
            FirstRetainedSeq = _nextSeq;
        }

        Cleared?.Invoke();
    }
}
