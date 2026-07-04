using System.Buffers.Binary;
using Fieldbench.Core.Modbus;
using Fieldbench.Core.Streams;

namespace Fieldbench.Core.Lenses;

/// <summary>
/// Shared frame description logic: given a parsed Modbus message and the raw ADU,
/// produce the summary line, the status tag and the colored field map that both
/// the timeline row and the byte inspector render.
/// </summary>
internal static class ModbusFrameDescriber
{
    /// <summary>Field layout for an RTU ADU: [unit][pdu…][crc lo][crc hi].</summary>
    public static List<FrameField> RtuFields(ModbusMessage msg, byte[] adu, bool crcOk)
    {
        var fields = new List<FrameField>
        {
            new(0, 1, FieldKind.Address, "Slave address", $"0x{adu[0]:X2}"),
            new(1, 1, FieldKind.Function, "Function",
                msg.Kind == ModbusMessageKind.Exception
                    ? $"0x{adu[1]:X2} · {ModbusFunction.Name(msg.Function)} + error flag"
                    : $"0x{adu[1]:X2} · {ModbusFunction.Name(msg.Function)}"),
        };

        DescribePduBody(msg, adu.AsSpan(1, adu.Length - 3), 1, fields);
        AppendCrcField(adu, crcOk, fields);
        return fields;
    }

    /// <summary>Field layout for a TCP ADU: [MBAP 7][pdu…].</summary>
    public static List<FrameField> TcpFields(ModbusMessage msg, byte[] adu)
    {
        ushort txId = BinaryPrimitives.ReadUInt16BigEndian(adu);
        ushort len = BinaryPrimitives.ReadUInt16BigEndian(adu.AsSpan(4));
        var fields = new List<FrameField>
        {
            new(0, 2, FieldKind.Length, "Transaction", $"0x{txId:X4}"),
            new(2, 2, FieldKind.Length, "Protocol", "0x0000"),
            new(4, 2, FieldKind.Length, "Length", len.ToString()),
            new(6, 1, FieldKind.Address, "Unit ID", $"0x{adu[6]:X2}"),
            new(7, 1, FieldKind.Function, "Function",
                msg.Kind == ModbusMessageKind.Exception
                    ? $"0x{adu[7]:X2} · {ModbusFunction.Name(msg.Function)} + error flag"
                    : $"0x{adu[7]:X2} · {ModbusFunction.Name(msg.Function)}"),
        };

        DescribePduBody(msg, adu.AsSpan(7), 7, fields);
        return fields;
    }

    private static void DescribePduBody(ModbusMessage msg, ReadOnlySpan<byte> pdu, int pduOffset, List<FrameField> fields)
    {
        switch (msg.Kind)
        {
            case ModbusMessageKind.Exception:
                fields.Add(new FrameField(pduOffset + 1, 1, FieldKind.Data, "Exception",
                    $"0x{msg.ExceptionCode:X2} · {ModbusExceptions.Name(msg.ExceptionCode)}",
                    ModbusExceptions.Hint(msg.ExceptionCode)));
                break;

            case ModbusMessageKind.Request when msg.Function is ModbusFunction.WriteMultipleCoils or ModbusFunction.WriteMultipleRegisters:
                fields.Add(new FrameField(pduOffset + 1, 2, FieldKind.Data, "Start address", msg.Address.ToString()));
                fields.Add(new FrameField(pduOffset + 3, 2, FieldKind.Data, "Quantity", msg.Quantity.ToString()));
                fields.Add(new FrameField(pduOffset + 5, 1, FieldKind.Length, "Byte count", msg.Data.Length.ToString()));
                if (msg.Data.Length > 0)
                    fields.Add(new FrameField(pduOffset + 6, msg.Data.Length, FieldKind.Data, "Values", ValuePreview(msg)));
                break;

            case ModbusMessageKind.Request when msg.Function is ModbusFunction.WriteSingleCoil or ModbusFunction.WriteSingleRegister:
                fields.Add(new FrameField(pduOffset + 1, 2, FieldKind.Data, "Address", msg.Address.ToString()));
                fields.Add(new FrameField(pduOffset + 3, 2, FieldKind.Data, "Value",
                    msg.Function == ModbusFunction.WriteSingleCoil
                        ? (msg.Quantity == 0xFF00 ? "ON" : "OFF")
                        : $"0x{msg.Quantity:X4} · {msg.Quantity}"));
                break;

            case ModbusMessageKind.Request:
                fields.Add(new FrameField(pduOffset + 1, 2, FieldKind.Data, "Start address", msg.Address.ToString()));
                fields.Add(new FrameField(pduOffset + 3, 2, FieldKind.Data, "Quantity", msg.Quantity.ToString()));
                break;

            case ModbusMessageKind.Response when msg.Function is >= ModbusFunction.ReadCoils and <= ModbusFunction.ReadInputRegisters:
                fields.Add(new FrameField(pduOffset + 1, 1, FieldKind.Length, "Byte count", $"0x{msg.Data.Length:X2} · {msg.Data.Length}"));
                if (msg.Data.Length > 0)
                    fields.Add(new FrameField(pduOffset + 2, msg.Data.Length, FieldKind.Data, "Data", ValuePreview(msg)));
                break;

            case ModbusMessageKind.Response:
                fields.Add(new FrameField(pduOffset + 1, 2, FieldKind.Data, "Address", msg.Address.ToString()));
                fields.Add(new FrameField(pduOffset + 3, 2, FieldKind.Data,
                    msg.Function is ModbusFunction.WriteMultipleCoils or ModbusFunction.WriteMultipleRegisters ? "Quantity" : "Value",
                    msg.Quantity.ToString()));
                break;
        }
    }

