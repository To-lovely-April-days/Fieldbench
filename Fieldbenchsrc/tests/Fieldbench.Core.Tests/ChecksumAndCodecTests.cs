using Fieldbench.Core.Modbus;

namespace Fieldbench.Core.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Checksums
// ─────────────────────────────────────────────────────────────────────────────

public class ChecksumTests
{
    private static readonly byte[] ReadHoldingRequest = [0x01, 0x03, 0x00, 0x00, 0x00, 0x0A];

    [Fact]
    public void Crc16Modbus_KnownVector_ReadHoldingRequest()
    {
        // Classic frame "01 03 00 00 00 0A C5 CD" — wire order lo-hi, so the ushort is 0xCDC5.
        Assert.Equal(0xCDC5, Checksums.Crc16Modbus(ReadHoldingRequest));
    }

    [Fact]
    public void Crc16Modbus_KnownVector_Check123456789()
    {
        // Standard CRC-16/MODBUS check value for ASCII "123456789".
        Assert.Equal(0x4B37, Checksums.Crc16Modbus("123456789"u8));
    }

    [Fact]
    public void Crc16Modbus_EmptyInput_ReturnsInit0xFFFF()
    {
        Assert.Equal(0xFFFF, Checksums.Crc16Modbus(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Crc16Ccitt_KnownVector_Check123456789()
    {
        // Standard CRC-16/XMODEM check value for ASCII "123456789".
        Assert.Equal(0x31C3, Checksums.Crc16Ccitt("123456789"u8));
    }

    [Fact]
    public void Crc16Ccitt_EmptyInput_ReturnsInitZero()
    {
        Assert.Equal(0x0000, Checksums.Crc16Ccitt(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Xor_KnownVector_Check123456789()
    {
        Assert.Equal(0x31, Checksums.Xor("123456789"u8));
    }

    [Fact]
    public void Xor_EmptyInput_ReturnsZero()
    {
        Assert.Equal(0, Checksums.Xor(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Xor_SameByteTwice_CancelsToZero()
    {
        Assert.Equal(0, Checksums.Xor([0xA5, 0xA5]));
    }

    [Fact]
    public void Sum8_KnownVector_Check123456789()
    {
        // 0x31 + … + 0x39 = 477 = 0x1DD, truncated to 8 bits.
        Assert.Equal(0xDD, Checksums.Sum8("123456789"u8));
    }

    [Fact]
    public void Sum8_EmptyInput_ReturnsZero()
    {
        Assert.Equal(0, Checksums.Sum8(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Sum8_WrapsAround256()
    {
        Assert.Equal(0x01, Checksums.Sum8([0xFF, 0x02]));
    }

    [Fact]
    public void ValidateModbusCrc_ValidFrame_ReturnsTrue()
    {
        byte[] frame = [0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, 0xC5, 0xCD];
        Assert.True(Checksums.ValidateModbusCrc(frame));
    }

    [Fact]
    public void ValidateModbusCrc_MinimumLengthFourByteFrame_ReturnsTrue()
    {
        // CRC16-Modbus of [01 02] is 0xE181, wire order lo-hi = 81 E1.
        byte[] frame = [0x01, 0x02, 0x81, 0xE1];
        Assert.True(Checksums.ValidateModbusCrc(frame));
    }

    [Fact]
    public void ValidateModbusCrc_SwappedCrcBytes_ReturnsFalse()
    {
        byte[] frame = [0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, 0xCD, 0xC5];
        Assert.False(Checksums.ValidateModbusCrc(frame));
    }

    [Fact]
    public void ValidateModbusCrc_CorruptedPayload_ReturnsFalse()
    {
        byte[] frame = [0x01, 0x03, 0x00, 0x01, 0x00, 0x0A, 0xC5, 0xCD];
        Assert.False(Checksums.ValidateModbusCrc(frame));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ValidateModbusCrc_BufferShorterThanFourBytes_ReturnsFalse(int length)
    {
        Assert.False(Checksums.ValidateModbusCrc(new byte[length]));
    }
}

public class ChecksumAppenderTests
{
    private static readonly byte[] ModbusPayload = [0x01, 0x03, 0x00, 0x00, 0x00, 0x0A];

    [Fact]
    public void Append_None_ReturnsPayloadUnchanged()
    {
        byte[] payload = [0x01, 0x02, 0x03];
        var result = ChecksumAppender.Append(payload, ChecksumKind.None);
        Assert.Same(payload, result);
    }

    [Fact]
    public void Append_Crc16Modbus_AppendsLowByteThenHighByte()
    {
        var result = ChecksumAppender.Append(ModbusPayload, ChecksumKind.Crc16Modbus);
        Assert.Equal(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, 0xC5, 0xCD }, result);
    }

    [Fact]
    public void Append_Crc16Modbus_ResultPassesValidateModbusCrc()
    {
        var result = ChecksumAppender.Append([0x11, 0x22, 0x33], ChecksumKind.Crc16Modbus);
        Assert.True(Checksums.ValidateModbusCrc(result));
    }

    [Fact]
    public void Append_Crc16Ccitt_AppendsHighByteThenLowByte()
    {
        var result = ChecksumAppender.Append("123456789"u8.ToArray(), ChecksumKind.Crc16Ccitt);
        Assert.Equal(11, result.Length);
        Assert.Equal(0x31, result[^2]); // hi byte of 0x31C3 first
        Assert.Equal(0xC3, result[^1]);
    }

    [Fact]
    public void Append_Xor_AppendsSingleXorByte()
    {
        var result = ChecksumAppender.Append("123456789"u8.ToArray(), ChecksumKind.Xor);
        Assert.Equal(10, result.Length);
        Assert.Equal(0x31, result[^1]);
    }

    [Fact]
    public void Append_Sum_AppendsSingleTruncatedSumByte()
    {
        var result = ChecksumAppender.Append("123456789"u8.ToArray(), ChecksumKind.Sum);
        Assert.Equal(10, result.Length);
        Assert.Equal(0xDD, result[^1]);
    }

    [Theory]
    [InlineData(ChecksumKind.Crc16Modbus, 2)]
    [InlineData(ChecksumKind.Crc16Ccitt, 2)]
    [InlineData(ChecksumKind.Xor, 1)]
    [InlineData(ChecksumKind.Sum, 1)]
    public void Append_PreservesOriginalPayloadBytes(ChecksumKind kind, int extraBytes)
    {
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        var result = ChecksumAppender.Append(payload, kind);
        Assert.Equal(payload.Length + extraBytes, result.Length);
        Assert.Equal(payload, result[..payload.Length]);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ModbusCodec — builder / parser round trips
// ─────────────────────────────────────────────────────────────────────────────

public class ModbusCodecRequestRoundTripTests
{
    [Theory]
    [InlineData(ModbusFunction.ReadCoils)]
    [InlineData(ModbusFunction.ReadDiscreteInputs)]
    [InlineData(ModbusFunction.ReadHoldingRegisters)]
    [InlineData(ModbusFunction.ReadInputRegisters)]
    public void ReadRequest_RoundTrips(byte fc)
    {
        var pdu = ModbusCodec.BuildReadRequestPdu(fc, 0x1234, 0x0056);
        var msg = ModbusCodec.TryParseRequestPdu(0x11, pdu);

        Assert.NotNull(msg);
        Assert.Equal(0x11, msg.Unit);
        Assert.Equal(fc, msg.Function);
        Assert.Equal(ModbusMessageKind.Request, msg.Kind);
        Assert.Equal(0x1234, msg.Address);
        Assert.Equal(0x0056, msg.Quantity);
        Assert.Empty(msg.Data);
        Assert.False(msg.IsWrite);
    }

    [Fact]
    public void ReadRequestPdu_HasBigEndianWireLayout()
    {
        var pdu = ModbusCodec.BuildReadRequestPdu(ModbusFunction.ReadHoldingRegisters, 0x0000, 0x000A);
        Assert.Equal(new byte[] { 0x03, 0x00, 0x00, 0x00, 0x0A }, pdu);
    }

    [Theory]
    [InlineData(true, 0xFF00)]
    [InlineData(false, 0x0000)]
    public void WriteSingleCoilRequest_RoundTrips(bool value, int expectedQuantity)
    {
        var pdu = ModbusCodec.BuildWriteSingleCoilPdu(0x00AC, value);
        var msg = ModbusCodec.TryParseRequestPdu(1, pdu);

        Assert.NotNull(msg);
        Assert.Equal(ModbusFunction.WriteSingleCoil, msg.Function);
        Assert.Equal(ModbusMessageKind.Request, msg.Kind);
        Assert.Equal(0x00AC, msg.Address);
        Assert.Equal((ushort)expectedQuantity, msg.Quantity);
        Assert.True(msg.IsWrite);
    }

    [Fact]
    public void WriteSingleRegisterRequest_RoundTrips()
    {
        var pdu = ModbusCodec.BuildWriteSingleRegisterPdu(0x0001, 0xABCD);
        var msg = ModbusCodec.TryParseRequestPdu(1, pdu);

        Assert.NotNull(msg);
        Assert.Equal(ModbusFunction.WriteSingleRegister, msg.Function);
        Assert.Equal(ModbusMessageKind.Request, msg.Kind);
        Assert.Equal(0x0001, msg.Address);
        Assert.Equal(0xABCD, msg.Quantity);
        Assert.True(msg.IsWrite);
    }

    [Fact]
    public void WriteMultipleCoilsRequest_RoundTrips_WithLsbFirstBitPacking()
    {
        // true, false, true → bit0 and bit2 set → 0b0000_0101
        var pdu = ModbusCodec.BuildWriteMultipleCoilsPdu(0x0013, [true, false, true]);
        Assert.Equal(new byte[] { 0x0F, 0x00, 0x13, 0x00, 0x03, 0x01, 0x05 }, pdu);

        var msg = ModbusCodec.TryParseRequestPdu(9, pdu);
        Assert.NotNull(msg);
        Assert.Equal(ModbusFunction.WriteMultipleCoils, msg.Function);
        Assert.Equal(ModbusMessageKind.Request, msg.Kind);
        Assert.Equal(0x0013, msg.Address);
        Assert.Equal(3, msg.Quantity);
        Assert.Equal(new byte[] { 0x05 }, msg.Data);
        Assert.True(msg.IsWrite);
    }

    [Fact]
    public void WriteMultipleCoilsRequest_NineCoils_UsesTwoDataBytes()
    {
        var values = new bool[9];
        Array.Fill(values, true);
        var pdu = ModbusCodec.BuildWriteMultipleCoilsPdu(0, values);

        Assert.Equal(2, pdu[5]); // byte count
        var msg = ModbusCodec.TryParseRequestPdu(1, pdu);
        Assert.NotNull(msg);
        Assert.Equal(9, msg.Quantity);
        Assert.Equal(new byte[] { 0xFF, 0x01 }, msg.Data);
    }

    [Fact]
    public void WriteMultipleRegistersRequest_RoundTrips_WithBigEndianData()
    {
        var pdu = ModbusCodec.BuildWriteMultipleRegistersPdu(0x0001, [0x1234, 0xABCD]);
        Assert.Equal(new byte[] { 0x10, 0x00, 0x01, 0x00, 0x02, 0x04, 0x12, 0x34, 0xAB, 0xCD }, pdu);

        var msg = ModbusCodec.TryParseRequestPdu(2, pdu);
        Assert.NotNull(msg);
        Assert.Equal(ModbusFunction.WriteMultipleRegisters, msg.Function);
        Assert.Equal(ModbusMessageKind.Request, msg.Kind);
        Assert.Equal(0x0001, msg.Address);
        Assert.Equal(2, msg.Quantity);
        Assert.Equal(new byte[] { 0x12, 0x34, 0xAB, 0xCD }, msg.Data);
        Assert.True(msg.IsWrite);
    }
}

public class ModbusCodecResponseRoundTripTests
{
    [Theory]
    [InlineData(ModbusFunction.ReadCoils)]
    [InlineData(ModbusFunction.ReadDiscreteInputs)]
    [InlineData(ModbusFunction.ReadHoldingRegisters)]
    [InlineData(ModbusFunction.ReadInputRegisters)]
    public void ReadResponse_RoundTrips(byte fc)
    {
        byte[] data = [0x00, 0x2A, 0x01, 0xFF];
        var pdu = ModbusCodec.BuildReadResponsePdu(fc, data);
        Assert.Equal(fc, pdu[0]);
        Assert.Equal(4, pdu[1]);

        var msg = ModbusCodec.TryParseResponsePdu(3, pdu);
        Assert.NotNull(msg);
        Assert.Equal(3, msg.Unit);
        Assert.Equal(fc, msg.Function);
        Assert.Equal(ModbusMessageKind.Response, msg.Kind);
        Assert.Equal(data, msg.Data);
    }

    [Fact]
    public void ReadResponse_EmptyData_RoundTrips()
    {
        var pdu = ModbusCodec.BuildReadResponsePdu(ModbusFunction.ReadCoils, ReadOnlySpan<byte>.Empty);
        Assert.Equal(new byte[] { 0x01, 0x00 }, pdu);

        var msg = ModbusCodec.TryParseResponsePdu(1, pdu);
        Assert.NotNull(msg);
        Assert.Empty(msg.Data);
    }

    [Theory]
    [InlineData(ModbusFunction.WriteSingleCoil)]
    [InlineData(ModbusFunction.WriteSingleRegister)]
    [InlineData(ModbusFunction.WriteMultipleCoils)]
    [InlineData(ModbusFunction.WriteMultipleRegisters)]
    public void EchoResponse_RoundTrips(byte fc)
    {
        var pdu = ModbusCodec.BuildEchoResponsePdu(fc, 0x0102, 0x0304);
        var msg = ModbusCodec.TryParseResponsePdu(7, pdu);

        Assert.NotNull(msg);
        Assert.Equal(fc, msg.Function);
        Assert.Equal(ModbusMessageKind.Response, msg.Kind);
        Assert.Equal(0x0102, msg.Address);
        Assert.Equal(0x0304, msg.Quantity);
    }

    [Theory]
    [InlineData(ModbusFunction.ReadCoils, 0x01)]
    [InlineData(ModbusFunction.ReadDiscreteInputs, 0x02)]
    [InlineData(ModbusFunction.ReadHoldingRegisters, 0x02)]
    [InlineData(ModbusFunction.ReadInputRegisters, 0x03)]
    [InlineData(ModbusFunction.WriteSingleCoil, 0x04)]
    [InlineData(ModbusFunction.WriteSingleRegister, 0x06)]
    [InlineData(ModbusFunction.WriteMultipleCoils, 0x0B)]
    [InlineData(ModbusFunction.WriteMultipleRegisters, 0x0A)]
    public void ExceptionPdu_RoundTrips_ForEveryFunctionCode(byte fc, byte exceptionCode)
    {
        var pdu = ModbusCodec.BuildExceptionPdu(fc, exceptionCode);
        Assert.Equal(2, pdu.Length);
        Assert.Equal((byte)(fc | 0x80), pdu[0]);

        var msg = ModbusCodec.TryParseResponsePdu(5, pdu);
        Assert.NotNull(msg);
        Assert.Equal(ModbusMessageKind.Exception, msg.Kind);
        Assert.Equal(fc, msg.Function); // exception flag stripped
        Assert.Equal(exceptionCode, msg.ExceptionCode);
    }
}

public class ModbusCodecParseFailureTests
{
    [Fact]
    public void TryParseRequestPdu_EmptyBuffer_ReturnsNull()
    {
        Assert.Null(ModbusCodec.TryParseRequestPdu(1, ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void TryParseRequestPdu_FixedLengthFcWithWrongLength_ReturnsNull(int length)
    {
        var pdu = new byte[length];
        pdu[0] = ModbusFunction.ReadHoldingRegisters;
        Assert.Null(ModbusCodec.TryParseRequestPdu(1, pdu));
    }

    [Fact]
    public void TryParseRequestPdu_WriteMultipleTooShortForHeader_ReturnsNull()
    {
        Assert.Null(ModbusCodec.TryParseRequestPdu(1, [0x10, 0x00, 0x00, 0x00, 0x01]));
    }

    [Fact]
    public void TryParseRequestPdu_WriteMultipleByteCountMismatch_ReturnsNull()
    {
        // Header says 4 data bytes but only 2 follow.
        Assert.Null(ModbusCodec.TryParseRequestPdu(1, [0x10, 0x00, 0x00, 0x00, 0x02, 0x04, 0x12, 0x34]));
    }

    [Fact]
    public void TryParseRequestPdu_UnknownFunctionCode_ReturnsNull()
    {
        Assert.Null(ModbusCodec.TryParseRequestPdu(1, [0x2B, 0x00, 0x00, 0x00, 0x01]));
    }

    [Fact]
    public void TryParseResponsePdu_SingleByte_ReturnsNull()
    {
        Assert.Null(ModbusCodec.TryParseResponsePdu(1, [0x03]));
    }

    [Fact]
    public void TryParseResponsePdu_ExceptionWithTrailingBytes_ReturnsNull()
    {
        Assert.Null(ModbusCodec.TryParseResponsePdu(1, [0x83, 0x02, 0x00]));
    }

    [Fact]
    public void TryParseResponsePdu_ReadResponseByteCountMismatch_ReturnsNull()
    {
        // Byte count says 4 but only 2 data bytes follow.
        Assert.Null(ModbusCodec.TryParseResponsePdu(1, [0x03, 0x04, 0x00, 0x2A]));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void TryParseResponsePdu_EchoResponseWrongLength_ReturnsNull(int length)
    {
        var pdu = new byte[length];
        pdu[0] = ModbusFunction.WriteSingleRegister;
        Assert.Null(ModbusCodec.TryParseResponsePdu(1, pdu));
    }

    [Fact]
    public void TryParseResponsePdu_UnknownFunctionCode_ReturnsNull()
    {
        Assert.Null(ModbusCodec.TryParseResponsePdu(1, [0x2B, 0x00, 0x00, 0x00, 0x01]));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ModbusCodec — RTU wrapping
// ─────────────────────────────────────────────────────────────────────────────

public class ModbusCodecWrapRtuTests
{
    [Fact]
    public void WrapRtu_ReadHoldingRequest_ProducesCanonicalFrame()
    {
        var pdu = ModbusCodec.BuildReadRequestPdu(ModbusFunction.ReadHoldingRegisters, 0, 10);
        var frame = ModbusCodec.WrapRtu(0x01, pdu);
        Assert.Equal(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, 0xC5, 0xCD }, frame);
    }

    [Theory]
    [InlineData(ModbusFunction.ReadCoils)]
    [InlineData(ModbusFunction.ReadDiscreteInputs)]
    [InlineData(ModbusFunction.ReadHoldingRegisters)]
    [InlineData(ModbusFunction.ReadInputRegisters)]
    public void WrapRtu_ReadRequests_AreCrcValid(byte fc)
    {
        var frame = ModbusCodec.WrapRtu(0x11, ModbusCodec.BuildReadRequestPdu(fc, 0x006B, 0x0003));
        Assert.True(Checksums.ValidateModbusCrc(frame));
        Assert.Equal(8, frame.Length);
        Assert.Equal(0x11, frame[0]);
        Assert.Equal(fc, frame[1]);
    }

    [Fact]
    public void WrapRtu_ExceptionPdu_IsCrcValidAndFiveBytes()
    {
        var frame = ModbusCodec.WrapRtu(0x0A, ModbusCodec.BuildExceptionPdu(ModbusFunction.ReadCoils, 0x02));
        Assert.Equal(5, frame.Length);
        Assert.True(Checksums.ValidateModbusCrc(frame));
        Assert.Equal(0x81, frame[1]);
    }

    [Fact]
    public void WrapRtu_WriteMultipleRegisters_IsCrcValid()
    {
        var frame = ModbusCodec.WrapRtu(0x02, ModbusCodec.BuildWriteMultipleRegistersPdu(0x0001, [0x000A, 0x0102]));
        Assert.True(Checksums.ValidateModbusCrc(frame));
    }

    [Fact]
    public void WrapRtu_UnwrapsBackToSamePdu()
    {
        var pdu = ModbusCodec.BuildWriteSingleCoilPdu(0x00AC, true);
        var frame = ModbusCodec.WrapRtu(0x07, pdu);

        Assert.Equal(0x07, frame[0]);
        Assert.Equal(pdu, frame[1..^2]);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ModbusCodec — length prediction
// ─────────────────────────────────────────────────────────────────────────────

public class ModbusCodecPredictRequestLengthTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void BufferShorterThanTwoBytes_ReturnsNull(int length)
    {
        Assert.Null(ModbusCodec.PredictRequestLength(new byte[length]));
    }

    [Theory]
    [InlineData(ModbusFunction.ReadCoils)]
    [InlineData(ModbusFunction.ReadDiscreteInputs)]
    [InlineData(ModbusFunction.ReadHoldingRegisters)]
    [InlineData(ModbusFunction.ReadInputRegisters)]
    [InlineData(ModbusFunction.WriteSingleCoil)]
    [InlineData(ModbusFunction.WriteSingleRegister)]
    public void FixedLengthFunctions_ReturnEight(byte fc)
    {
        Assert.Equal(8, ModbusCodec.PredictRequestLength([0x01, fc]));
    }

    [Theory]
    [InlineData(ModbusFunction.WriteMultipleCoils)]
    [InlineData(ModbusFunction.WriteMultipleRegisters)]
    public void WriteMultiple_BufferTooShortForByteCount_ReturnsNull(byte fc)
    {
        Assert.Null(ModbusCodec.PredictRequestLength([0x01, fc, 0x00, 0x00, 0x00, 0x02]));
    }

    [Theory]
    [InlineData(ModbusFunction.WriteMultipleCoils, 1, 10)]
    [InlineData(ModbusFunction.WriteMultipleRegisters, 4, 13)]
    [InlineData(ModbusFunction.WriteMultipleRegisters, 0, 9)]     // degenerate zero byte count
    [InlineData(ModbusFunction.WriteMultipleRegisters, 246, 255)] // max register write: 123 regs
    [InlineData(ModbusFunction.WriteMultipleCoils, 246, 255)]     // max coil write: 1968 coils
    [InlineData(ModbusFunction.WriteMultipleCoils, 247, -1)]      // beyond the spec limit: rejected
    [InlineData(ModbusFunction.WriteMultipleRegisters, 255, -1)]  // corrupt byte count: rejected
    public void WriteMultiple_UsesByteCountFromOffsetSix(byte fc, int byteCount, int expected)
    {
        byte[] buf = [0x01, fc, 0x00, 0x00, 0x00, 0x02, (byte)byteCount];
        Assert.Equal(expected, ModbusCodec.PredictRequestLength(buf));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x07)]
    [InlineData(0x2B)]
    [InlineData(0x83)] // exception fc is never a request
    public void UnknownFunctionCode_ReturnsMinusOne(byte fc)
    {
        Assert.Equal(-1, ModbusCodec.PredictRequestLength([0x01, fc]));
    }
}

public class ModbusCodecPredictResponseLengthTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void BufferShorterThanTwoBytes_ReturnsNull(int length)
    {
        Assert.Null(ModbusCodec.PredictResponseLength(new byte[length]));
    }

    [Theory]
    [InlineData(0x81)]
    [InlineData(0x83)]
    [InlineData(0x90)]
    public void ExceptionResponse_ReturnsFive(byte fc)
    {
        Assert.Equal(5, ModbusCodec.PredictResponseLength([0x01, fc]));
    }

    [Theory]
    [InlineData(ModbusFunction.ReadCoils)]
    [InlineData(ModbusFunction.ReadDiscreteInputs)]
    [InlineData(ModbusFunction.ReadHoldingRegisters)]
    [InlineData(ModbusFunction.ReadInputRegisters)]
    public void ReadResponse_ByteCountNotYetAvailable_ReturnsNull(byte fc)
    {
        Assert.Null(ModbusCodec.PredictResponseLength([0x01, fc]));
    }

    [Theory]
    [InlineData(ModbusFunction.ReadCoils, 1, 6)]
    [InlineData(ModbusFunction.ReadHoldingRegisters, 20, 25)]
    [InlineData(ModbusFunction.ReadInputRegisters, 0, 5)]      // degenerate zero byte count
    [InlineData(ModbusFunction.ReadHoldingRegisters, 250, 255)] // max read: 125 regs = 250 bytes
    public void ReadResponse_UsesByteCountFromOffsetTwo(byte fc, int byteCount, int expected)
    {
        Assert.Equal(expected, ModbusCodec.PredictResponseLength([0x01, fc, (byte)byteCount]));
    }

    [Theory]
    [InlineData(251)]
    [InlineData(255)]
    public void ReadResponse_ByteCountOver250_ReturnsMinusOne(int byteCount)
    {
        Assert.Equal(-1, ModbusCodec.PredictResponseLength([0x01, ModbusFunction.ReadHoldingRegisters, (byte)byteCount]));
    }

    [Theory]
    [InlineData(ModbusFunction.WriteSingleCoil)]
    [InlineData(ModbusFunction.WriteSingleRegister)]
    [InlineData(ModbusFunction.WriteMultipleCoils)]
    [InlineData(ModbusFunction.WriteMultipleRegisters)]
    public void WriteResponses_ReturnEight(byte fc)
    {
        Assert.Equal(8, ModbusCodec.PredictResponseLength([0x01, fc]));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x07)]
    [InlineData(0x2B)]
    public void UnknownFunctionCode_ReturnsMinusOne(byte fc)
    {
        Assert.Equal(-1, ModbusCodec.PredictResponseLength([0x01, fc]));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// RegisterValueCodec
// ─────────────────────────────────────────────────────────────────────────────

public class RegisterValueCodecNumericTests
{
    public static IEnumerable<object[]> TypeOrderValueCombos()
    {
        var cases = new (RegisterDataType Type, double Value)[]
        {
            (RegisterDataType.Bit, 1),
            (RegisterDataType.Int16, -12345),
            (RegisterDataType.UInt16, 54321),
            (RegisterDataType.Int32, -123456789),
            (RegisterDataType.UInt32, 3123456789),
            (RegisterDataType.Float32, 25.0f),
            (RegisterDataType.Double64, -1234.5),
        };
        foreach (var (type, value) in cases)
        {
            foreach (var order in Enum.GetValues<WordOrder>())
            {
                yield return [type, order, value];
            }
        }
    }

    [Theory]
    [MemberData(nameof(TypeOrderValueCombos))]
    public void EncodeDecode_RoundTrips_ForEveryTypeAndWordOrder(RegisterDataType type, WordOrder order, double value)
    {
        var regs = RegisterValueCodec.EncodeNumeric(value, type, order);
        Assert.Equal(type.RegisterCount(), regs.Length);
        Assert.Equal(value, RegisterValueCodec.DecodeNumeric(regs, type, order));
    }

    [Theory]
    [InlineData(WordOrder.ABCD, 0x41C8, 0x0000)]
    [InlineData(WordOrder.CDAB, 0x0000, 0x41C8)]
    [InlineData(WordOrder.BADC, 0xC841, 0x0000)]
    [InlineData(WordOrder.DCBA, 0x0000, 0xC841)]
    public void Float32_TwentyFive_HasExpectedRegisterLayout(WordOrder order, int reg0, int reg1)
    {
        // IEEE-754: 25.0f = 0x41C80000, byte layout ABCD = 41 C8 00 00.
        var regs = RegisterValueCodec.EncodeNumeric(25.0, RegisterDataType.Float32, order);
        Assert.Equal(new[] { (ushort)reg0, (ushort)reg1 }, regs);
    }

    [Theory]
    [InlineData(WordOrder.ABCD, 0x0A0B, 0x0C0D)]
    [InlineData(WordOrder.CDAB, 0x0C0D, 0x0A0B)]
    [InlineData(WordOrder.BADC, 0x0B0A, 0x0D0C)]
    [InlineData(WordOrder.DCBA, 0x0D0C, 0x0B0A)]
    public void UInt32_0A0B0C0D_MatchesWordOrderNamingConvention(WordOrder order, int reg0, int reg1)
    {
        var regs = RegisterValueCodec.EncodeNumeric(0x0A0B0C0D, RegisterDataType.UInt32, order);
        Assert.Equal(new[] { (ushort)reg0, (ushort)reg1 }, regs);
    }

    [Theory]
    [InlineData(WordOrder.ABCD)]
    [InlineData(WordOrder.CDAB)]
    [InlineData(WordOrder.BADC)]
    [InlineData(WordOrder.DCBA)]
    public void Decode_Float32_TwentyFive_FromRegisterLayout(WordOrder order)
    {
        var regs = RegisterValueCodec.EncodeNumeric(25.0, RegisterDataType.Float32, order);
        Assert.Equal(25.0, RegisterValueCodec.DecodeNumeric(regs, RegisterDataType.Float32, order));
    }

    [Fact]
    public void Decode_Float32_Abcd_KnownBytes()
    {
        Assert.Equal(25.0, RegisterValueCodec.DecodeNumeric([0x41C8, 0x0000], RegisterDataType.Float32, WordOrder.ABCD));
    }

    [Fact]
    public void Double64_UsesFourRegisters_AndRoundTripsExactValue()
    {
        // -1234.5 = 0xC0934A0000000000 big-endian.
        var regs = RegisterValueCodec.EncodeNumeric(-1234.5, RegisterDataType.Double64, WordOrder.ABCD);
        Assert.Equal(new ushort[] { 0xC093, 0x4A00, 0x0000, 0x0000 }, regs);
        Assert.Equal(-1234.5, RegisterValueCodec.DecodeNumeric(regs, RegisterDataType.Double64, WordOrder.ABCD));
    }

    [Theory]
    [InlineData(0x0000, 0)]
    [InlineData(0x0001, 1)]
    [InlineData(0x0500, 1)] // any nonzero register reads as 1
    public void Bit_Decode_NormalizesToZeroOrOne(int reg, double expected)
    {
        Assert.Equal(expected, RegisterValueCodec.DecodeNumeric([(ushort)reg], RegisterDataType.Bit, WordOrder.ABCD));
    }

    [Fact]
    public void Bit_Decode_EmptySpan_ReturnsNaN()
    {
        // Short reads decode to NaN uniformly across all numeric types.
        Assert.True(double.IsNaN(RegisterValueCodec.DecodeNumeric(ReadOnlySpan<ushort>.Empty, RegisterDataType.Bit, WordOrder.ABCD)));
    }

    [Fact]
    public void Int16_Decode_IsSignExtended()
    {
        Assert.Equal(-1, RegisterValueCodec.DecodeNumeric([0xFFFF], RegisterDataType.Int16, WordOrder.ABCD));
    }

    [Fact]
    public void UInt16_Decode_IsUnsigned()
    {
        Assert.Equal(65535, RegisterValueCodec.DecodeNumeric([0xFFFF], RegisterDataType.UInt16, WordOrder.ABCD));
    }

    [Theory]
    [InlineData(RegisterDataType.UInt16, -5, 0)]
    [InlineData(RegisterDataType.UInt16, 70000, 65535)]
    [InlineData(RegisterDataType.Int16, 40000, 32767)]
    [InlineData(RegisterDataType.Int16, -40000, -32768)]
    [InlineData(RegisterDataType.Int32, 3e9, 2147483647)]
    [InlineData(RegisterDataType.UInt32, -1, 0)]
    public void EncodeNumeric_ClampsOutOfRangeIntegerValues(RegisterDataType type, double input, double expected)
    {
        var regs = RegisterValueCodec.EncodeNumeric(input, type, WordOrder.ABCD);
        Assert.Equal(expected, RegisterValueCodec.DecodeNumeric(regs, type, WordOrder.ABCD));
    }
}

public class RegisterValueCodecTextTests
{
    [Fact]
    public void EncodeText_Abcd_PacksTwoAsciiCharsPerRegister()
    {
        Assert.Equal(new ushort[] { 0x4142 }, RegisterValueCodec.EncodeText("AB", 1, WordOrder.ABCD));
    }

    [Fact]
    public void EncodeText_OddLength_PadsWithNul()
    {
        Assert.Equal(new ushort[] { 0x4142, 0x4300 }, RegisterValueCodec.EncodeText("ABC", 2, WordOrder.ABCD));
    }

    [Fact]
    public void DecodeText_StopsAtNulTerminator()
    {
        Assert.Equal("ABC", RegisterValueCodec.DecodeText([0x4142, 0x4300], WordOrder.ABCD));
    }

    [Fact]
    public void DecodeText_NoTerminator_ReadsAllBytes()
    {
        Assert.Equal("ABCD", RegisterValueCodec.DecodeText([0x4142, 0x4344], WordOrder.ABCD));
    }

    [Fact]
    public void EncodeText_TruncatesToRegisterCapacity()
    {
        var regs = RegisterValueCodec.EncodeText("ABCDEF", 2, WordOrder.ABCD);
        Assert.Equal(2, regs.Length);
        Assert.Equal("ABCD", RegisterValueCodec.DecodeText(regs, WordOrder.ABCD));
    }

    [Theory]
    [InlineData(WordOrder.ABCD)]
    [InlineData(WordOrder.CDAB)]
    [InlineData(WordOrder.BADC)]
    [InlineData(WordOrder.DCBA)]
    public void Text_RoundTrips_ForEveryWordOrder(WordOrder order)
    {
        var regs = RegisterValueCodec.EncodeText("PUMP", 2, order);
        Assert.Equal(2, regs.Length);
        Assert.Equal("PUMP", RegisterValueCodec.DecodeText(regs, order));
    }

    [Theory]
    [InlineData(WordOrder.ABCD)]
    [InlineData(WordOrder.CDAB)]
    [InlineData(WordOrder.BADC)]
    [InlineData(WordOrder.DCBA)]
    public void Text_ShorterThanCapacity_RoundTripsForEveryWordOrder(WordOrder order)
    {
        var regs = RegisterValueCodec.EncodeText("OK", 4, order);
        Assert.Equal(4, regs.Length);
        Assert.Equal("OK", RegisterValueCodec.DecodeText(regs, order));
    }
}

public class RegisterValueCodecFormatTests
{
    [Theory]
    [InlineData(1, "1")]
    [InlineData(0, "0")]
    [InlineData(-1, "1")] // any nonzero displays as 1
    public void Format_Bit_ShowsZeroOrOne(double value, string expected)
    {
        Assert.Equal(expected, RegisterValueCodec.Format(value, RegisterDataType.Bit, 1.0));
    }

    [Theory]
    [InlineData(RegisterDataType.Int16, 7, "7")]
    [InlineData(RegisterDataType.UInt16, 65535, "65535")]
    [InlineData(RegisterDataType.Int32, -3, "-3")]
    [InlineData(RegisterDataType.UInt32, 100000, "100000")]
    public void Format_IntegerTypesAtUnityScale_HaveNoDecimals(RegisterDataType type, double value, string expected)
    {
        Assert.Equal(expected, RegisterValueCodec.Format(value, type, 1.0));
    }

    [Theory]
    [InlineData(0.1, 0.5, "0.5")]
    [InlineData(0.01, 0.25, "0.25")]
    [InlineData(0.001, 0.125, "0.125")]
    [InlineData(0.0001, 0.0625, "0.0625")]
    public void Format_ScaledIntegers_UsePrecisionMatchingScale(double scale, double value, string expected)
    {
        Assert.Equal(expected, RegisterValueCodec.Format(value, RegisterDataType.UInt16, scale));
    }

    [Fact]
    public void Format_Float32_SmallMagnitudeAtUnityScale_UsesOneDecimal()
    {
        Assert.Equal("25.0", RegisterValueCodec.Format(25.0, RegisterDataType.Float32, 1.0));
    }

    [Fact]
    public void Format_Float32_LargeMagnitudeAtUnityScale_UsesNoDecimals()
    {
        Assert.Equal("123", RegisterValueCodec.Format(123.25, RegisterDataType.Float32, 1.0));
    }

    [Fact]
    public void Format_Double64_ScaledFine_UsesFourDecimals()
    {
        Assert.Equal("0.1250", RegisterValueCodec.Format(0.125, RegisterDataType.Double64, 0.0001));
    }

    [Fact]
    public void Format_UsesInvariantCulture_DecimalPoint()
    {
        var formatted = RegisterValueCodec.Format(1.5, RegisterDataType.Float32, 1.0);
        Assert.Equal("1.5", formatted);
        Assert.DoesNotContain(",", formatted);
    }
}
