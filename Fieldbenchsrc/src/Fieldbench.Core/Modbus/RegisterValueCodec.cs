using System.Buffers.Binary;
using System.Globalization;

namespace Fieldbench.Core.Modbus;

public enum RegisterDataType
{
    Bit,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Float32,
    Double64,
    Text,
}

/// <summary>
/// Word/byte order for multi-register values, named by the byte layout of the
/// value 0x0A0B0C0D on the wire (AB CD = big-endian words, big-endian bytes).
/// </summary>
public enum WordOrder
{
    ABCD,
    CDAB,
    BADC,
    DCBA,
}

public static class RegisterDataTypeInfo
{
    /// <summary>Number of 16-bit registers this type occupies (Text uses its own length).</summary>
    public static int RegisterCount(this RegisterDataType type, int textLength = 8) => type switch
    {
        RegisterDataType.Bit => 1,
        RegisterDataType.Int16 or RegisterDataType.UInt16 => 1,
        RegisterDataType.Int32 or RegisterDataType.UInt32 or RegisterDataType.Float32 => 2,
        RegisterDataType.Double64 => 4,
        RegisterDataType.Text => (textLength + 1) / 2,
        _ => 1,
    };

    public static string Label(this RegisterDataType type) => type switch
    {
        RegisterDataType.Bit => "bit",
        RegisterDataType.Int16 => "int16",
        RegisterDataType.UInt16 => "uint16",
        RegisterDataType.Int32 => "int32",
        RegisterDataType.UInt32 => "uint32",
        RegisterDataType.Float32 => "float32",
        RegisterDataType.Double64 => "double",
        RegisterDataType.Text => "string",
        _ => "?",
    };

    public static string Label(this WordOrder order) => order switch
    {
        WordOrder.ABCD => "AB CD",
        WordOrder.CDAB => "CD AB",
        WordOrder.BADC => "BA DC",
        WordOrder.DCBA => "DC BA",
        _ => "?",
    };
}

/// <summary>
/// Decode/encode typed values over arrays of 16-bit registers with the four
/// byte-order conventions found in the field. Registers are the big-endian
/// words as they appear on the wire.
/// </summary>
public static class RegisterValueCodec
{
    /// <summary>Reorder raw big-endian register bytes into logical big-endian bytes for the value.</summary>
    private static byte[] ToLogicalBytes(ReadOnlySpan<ushort> regs, WordOrder order)
    {
        int n = regs.Length;
        var words = new ushort[n];

        // Word swap
        for (int i = 0; i < n; i++)
        {
            words[i] = order is WordOrder.CDAB or WordOrder.DCBA ? regs[n - 1 - i] : regs[i];
        }

        var bytes = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            ushort w = words[i];
            if (order is WordOrder.BADC or WordOrder.DCBA)
            {
                bytes[i * 2] = (byte)(w & 0xFF);
                bytes[i * 2 + 1] = (byte)(w >> 8);
            }
            else
            {
                bytes[i * 2] = (byte)(w >> 8);
                bytes[i * 2 + 1] = (byte)(w & 0xFF);
            }
        }

        return bytes;
    }

    private static ushort[] FromLogicalBytes(ReadOnlySpan<byte> bytes, WordOrder order)
    {
        int n = bytes.Length / 2;
        var words = new ushort[n];
        for (int i = 0; i < n; i++)
        {
            words[i] = order is WordOrder.BADC or WordOrder.DCBA
                ? (ushort)(bytes[i * 2] | (bytes[i * 2 + 1] << 8))
                : (ushort)((bytes[i * 2] << 8) | bytes[i * 2 + 1]);
        }

        if (order is WordOrder.CDAB or WordOrder.DCBA) Array.Reverse(words);
        return words;
    }

    public static double DecodeNumeric(ReadOnlySpan<ushort> regs, RegisterDataType type, WordOrder order)
    {
        if (regs.Length < type.RegisterCount()) return double.NaN;
        switch (type)
        {
            case RegisterDataType.Bit:
                return regs.Length > 0 && regs[0] != 0 ? 1 : 0;
            case RegisterDataType.Int16:
                return (short)regs[0];
            case RegisterDataType.UInt16:
                return regs[0];
        }

        var bytes = ToLogicalBytes(regs, order);
        return type switch
        {
            RegisterDataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(bytes),
            RegisterDataType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(bytes),
            RegisterDataType.Float32 => BinaryPrimitives.ReadSingleBigEndian(bytes),
            RegisterDataType.Double64 => BinaryPrimitives.ReadDoubleBigEndian(bytes),
            _ => double.NaN,
        };
    }

    public static string DecodeText(ReadOnlySpan<ushort> regs, WordOrder order)
    {
        var bytes = ToLogicalBytes(regs, order);
        int end = ((ReadOnlySpan<byte>)bytes).IndexOf((byte)0);
        if (end < 0) end = bytes.Length;
        return System.Text.Encoding.ASCII.GetString(bytes, 0, end);
    }

    public static ushort[] EncodeNumeric(double value, RegisterDataType type, WordOrder order)
    {
        switch (type)
        {
            case RegisterDataType.Bit:
                return [(ushort)(value != 0 ? 1 : 0)];
            case RegisterDataType.Int16:
                return [(ushort)(short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue)];
            case RegisterDataType.UInt16:
                return [(ushort)Math.Clamp(Math.Round(value), ushort.MinValue, ushort.MaxValue)];
        }

        byte[] bytes;
        switch (type)
        {
            case RegisterDataType.Int32:
                bytes = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(bytes, (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue));
                break;
            case RegisterDataType.UInt32:
                bytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)Math.Clamp(Math.Round(value), uint.MinValue, uint.MaxValue));
                break;
            case RegisterDataType.Float32:
                bytes = new byte[4];
                BinaryPrimitives.WriteSingleBigEndian(bytes, (float)value);
                break;
            case RegisterDataType.Double64:
                bytes = new byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(bytes, value);
                break;
            default:
                return [0];
        }

        return FromLogicalBytes(bytes, order);
    }

    public static ushort[] EncodeText(string text, int registerCount, WordOrder order)
    {
        var bytes = new byte[registerCount * 2];
        var src = System.Text.Encoding.ASCII.GetBytes(text);
        Array.Copy(src, bytes, Math.Min(src.Length, bytes.Length));
        return FromLogicalBytes(bytes, order);
    }

    /// <summary>Format a scaled numeric value for display with sensible precision.</summary>
    public static string Format(double scaledValue, RegisterDataType type, double scale)
    {
        if (type is RegisterDataType.Bit) return scaledValue != 0 ? "1" : "0";
        if (type is RegisterDataType.Int16 or RegisterDataType.UInt16 or RegisterDataType.Int32 or RegisterDataType.UInt32
            && Math.Abs(scale - 1) < double.Epsilon)
        {
            return scaledValue.ToString("0", CultureInfo.InvariantCulture);
        }

        int decimals = scale switch
        {
            <= 0.0001 => 4,
            <= 0.001 => 3,
            <= 0.01 => 2,
            <= 0.1 => 1,
            _ => Math.Abs(scaledValue) < 100 ? 1 : 0,
        };
        return scaledValue.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }
}