    private static void AppendCrcField(byte[] adu, bool crcOk, List<FrameField> fields)
    {
        if (crcOk)
        {
            fields.Add(new FrameField(adu.Length - 2, 2, FieldKind.Checksum, "CRC-16",
                $"{adu[^2]:X2} {adu[^1]:X2} ✓"));
        }
        else
        {
            ushort expected = Checksums.Crc16Modbus(adu.AsSpan(0, adu.Length - 2));
            fields.Add(new FrameField(adu.Length - 2, 2, FieldKind.ChecksumBad, "CRC-16",
                $"{adu[^2]:X2} {adu[^1]:X2} ✕",
                $"expected {(byte)(expected & 0xFF):X2} {(byte)(expected >> 8):X2}"));
        }
    }

    private static string ValuePreview(ModbusMessage msg)
    {
        if (msg.Function is ModbusFunction.ReadCoils or ModbusFunction.ReadDiscreteInputs or ModbusFunction.WriteMultipleCoils)
            return $"{msg.Data.Length * 8} bits max";
        return $"{msg.Data.Length / 2} registers";
    }

    public static string Summary(ModbusMessage msg) => msg.Kind switch
    {
        ModbusMessageKind.Exception => ModbusExceptions.Name(msg.ExceptionCode),
        ModbusMessageKind.Request => msg.Function switch
        {
            ModbusFunction.WriteSingleCoil => $"Write coil {msg.Address} = {(msg.Quantity == 0xFF00 ? "ON" : "OFF")}",
            ModbusFunction.WriteSingleRegister => $"Write single {RegisterArea.HoldingRegisters.DisplayAddress(msg.Address)} = {msg.Quantity}",
            ModbusFunction.WriteMultipleCoils => $"Write {msg.Quantity} coils @ {msg.Address}",
            ModbusFunction.WriteMultipleRegisters => $"Write {msg.Quantity} regs @ {msg.Address}",
            _ => $"{ModbusFunction.Name(msg.Function)} {msg.Address}–{msg.Address + Math.Max(1, (int)msg.Quantity) - 1}",
        },
        _ => msg.Function switch
        {
            ModbusFunction.WriteSingleCoil => $"Echo · coil {msg.Address}",
            ModbusFunction.WriteSingleRegister => "Echo · accepted",
            ModbusFunction.WriteMultipleCoils or ModbusFunction.WriteMultipleRegisters => $"Echo · {msg.Quantity} written",
            _ => $"{msg.Data.Length} bytes",
        },
    };

    public static string AddressToken(ModbusMessage msg, bool tcp) => tcp ? $"U{msg.Unit:00}" : $"@{msg.Unit:00}";

    public static string FunctionToken(ModbusMessage msg) =>
        msg.Kind == ModbusMessageKind.Exception ? $"FC{msg.Function | 0x80:X2}" : $"FC{msg.Function:00}";

    /// <summary>Request/response pairing + latency + monitor role inference, applied incrementally.</summary>
    public sealed class Pairing
    {
        private Frame? _pendingRequest;
        private ModbusMessage? _pendingMessage;

        /// <summary>True when a request for this unit+function is awaiting its response.</summary>
        public bool ExpectsResponse(byte unit, byte function) =>
            _pendingMessage is { Kind: ModbusMessageKind.Request } m && m.Unit == unit && m.Function == function;

        public void Apply(Frame frame, ModbusMessage? msg)
        {
            if (msg is null)
            {
                _pendingRequest = null;
                _pendingMessage = null;
                return;
            }

            if (msg.Kind == ModbusMessageKind.Request)
            {
                frame.Role = FrameRole.MasterToSlave;
                _pendingRequest = frame;
                _pendingMessage = msg;
            }
            else
            {
                if (_pendingRequest is not null && _pendingMessage is not null
                    && _pendingMessage.Unit == msg.Unit && _pendingMessage.Function == msg.Function)
                {
                    frame.DeltaMs = Math.Max(0, (frame.TimestampUtc - _pendingRequest.TimestampUtc).TotalMilliseconds);
                }

                frame.Role = FrameRole.SlaveToMaster;
                _pendingRequest = null;
                _pendingMessage = null;
            }
        }

        public void Reset()
        {
            _pendingRequest = null;
            _pendingMessage = null;
        }
    }
}
