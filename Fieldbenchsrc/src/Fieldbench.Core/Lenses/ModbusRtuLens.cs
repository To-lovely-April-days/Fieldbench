using Fieldbench.Core.Modbus;
using Fieldbench.Core.Streams;

namespace Fieldbench.Core.Lenses;

/// <summary>Which side of the conversation this session is, driving request/response interpretation.</summary>
public enum LensPerspective
{
    /// <summary>Passive bus tap: everything is physically RX; roles are inferred from frame semantics.</summary>
    Monitor,
    /// <summary>We poll: TX = requests, RX = responses.</summary>
    Master,
    /// <summary>We simulate a device: RX = requests, TX = responses.</summary>
    Slave,
}

/// <summary>
/// Modbus RTU lens with the hybrid framing strategy the PRD mandates:
/// candidate-length prediction + CRC validation extracts frames immediately even
/// when high-baud chunks stick together, and the 3.5T silence gap (with a floor,
/// because Windows timer precision is unreliable) closes frames the CRC path
/// could not decide. On silence expiry a bounded CRC sliding-window scan
/// recovers sync after corrupted bytes.
/// </summary>
public sealed class ModbusRtuLens : IProtocolLens
{
    private readonly FramingBuffer _tx = new();
    private readonly FramingBuffer _rx = new();
    private readonly ModbusFrameDescriber.Pairing _pairing = new();
    private long _nextId;

    public string Id => "modbus-rtu";

    public string DisplayName => "Modbus RTU";

    public LensPerspective Perspective { get; set; } = LensPerspective.Monitor;

    /// <summary>Character time provider (seconds), fed from the live serial config for 3.5T computation.</summary>
    public Func<double>? CharTimeSecondsProvider { get; set; }

    /// <summary>Floor for the silence gap in ms — pure 3.5T is unreliable under Windows timing.</summary>
    public double MinSilenceMs { get; set; } = 8;

    public double SilenceMs
    {
        get
        {
            double t35 = (CharTimeSecondsProvider?.Invoke() ?? 0.001) * 3.5 * 1000;
            return Math.Max(t35, MinSilenceMs);
        }
    }

    public IReadOnlyList<Frame> Feed(StreamChunk chunk)
    {
        var buffer = BufferFor(chunk.Direction);
        var result = new List<Frame>();

        // A silence boundary passed between the previous bytes and this chunk:
        // whatever is pending is a complete (possibly broken) frame.
        if (buffer.Length > 0 && (chunk.TimestampUtc - buffer.LastUtc).TotalMilliseconds >= SilenceMs)
        {
            DrainOnSilence(buffer, chunk.Direction, result);
        }

        buffer.Append(chunk);
        ExtractEager(buffer, chunk.Direction, result);
        return result;
    }

    public IReadOnlyList<Frame> FlushPending(DateTime nowUtc)
    {
        var result = new List<Frame>();
        foreach (var dir in new[] { StreamDirection.Tx, StreamDirection.Rx })
        {
            var buffer = BufferFor(dir);
            if (buffer.Length == 0) continue;
            bool expired = nowUtc == DateTime.MaxValue || (nowUtc - buffer.LastUtc).TotalMilliseconds >= SilenceMs;
            if (!expired) continue;
            ExtractEager(buffer, dir, result);
            if (buffer.Length > 0) DrainOnSilence(buffer, dir, result);
        }

        return result;
    }

    public void Reset()
    {
        _tx.Clear();
        _rx.Clear();
        _pairing.Reset();
        _nextId = 0;
    }

    private FramingBuffer BufferFor(StreamDirection dir) => dir == StreamDirection.Tx ? _tx : _rx;

    private (bool AllowRequest, bool AllowResponse) Expectation(StreamDirection dir) => Perspective switch
    {
        LensPerspective.Master => dir == StreamDirection.Tx ? (true, false) : (false, true),
        LensPerspective.Slave => dir == StreamDirection.Tx ? (false, true) : (true, false),
        _ => (true, true),
    };

