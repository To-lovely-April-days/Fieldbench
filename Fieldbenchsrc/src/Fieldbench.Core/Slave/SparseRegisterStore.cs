using Fieldbench.Core.Modbus;

namespace Fieldbench.Core.Slave;

/// <summary>
/// The four Modbus data areas with sparse storage over the full 65536 address
/// space. Only defined addresses respond; reads/writes touching an undefined
/// address raise EXC 02 automatically (PRD §6.5).
/// </summary>
public sealed class SparseRegisterStore
{
    private readonly object _gate = new();
    private readonly Dictionary<ushort, ushort> _holding = new();
    private readonly Dictionary<ushort, ushort> _input = new();
    private readonly Dictionary<ushort, bool> _coils = new();
    private readonly Dictionary<ushort, bool> _discrete = new();

    /// <summary>When true, undefined addresses read as 0 instead of EXC 02.</summary>
    public bool AllowUndefined { get; set; }

    public event Action<RegisterArea, ushort, int>? RangeChanged;

    // ── definition ──

    public void DefineWords(RegisterArea area, ushort start, int count, ushort initial = 0)
    {
        lock (_gate)
        {
            var map = WordsFor(area);
            for (int i = 0; i < count; i++) map.TryAdd((ushort)(start + i), initial);
        }

        RangeChanged?.Invoke(area, start, count);
    }

    public void DefineBits(RegisterArea area, ushort start, int count, bool initial = false)
    {
        lock (_gate)
        {
            var map = BitsFor(area);
            for (int i = 0; i < count; i++) map.TryAdd((ushort)(start + i), initial);
        }

        RangeChanged?.Invoke(area, start, count);
    }

    public bool IsDefined(RegisterArea area, ushort start, int count)
    {
        lock (_gate)
        {
            if (area.IsBitArea())
            {
                var map = BitsFor(area);
                for (int i = 0; i < count; i++)
                {
                    if (!map.ContainsKey((ushort)(start + i))) return false;
                }
            }
            else
            {
                var map = WordsFor(area);
                for (int i = 0; i < count; i++)
                {
                    if (!map.ContainsKey((ushort)(start + i))) return false;
                }
            }

            return true;
        }
    }

    // ── access ──

    public ushort[] ReadWords(RegisterArea area, ushort start, int count)
    {
        lock (_gate)
        {
            var map = WordsFor(area);
            var result = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                map.TryGetValue((ushort)(start + i), out result[i]);
            }

            return result;
        }
    }

    public bool[] ReadBits(RegisterArea area, ushort start, int count)
    {
        lock (_gate)
        {
            var map = BitsFor(area);
            var result = new bool[count];
            for (int i = 0; i < count; i++)
            {
                map.TryGetValue((ushort)(start + i), out result[i]);
            }

            return result;
        }
    }

    public void WriteWords(RegisterArea area, ushort start, ReadOnlySpan<ushort> values, bool define = false)
    {
        lock (_gate)
        {
            var map = WordsFor(area);
            for (int i = 0; i < values.Length; i++)
            {
                var addr = (ushort)(start + i);
                if (define || map.ContainsKey(addr) || AllowUndefined) map[addr] = values[i];
            }
        }

        RangeChanged?.Invoke(area, start, values.Length);
    }

    public void WriteBits(RegisterArea area, ushort start, ReadOnlySpan<bool> values, bool define = false)
    {
        lock (_gate)
        {
            var map = BitsFor(area);
            for (int i = 0; i < values.Length; i++)
            {
                var addr = (ushort)(start + i);
                if (define || map.ContainsKey(addr) || AllowUndefined) map[addr] = values[i];
            }
        }

        RangeChanged?.Invoke(area, start, values.Length);
    }

    public IReadOnlyList<(ushort Address, ushort Value)> SnapshotWords(RegisterArea area)
    {
        lock (_gate) return WordsFor(area).OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)).ToArray();
    }

    public IReadOnlyList<(ushort Address, bool Value)> SnapshotBits(RegisterArea area)
    {
        lock (_gate) return BitsFor(area).OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)).ToArray();
    }

    private Dictionary<ushort, ushort> WordsFor(RegisterArea area) => area switch
    {
        RegisterArea.HoldingRegisters => _holding,
        RegisterArea.InputRegisters => _input,
        _ => throw new ArgumentException($"{area} is a bit area."),
    };

    private Dictionary<ushort, bool> BitsFor(RegisterArea area) => area switch
    {
        RegisterArea.Coils => _coils,
        RegisterArea.DiscreteInputs => _discrete,
        _ => throw new ArgumentException($"{area} is a word area."),
    };
}
