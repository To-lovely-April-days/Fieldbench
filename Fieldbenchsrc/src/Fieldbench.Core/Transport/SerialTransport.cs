using System.IO.Ports;

namespace Fieldbench.Core.Transport;

/// <summary>
/// Serial port transport over System.IO.Ports. Supports hot parameter apply:
/// baud/parity/data/stop/flow changes are applied to the live port where the
/// driver allows it, otherwise via a fast close/reopen that preserves the
/// owning connection, sessions and captured stream.
/// </summary>
public sealed class SerialTransport : ITransport
{
    private readonly object _gate = new();
    private SerialPort? _port;
    private ConnectionConfig _config;
    private CancellationTokenSource? _readCts;
    private Task? _readLoop;

    public SerialTransport(ConnectionConfig config)
    {
        _config = config.Clone();
    }

    public TransportState State { get; private set; } = TransportState.Closed;

    public event Action<ReceivedData>? DataReceived;
    public event Action<TransportState>? StateChanged;
    public event Action? ClientsChanged { add { } remove { } }

    public IReadOnlyList<TransportClientInfo> Clients => Array.Empty<TransportClientInfo>();

    /// <summary>Fired when the device disappears mid-session (USB unplug). The session must survive.</summary>
    public event Action<Exception>? DeviceLost;

    public string Describe() => _config.ShortLabel();

    public ConnectionConfig Config => _config;

    public Task OpenAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (State == TransportState.Open) return Task.CompletedTask;
            SetState(TransportState.Opening);
            try
            {
                _port = new SerialPort(_config.PortName)
                {
                    BaudRate = _config.BaudRate,
                    DataBits = _config.DataBits,
                    Parity = _config.Parity,
                    StopBits = _config.StopBits,
                    Handshake = _config.FlowControl,
                    ReadTimeout = SerialPort.InfiniteTimeout,
                    WriteTimeout = 2000,
                };
                _port.Open();
            }
            catch
            {
                _port?.Dispose();
                _port = null;
                SetState(TransportState.Closed);
                throw;
            }

            _readCts = new CancellationTokenSource();
            var token = _readCts.Token;
            var stream = _port.BaseStream;
            _readLoop = Task.Run(() => ReadLoopAsync(stream, token), CancellationToken.None);
            SetState(TransportState.Open);
        }

        return Task.CompletedTask;
    }

    private async Task ReadLoopAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
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
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Device unplugged or driver fault: keep the session alive, surface the loss.
            if (!ct.IsCancellationRequested)
            {
                SetState(TransportState.Faulted);
                DeviceLost?.Invoke(ex);
            }
        }
    }

    /// <summary>
    /// Hot-apply new serial parameters. Baud/parity/etc. are set on the live port;
    /// a port-name change requires reopen. Never touches the owning session.
    /// </summary>
    public async Task ApplyConfigAsync(ConnectionConfig config)
    {
        bool needReopen;
        lock (_gate)
        {
            needReopen = _port is null
                         || State != TransportState.Open
                         || !string.Equals(config.PortName, _config.PortName, StringComparison.OrdinalIgnoreCase);
            _config = config.Clone();

            if (!needReopen && _port is not null)
            {
                try
                {
                    // SerialPort supports live reconfiguration of these properties.
                    _port.BaudRate = config.BaudRate;
                    _port.DataBits = config.DataBits;
                    _port.Parity = config.Parity;
                    _port.StopBits = config.StopBits;
                    _port.Handshake = config.FlowControl;
                    return;
                }
                catch
                {
                    needReopen = true;
                }
            }
        }

        if (needReopen && State is TransportState.Open or TransportState.Faulted)
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
            _readCts?.Cancel();
            loop = _readLoop;
            try
            {
                _port?.Close();
            }
            catch
            {
                // Port may already be gone (USB unplug) — closing must never throw upward.
            }

            _port?.Dispose();
            _port = null;
            _readCts = null;
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
        SerialPort? port;
        lock (_gate) port = _port;
        if (port is null || State != TransportState.Open)
            throw new InvalidOperationException("Serial port is not open.");
        return port.BaseStream.WriteAsync(data, ct).AsTask();
    }

    private void SetState(TransportState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    /// <summary>Enumerate serial ports with friendly names where the platform provides them.</summary>
    public static IReadOnlyList<(string Name, string FriendlyName)> ListPorts()
    {
        var result = new List<(string, string)>();
        foreach (var name in SerialPort.GetPortNames().Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            result.Add((name, ""));
        }

        return result;
    }
}
