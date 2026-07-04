using System.Globalization;
using System.Text.RegularExpressions;
using Fieldbench.Core.Master;
using Fieldbench.Core.Modbus;

namespace Fieldbench.Core.Ai;

public enum AddressBase
{
    /// <summary>PLC-style 1-based addresses with area prefix: 40001 = holding register 0.</summary>
    Plc1Based,
    /// <summary>Raw 0-based protocol addresses; area chosen separately.</summary>
    Protocol0Based,
}

public enum PointStatus
{
    Ready,
    Warning,
    Invalid,
}

/// <summary>One extracted point-map row, editable in the review sheet.</summary>
public sealed class PointRow
{
    public string RawAddress { get; set; } = "";

    public string Name { get; set; } = "";

    public RegisterDataType DataType { get; set; } = RegisterDataType.UInt16;

    public WordOrder WordOrder { get; set; } = WordOrder.ABCD;

    public double Scale { get; set; } = 1;

    public double Offset { get; set; }

    public string Unit { get; set; } = "";

    public bool Writable { get; set; }

    public string Notes { get; set; } = "";

    public bool Selected { get; set; } = true;

    // Review results
    public PointStatus Status { get; set; } = PointStatus.Ready;

    public string? StatusNote { get; set; }

    /// <summary>Resolved after base confirmation.</summary>
    public RegisterArea Area { get; set; } = RegisterArea.HoldingRegisters;

    public int ProtocolAddress { get; set; } = -1;

    public int RegisterCount => DataType.RegisterCount();

    public RegisterTag ToTag() => new()
    {
        Area = Area,
        Address = (ushort)Math.Clamp(ProtocolAddress, 0, ushort.MaxValue),
        Name = Name,
        DataType = DataType,
        WordOrder = WordOrder,
        Scale = Scale,
        Offset = Offset,
        Unit = Unit,
        Writable = Writable,
        Notes = Notes,
    };
}

public sealed class PointMapExtraction
{
    public List<PointRow> Rows { get; } = new();

    public string? SourceTitle { get; set; }

    public string? Error { get; set; }

    public AddressBase SuggestedBase { get; set; } = AddressBase.Plc1Based;
}

/// <summary>
/// Heuristic parser for pasted register tables (tab/comma/pipe/space separated).
/// The gateway's vision model produces the same row schema; this covers the
/// offline paste-text path and every unit test.
/// </summary>
public static partial class PointMapTextParser
{
    [GeneratedRegex(@"^\s*(?<addr>[0-9A-Fa-fxX]{1,7})\b")]
    private static partial Regex AddressPattern();

    [GeneratedRegex(@"[×xX*]\s*(?<scale>0?\.\d+|\d+\.?\d*)")]
    private static partial Regex ScalePattern();

