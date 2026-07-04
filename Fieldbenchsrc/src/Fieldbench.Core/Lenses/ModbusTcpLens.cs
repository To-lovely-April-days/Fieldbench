using System.Buffers.Binary;
using Fieldbench.Core.Modbus;
using Fieldbench.Core.Streams;

namespace Fieldbench.Core.Lenses;

/// <summary>
/// Modbus TCP lens: frames by the MBAP length field (deterministic — no timing
/// heuristics needed). Malformed headers resync by dropping bytes until a
/// plausible MBAP appears.
/// </summary>
public sealed class ModbusTcpLens : IProtocolLens
{
    private const int MbapSize = 7;

    private readonly Dictionary<(StreamDirection, int), FramingBuffer> _buffers = new();
    private readonly ModbusFrameDescriber.Pairing _pairing = new();
    private long _nextId;

    public string Id => "modbus-tcp";

    public string DisplayName => "Modbus TCP";

    public LensPerspective Perspective { get; set; } = LensPerspective.Monitor;

    public IReadOnlyList<Frame> Feed(StreamChunk chunk)
    {
        var key = (chunk.Direction, chunk.ClientId);
        if (!_buffers.TryGetValue(key, out var buffer))
        {
            buffer = new FramingBuffer();
            _buffers[key] = buffer;
        }

        buffer.Append(chunk);
        var result = new List<Frame>();
        Extract(buffer, chunk.Direction, result);
        return result;
    }

    private void Extract(FramingBuffer buffer, StreamDirection dir, List<Frame> result)
    {
        while (buffer.Length >= MbapSize)
        {
            var span = buffer.Span;
            ushort proto = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
            ushort len = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);

            // Plausibility: protocol id must be 0, length covers unit id + PDU (2..254).
            if (proto != 0 || len < 2 || len > 254)
            {
                // Resync: drop bytes until the next plausible header start.
                int skip = 1;
                while (skip + MbapSize <= span.Length)
                {
                    ushort p = BinaryPrimitives.ReadUInt16BigEndian(span[(skip + 2)..]);
                    ushort l = BinaryPrimitives.ReadUInt16BigEndian(span[(skip + 4)..]);
                    if (p == 0 && l >= 2 && l <= 254) break;
                    skip++;
                }

                EmitGarbage(buffer.FirstUtc, buffer.ClientId, dir, buffer.Take(Math.Min(skip, buffer.Length)), result);
                continue;
            }

            int total = 6 + len;
            if (buffer.Length < total) return;

            EmitAdu(buffer.FirstUtc, buffer.ClientId, dir, buffer.Take(total), result);
        }
    }

    public IReadOnlyList<Frame> FlushPending(DateTime nowUtc)
    {
        // TCP framing is deterministic; only a final flush (stream end) drains leftovers.
        if (nowUtc != DateTime.MaxValue) return Array.Empty<Frame>();
        var result = new List<Frame>();
        foreach (var ((dir, _), buffer) in _buffers)
        {
            if (buffer.Length > 0) EmitGarbage(buffer.FirstUtc, buffer.ClientId, dir, buffer.TakeAll(), result);
        }

        return result;
    }

    public void Reset()
    {
        _buffers.Clear();
        _pairing.Reset();
        _nextId = 0;
    }

    private (bool AllowRequest, bool AllowResponse) Expectation(StreamDirection dir) => Perspective switch
    {
        LensPerspective.Master => dir == StreamDirection.Tx ? (true, false) : (false, true),
        LensPerspective.Slave => dir == StreamDirection.Tx ? (false, true) : (true, false),
        _ => (true, true),
    };

    private void EmitAdu(DateTime tsUtc, int clientId, StreamDirection dir, byte[] adu, List<Frame> result)
    {
        var (allowReq, allowResp) = Expectation(dir);
        byte unit = adu[6];
        var pdu = adu.AsSpan(MbapSize);
        ModbusMessage? asReq = allowReq ? ModbusCodec.TryParseRequestPdu(unit, pdu) : null;
        ModbusMessage? asResp = allowResp ? ModbusCodec.TryParseResponsePdu(unit, pdu) : null;
        var msg = asReq is null ? asResp
            : asResp is null ? asReq
            : _pairing.ExpectsResponse(unit, asResp.Function) ? asResp : asReq;

        Frame frame;
        if (msg is not null)
        {
            frame = new Frame
            {
                Id = _nextId++,
                TimestampUtc = tsUtc,
                Direction = dir,
                ClientId = clientId,
                Bytes = adu,
                Status = msg.Kind == ModbusMessageKind.Exception ? FrameStatus.Warning : FrameStatus.Ok,
                AddressToken = ModbusFrameDescriber.AddressToken(msg, tcp: true),
                FunctionToken = ModbusFrameDescriber.FunctionToken(msg),
                Summary = msg.Kind == ModbusMessageKind.Request
                    ? ModbusFrameDescriber.Summary(msg) + " · request"
                    : ModbusFrameDescriber.Summary(msg) + (msg.Kind == ModbusMessageKind.Response ? " · response" : ""),
                StatusTag = msg.Kind == ModbusMessageKind.Exception ? $"EXC {msg.ExceptionCode:00}"
                    : msg.Kind == ModbusMessageKind.Response ? "OK" : null,
                Fields = ModbusFrameDescriber.TcpFields(msg, adu),
                FunctionCode = msg.Function,
                UnitId = msg.Unit,
                ExceptionCode = msg.Kind == ModbusMessageKind.Exception ? msg.ExceptionCode : null,
            };

            if (Perspective == LensPerspective.Monitor)
            {
                _pairing.Apply(frame, msg);
                frame.RoleInferred = frame.Role != FrameRole.None;
            }
            else
            {
                frame.Role = msg.Kind == ModbusMessageKind.Request ? FrameRole.MasterToSlave : FrameRole.SlaveToMaster;
                _pairing.Apply(frame, msg);
            }
        }
        else
        {
            frame = new Frame
            {
                Id = _nextId++,
                TimestampUtc = tsUtc,
                Direction = dir,
                ClientId = clientId,
                Bytes = adu,
                Status = FrameStatus.Error,
                Summary = "Malformed PDU",
                StatusTag = "BAD",
                Fields = [new FrameField(0, adu.Length, FieldKind.Data, "Bytes", $"{adu.Length} bytes")],
            };
        }

        result.Add(frame);
    }

    private void EmitGarbage(DateTime tsUtc, int clientId, StreamDirection dir, byte[] bytes, List<Frame> result)
    {
        if (bytes.Length == 0) return;
        result.Add(new Frame
        {
            Id = _nextId++,
            TimestampUtc = tsUtc,
            Direction = dir,
            ClientId = clientId,
            Bytes = bytes,
            Status = FrameStatus.Error,
            Summary = "Not a Modbus TCP header",
            StatusTag = "BAD",
            Fields = [new FrameField(0, bytes.Length, FieldKind.Data, "Bytes", $"{bytes.Length} bytes")],
        });
    }
}
