using System.Buffers.Binary;
using Fieldbench.Core.Modbus;
using Fieldbench.Core.Streams;

namespace Fieldbench.Core.Lenses;

public sealed class DetectionResult
{
    public required string LensId { get; init; }
    public required string DisplayName { get; init; }
    public required int PassCount { get; init; }
    public required int TotalCount { get; init; }
    public required string Evidence { get; init; }
}

/// <summary>
/// Heuristic protocol detection over a Raw/Monitor stream (PRD §6.2):
/// ≥3 consecutive chunks passing CRC16-Modbus (a single random frame collides
/// with probability 1/65536, three make it certain) → Modbus RTU; consistent
/// MBAP headers → Modbus TCP. Random streams must produce zero false positives.
/// </summary>
public sealed class ProtocolDetector
{
    private const int Threshold = 3;

    private int _rtuStreak;
    private int _rtuSeen;
    private int _rtuPass;
    private int _tcpStreak;
    private byte? _rtuUnit;
    private byte? _rtuFunc;

    /// <summary>Fires once when confidence is reached; reset() re-arms.</summary>
    public event Action<DetectionResult>? Detected;

    public bool HasFired { get; private set; }

    /// <summary>Feed a raw display chunk (RawLens frame bytes are fine too).</summary>
    public void Feed(StreamChunk chunk) => Feed(chunk.Data);

    public void Feed(ReadOnlySpan<byte> block)
    {
        if (HasFired) return;
        if (block.Length < 4)
        {
            // Tiny noise still interrupts a streak — random streams must never fire.
            _rtuStreak = 0;
            _tcpStreak = 0;
            return;
        }

        // — Modbus RTU: whole block (or a clean split of it) validates CRC —
        bool rtuOk = LooksLikeRtu(block);
        _rtuSeen++;
        if (rtuOk)
        {
            _rtuPass++;
            _rtuStreak++;
            if (block.Length >= 2)
            {
                _rtuUnit ??= block[0];
                _rtuFunc ??= (byte)(block[1] & 0x7F);
            }
        }
        else
        {
            _rtuStreak = 0;
            _rtuUnit = null;
            _rtuFunc = null;
        }

        if (_rtuStreak >= Threshold)
        {
            Fire(new DetectionResult
            {
                LensId = "modbus-rtu",
                DisplayName = "Modbus RTU",
                PassCount = _rtuPass,
                TotalCount = _rtuSeen,
                Evidence = _rtuUnit is { } u && _rtuFunc is { } f
                    ? $"@{u:00} polling FC{f:00}"
                    : "CRC-16 valid",
            });
            return;
        }

        // — Modbus TCP: MBAP structure (proto=0, plausible length) —
        // A CRC-valid RTU block never counts toward TCP: valid CRC16 is far
        // stronger evidence than header structure (an FC03 quantity-2 RTU
        // request is byte-for-byte a plausible 8-byte MBAP header otherwise).
        if (!rtuOk && LooksLikeMbap(block)) _tcpStreak++;
        else _tcpStreak = 0;

        if (_tcpStreak >= Threshold)
        {
            Fire(new DetectionResult
            {
                LensId = "modbus-tcp",
                DisplayName = "Modbus TCP",
                PassCount = _tcpStreak,
                TotalCount = _tcpStreak,
                Evidence = "MBAP headers consistent",
            });
        }
    }

    private static bool LooksLikeRtu(ReadOnlySpan<byte> block)
    {
        // Unit id sanity (1–247, 0 broadcast) plus CRC over the whole block…
        if (block.Length is < 4 or > 256) return false;
        if (block[0] > 247) return false;
        if (Checksums.ValidateModbusCrc(block)) return true;

        // …or the block is a request+response pair glued by display chunking.
        int? reqLen = ModbusCodec.PredictRequestLength(block);
        if (reqLen is > 0 && reqLen < block.Length
            && Checksums.ValidateModbusCrc(block[..reqLen.Value])
            && Checksums.ValidateModbusCrc(block[reqLen.Value..]))
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeMbap(ReadOnlySpan<byte> block)
    {
        if (block.Length < 9) return false;
        ushort proto = BinaryPrimitives.ReadUInt16BigEndian(block[2..]);
        ushort len = BinaryPrimitives.ReadUInt16BigEndian(block[4..]);
        // Minimum real ADU: unit + function + 1 byte → len ≥ 3.
        if (proto != 0 || len < 3 || len > 254) return false;
        if (block.Length < 6 + len) return false;
        // The PDU function code must be plausible (supported or its exception form).
        byte fc = block[7];
        return ModbusFunction.IsSupported((byte)(fc & 0x7F));
    }

    private void Fire(DetectionResult result)
    {
        HasFired = true;
        Detected?.Invoke(result);
    }

    public void Reset()
    {
        _rtuStreak = 0;
        _rtuSeen = 0;
        _rtuPass = 0;
        _tcpStreak = 0;
        _rtuUnit = null;
        _rtuFunc = null;
        HasFired = false;
    }
}
