using System.Buffers.Binary;

namespace Fieldbench.Core.Modbus;

public enum ModbusMessageKind
{
    Request,
    Response,
    Exception,
}

/// <summary>A parsed Modbus PDU with its unit id (transport framing already stripped).</summary>
public sealed class ModbusMessage
{
    public required byte Unit { get; init; }

    /// <summary>Function code without the exception flag.</summary>
    public required byte Function { get; init; }

    public required ModbusMessageKind Kind { get; init; }

    /// <summary>Start address (requests, single/multiple write echoes).</summary>
    public ushort Address { get; init; }

    /// <summary>Quantity of coils/registers (requests, multiple write echoes) or value for single writes.</summary>
    public ushort Quantity { get; init; }

    /// <summary>Data payload of read responses / multi-write requests.</summary>
    public byte[] Data { get; init; } = [];

    public byte ExceptionCode { get; init; }

    public bool IsWrite => Function is ModbusFunction.WriteSingleCoil or ModbusFunction.WriteSingleRegister
        or ModbusFunction.WriteMultipleCoils or ModbusFunction.WriteMultipleRegisters;
}

/// <summary>PDU builders and parsers for FC 01/02/03/04/05/06/0F/10 + exceptions.</summary>
public static class ModbusCodec
{
    public const int MaxReadRegisters = 125;
    public const int MaxWriteRegisters = 123;
    public const int MaxReadCoils = 2000;

    // ── PDU builders (function code + data, no unit / CRC / MBAP) ──