    /// <summary>
    /// Extract every frame that candidate-length prediction + CRC can prove
    /// from the head of the buffer. Stops at the first undecidable prefix.
    /// </summary>
    private void ExtractEager(FramingBuffer buffer, StreamDirection dir, List<Frame> result)
    {
        var (allowReq, allowResp) = Expectation(dir);
        while (buffer.Length >= 4)
        {
            var span = buffer.Span;
            int? reqLen = allowReq ? ModbusCodec.PredictRequestLength(span) : -1;
            int? respLen = allowResp ? ModbusCodec.PredictResponseLength(span) : -1;

            int matched = 0;
            foreach (var len in CandidateLengths(reqLen, respLen))
            {
                if (len > buffer.Length) continue;
                if (Checksums.ValidateModbusCrc(span[..len]))
                {
                    matched = len;
                    break;
                }
            }

            if (matched > 0)
            {
                EmitParsed(buffer.FirstUtc, buffer.ClientId, dir, buffer.Take(matched), crcOk: true, result);
                continue;
            }

            // Undecidable yet? (a prediction still needs more bytes, or a known
            // candidate is longer than what we have) → wait for more data.
            bool waiting = reqLen is null || respLen is null
                           || (reqLen > 0 && reqLen > buffer.Length)
                           || (respLen > 0 && respLen > buffer.Length);
            if (waiting) return;

            // Both interpretations are impossible or CRC-failed at their full
            // lengths: the head is corrupt. Silence expiry will resync via scan.
            return;
        }
    }

    private static IEnumerable<int> CandidateLengths(int? reqLen, int? respLen)
    {
        if (reqLen is > 0) yield return reqLen.Value;
        if (respLen is > 0 && respLen != reqLen) yield return respLen.Value;
    }

    /// <summary>
    /// Silence elapsed with undecodable bytes pending: CRC sliding-window scan.
    /// Finds the earliest offset/length that starts a CRC-valid frame, emits the
    /// garbage prefix as an error frame, then resumes normal extraction.
    /// </summary>
    private void DrainOnSilence(FramingBuffer buffer, StreamDirection dir, List<Frame> result)
    {
        while (buffer.Length > 0)
        {
            var span = buffer.Span;
            int n = span.Length;

            // 1) CRC-valid frame right at the head (any length ≥ 4, unknown FCs included)?
            int lenAtHead = FindCrcValidLength(span, maxLen: Math.Min(n, 256));
            if (lenAtHead > 0)
            {
                EmitParsed(buffer.FirstUtc, buffer.ClientId, dir, buffer.Take(lenAtHead), crcOk: true, result);
                continue;
            }

            // 2) Resync: earliest offset where a CRC-valid frame begins.
            if (n <= 4096)
            {
                for (int offset = 1; offset + 4 <= n; offset++)
                {
                    int len = FindCrcValidLength(span[offset..], maxLen: Math.Min(n - offset, 256));
                    if (len > 0)
                    {
                        EmitParsed(buffer.FirstUtc, buffer.ClientId, dir, buffer.Take(offset), crcOk: false, result);
                        goto continueOuter;
                    }
                }
            }

            // 3) Nothing recoverable: the whole pending buffer is one broken frame.
            EmitParsed(buffer.FirstUtc, buffer.ClientId, dir, buffer.TakeAll(), crcOk: false, result);
            continueOuter: ;
        }
    }

    private static int FindCrcValidLength(ReadOnlySpan<byte> span, int maxLen)
    {
        // Prefer predicted lengths (cheap, exact), then fall back to the window scan.
        int? reqLen = ModbusCodec.PredictRequestLength(span);
        int? respLen = ModbusCodec.PredictResponseLength(span);
        foreach (var len in CandidateLengths(reqLen, respLen))
        {
            if (len <= span.Length && Checksums.ValidateModbusCrc(span[..len])) return len;
        }

        for (int len = 4; len <= maxLen; len++)
        {
            if (Checksums.ValidateModbusCrc(span[..len])) return len;
        }

        return 0;
    }

