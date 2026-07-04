using Fieldbench.Core.Streams;

namespace Fieldbench.Core.Lenses;

public enum FrameStatus
{
    Ok,
    /// <summary>Protocol-level exception response (e.g. Modbus EXC 02).</summary>
    Warning,
    /// <summary>Integrity failure: bad CRC, truncated, malformed.</summary>
    Error,
    /// <summary>Raw chunk with no protocol interpretation.</summary>
    Raw,
}

/// <summary>Semantic role inferred for passive bus monitoring (all bytes physically RX).</summary>
public enum FrameRole
{
    None,
    MasterToSlave,
    SlaveToMaster,
}

/// <summary>Field kinds map 1:1 to the fixed product-wide color board.</summary>
public enum FieldKind
{
    Address,
    Function,
    Length,
    Data,
    Checksum,
    ChecksumBad,
}

/// <summary>A colored, named byte range inside a frame (byte inspector + hex highlighting).</summary>
public readonly record struct FrameField(int Offset, int Count, FieldKind Kind, string Name, string Value, string? Detail = null);

/// <summary>
/// A protocol frame: a derived view over the byte stream produced by a lens.
/// Never persisted — recomputed from the stream on lens switch.
/// </summary>
public sealed class Frame
{
    public long Id { get; init; }

    public DateTime TimestampUtc { get; init; }

    public StreamDirection Direction { get; init; }

    public int ClientId { get; init; }

    public required byte[] Bytes { get; init; }

    public FrameStatus Status { get; init; }

    public FrameRole Role { get; set; }

    /// <summary>True when the role came from passive-monitor inference (bus tap):
    /// the timeline then shows M→S / S→M instead of the physical TX/RX.</summary>
    public bool RoleInferred { get; set; }

    /// <summary>"@01" / "U01" token, colored as Address.</summary>
    public string? AddressToken { get; init; }

    /// <summary>"FC03" / "FC83" token, colored as Function.</summary>
    public string? FunctionToken { get; init; }

    /// <summary>Free-text summary: "Read holding 0–9", "20 bytes", "Illegal data address".</summary>
    public string Summary { get; init; } = "";

    /// <summary>"OK" / "EXC 02" / "CRC FAIL" chip.</summary>
    public string? StatusTag { get; init; }

    public IReadOnlyList<FrameField> Fields { get; init; } = Array.Empty<FrameField>();

    /// <summary>Response latency vs the matching request, filled by the pairing pass.</summary>
    public double? DeltaMs { get; set; }

    /// <summary>Modbus function code if parsed (without exception flag).</summary>
    public byte? FunctionCode { get; init; }

    /// <summary>Modbus unit/slave id if parsed.</summary>
    public byte? UnitId { get; init; }

    public byte? ExceptionCode { get; init; }

    public bool IsAbnormal => Status is FrameStatus.Warning or FrameStatus.Error;

    public string HexString(int max = int.MaxValue)
    {
        var span = Bytes.AsSpan(0, Math.Min(Bytes.Length, max));
        return Convert.ToHexString(span).Chunk2();
    }
}

internal static class HexFormat
{
    /// <summary>"01031400E5" → "01 03 14 00 E5".</summary>
    public static string Chunk2(this string hex)
    {
        if (hex.Length <= 2) return hex;
        return string.Create(hex.Length + hex.Length / 2 - 1, hex, static (dst, src) =>
        {
            int j = 0;
            for (int i = 0; i < src.Length; i += 2)
            {
                if (i > 0) dst[j++] = ' ';
                dst[j++] = src[i];
                dst[j++] = src[i + 1];
            }
        });
    }
}
