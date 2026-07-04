using Avalonia.Data.Converters;
using Fieldbench.Core.Modbus;
using Fieldbench.Core.Slave;

namespace Fieldbench.App.Converters;

public static class MiscConverters
{
    /// <summary>
    /// Radio-button-safe bool binding: passes the value through, but only writes
    /// back on check (uncheck writes nothing) — breaks the two-radio ping-pong loop.
    /// </summary>
    public static readonly IValueConverter TrueGate = new GateConverter(invert: false);

    /// <summary>Inverted radio gate: checked ⇒ write false to the source.</summary>
    public static readonly IValueConverter FalseGate = new GateConverter(invert: true);

    private sealed class GateConverter(bool invert) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => value is bool b && (invert ? !b : b);

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => value is true ? !invert : Avalonia.Data.BindingOperations.DoNothing;
    }

    public static readonly IValueConverter ChecksumLabel =
        new FuncValueConverter<ChecksumKind, string>(k => k switch
        {
            ChecksumKind.None => "No checksum",
            ChecksumKind.Crc16Modbus => "+ CRC-16 Modbus",
            ChecksumKind.Crc16Ccitt => "+ CRC-16 CCITT",
            ChecksumKind.Xor => "+ XOR",
            ChecksumKind.Sum => "+ SUM",
            _ => k.ToString(),
        });

    public static readonly IValueConverter GeneratorLabel =
        new FuncValueConverter<GeneratorKind, string>(k => k switch
        {
            GeneratorKind.Static => "Static",
            GeneratorKind.Increment => "Increment",
            GeneratorKind.RandomRange => "Random",
            GeneratorKind.Sine => "Sine",
            _ => k.ToString(),
        });

    public static readonly IValueConverter ProbeFcLabel =
        new FuncValueConverter<byte, string>(fc => $"FC{fc:00} · 1 reg");

    public static readonly IValueConverter TimeoutLabel =
        new FuncValueConverter<int, string>(ms => $"{ms} ms");
}
