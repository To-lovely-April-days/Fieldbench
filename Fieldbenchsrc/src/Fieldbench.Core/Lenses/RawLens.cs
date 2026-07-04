using Fieldbench.Core.Streams;

namespace Fieldbench.Core.Lenses;

public enum RawSplitMode
{
    /// <summary>Split on silence gap (default 20 ms). Cosmetic only — display beautification.</summary>
    SilenceGap,
    FixedLength,
    Terminator,
}

/// <summary>
/// The default lens for Monitor sessions: no protocol knowledge, chunks the
/// stream into display blocks. Time/direction/length/hex never depend on
/// protocol knowledge, so an unknown protocol blocks nothing (PRD §5.1).
/// </summary>
public sealed class RawLens : IProtocolLens
{
    private readonly FramingBuffer _tx = new();
    private readonly FramingBuffer _rx = new();
    private long _nextId;

    public string Id => "raw";

    public string DisplayName => "Raw";

    public RawSplitMode SplitMode { get; set; } = RawSplitMode.SilenceGap;

    /// <summary>Silence gap that closes a block, milliseconds.</summary>
    public double GapMs { get; set; } = 20;

    public int FixedLength { get; set; } = 16;

    public byte[] Terminator { get; set; } = [0x0A];

    public IReadOnlyList<Frame> Feed(StreamChunk chunk)
    {
        var buffer = chunk.Direction == StreamDirection.Tx ? _tx : _rx;
        var result = new List<Frame>();

        // Gap mode: if the pause before this chunk exceeded the gap, flush what was pending.
        if (SplitMode == RawSplitMode.SilenceGap && buffer.Length > 0)
        {
            var gap = (chunk.TimestampUtc - buffer.LastUtc).TotalMilliseconds;
            if (gap >= GapMs)
            {
                result.Add(MakeFrame(buffer.FirstUtc, buffer.ClientId, chunk.Direction, buffer.TakeAll()));
            }
        }

        buffer.Append(chunk);
        Extract(buffer, chunk.Direction, result);
        return result;
    }

    private void Extract(FramingBuffer buffer, StreamDirection dir, List<Frame> result)
    {
        switch (SplitMode)
        {
            case RawSplitMode.FixedLength:
                while (buffer.Length >= FixedLength)
                {
                    result.Add(MakeFrame(buffer.FirstUtc, buffer.ClientId, dir, buffer.Take(FixedLength)));
                }

                break;

            case RawSplitMode.Terminator:
                while (true)
                {
                    int idx = buffer.Span.IndexOf(Terminator);
                    if (idx < 0) break;
                    result.Add(MakeFrame(buffer.FirstUtc, buffer.ClientId, dir, buffer.Take(idx + Terminator.Length)));
                }

                break;

            case RawSplitMode.SilenceGap:
                // Nothing to do here; blocks close on the next gap or FlushPending tick.
                break;
        }
    }

    public IReadOnlyList<Frame> FlushPending(DateTime nowUtc)
    {
        var result = new List<Frame>();
        FlushIfExpired(_tx, StreamDirection.Tx, nowUtc, result);
        FlushIfExpired(_rx, StreamDirection.Rx, nowUtc, result);
        return result;
    }

    private void FlushIfExpired(FramingBuffer buffer, StreamDirection dir, DateTime nowUtc, List<Frame> result)
    {
        if (buffer.Length == 0) return;
        bool expired = nowUtc == DateTime.MaxValue
                       || SplitMode != RawSplitMode.SilenceGap
                       || (nowUtc - buffer.LastUtc).TotalMilliseconds >= GapMs;
        if (expired)
        {
            result.Add(MakeFrame(buffer.FirstUtc, buffer.ClientId, dir, buffer.TakeAll()));
        }
    }

    private Frame MakeFrame(DateTime tsUtc, int clientId, StreamDirection dir, byte[] bytes) => new()
    {
        Id = _nextId++,
        TimestampUtc = tsUtc,
        Direction = dir,
        ClientId = clientId,
        Bytes = bytes,
        Status = FrameStatus.Raw,
        Summary = AsciiPreview(bytes),
        Fields = [new FrameField(0, bytes.Length, FieldKind.Data, "Raw", $"{bytes.Length} bytes")],
    };

    /// <summary>Printable ASCII with middle dots for control bytes — the ASCII column.</summary>
    public static string AsciiPreview(ReadOnlySpan<byte> bytes, int max = 64)
    {
        var n = Math.Min(bytes.Length, max);
        return string.Create(n, bytes[..n].ToArray(), static (dst, src) =>
        {
            for (int i = 0; i < src.Length; i++)
            {
                dst[i] = src[i] is >= 0x20 and < 0x7F ? (char)src[i] : '·';
            }
        });
    }

    public void Reset()
    {
        _tx.Clear();
        _rx.Clear();
        _nextId = 0;
    }
}
