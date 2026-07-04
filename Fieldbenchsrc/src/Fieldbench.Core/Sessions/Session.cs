using Fieldbench.Core.Lenses;
using Fieldbench.Core.Streams;

namespace Fieldbench.Core.Sessions;

public enum SessionKind
{
    Monitor,
    Master,
    Slave,
}

/// <summary>
/// A protocol viewpoint over one connection: kind + lens + the derived frame list.
/// Frames are a bounded ring (default 100k); the byte stream underneath remains
/// the truth, so switching lens replays the entire retained history.
/// </summary>
public sealed class Session : IDisposable
{
    private readonly object _gate = new();
    private readonly List<Frame> _frames = new();
    private readonly Timer _flushTimer;
    private IProtocolLens _lens;
    private long _droppedFrames;
    private bool _disposed;

    public Session(Connection connection, SessionKind kind, IProtocolLens lens, string? name = null)
    {
        Connection = connection;
        Kind = kind;
        _lens = lens;
        Name = name ?? kind.ToString();

        connection.Store.ChunkAppended += OnChunkAppended;
        connection.Store.Cleared += OnStoreCleared;
        connection.Sessions.Add(this);

        // Periodic flush closes silence-gap frames even when no new bytes arrive.
        _flushTimer = new Timer(_ => FlushTick(), null, 25, 25);
    }

    public Guid Id { get; } = Guid.NewGuid();

    public Connection Connection { get; }

    public SessionKind Kind { get; }

    public string Name { get; set; }

    public IProtocolLens Lens => _lens;

    /// <summary>Frame ring capacity (PRD default 100,000, configurable).</summary>
    public int MaxFrames { get; set; } = 100_000;

    public long TotalFrames { get; private set; }

    public long ErrorFrames { get; private set; }

    /// <summary>Detection runs on monitor/raw streams only.</summary>
    public ProtocolDetector? Detector { get; init; }

    public event Action<IReadOnlyList<Frame>>? FramesAdded;

    /// <summary>Raised when the frame list was rebuilt from scratch (lens switch / clear).</summary>
    public event Action? FramesReset;

    public IReadOnlyList<Frame> SnapshotFrames()
    {
        lock (_gate) return _frames.ToArray();
    }

    public int FrameCount { get { lock (_gate) return _frames.Count; } }

    /// <summary>Buffer fill 0..1 for the status bar gauge.</summary>
    public double BufferUsage { get { lock (_gate) return Math.Min(1.0, (double)_frames.Count / MaxFrames); } }

    private readonly object _detectorGate = new();
    private readonly object _dispatchGate = new();

    private void OnChunkAppended(StreamChunk chunk)
    {
        IReadOnlyList<Frame> frames;
        lock (_gate)
        {
            frames = _lens.Feed(chunk);
            Admit(frames);
        }

        if (Detector is { } det)
        {
            lock (_detectorGate) det.Feed(chunk);
        }

        Dispatch(frames);
    }

    /// <summary>Serialize FramesAdded across producer threads so batch order matches admit order.</summary>
    private void Dispatch(IReadOnlyList<Frame> frames)
    {
        if (frames.Count == 0) return;
        lock (_dispatchGate)
        {
            FramesAdded?.Invoke(frames);
        }
    }

    private void FlushTick()
    {
        if (_disposed) return;
        IReadOnlyList<Frame> frames;
        lock (_gate)
        {
            frames = _lens.FlushPending(DateTime.UtcNow);
            Admit(frames);
        }

        Dispatch(frames);
    }

    private void Admit(IReadOnlyList<Frame> frames)
    {
        if (frames.Count == 0) return;
        foreach (var f in frames)
        {
            _frames.Add(f);
            TotalFrames++;
            if (f.Status == FrameStatus.Error || f.Status == FrameStatus.Warning) ErrorFrames++;
        }

        int overflow = _frames.Count - MaxFrames;
        if (overflow > 0)
        {
            _frames.RemoveRange(0, overflow);
            _droppedFrames += overflow;
        }
    }

    /// <summary>
    /// Retroactive lens switch: replay the full retained byte history through the
    /// new lens. Already-captured data re-frames, re-colors and re-parses (W1 gate).
    /// </summary>
    public void SwitchLens(IProtocolLens newLens)
    {
        lock (_gate)
        {
            // Snapshot inside the gate: a chunk landing between snapshot and swap
            // would otherwise be double-parsed (replay + live) or lost entirely.
            var snapshot = Connection.Store.Snapshot();
            _lens = newLens;
            _frames.Clear();
            TotalFrames = 0;
            ErrorFrames = 0;
            var frames = LensReplay.Replay(newLens, snapshot);
            Admit(frames);
        }

        FramesReset?.Invoke();
    }

    /// <summary>Re-parse in place (e.g. after changing raw split settings).</summary>
    public void Reparse() => SwitchLens(_lens);

    public void ClearFrames()
    {
        lock (_gate)
        {
            _frames.Clear();
            TotalFrames = 0;
            ErrorFrames = 0;
            _lens.Reset();
        }

        FramesReset?.Invoke();
    }

    private void OnStoreCleared()
    {
        ClearFrames();
        Detector?.Reset();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushTimer.Dispose();
        Connection.Store.ChunkAppended -= OnChunkAppended;
        Connection.Store.Cleared -= OnStoreCleared;
        Connection.Sessions.Remove(this);
    }
}
