using System.Globalization;
using System.Text;
using System.Text.Json;
using Fieldbench.Core.Lenses;
using Fieldbench.Core.Master;

namespace Fieldbench.Core.Export;

public enum ExportFormat
{
    Csv,
    Json,
    Bin,
    Txt,
}

/// <summary>Frame export: selection or full timeline → CSV / JSON / raw bin / text log.</summary>
public static class FrameExporter
{
    public static byte[] Export(IEnumerable<Frame> frames, ExportFormat format) => format switch
    {
        ExportFormat.Csv => Encoding.UTF8.GetBytes(ToCsv(frames)),
        ExportFormat.Json => JsonSerializer.SerializeToUtf8Bytes(frames.Select(ToDto), JsonOpts),
        ExportFormat.Bin => ToBin(frames),
        ExportFormat.Txt => Encoding.UTF8.GetBytes(ToTxt(frames)),
        _ => [],
    };

    public static string DefaultExtension(ExportFormat format) => format switch
    {
        ExportFormat.Csv => "csv",
        ExportFormat.Json => "json",
        ExportFormat.Bin => "bin",
        _ => "log",
    };

    public static string ToCsv(IEnumerable<Frame> frames)
    {
        var sb = new StringBuilder();
        sb.AppendLine("timestamp,direction,length,summary,status,hex");
        foreach (var f in frames)
        {
            sb.Append(f.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(f.Direction).Append(',');
            sb.Append(f.Bytes.Length).Append(',');
            sb.Append('"').Append(FullSummary(f).Replace("\"", "\"\"")).Append('"').Append(',');
            sb.Append(f.StatusTag ?? "").Append(',');
            sb.AppendLine(f.HexString());
        }

        return sb.ToString();
    }

    public static string ToTxt(IEnumerable<Frame> frames)
    {
        var sb = new StringBuilder();
        foreach (var f in frames)
        {
            sb.Append(f.TimestampUtc.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
            sb.Append(f.Direction == Streams.StreamDirection.Tx ? "  TX  " : "  RX  ");
            sb.Append(f.HexString().PadRight(52));
            sb.Append("  ").Append(FullSummary(f));
            if (f.StatusTag is { } tag && tag != "OK") sb.Append("  [").Append(tag).Append(']');
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static byte[] ToBin(IEnumerable<Frame> frames)
    {
        using var ms = new MemoryStream();
        foreach (var f in frames) ms.Write(f.Bytes);
        return ms.ToArray();
    }

    private static string FullSummary(Frame f)
    {
        var parts = new List<string>(3);
        if (f.AddressToken is { } a) parts.Add(a);
        if (f.FunctionToken is { } fn) parts.Add(fn);
        parts.Add(f.Summary);
        return string.Join(' ', parts);
    }

    private static object ToDto(Frame f) => new
    {
        timestamp = f.TimestampUtc,
        direction = f.Direction.ToString(),
        length = f.Bytes.Length,
        address = f.AddressToken,
        function = f.FunctionToken,
        summary = f.Summary,
        status = f.StatusTag,
        deltaMs = f.DeltaMs,
        hex = f.HexString(),
        fields = f.Fields.Select(field => new { field.Name, field.Value, field.Detail }),
    };

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

/// <summary>Chart CSV: one timestamp column + one column per channel (PRD §6.6).</summary>
public static class ChartExporter
{
    public static string ToCsv(IReadOnlyList<RegisterTag> channels)
    {
        var sb = new StringBuilder();
        sb.Append("timestamp");
        foreach (var c in channels) sb.Append(',').Append(c.Name);
        sb.AppendLine();

        var histories = channels.Select(c => c.HistorySnapshot()).ToArray();
        var allTimes = histories.SelectMany(h => h.Select(s => s.TimestampUtc)).Distinct().OrderBy(t => t).ToArray();
        var cursors = new int[channels.Count];

        foreach (var t in allTimes)
        {
            sb.Append(t.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            for (int i = 0; i < histories.Length; i++)
            {
                var h = histories[i];
                while (cursors[i] < h.Count - 1 && h[cursors[i] + 1].TimestampUtc <= t) cursors[i]++;
                sb.Append(',');
                if (h.Count > 0 && h[cursors[i]].TimestampUtc <= t)
                {
                    sb.Append(h[cursors[i]].Value.ToString("0.#####", CultureInfo.InvariantCulture));
                }
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
