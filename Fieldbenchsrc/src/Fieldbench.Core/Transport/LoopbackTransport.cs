namespace Fieldbench.Core.Transport;

/// <summary>
/// In-process loopback pair used by Demo mode: two endpoints wired together,
/// bytes written to one arrive at the other. An optional corruption hook lets
/// the demo inject wire errors (a flipped CRC bit) to showcase error handling
/// and AI explain without hardware.
/// </summary>
public sealed class LoopbackHub
{
    public LoopbackHub()
    {
        A = new LoopbackTransport(this, "loop·A");
        B = new LoopbackTransport(this, "loop·B");
    }

    public LoopbackTransport A { get; }

    public LoopbackTransport B { get; }

    /// <summary>Optional transformation applied to bytes in flight (demo error injection).</summary>
    public Func<byte[], byte[]>? WireMutator { get; set; }

    /// <summary>Artificial one-way latency, to make the timeline Δ column realistic.</summary>
    public TimeSpan Latency { get; set; } = TimeSpan.FromMilliseconds(4);

    internal async Task RelayAsync(LoopbackTransport from, byte[] data)
    {
        var to = ReferenceEquals(from, A) ? B : A;
        if (Latency > TimeSpan.Zero) await Task.Delay(Latency).ConfigureAwait(false);
        var payload = WireMutator?.Invoke(data) ?? data;
        to.Inject(payload);
    }
}

public sealed class LoopbackTransport : ITransport
{
    private readonly LoopbackHub _hub;
    private readonly string _label;

    internal LoopbackTransport(LoopbackHub hub, string label)
    {
        _hub = hub;
        _label = label;
    }

    public TransportState State { get; private set; } = TransportState.Closed;

    public event Action<ReceivedData>? DataReceived;
    public event Action<TransportState>? StateChanged;
    public event Action? ClientsChanged { add { } remove { } }

    public IReadOnlyList<TransportClientInfo> Clients => Array.Empty<TransportClientInfo>();

    public string Describe() => _label;

    public Task OpenAsync(CancellationToken ct = default)
    {
        SetState(TransportState.Open);
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        SetState(TransportState.Closed);
        return Task.CompletedTask;
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, int clientId = 0, CancellationToken ct = default)
    {
        if (State != TransportState.Open) throw new InvalidOperationException("Loopback endpoint is closed.");
        _ = _hub.RelayAsync(this, data.ToArray());
        return Task.CompletedTask;
    }

    internal void Inject(byte[] data)
    {
        if (State == TransportState.Open) DataReceived?.Invoke(new ReceivedData(data, 0));
    }

    private void SetState(TransportState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    public ValueTask DisposeAsync()
    {
        SetState(TransportState.Closed);
        return ValueTask.CompletedTask;
    }
}