    public static byte[] BuildReadRequestPdu(byte fc, ushort start, ushort quantity)
    {
        var pdu = new byte[5];
        pdu[0] = fc;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), quantity);
        return pdu;
    }

    public static byte[] BuildWriteSingleCoilPdu(ushort address, bool value)
    {
        var pdu = new byte[5];
        pdu[0] = ModbusFunction.WriteSingleCoil;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), value ? (ushort)0xFF00 : (ushort)0x0000);
        return pdu;
    }

    public static byte[] BuildWriteSingleRegisterPdu(ushort address, ushort value)
    {
        var pdu = new byte[5];
        pdu[0] = ModbusFunction.WriteSingleRegister;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), value);
        return pdu;
    }

    public static byte[] BuildWriteMultipleCoilsPdu(ushort start, ReadOnlySpan<bool> values)
    {
        int byteCount = (values.Length + 7) / 8;
        var pdu = new byte[6 + byteCount];
        pdu[0] = ModbusFunction.WriteMultipleCoils;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Length);
        pdu[5] = (byte)byteCount;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i]) pdu[6 + i / 8] |= (byte)(1 << (i % 8));
        }

        return pdu;
    }

    public static byte[] BuildWriteMultipleRegistersPdu(ushort start, ReadOnlySpan<ushort> values)
    {
        var pdu = new byte[6 + values.Length * 2];
        pdu[0] = ModbusFunction.WriteMultipleRegisters;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Length);
        pdu[5] = (byte)(values.Length * 2);
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + i * 2), values[i]);
        }

        return pdu;
    }

    public static byte[] BuildReadResponsePdu(byte fc, ReadOnlySpan<byte> data)
    {
        var pdu = new byte[2 + data.Length];
        pdu[0] = fc;
        pdu[1] = (byte)data.Length;
        data.CopyTo(pdu.AsSpan(2));
        return pdu;
    }

    public static byte[] BuildEchoResponsePdu(byte fc, ushort address, ushort valueOrQty)
    {
        var pdu = new byte[5];
        pdu[0] = fc;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), valueOrQty);
        return pdu;
    }

    public static byte[] BuildExceptionPdu(byte fc, byte exceptionCode) => [(byte)(fc | 0x80), exceptionCode];

    // ── RTU ADU wrap/unwrap ──

    public static byte[] WrapRtu(byte unit, ReadOnlySpan<byte> pdu)
    {
        var frame = new byte[1 + pdu.Length + 2];
        frame[0] = unit;
        pdu.CopyTo(frame.AsSpan(1));
        ushort crc = Checksums.Crc16Modbus(frame.AsSpan(0, frame.Length - 2));
        frame[^2] = (byte)(crc & 0xFF);
        frame[^1] = (byte)(crc >> 8);
        return frame;
    }

    // ── length prediction for RTU framing (hybrid 3.5T + CRC strategy) ──

    /// <summary>Expected total RTU frame length if the buffer starts with a request; null when undecidable yet (-1 = never a request).</summary>
    public static int? PredictRequestLength(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 2) return null;
        byte fc = buf[1];
        return fc switch
        {
            ModbusFunction.ReadCoils or ModbusFunction.ReadDiscreteInputs
                or ModbusFunction.ReadHoldingRegisters or ModbusFunction.ReadInputRegisters
                or ModbusFunction.WriteSingleCoil or ModbusFunction.WriteSingleRegister => 8,
            ModbusFunction.WriteMultipleCoils or ModbusFunction.WriteMultipleRegisters =>
                buf.Length < 7 ? null : buf[6] > 246 ? -1 : 9 + buf[6],
            _ => -1,
        };
    }

    /// <summary>Expected total RTU frame length if the buffer starts with a response; null when undecidable yet (-1 = never).</summary>
    public static int? PredictResponseLength(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 2) return null;
        byte fc = buf[1];
        if ((fc & 0x80) != 0) return 5;
        return fc switch
        {
            ModbusFunction.ReadCoils or ModbusFunction.ReadDiscreteInputs
                or ModbusFunction.ReadHoldingRegisters or ModbusFunction.ReadInputRegisters =>
                buf.Length < 3 ? null : buf[2] > 250 ? -1 : 5 + buf[2],
            ModbusFunction.WriteSingleCoil or ModbusFunction.WriteSingleRegister
                or ModbusFunction.WriteMultipleCoils or ModbusFunction.WriteMultipleRegisters => 8,
            _ => -1,
        };
    }

    // ── PDU parsing ──

    /// <summary>
    /// Parse a PDU as a request. Returns null when the shape is invalid.
    /// </summary>
    public static ModbusMessage? TryParseRequestPdu(byte unit, ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 1) return null;
        byte fc = pdu[0];
        switch (fc)
        {
            case ModbusFunction.ReadCoils:
            case ModbusFunction.ReadDiscreteInputs:
            case ModbusFunction.ReadHoldingRegisters:
            case ModbusFunction.ReadInputRegisters:
            case ModbusFunction.WriteSingleCoil:
            case ModbusFunction.WriteSingleRegister:
                if (pdu.Length != 5) return null;
                return new ModbusMessage
                {
                    Unit = unit,
                    Function = fc,
                    Kind = ModbusMessageKind.Request,
                    Address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]),
                    Quantity = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]),
                };

            case ModbusFunction.WriteMultipleCoils:
            case ModbusFunction.WriteMultipleRegisters:
            {
                if (pdu.Length < 6) return null;
                byte byteCount = pdu[5];
                if (pdu.Length != 6 + byteCount) return null;
                return new ModbusMessage
                {
                    Unit = unit,
                    Function = fc,
                    Kind = ModbusMessageKind.Request,
                    Address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]),
                    Quantity = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]),
                    Data = pdu[6..].ToArray(),
                };
            }

            default:
                return null;
        }
    }

    /// <summary>Parse a PDU as a response (including exceptions). Returns null when invalid.</summary>
    public static ModbusMessage? TryParseResponsePdu(byte unit, ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 2) return null;
        byte fc = pdu[0];

        if ((fc & 0x80) != 0)
        {
            if (pdu.Length != 2) return null;
            return new ModbusMessage
            {
                Unit = unit,
                Function = (byte)(fc & 0x7F),
                Kind = ModbusMessageKind.Exception,
                ExceptionCode = pdu[1],
            };
        }

        switch (fc)
        {
            case ModbusFunction.ReadCoils:
            case ModbusFunction.ReadDiscreteInputs:
            case ModbusFunction.ReadHoldingRegisters:
            case ModbusFunction.ReadInputRegisters:
            {
                byte byteCount = pdu[1];
                if (pdu.Length != 2 + byteCount) return null;
                return new ModbusMessage
                {
                    Unit = unit,
                    Function = fc,
                    Kind = ModbusMessageKind.Response,
                    Data = pdu[2..].ToArray(),
                };
            }

            case ModbusFunction.WriteSingleCoil:
            case ModbusFunction.WriteSingleRegister:
            case ModbusFunction.WriteMultipleCoils:
            case ModbusFunction.WriteMultipleRegisters:
                if (pdu.Length != 5) return null;
                return new ModbusMessage
                {
                    Unit = unit,
                    Function = fc,
                    Kind = ModbusMessageKind.Response,
                    Address = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]),
                    Quantity = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]),
                };

            default:
                return null;
        }
    }
}
