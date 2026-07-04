using System.Net;
using System.Net.Sockets;

namespace Fieldbench.Core.Transport;

/// <summary>
/// TCP listener transport with multi-client support (a Modbus TCP slave must accept
/// several masters at once). Received data carries the client id so the slave engine
/// can reply to the requesting client only.
/// </summary>
public sealed class TcpServerTransport : ITransport
{
    private sealed class ClientSlot
    {
        public required int Id { get; init; }
        public required TcpClient Tcp { get; init; }
        public required TransportClientInfo Info { get; init; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<int, ClientSlot> _clients = new();
    private ConnectionConfig _config;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _nextClientId = 1;

    public TcpServerTransport(ConnectionConfig config)
    {
        _config = config.Clone();
    }

    public TransportState State { get; private set; } = TransportState.Closed;

    public event Action<ReceivedData>? DataReceived;
    public event Action<TransportState>? StateChanged;
    public event Action? ClientsChanged;

    public IReadOnlyList<TransportClientInfo> Clients
    {
        get { lock (_gate) return _clients.Values.Select(c => c.Info).ToArray(); }
    }

    public string Describe() => $"TCP :{_config.ListenPort}";

    public ConnectionConfig Config => _config;

    public Task OpenAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (State == TransportState.Listening) return Task.CompletedTask;
            SetState(TransportState.Opening);
            try
            {
                _listener = new TcpListener(IPAddress.Any, _config.ListenPort);
                _listener.Start();
            }
            catch
            {
                _listener = null;
                SetState(TransportState.Closed);
                throw;
            }

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token), CancellationToken.None);
            SetState(TransportState.Listening);
        }

        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                tcp.NoDelay = true;
                ClientSlot slot;
                lock (_gate)
                {
                    slot = new ClientSlot
                    {
                        Id = _nextClientId++,
                        Tcp = tcp,
                        Info = new TransportClientInfo
                        {
                            ClientId = 0,
                            RemoteEndpoint = tcp.Client.RemoteEndPoint?.ToString() ?? "?",
                            ConnectedAtUtc = DateTime.UtcNow,
                        },
                    };
                    // TransportClientInfo.ClientId is init-only; rebuild with the real id.
                    slot = new ClientSlot
                    {
                        Id = slot.Id,
                        Tcp = tcp,
                        Info = new TransportClientInfo
                        {
                            ClientId = slot.Id,
                            RemoteEndpoint = slot.Info.RemoteEndpoint,
                            ConnectedAtUtc = slot.Info.ConnectedAtUtc,
                        },
                    };
                    _clients[slot.Id] = slot;
                }

                ClientsChanged?.Invoke();
                _ = Task.Run(() => ClientReadLoopAsync(slot, ct), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (!ct.IsCancellationRequested) SetState(TransportState.Faulted);
        }
    }

    private async Task ClientReadLoopAsync(ClientSlot slot, CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            var stream = slot.Tcp.GetStream();
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (n <= 0) break;
                var data = new byte[n];
                Array.Copy(buffer, data, n);
                DataReceived?.Invoke(new ReceivedData(data, slot.Id));
            }
        }
        catch
        {
            // Client dropped; fall through to cleanup.
        }
        finally
        {
            lock (_gate) _clients.Remove(slot.Id);
            slot.Tcp.Dispose();
            ClientsChanged?.Invoke();
        }
    }

    public async Task ApplyConfigAsync(ConnectionConfig config)
    {
        bool portChanged = config.ListenPort != _config.ListenPort;
        _config = config.Clone();
        if (portChanged && State == TransportState.Listening)
        {
            await CloseAsync().ConfigureAwait(false);
            await OpenAsync().ConfigureAwait(false);
        }
    }

    public Task CloseAsync()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener = null;
            foreach (var slot in _clients.Values) slot.Tcp.Dispose();
            _clients.Clear();
            _cts = null;
        }

        ClientsChanged?.Invoke();
        SetState(TransportState.Closed);
        return Task.CompletedTask;
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, int clientId = 0, CancellationToken ct = default)
    {
        ClientSlot[] targets;
        lock (_gate)
        {
            targets = clientId == 0
                ? _clients.Values.ToArray()
                : _clients.TryGetValue(clientId, out var one) ? [one] : [];
        }

        foreach (var t in targets)
        {
            try
            {
                await t.Tcp.GetStream().WriteAsync(data, ct).ConfigureAwait(false);
            }
            catch
            {
                // Client vanished between snapshot and write; its read loop will clean up.
            }
        }
    }

    /// <summary>Track served requests for the client list UI.</summary>
    public void BumpRequestCount(int clientId)
    {
        lock (_gate)
        {
            if (_clients.TryGetValue(clientId, out var slot)) slot.Info.RequestCount++;
        }
    }

    private void SetState(TransportState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
