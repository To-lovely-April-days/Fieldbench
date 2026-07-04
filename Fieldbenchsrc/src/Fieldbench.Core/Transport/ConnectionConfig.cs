using System.IO.Ports;

namespace Fieldbench.Core.Transport;

public enum ConnectionKind
{
    Serial,
    TcpClient,
    TcpServer,
    Loopback,
}

/// <summary>
/// Full configuration of a connection. Serial parameters support hot apply:
/// changing them on a live connection re-configures the port without dropping
/// the session or the captured stream (PRD hard rule).
/// </summary>
public sealed class ConnectionConfig
{
    public ConnectionKind Kind { get; set; } = ConnectionKind.Serial;

    // Serial
    public string PortName { get; set; } = "";
    public string PortFriendlyName { get; set; } = "";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;
    public Handshake FlowControl { get; set; } = Handshake.None;

    // TCP
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 502;
    public int ListenPort { get; set; } = 502;

    // Reconnect
    public bool AutoReconnect { get; set; }
    public int ReconnectIntervalMs { get; set; } = 3000;

    public ConnectionConfig Clone() => (ConnectionConfig)MemberwiseClone();

    public string ShortLabel() => Kind switch
    {
        ConnectionKind.Serial => string.IsNullOrEmpty(PortFriendlyName) ? PortName : $"{PortName} — {PortFriendlyName}",
        ConnectionKind.TcpClient => $"{Host}:{Port}",
        ConnectionKind.TcpServer => $"TCP :{ListenPort}",
        ConnectionKind.Loopback => "Demo loop",
        _ => "?",
    };

    /// <summary>"9600 8N1" style parameter summary for status bar / tree.</summary>
    public string ParamSummary() => Kind switch
    {
        ConnectionKind.Serial => $"{BaudRate} {DataBits}{ParityChar()}{StopBitsLabel()}",
        ConnectionKind.TcpClient => $":{Port}",
        ConnectionKind.TcpServer => $":{ListenPort}",
        ConnectionKind.Loopback => "in-proc",
        _ => "",
    };

    public char ParityChar() => Parity switch
    {
        Parity.None => 'N',
        Parity.Even => 'E',
        Parity.Odd => 'O',
        Parity.Mark => 'M',
        Parity.Space => 'S',
        _ => '?',
    };

    public string StopBitsLabel() => StopBits switch
    {
        StopBits.One => "1",
        StopBits.OnePointFive => "1.5",
        StopBits.Two => "2",
        _ => "?",
    };

    /// <summary>Seconds per character on the wire (start + data + parity + stop bits).</summary>
    public double CharTimeSeconds()
    {
        if (Kind != ConnectionKind.Serial || BaudRate <= 0) return 0.001;
        double bits = 1 + DataBits + (Parity == Parity.None ? 0 : 1) + StopBits switch
        {
            StopBits.One => 1.0,
            StopBits.OnePointFive => 1.5,
            StopBits.Two => 2.0,
            _ => 1.0,
        };
        return bits / BaudRate;
    }
}
