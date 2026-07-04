using Fieldbench.Core.Modbus;

namespace Fieldbench.Core.Master;

/// <summary>One timestamped sample of a tag's scaled value (chart + sparkline source).</summary>
public readonly record struct TagSample(DateTime TimestampUtc, double Value, long? FrameId = null);

/// <summary>
/// A named register with interpretation: type, byte order, scale/offset, unit.
/// Owned by a RegisterMap; current value is derived from the raw word cache.
/// </summary>
public sealed class RegisterTag
{
    public Guid Id { get; } = Guid.NewGuid();

    public RegisterArea Area { get; set; } = RegisterArea.HoldingRegisters;

    /// <summary>0-based protocol address.</summary>
    public ushort Address { get; set; }

    public string Name { get; set; } = "";

    public RegisterDataType DataType { get; set; } = RegisterDataType.UInt16;

    public WordOrder WordOrder { get; set; } = WordOrder.ABCD;

    public double Scale { get; set; } = 1;

    public double Offset { get; set; }

    public string Unit { get; set; } = "";

    public bool Writable { get; set; }

    public int TextLength { get; set; } = 8;

    public string Notes { get; set; } = "";

    public int RegisterCount => DataType.RegisterCount(TextLength);

    public string DisplayAddress => Area.DisplayAddress(Address);

    public string TypeLabel =>
        Math.Abs(Scale - 1) > double.Epsilon
            ? $"{DataType.Label()} ×{Scale:0.####}"
            : RegisterCount > 1 && DataType != RegisterDataType.Text
                ? $"{DataType.Label()} · {WordOrder.Label()}"
                : DataType == RegisterDataType.Bit ? "uint16 · bits" : DataType.Label();

    // ── live state ──

    public double? RawNumeric { get; private set; }

    public double? ScaledValue { get; private set; }

    public string? TextValue { get; private set; }

    public ushort[] RawWords { get; private set; } = [];

    public DateTime? LastUpdateUtc { get; private set; }

    private readonly object _histGate = new();
    private readonly List<TagSample> _history = new();

    public const int MaxHistory = 4096;

    public event Action<RegisterTag>? Updated;

    public void UpdateFromWords(ReadOnlySpan<ushort> words, DateTime tsUtc, long? frameId = null)
    {
        RawWords = words.ToArray();
        if (DataType == RegisterDataType.Text)
        {
            TextValue = RegisterValueCodec.DecodeText(words, WordOrder);
            RawNumeric = null;
            ScaledValue = null;
        }
        else
        {
            double raw = RegisterValueCodec.DecodeNumeric(words, DataType, WordOrder);
            RawNumeric = raw;
            double scaled = raw * Scale + Offset;
            ScaledValue = scaled;
            lock (_histGate)
            {
                _history.Add(new TagSample(tsUtc, scaled, frameId));
                if (_history.Count > MaxHistory) _history.RemoveRange(0, _history.Count - MaxHistory);
            }
        }

        LastUpdateUtc = tsUtc;
        Updated?.Invoke(this);
    }

    public void UpdateFromBit(bool value, DateTime tsUtc, long? frameId = null)
    {
        RawNumeric = value ? 1 : 0;
        ScaledValue = value ? 1 : 0;
        RawWords = [(ushort)(value ? 1 : 0)];
        lock (_histGate)
        {
            _history.Add(new TagSample(tsUtc, value ? 1 : 0, frameId));
            if (_history.Count > MaxHistory) _history.RemoveRange(0, _history.Count - MaxHistory);
        }

        LastUpdateUtc = tsUtc;
        Updated?.Invoke(this);
    }

    public IReadOnlyList<TagSample> HistorySnapshot()
    {
        lock (_histGate) return _history.ToArray();
    }

    public string FormatValue()
    {
        if (DataType == RegisterDataType.Text) return TextValue ?? "—";
        if (ScaledValue is not { } v) return "—";
        if (DataType == RegisterDataType.Bit) return v != 0 ? "ON" : "OFF";
        if (Name.Contains("status", StringComparison.OrdinalIgnoreCase) && DataType == RegisterDataType.UInt16 && Math.Abs(Scale - 1) < double.Epsilon)
            return $"0x{(ushort)(RawNumeric ?? 0):X4}";
        return RegisterValueCodec.Format(v, DataType, Scale);
    }

    public string RawHex()
    {
        if (RawWords.Length == 0) return "—";
        return string.Join(" ", RawWords.Select(w => $"{w >> 8:X2} {w & 0xFF:X2}"));
    }

    /// <summary>Scaled UI value → raw register words for a write.</summary>
    public ushort[] EncodeForWrite(double scaledValue)
    {
        double raw = Math.Abs(Scale) < double.Epsilon ? scaledValue : (scaledValue - Offset) / Scale;
        return RegisterValueCodec.EncodeNumeric(raw, DataType, WordOrder);
    }
}
