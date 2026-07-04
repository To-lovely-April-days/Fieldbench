namespace Fieldbench.Core.Transport;

public enum TransportState
{
    Closed,
    Opening,
    Open,
    Listening,
    Faulted,
}

/// <summary>Bytes received from the wire, with the multi-client source when applicable.</summary>
public readonly record struct ReceivedData(byte[] Data, int ClientId);

public sealed class TransportClientInfo
{
    public required int ClientId { get; init; }
    public required string RemoteEndpoint { get; init; }
    public DateTime ConnectedAtUtc { get; init; }
    public long RequestCount { get; set; }
}

/// <summary>
/// A physical/network channel: serial port, TCP client, TCP server or in-process loopback.
/// Transports move raw bytes only; all protocol knowledge lives in lenses/engines.
/// </summary>
public interface ITransport : IAsyncDisposable
{
    TransportState State { get; }

    /// <summary>Short human label, e.g. "COM3 · CH340" or "192.168.1.50:502".</summary>
    string Describe();

    /// <summary>Fired for every received chunk, on a background thread.</summary>
    event Action<ReceivedData>? DataReceived;

    event Action<TransportState>? StateChanged;

    /// <summary>Multi-client transports (TCP server) report joins/leaves; others never fire.</summary>
    event Action? ClientsChanged;

    IReadOnlyList<TransportClientInfo> Clients { get; }

    Task OpenAsync(CancellationToken ct = default);

    Task CloseAsync();

    /// <summary>Send to the peer; for multi-client transports, clientId 0 broadcasts.</summary>
    Task SendAsync(ReadOnlyMemory<byte> data, int clientId = 0, CancellationToken ct = default);
}
