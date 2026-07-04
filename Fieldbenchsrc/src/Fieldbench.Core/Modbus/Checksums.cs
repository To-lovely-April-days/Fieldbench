namespace Fieldbench.Core.Modbus;

/// <summary>CRC16-Modbus plus the sender's other append options (CCITT / XOR / SUM).</summary>
public static class Checksums
{
    private static readonly ushort[] CrcTable = BuildModbusTable();

    private static ushort[] BuildModbusTable()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort crc = (ushort)i;
            for (int b = 0; b < 8; b++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }

            table[i] = crc;
        }

        return table;
    }

    /// <summary>CRC16-Modbus (poly 0x8005 reflected, init 0xFFFF). Wire order: lo byte first.</summary>
    public static ushort Crc16Modbus(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc = (ushort)((crc >> 8) ^ CrcTable[(crc ^ b) & 0xFF]);
        }

        return crc;
    }

    /// <summary>True when the trailing two bytes are a valid CRC16-Modbus of the preceding bytes.</summary>
    public static bool ValidateModbusCrc(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4) return false;
        ushort computed = Crc16Modbus(frame[..^2]);
        ushort received = (ushort)(frame[^2] | (frame[^1] << 8));
        return computed == received;
    }

    /// <summary>CRC16-CCITT (XModem variant: poly 0x1021, init 0x0000). Wire order: hi byte first.</summary>
    public static ushort Crc16Ccitt(ReadOnlySpan<byte> data)
    {
        ushort crc = 0x0000;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
        }

        return crc;
    }

    public static byte Xor(ReadOnlySpan<byte> data)
    {
        byte x = 0;
        foreach (var b in data) x ^= b;
        return x;
    }

    public static byte Sum8(ReadOnlySpan<byte> data)
    {
        byte s = 0;
        foreach (var b in data) s += b;
        return s;
    }
}

public enum ChecksumKind
{
    None,
    Crc16Modbus,
    Crc16Ccitt,
    Xor,
    Sum,
}

public static class ChecksumAppender
{
    public static byte[] Append(byte[] payload, ChecksumKind kind)
    {
        switch (kind)
        {
            case ChecksumKind.None:
                return payload;
            case ChecksumKind.Crc16Modbus:
            {
                ushort crc = Checksums.Crc16Modbus(payload);
                var result = new byte[payload.Length + 2];
                payload.CopyTo(result, 0);
                result[^2] = (byte)(crc & 0xFF);
                result[^1] = (byte)(crc >> 8);
                return result;
            }

            case ChecksumKind.Crc16Ccitt:
            {
                ushort crc = Checksums.Crc16Ccitt(payload);
                var result = new byte[payload.Length + 2];
                payload.CopyTo(result, 0);
                result[^2] = (byte)(crc >> 8);
                result[^1] = (byte)(crc & 0xFF);
                return result;
            }

            case ChecksumKind.Xor:
            {
                var result = new byte[payload.Length + 1];
                payload.CopyTo(result, 0);
                result[^1] = Checksums.Xor(payload);
                return result;
            }

            case ChecksumKind.Sum:
            {
                var result = new byte[payload.Length + 1];
                payload.CopyTo(result, 0);
                result[^1] = Checksums.Sum8(payload);
                return result;
            }

            default:
                return payload;
        }
    }
}