    private void EmitParsed(DateTime tsUtc, int clientId, StreamDirection dir, byte[] adu, bool crcOk, List<Frame> result)
    {
        var (allowReq, allowResp) = Expectation(dir);
        ModbusMessage? msg = null;

        if (adu.Length >= 4)
        {
            var pdu = adu.AsSpan(1, adu.Length - 3);
            ModbusMessage? asReq = allowReq ? ModbusCodec.TryParseRequestPdu(adu[0], pdu) : null;
            ModbusMessage? asResp = allowResp ? ModbusCodec.TryParseResponsePdu(adu[0], pdu) : null;
            msg = ChooseInterpretation(asReq, asResp);
        }

        Frame frame;
        if (msg is not null)
        {
            var status = !crcOk ? FrameStatus.Error
                : msg.Kind == ModbusMessageKind.Exception ? FrameStatus.Warning
                : FrameStatus.Ok;
            frame = new Frame
            {
                Id = _nextId++,
                TimestampUtc = tsUtc,
                Direction = dir,
                ClientId = clientId,
                Bytes = adu,
                Status = status,
                AddressToken = ModbusFrameDescriber.AddressToken(msg, tcp: false),
                FunctionToken = ModbusFrameDescriber.FunctionToken(msg),
                Summary = ModbusFrameDescriber.Summary(msg),
                StatusTag = !crcOk ? "CRC FAIL" : msg.Kind == ModbusMessageKind.Exception ? $"EXC {msg.ExceptionCode:00}" : "OK",
                Fields = ModbusFrameDescriber.RtuFields(msg, adu, crcOk),
                FunctionCode = msg.Function,
                UnitId = msg.Unit,
                ExceptionCode = msg.Kind == ModbusMessageKind.Exception ? msg.ExceptionCode : null,
            };
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
                Status = crcOk ? FrameStatus.Ok : FrameStatus.Error,
                Summary = crcOk ? $"FC 0x{(adu.Length > 1 ? adu[1] : 0):X2} · unsupported" : "Unrecognized bytes",
                StatusTag = crcOk ? "OK" : "CRC FAIL",
                Fields = crcOk && adu.Length >= 4
                    ?
                    [
                        new FrameField(0, 1, FieldKind.Address, "Slave address", $"0x{adu[0]:X2}"),
                        new FrameField(1, 1, FieldKind.Function, "Function", $"0x{adu[1]:X2}"),
                        new FrameField(2, adu.Length - 4, FieldKind.Data, "Data", $"{adu.Length - 4} bytes"),
                        new FrameField(adu.Length - 2, 2, FieldKind.Checksum, "CRC-16", "✓"),
                    ]
                    : [new FrameField(0, adu.Length, FieldKind.Data, "Bytes", $"{adu.Length} bytes")],
            };
        }

        // Master/Slave perspectives already know roles; Monitor infers via pairing.
        if (Perspective == LensPerspective.Monitor)
        {
            _pairing.Apply(frame, msg);
            frame.RoleInferred = frame.Role != FrameRole.None;
        }
        else if (msg is not null)
        {
            frame.Role = msg.Kind == ModbusMessageKind.Request ? FrameRole.MasterToSlave : FrameRole.SlaveToMaster;
            _pairing.Apply(frame, msg);
        }

        result.Add(frame);
    }

    private ModbusMessage? ChooseInterpretation(ModbusMessage? asReq, ModbusMessage? asResp)
    {
        if (asReq is null) return asResp;
        if (asResp is null) return asReq;
        // Both shapes parse (rare, e.g. FC01 response whose byte count mimics a
        // request). Use conversation context: a pending request expects a response.
        return _pairing.ExpectsResponse(asResp.Unit, asResp.Function) ? asResp : asReq;
    }
}