    public static PointMapExtraction Parse(string text)
    {
        var result = new PointMapExtraction();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int plcStyle = 0, zeroStyle = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cells = SplitCells(line);
            if (cells.Count < 2) continue;

            var addrMatch = AddressPattern().Match(cells[0]);
            if (!addrMatch.Success) continue;
            string rawAddr = cells[0].Trim();

            // Header rows ("Addr", "Address"…) fail the numeric check above.
            var row = new PointRow { RawAddress = rawAddr };

            // Column heuristics: name = first non-numeric text cell; type keywords; unit; R/W.
            foreach (var cell in cells.Skip(1))
            {
                var c = cell.Trim();
                if (c.Length == 0) continue;
                if (TryType(c, row)) continue;
                if (TryReadWrite(c, row)) continue;
                if (TryUnit(c, row)) continue;
                if (row.Name.Length == 0)
                {
                    row.Name = Slugify(c);
                    continue;
                }

                if (row.Notes.Length == 0 && c.Length > 1) row.Notes = c;
            }

            if (row.Name.Length == 0) row.Name = $"point_{rawAddr}";

            if (int.TryParse(rawAddr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            {
                if (numeric is >= 10000 and <= 49999 || numeric is >= 100001 and <= 465535) plcStyle++;
                else zeroStyle++;
            }

            result.Rows.Add(row);
        }

        result.SuggestedBase = plcStyle >= zeroStyle ? AddressBase.Plc1Based : AddressBase.Protocol0Based;
        return result;
    }

    private static List<string> SplitCells(string line)
    {
        string[] parts;
        if (line.Contains('\t')) parts = line.Split('\t');
        else if (line.Contains('|')) parts = line.Split('|');
        else if (line.Contains(',')) parts = line.Split(',');
        else parts = Regex.Split(line.Trim(), @"\s{2,}");
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    private static bool TryType(string cell, PointRow row)
    {
        var upper = cell.ToUpperInvariant().Replace(" ", "");
        RegisterDataType? type = null;
        if (upper.Contains("FLOAT64") || upper.Contains("DOUBLE")) type = RegisterDataType.Double64;
        else if (upper.Contains("FLOAT") || upper.Contains("REAL")) type = RegisterDataType.Float32;
        else if (upper.Contains("UINT32") || upper.Contains("UDINT") || upper.Contains("DWORD")) type = RegisterDataType.UInt32;
        else if (upper.Contains("INT32") || upper.Contains("DINT") || upper.Contains("LONG")) type = RegisterDataType.Int32;
        else if (upper.Contains("UINT16") || upper.Contains("UINT") || upper.Contains("WORD")) type = RegisterDataType.UInt16;
        else if (upper.Contains("INT16") || upper.Contains("INT") || upper.Contains("SHORT")) type = RegisterDataType.Int16;
        else if (upper.Contains("BIT") || upper.Contains("BOOL")) type = RegisterDataType.Bit;
        else if (upper.Contains("STRING") || upper.Contains("ASCII") || upper.Contains("CHAR")) type = RegisterDataType.Text;

        if (type is null && !upper.Contains('×') && !upper.Contains('X')) return false;
        if (type is not null) row.DataType = type.Value;

        var scale = ScalePattern().Match(cell);
        if (scale.Success && double.TryParse(scale.Groups["scale"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0)
        {
            row.Scale = s;
        }

        return type is not null || scale.Success;
    }

    private static bool TryReadWrite(string cell, PointRow row)
    {
        var upper = cell.ToUpperInvariant().Replace(" ", "");
        switch (upper)
        {
            case "R" or "RO" or "READ":
                row.Writable = false;
                return true;
            case "RW" or "R/W" or "W" or "WR" or "WRITE" or "READ/WRITE":
                row.Writable = true;
                return true;
            default:
                return false;
        }
    }

    private static readonly string[] KnownUnits =
    [
        "°C", "°F", "K", "BAR", "MBAR", "KPA", "MPA", "PA", "PSI", "L/MIN", "L/S", "M3/H", "M³/H",
        "V", "MV", "KV", "A", "MA", "KW", "W", "KWH", "HZ", "RPM", "%", "MM", "CM", "M", "S", "MS", "MIN", "H",
    ];

    private static bool TryUnit(string cell, PointRow row)
    {
        var upper = cell.ToUpperInvariant();
        if (KnownUnits.Contains(upper) || cell is "°C" or "°F")
        {
            row.Unit = cell;
            return true;
        }

        return false;
    }

    private static string Slugify(string text)
    {
        var slug = Regex.Replace(text.Trim().ToLowerInvariant(), @"[^\w]+", "_").Trim('_');
        return slug.Length > 24 ? slug[..24].TrimEnd('_') : slug;
    }
}

/// <summary>
/// Review pipeline: forced address-base resolution, conflict detection and
/// segmented poll-task generation (contiguous merge, ≤125 registers per task).
/// AI results never import silently — this validates what the user confirms.
/// </summary>
public static class PointMapReview
{
    /// <summary>Resolve areas + protocol addresses under the chosen base and flag problems.</summary>
    public static void Resolve(IList<PointRow> rows, AddressBase addressBase)
    {
        foreach (var row in rows)
        {
            row.Status = PointStatus.Ready;
            row.StatusNote = null;

            if (!int.TryParse(row.RawAddress, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                row.Status = PointStatus.Invalid;
                row.StatusNote = "check address";
                row.ProtocolAddress = -1;
                continue;
            }

            if (addressBase == AddressBase.Plc1Based)
            {
                (RegisterArea area, int offset)? resolved = value switch
                {
                    >= 1 and <= 9999 => (RegisterArea.Coils, value - 1),
                    >= 10001 and <= 19999 => (RegisterArea.DiscreteInputs, value - 10001),
                    >= 30001 and <= 39999 => (RegisterArea.InputRegisters, value - 30001),
                    >= 40001 and <= 49999 => (RegisterArea.HoldingRegisters, value - 40001),
                    >= 100001 and <= 165536 => (RegisterArea.DiscreteInputs, value - 100001),
                    >= 300001 and <= 365536 => (RegisterArea.InputRegisters, value - 300001),
                    >= 400001 and <= 465536 => (RegisterArea.HoldingRegisters, value - 400001),
                    _ => null,
                };

                if (resolved is null)
                {
                    row.Status = PointStatus.Invalid;
                    row.StatusNote = "check address";
                    row.ProtocolAddress = -1;
                    continue;
                }

                row.Area = resolved.Value.area;
                row.ProtocolAddress = resolved.Value.offset;
            }
            else
            {
                if (value is < 0 or > 65535)
                {
                    row.Status = PointStatus.Invalid;
                    row.StatusNote = "out of range";
                    row.ProtocolAddress = -1;
                    continue;
                }

                row.ProtocolAddress = value;
                if (row.Area == RegisterArea.Coils && row.DataType != RegisterDataType.Bit)
                {
                    row.Area = RegisterArea.HoldingRegisters;
                }
            }

            if (row.DataType == RegisterDataType.Bit && !row.Area.IsBitArea())
            {
                // A bits word in a register area is fine (status words); keep as uint16.
                row.DataType = RegisterDataType.UInt16;
            }
        }

        DetectOverlaps(rows);
    }

    private static void DetectOverlaps(IList<PointRow> rows)
    {
        var valid = rows.Where(r => r.Status != PointStatus.Invalid && r.Selected).OrderBy(r => r.Area).ThenBy(r => r.ProtocolAddress).ToList();
        for (int i = 1; i < valid.Count; i++)
        {
            var prev = valid[i - 1];
            var cur = valid[i];
            if (prev.Area == cur.Area && cur.ProtocolAddress < prev.ProtocolAddress + prev.RegisterCount)
            {
                if (cur.Status == PointStatus.Ready)
                {
                    cur.Status = PointStatus.Warning;
                    cur.StatusNote = $"overlaps {prev.Area.DisplayAddress(prev.ProtocolAddress)} {prev.DataType.Label()}";
                }
            }
        }
    }

    /// <summary>
    /// Merge contiguous selected points into poll tasks, honoring the 125-register
    /// read limit. "One-click read-all" comes from running these once.
    /// </summary>
    public static List<PollTask> BuildPollTasks(IEnumerable<PointRow> rows, byte unit, int periodMs = 500)
    {
        var tasks = new List<PollTask>();
        foreach (var group in rows
                     .Where(r => r.Selected && r.Status != PointStatus.Invalid && r.ProtocolAddress >= 0)
                     .GroupBy(r => r.Area))
        {
            var spans = group
                .Select(r => (Start: r.ProtocolAddress, End: r.ProtocolAddress + r.RegisterCount))
                .OrderBy(s => s.Start)
                .ToList();
            if (spans.Count == 0) continue;

            int limit = group.Key.IsBitArea() ? ModbusCodec.MaxReadCoils : ModbusCodec.MaxReadRegisters;
            int curStart = spans[0].Start, curEnd = spans[0].End;
            foreach (var (start, end) in spans.Skip(1))
            {
                bool contiguous = start <= curEnd; // adjacent or overlapping
                bool withinLimit = Math.Max(end, curEnd) - curStart <= limit;
                if (contiguous && withinLimit)
                {
                    curEnd = Math.Max(curEnd, end);
                }
                else
                {
                    tasks.Add(MakeTask(group.Key, unit, curStart, curEnd, periodMs, tasks.Count));
                    curStart = start;
                    curEnd = end;
                }
            }

            tasks.Add(MakeTask(group.Key, unit, curStart, curEnd, periodMs, tasks.Count));
        }

        return tasks;
    }

    private static PollTask MakeTask(RegisterArea area, byte unit, int start, int end, int periodMs, int index) => new()
    {
        Name = $"Task {index + 1}",
        Unit = unit,
        Function = RegisterAreaInfo.ReadFcForArea(area),
        Start = (ushort)start,
        Count = (ushort)(end - start),
        PeriodMs = periodMs,
        Enabled = false,
    };
}
