namespace Fieldbench.Core.Streams;

/// <summary>Direction of bytes relative to this application.</summary>
public enum StreamDirection
{
    /// <summary>Bytes we transmitted.</summary>
    Tx,
    /// <summary>Bytes we received.</summary>
    Rx,
}

/// <summary>
/// One timestamped, directional slice of the raw byte stream.
/// The byte stream is the source of truth; frames are derived views (see IProtocolLens).
/// </summary>
public sealed class StreamChunk
{
    public StreamChunk(long seq, DateTime timestampUtc, StreamDirection direction, byte[] data, int clientId = 0)
    {
        Seq = seq;
        TimestampUtc = timestampUtc;
        Direction = direction;
        Data = data;
        ClientId = clientId;
    }

    /// <summary>Monotonically increasing sequence number within one store.</summary>
    public long Seq { get; }

    public DateTime TimestampUtc { get; }

    public StreamDirection Direction { get; }

    public byte[] Data { get; }

    /// <summary>For multi-client transports (TCP server): which remote endpoint produced/consumed these bytes.</summary>
    public int ClientId { get; }
}
