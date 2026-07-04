using System.Net.Sockets;

namespace Fieldbench.Core.Transport;

public sealed class TcpClientTransport : ITransport
{
    private readonly object _gate = new();
    private ConnectionConfig _config;
    private TcpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;

    public TcpClientTransport(ConnectionConfig config)
    {
        _config = config.Clone();
    }

    public TransportState State { get; private set; } = TransportState.Closed;

    public event Action<ReceivedData>? DataReceived;
    public event Action<TransportState>? StateChanged;
    public event Action? ClientsChanged { add { } remove { } }
    public event Action<Exception>? ConnectionLost;

    public IReadOnlyList<TransportClientInfo> Clients => Array.Empty<TransportClientInfo>();

    public string Describe() => $"{_config.Host}:{_config.Port}";

    public ConnectionConfig Config => _config;

    public async Task OpenAsync(CancellationToken ct = default)
    {
        if (State == TransportState.Open) return;
        SetState(TransportState.Opening);
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(_config.Host, _config.Port, ct).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            SetState(TransportState.Closed);
            throw;
        }

        lock (_gate)
        {
            _client = client;
            _cts = new CancellationTokenSource();
            _readLoop = Task.Run(() => ReadLoopAsync(client.GetStream(), _cts.Token), CancellationToken.None);
        }

        SetState(TransportState.Open);
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (n <= 0) break;
                var data = new byte[n];
                Array.Copy(buffer, data, n);
                DataReceived?.Invoke(new ReceivedData(data, 0));
            }

            if (!ct.IsCancellationRequested)
            {
                SetState(TransportState.Closed);
                ConnectionLost?.Invoke(new IOException("Remote closed the connection."));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                SetState(TransportState.Faulted);
                ConnectionLost?.Invoke(ex);
            }
        }
    }

    public async Task ApplyConfigAsync(ConnectionConfig config)
    {
        bool endpointChanged = config.Host != _config.Host || config.Port != _config.Port;
        _config = config.Clone();
        if (endpointChanged && State == TransportState.Open)
        {
            await CloseAsync().ConfigureAwait(false);
            await OpenAsync().ConfigureAwait(false);
        }
    }

    public async Task CloseAsync()
    {
        Task? loop;
        lock (_gate)
        {
            _cts?.Cancel();
            loop = _readLoop;
            _client?.Dispose();
            _client = null;
            _cts = null;
            _readLoop = null;
        }

        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch { }
        }

        SetState(TransportState.Closed);
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, int clientId = 0, CancellationToken ct = default)
    {
        TcpClient? client;
        lock (_gate) client = _client;
        if (client is null || State != TransportState.Open)
            throw new InvalidOperationException("TCP connection is not open.");
        return client.GetStream().WriteAsync(data, ct).AsTask();
    }

    private void SetState(TransportState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
