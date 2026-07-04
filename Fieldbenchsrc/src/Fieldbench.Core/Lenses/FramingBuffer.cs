using Fieldbench.Core.Streams;

namespace Fieldbench.Core.Lenses;

/// <summary>
/// Per-direction pending byte accumulator used by framers. Serial read events
/// arrive with arbitrary chunking (Windows driver timing), so framers must be
/// able to join and split across chunk boundaries while keeping the timestamp
/// of the first byte of every extracted frame.
/// </summary>
public sealed class FramingBuffer
{
    private byte[] _buf = new byte[512];
    private int _len;

    /// <summary>Timestamp of the first pending byte.</summary>
    public DateTime FirstUtc { get; private set; }

    /// <summary>Timestamp of the most recently appended byte.</summary>
    public DateTime LastUtc { get; private set; }

    public int ClientId { get; private set; }

    public int Length => _len;

    public ReadOnlySpan<byte> Span => _buf.AsSpan(0, _len);

    public void Append(StreamChunk chunk)
    {
        if (_len == 0)
        {
            FirstUtc = chunk.TimestampUtc;
            ClientId = chunk.ClientId;
        }

        LastUtc = chunk.TimestampUtc;
        EnsureCapacity(_len + chunk.Data.Length);
        chunk.Data.CopyTo(_buf.AsSpan(_len));
        _len += chunk.Data.Length;
    }

    /// <summary>Remove and return the first <paramref name="count"/> bytes.</summary>
    public byte[] Take(int count)
    {
        var result = new byte[count];
        Array.Copy(_buf, result, count);
        _len -= count;
        Array.Copy(_buf, count, _buf, 0, _len);
        // After a partial take the remaining bytes belong to a follow-on frame whose
        // start time we approximate with the last append time.
        if (_len > 0) FirstUtc = LastUtc;
        return result;
    }

    public byte[] TakeAll() => Take(_len);

    public void Clear() => _len = 0;

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buf.Length) return;
        int size = _buf.Length;
        while (size < needed) size *= 2;
        Array.Resize(ref _buf, size);
    }
}
