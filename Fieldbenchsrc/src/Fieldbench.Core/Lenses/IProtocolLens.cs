using Fieldbench.Core.Streams;

namespace Fieldbench.Core.Lenses;

/// <summary>
/// A protocol lens turns the raw byte stream into frames: it owns framing
/// (where does a frame start/end), parsing (field tree), coloring (FieldKind
/// spans) and the one-line summary. Lenses are stateful and incremental —
/// feed chunks as they arrive — and cheap to rebuild: retroactive switching
/// re-runs a fresh lens over the full stream snapshot (Wireshark dissector model).
///
/// v1.0 ships Raw, Modbus RTU and Modbus TCP. The interface stays internal-only
/// (no plugin loading) but is the seam future protocols implement.
/// </summary>
public interface IProtocolLens
{
    string Id { get; }

    string DisplayName { get; }

    /// <summary>
    /// Feed one stream chunk; returns zero or more completed frames.
    /// Framing may hold bytes back waiting for more data (sticky frames).
    /// </summary>
    IReadOnlyList<Frame> Feed(StreamChunk chunk);

    /// <summary>
    /// Flush any pending bytes as frames (silence-gap expiry or stream end).
    /// <paramref name="nowUtc"/> lets timers decide whether the gap elapsed.
    /// </summary>
    IReadOnlyList<Frame> FlushPending(DateTime nowUtc);

    /// <summary>Reset all framing state (before a full re-parse).</summary>
    void Reset();
}

/// <summary>
/// Rebuilds the complete frame list of a lens from a stream snapshot —
/// the retroactive lens switch (W1 acceptance: history must fully re-parse).
/// </summary>
public static class LensReplay
{
    public static List<Frame> Replay(IProtocolLens lens, IReadOnlyList<StreamChunk> snapshot)
    {
        lens.Reset();
        var frames = new List<Frame>(snapshot.Count);
        foreach (var chunk in snapshot)
        {
            frames.AddRange(lens.Feed(chunk));
        }

        frames.AddRange(lens.FlushPending(DateTime.MaxValue));
        return frames;
    }
}
