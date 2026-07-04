using Fieldbench.Core.Streams;
using Fieldbench.Core.Transport;

namespace Fieldbench.Core.Sessions;

/// <summary>
/// A physical/network channel plus its captured byte stream. Sessions attach to
/// a connection to view (and drive) the same stream through different lenses.
/// Hot parameter changes reconfigure the transport in place — sessions and the
/// captured history always survive (PRD hard rule: never delete-and-recreate).
/// </summary>
public sealed class Connection : IAsyncDisposable
{
    private ITransport _transport;
    private CancellationTokenSource? _reconnectCts;

    public Connection(ConnectionConfig config, string? name = null)
    {
        Config = config.Clone();
        Name = name ?? config.ShortLabel();
        Store = new ByteStreamStore();
        _transport = CreateTransport(Config);
        HookTransport(_transport);
    }

    /// <summary>Demo-mode constructor: wraps an existing loopback endpoint.</summary>
    public Connection(ITransport transport, ConnectionConfig config, string name)
    {
        Config = config;
        Name = name;
        Store = new ByteStreamStore();
        _transport = transport;
        HookTransport(_transport);
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string Name { get; set; }

    public ConnectionConfig Config { get; private set; }

    public ByteStreamStore Store { get; }

    public ITransport Transport => _transport;

    public TransportState State => _transport.State;

    public DateTime? ConnectedAtUtc { get; private set; }

    public List<Session> Sessions { get; } = new();

    public event Action? StateChanged;

    public event Action? ClientsChanged;

    public event Action<Exception>? DeviceLost;

    private ITransport CreateTransport(ConnectionConfig config) => config.Kind switch
    {
        ConnectionKind.Serial => new SerialTransport(config),
        ConnectionKind.TcpClient => new TcpClientTransport(config),
        ConnectionKind.TcpServer => new TcpServerTransport(config),
        _ => throw new NotSupportedException($"Transport kind {config.Kind} requires an explicit transport instance."),
    };

    private void HookTransport(ITransport transport)
    {
        transport.DataReceived += OnDataReceived;
        transport.StateChanged += OnTransportStateChanged;
        transport.ClientsChanged += OnClientsChanged;
        if (transport is SerialTransport serial)
        {
            serial.DeviceLost += OnDeviceLost;
        }
        else if (transport is TcpClientTransport tcp)
        {
            tcp.ConnectionLost += OnDeviceLost;
        }
    }

    private void UnhookTransport(ITransport transport)
    {
        transport.DataReceived -= OnDataReceived;
        transport.StateChanged -= OnTransportStateChanged;
        transport.ClientsChanged -= OnClientsChanged;
        if (transport is SerialTransport serial) serial.DeviceLost -= OnDeviceLost;
        else if (transport is TcpClientTransport tcp) tcp.ConnectionLost -= OnDeviceLost;
    }

    private void OnDataReceived(ReceivedData data) =>
        Store.Append(DateTime.UtcNow, StreamDirection.Rx, data.Data, data.ClientId);

    private void OnTransportStateChanged(TransportState state)
    {
        if (state is TransportState.Open or TransportState.Listening)
        {
            ConnectedAtUtc ??= DateTime.UtcNow;
        }
        else if (state == TransportState.Closed)
        {
            ConnectedAtUtc = null;
        }

        StateChanged?.Invoke();
    }

    private void OnClientsChanged() => ClientsChanged?.Invoke();

    private void OnDeviceLost(Exception ex)
    {
        DeviceLost?.Invoke(ex);
        if (Config.AutoReconnect) _ = ReconnectLoopAsync();
    }

    private async Task ReconnectLoopAsync()
    {
        _reconnectCts?.Cancel();
        var cts = new CancellationTokenSource();
        _reconnectCts = cts;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Config.ReconnectIntervalMs, cts.Token).ConfigureAwait(false);
                await _transport.CloseAsync().ConfigureAwait(false);
                if (cts.IsCancellationRequested) return; // user closed while we were tearing down
                await _transport.OpenAsync(cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested) await _transport.CloseAsync().ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Keep trying at the configured interval.
            }
        }
    }

    public Task OpenAsync(CancellationToken ct = default) => _transport.OpenAsync(ct);

    public async Task CloseAsync()
    {
        _reconnectCts?.Cancel();
        await _transport.CloseAsync().ConfigureAwait(false);
    }

    /// <summary>Record + transmit. All TX bytes go through here so the stream stays the truth.</summary>
    public async Task SendAsync(byte[] data, int clientId = 0, CancellationToken ct = default)
    {
        Store.Append(DateTime.UtcNow, StreamDirection.Tx, data, clientId);
        await _transport.SendAsync(data, clientId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Hot-apply new parameters. Same-kind changes reconfigure the live transport;
    /// a kind change swaps the transport while keeping store and sessions intact.
    /// </summary>
    public async Task ApplyConfigAsync(ConnectionConfig config)
    {
        if (config.Kind == Config.Kind)
        {
            Config = config.Clone();
            switch (_transport)
            {
                case SerialTransport s:
                    await s.ApplyConfigAsync(config).ConfigureAwait(false);
                    break;
                case TcpClientTransport c:
                    await c.ApplyConfigAsync(config).ConfigureAwait(false);
                    break;
                case TcpServerTransport srv:
                    await srv.ApplyConfigAsync(config).ConfigureAwait(false);
                    break;
            }
        }
        else
        {
            bool wasOpen = State is TransportState.Open or TransportState.Listening;
            UnhookTransport(_transport);
            await _transport.DisposeAsync().ConfigureAwait(false);
            Config = config.Clone();
            _transport = CreateTransport(Config);
            HookTransport(_transport);
            if (wasOpen) await _transport.OpenAsync().ConfigureAwait(false);
        }

        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _reconnectCts?.Cancel();
        foreach (var session in Sessions.ToArray()) session.Dispose();
        UnhookTransport(_transport);
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
