using Fieldbench.Core.Lenses;
using Fieldbench.Core.Modbus;
using Fieldbench.Core.Streams;

namespace Fieldbench.Core.Tests;

/// <summary>
/// Framing tests for <see cref="ModbusRtuLens"/> + <see cref="FramingBuffer"/>.
/// All chunk timestamps are explicit DateTime values so the tests are fully
/// deterministic: the default silence window is max(3.5T, MinSilenceMs=8) = 8 ms,
/// so deltas below 8 ms are "same burst" and deltas of 8 ms or more are "silence".
///
/// Reference frames (CRC values verified independently):
///   request  = 01 03 00 00 00 02 C4 0B   (FC03 read holding 0..1)
///   response = 01 03 04 00 0A 00 14 DA 3E (FC03 response, 4 data bytes)
///   exception= 01 83 02 C0 F1             (FC03 + 0x80, EXC 02)
/// </summary>
public class RtuFramingTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double ms) => T0.AddMilliseconds(ms);

    private static StreamChunk Chunk(long seq, double atMs, StreamDirection dir, byte[] data, int clientId = 0) =>
        new(seq, At(atMs), dir, data, clientId);

    private static byte[] Request() =>
        ModbusCodec.WrapRtu(1, ModbusCodec.BuildReadRequestPdu(ModbusFunction.ReadHoldingRegisters, 0, 2));

    private static byte[] Response() =>
        ModbusCodec.WrapRtu(1, ModbusCodec.BuildReadResponsePdu(
            ModbusFunction.ReadHoldingRegisters, new byte[] { 0x00, 0x0A, 0x00, 0x14 }));

    private static byte[] ExceptionFrame() =>
        ModbusCodec.WrapRtu(1, ModbusCodec.BuildExceptionPdu(ModbusFunction.ReadHoldingRegisters, 0x02));

    // ── 1. Master perspective: clean request / response extract immediately ──

    [Fact]
    public void Master_TxRequest_ExtractsImmediately_WithoutSilence()
    {
        var lens = new ModbusRtuLens { Perspective = LensPerspective.Master };
        var request = Request();

        var frames = lens.Feed(Chunk(0, 0, StreamDirection.Tx, request));

        var frame = Assert.Single(frames);
        Assert.Equal(request, frame.Bytes);
        Assert.Equal(FrameStatus.Ok, frame.Status);
        Assert.Equal("OK", frame.StatusTag);
        Assert.Equal(StreamDirection.Tx, frame.Direction);
        Assert.Equal(FrameRole.MasterToSlave, frame.Role);
        Assert.Equal((byte)ModbusFunction.ReadHoldingRegisters, frame.FunctionCode);
        Assert.Equal((byte)1, frame.UnitId);
        Assert.Equal("@01", frame.AddressToken);
        Assert.Equal("FC03", frame.FunctionToken);
        Assert.Equal(At(0), frame.TimestampUtc);
        Assert.Empty(lens.FlushPending(DateTime.MaxValue));
    }

    [Fact]
    public void Master_RequestThenResponse_SeparateChunks_ExtractImmediately_AndPairDelta()
    {
        var lens = new ModbusRtuLens { Perspective = LensPerspective.Master };

        var txFrames = lens.Feed(Chunk(0, 0, StreamDirection.Tx, Request()));
        var rxFrames = lens.Feed(Chunk(1, 2, StreamDirection.Rx, Response()));

        var req = Assert.Single(txFrames);
        var resp = Assert.Single(rxFrames);

        Assert.Equal(FrameRole.MasterToSlave, req.Role);
        Assert.Equal(FrameRole.SlaveToMaster, resp.Role);
        Assert.Equal(FrameStatus.Ok, resp.Status);
        Assert.Equal(Response(), resp.Bytes);
        Assert.Equal(At(2), resp.TimestampUtc);
        Assert.NotNull(resp.DeltaMs);
        Assert.Equal(2.0, resp.DeltaMs!.Value);
    }

    // ── 2. Sticky frames: two complete frames in one chunk ──

    [Fact]
    public void StickyFrames_TwoCompleteFramesInOneChunk_BothExtract()
    {
        var lens = new ModbusRtuLens(); // Monitor: passive tap, everything Rx
        var request = Request();
        var response = Response();
        var sticky = new byte[request.Length + response.Length];
        request.CopyTo(sticky, 0);
        response.CopyTo(sticky, request.Length);

        var frames = lens.Feed(Chunk(0, 0, StreamDirection.Rx, sticky));

        Assert.Equal(2, frames.Count);
        Assert.Equal(request, frames[0].Bytes);
        Assert.Equal(response, frames[1].Bytes);
        Assert.All(frames, f => Assert.Equal(FrameStatus.Ok, f.Status));
        Assert.Equal(FrameRole.MasterToSlave, frames[0].Role);
        Assert.Equal(FrameRole.SlaveToMaster, frames[1].Role);
        Assert.Empty(lens.FlushPending(DateTime.MaxValue));
    }

    // ── 3. Split frame inside the silence window ──

    [Fact]
    public void SplitFrame_TwoChunksWithinSilence_WaitsThenCompletesOnSecondChunk()
    {
        var lens = new ModbusRtuLens { Perspective = LensPerspective.Master };
        var response = Response();

        var first = lens.Feed(Chunk(0, 0, StreamDirection.Rx, response[..5]));
        Assert.Empty(first); // predicted length 9 > 5 available: must wait

        var second = lens.Feed(Chunk(1, 1, StreamDirection.Rx, response[5..])); // 1 ms < 8 ms silence
        var frame = Assert.Single(second);
        Assert.Equal(response, frame.Bytes);
        Assert.Equal(FrameStatus.Ok, frame.Status);
        Assert.Equal(At(0), frame.TimestampUtc); // stamped with the first byte's arrival
        Assert.Empty(lens.FlushPending(DateTime.MaxValue));
    }

    [Fact]
    public void SplitFrame_ThreeChunks_OnlyFinalChunkYieldsFrame()
    {
        var lens = new ModbusRtuLens { Perspective = LensPerspective.Master };
        var response = Response();

        Assert.Empty(lens.Feed(Chunk(0, 0, StreamDirection.Rx, response[..2]))); // length still unpredictable
        Assert.Empty(lens.Feed(Chunk(1, 1, StreamDirection.Rx, response[2..5]))); // predicted 9 > 5
        var frames = lens.Feed(Chunk(2, 2, StreamDirection.Rx, response[5..]));

        var frame = Assert.Single(frames);
        Assert.Equal(response, frame.Bytes);
        Assert.Equal(At(0), frame.TimestampUtc);
    }

    // ── 4. Corrupted CRC closed by a silence gap ──

    [Fact]
    public void CorruptCrc_SilenceGapBeforeNextChunk_EmitsErrorFrameThenValidFrame()
    {
        var lens = new ModbusRtuLens { Perspective = LensPerspective.Master };
        var corrupt = Response();
        corrupt[^1] ^= 0x01; // flip one CRC bit

        Assert.Empty(lens.Feed(Chunk(0, 0, StreamDirection.Rx, corrupt)));

        // Next traffic arrives long after the 8 ms silence window: pending bytes
        // are a closed (broken) frame, then the new chunk parses normally.
        var frames = lens.Feed(Chunk(1, 100, StreamDirection.Rx, Response()));

        Assert.Equal(2, frames.Count);
        Assert.Equal(FrameStatus.Error, frames[0].Status);
        Assert.Equal("CRC FAIL", frames[0].StatusTag);
        Assert.Equal(corrupt, frames[0].Bytes);
        Assert.Equal(At(0), frames[0].TimestampUtc);
        // structure was intact, so the payload still parses semantically
        Assert.Equal((byte)ModbusFunction.ReadHoldingRegisters, frames[0].FunctionCode);
        Assert.Equal((byte)1, frames[0].UnitId);
        Assert.Equal(FieldKind.ChecksumBad, frames[0].Fields[^1].Kind);

        Assert.Equal(FrameStatus.Ok, frames[1].Status);
        Assert.Equal(Response(), frames[1].Bytes);
        Assert.Equal(At(100), frames[1].TimestampUtc);
    }

    [Fact]
    public void FlushPending_BeforeSilenceExpiry_KeepsBytes_AfterExpiry_DrainsCorruptFrame()
    {
        var lens = new ModbusRtuLens { Perspective = LensPerspective.Master };
        var corrupt = Response();
        corrupt[^2] ^= 0x80;

        Assert.Empty(lens.Feed(Chunk(0, 0, StreamDirection.Rx, corrupt)));
        Assert.Empty(lens.FlushPending(At(3))); // 3 ms < 8 ms: still inside the frame window

        var frames = lens.FlushPending(At(50)); // silence elapsed
        var frame = Assert.Single(frames);
        Assert.Equal(FrameStatus.Error, frame.Status);
        Assert.Equal("CRC FAIL", frame.StatusTag);
        Assert.Equal(corrupt, frame.Bytes);
        Assert.Empty(lens.FlushPending(DateTime.MaxValue));
    }

    // ── 5. Resync after garbage prefix ──

    [Fact]
    public void Resync_GarbagePrefixThenValidFrame_FlushEmitsGarbageErrorThenParsedFrame()
    {
        var lens = new ModbusRtuLens(); // Monitor
        var request = Request();
        var garbage = new byte[] { 0xDE, 0xAD, 0xBE };
        var buffer = new byte[garbage.Length + request.Length];
        garbage.CopyTo(buffer, 0);
        request.CopyTo(buffer, garbage.Length);

        Assert.Empty(lens.Feed(Chunk(0, 0, StreamDirection.Rx, buffer)));

        var frames = lens.FlushPending(DateTime.MaxValue);

        Assert.Equal(2, frames.Count);
        Assert.Equal(garbage, frames[0].Bytes);
        Assert.Equal(FrameStatus.Error, frames[0].Status);
        Assert.Equal("CRC FAIL", frames[0].StatusTag);
        Assert.Equal("Unrecognized bytes", frames[0].Summary);

        Assert.Equal(request, frames[1].Bytes);
        Assert.Equal(FrameStatus.Ok, frames[1].Status);
        Assert.Equal((byte)ModbusFunction.ReadHoldingRegisters, frames[1].FunctionCode);
        Assert.Equal(FrameRole.MasterToSlave, frames[1].Role);
    }

    // ── 6. Monitor role inference ──

    [Fact]
    public void Monitor_RequestThenResponse_BothRx_InfersRolesAndDeltaMs()
    {
        var lens = new ModbusRtuLens { Perspective = LensPerspective.Monitor };

        var first = lens.Feed(Chunk(0, 0, StreamDirection.Rx, Request()));
        var second = lens.Feed(Chunk(1, 5, StreamDirection.Rx, Response()));

        var req = Assert.Single(first);
        var resp = Assert.Single(second);

        Assert.Equal(FrameRole.MasterToSlave, req.Role);
        Assert.Null(req.DeltaMs);
        Assert.Equal(FrameRole.SlaveToMaster, resp.Role);
        Assert.NotNull(resp.DeltaMs);
        Assert.Equal(5.0, resp.DeltaMs!.Value);
    }

    // ── 7. Exception response ──

    [Fact]
    public void ExceptionResponse_ParsesAsWarning_WithExcTagAndCode()
    {
        var lens = new ModbusRtuLens { Perspective = LensPerspective.Master };

        var frames = lens.Feed(Chunk(0, 0, StreamDirection.Rx, ExceptionFrame()));

        var frame = Assert.Single(frames);
        Assert.Equal(FrameStatus.Warning, frame.Status);
        Assert.Equal("EXC 02", frame.StatusTag);
        Assert.Equal((byte)2, frame.ExceptionCode);
        Assert.Equal((byte)ModbusFunction.ReadHoldingRegisters, frame.FunctionCode); // flag stripped
        Assert.Equal("FC83", frame.FunctionToken);
        Assert.Equal((byte)1, frame.UnitId);
        Assert.True(frame.IsAbnormal);
    }

    // ── 8. Field map for an FC03 response ──

    [Fact]
    public void Fc03ResponseFrame_Fields_HaveCorrectOffsetsKindsAndValues()
    {
        var lens = new ModbusRtuLens(); // Monitor: parses as response
        var response = Response(); // 01 03 04 00 0A 00 14 DA 3E

        var frame = Assert.Single(lens.Feed(Chunk(0, 0, StreamDirection.Rx, response)));

        Assert.Equal(5, frame.Fields.Count);
        Assert.Equal(new FrameField(0, 1, FieldKind.Address, "Slave address", "0x01"), frame.Fields[0]);
        Assert.Equal(new FrameField(1, 1, FieldKind.Function, "Function", "0x03 · Read holding"), frame.Fields[1]);
        Assert.Equal(new FrameField(2, 1, FieldKind.Length, "Byte count", "0x04 · 4"), frame.Fields[2]);
        Assert.Equal(new FrameField(3, 4, FieldKind.Data, "Data", "2 registers"), frame.Fields[3]);
        Assert.Equal(new FrameField(7, 2, FieldKind.Checksum, "CRC-16", "DA 3E ✓"), frame.Fields[4]);
    }

    // ── Silence window computation ──

    [Theory]
    [InlineData(0.001, 8.0)]  // 3.5T = 3.5 ms, below the 8 ms floor
    [InlineData(0.004, 14.0)] // 3.5T = 14 ms, above the floor
    [InlineData(0.010, 35.0)] // 3.5T = 35 ms
    public void SilenceMs_IsMaxOf35TAndFloor(double charTimeSeconds, double expectedMs)
    {
        var lens = new ModbusRtuLens { CharTimeSecondsProvider = () => charTimeSeconds };
        Assert.Equal(expectedMs, lens.SilenceMs);
    }

    [Fact]
    public void SilenceMs_DefaultsToFloor_WhenNoCharTimeProvider()
    {
        var lens = new ModbusRtuLens();
        Assert.Equal(8.0, lens.SilenceMs);
    }

    // ── Variable-length request framing ──

    [Fact]
    public void WriteMultipleRegistersRequest_VariableLength_ExtractsEagerly()
    {
        var lens = new ModbusRtuLens(); // Monitor
        var request = ModbusCodec.WrapRtu(2, ModbusCodec.BuildWriteMultipleRegistersPdu(
            10, stackalloc ushort[] { 1, 2, 3 })); // 15-byte ADU, length from byte-count field

        var frame = Assert.Single(lens.Feed(Chunk(0, 0, StreamDirection.Rx, request)));

        Assert.Equal(15, frame.Bytes.Length);
        Assert.Equal(FrameStatus.Ok, frame.Status);
        Assert.Equal((byte)ModbusFunction.WriteMultipleRegisters, frame.FunctionCode);
        Assert.Equal((byte)2, frame.UnitId);
        Assert.Equal(FrameRole.MasterToSlave, frame.Role);
    }

    // ── Perspective gating ──

    [Fact]
    public void Master_TxResponseShapedBytes_NotParsed_FlushedAsUnsupportedGenericFrame()
    {
        // A master never transmits responses, so a response-shaped TX frame is
        // not interpreted as one; it drains as a CRC-valid but unparsed frame.
        var lens = new ModbusRtuLens { Perspective = LensPerspective.Master };

        Assert.Empty(lens.Feed(Chunk(0, 0, StreamDirection.Tx, Response())));

        var frames = lens.FlushPending(DateTime.MaxValue);
        var frame = Assert.Single(frames);
        Assert.Equal(FrameStatus.Ok, frame.Status);
        Assert.Equal("OK", frame.StatusTag);
        Assert.Null(frame.FunctionCode);
        Assert.Contains("unsupported", frame.Summary);
    }

    // ── Reset ──

    [Fact]
    public void Reset_DiscardsPendingBytes()
    {
        var lens = new ModbusRtuLens { Perspective = LensPerspective.Master };
        Assert.Empty(lens.Feed(Chunk(0, 0, StreamDirection.Rx, Response()[..5])));

        lens.Reset();

        Assert.Empty(lens.FlushPending(DateTime.MaxValue));
    }

    // ── FramingBuffer ──

    [Fact]
    public void FramingBuffer_Append_TracksTimestampsAndClientId()
    {
        var buffer = new FramingBuffer();

        buffer.Append(Chunk(0, 0, StreamDirection.Rx, new byte[] { 1, 2, 3 }, clientId: 7));
        buffer.Append(Chunk(1, 2, StreamDirection.Rx, new byte[] { 4, 5 }, clientId: 9));

        Assert.Equal(5, buffer.Length);
        Assert.Equal(At(0), buffer.FirstUtc);
        Assert.Equal(At(2), buffer.LastUtc);
        Assert.Equal(7, buffer.ClientId); // first chunk wins while bytes are pending
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, buffer.Span.ToArray());
    }

    [Fact]
    public void FramingBuffer_Take_RemovesHead_ShiftsRemainder_AndReassignsFirstUtc()
    {
        var buffer = new FramingBuffer();
        buffer.Append(Chunk(0, 0, StreamDirection.Rx, new byte[] { 1, 2, 3, 4, 5 }));
        buffer.Append(Chunk(1, 3, StreamDirection.Rx, new byte[] { 6, 7 }));

        var taken = buffer.Take(3);

        Assert.Equal(new byte[] { 1, 2, 3 }, taken);
        Assert.Equal(4, buffer.Length);
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, buffer.Span.ToArray());
        Assert.Equal(At(3), buffer.FirstUtc); // follow-on frame start approximated by last append
    }

    [Fact]
    public void FramingBuffer_TakeAll_ReturnsEverything_AndEmptiesBuffer()
    {
        var buffer = new FramingBuffer();
        buffer.Append(Chunk(0, 0, StreamDirection.Rx, new byte[] { 9, 8, 7 }));

        var taken = buffer.TakeAll();

        Assert.Equal(new byte[] { 9, 8, 7 }, taken);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void FramingBuffer_AppendAfterEmptied_ResetsFirstUtcAndClientId()
    {
        var buffer = new FramingBuffer();
        buffer.Append(Chunk(0, 0, StreamDirection.Rx, new byte[] { 1, 2, 3, 4 }, clientId: 1));
        buffer.TakeAll();

        buffer.Append(Chunk(1, 20, StreamDirection.Rx, new byte[] { 5 }, clientId: 2));

        Assert.Equal(At(20), buffer.FirstUtc);
        Assert.Equal(At(20), buffer.LastUtc);
        Assert.Equal(2, buffer.ClientId);
        Assert.Equal(1, buffer.Length);
    }

    [Fact]
    public void FramingBuffer_Clear_DropsPendingBytes()
    {
        var buffer = new FramingBuffer();
        buffer.Append(Chunk(0, 0, StreamDirection.Rx, new byte[] { 1, 2, 3 }));

        buffer.Clear();

        Assert.Equal(0, buffer.Length);
        Assert.True(buffer.Span.IsEmpty);
    }

    [Fact]
    public void FramingBuffer_GrowsBeyondInitialCapacity()
    {
        var buffer = new FramingBuffer();
        var big = new byte[1200]; // > the 512-byte initial capacity
        for (int i = 0; i < big.Length; i++) big[i] = (byte)(i % 251);

        buffer.Append(Chunk(0, 0, StreamDirection.Rx, big[..500]));
        buffer.Append(Chunk(1, 1, StreamDirection.Rx, big[500..]));

        Assert.Equal(1200, buffer.Length);
        Assert.Equal(big, buffer.Span.ToArray());
        Assert.Equal(big, buffer.TakeAll());
    }
}
