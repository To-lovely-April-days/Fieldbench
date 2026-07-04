using System.Buffers.Binary;
using Fieldbench.Core.Lenses;
using Fieldbench.Core.Modbus;
using Fieldbench.Core.Streams;

namespace Fieldbench.Core.Tests;

/// <summary>Shared builders for lens/detector tests. All timestamps are explicit — no sleeps.</summary>
internal static class LensTestData
{
    public static readonly DateTime T0 = new(2026, 7, 4, 10, 0, 0, DateTimeKind.Utc);

    private static long _seq;

    public static StreamChunk Chunk(StreamDirection dir, DateTime ts, byte[] data, int clientId = 0) =>
        new(Interlocked.Increment(ref _seq), ts, dir, data, clientId);

    public static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        int offset = 0;
        foreach (var p in parts)
        {
            p.CopyTo(result, offset);
            offset += p.Length;
        }

        return result;
    }

    /// <summary>Wrap a PDU in an MBAP header: [txId 2][proto 2][len 2][unit 1][pdu].</summary>
    public static byte[] Mbap(ushort txId, byte unit, byte[] pdu)
    {
        var adu = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(adu, txId);
        // proto id bytes [2..4] stay 0.
        BinaryPrimitives.WriteUInt16BigEndian(adu.AsSpan(4), (ushort)(1 + pdu.Length));
        adu[6] = unit;
        pdu.CopyTo(adu.AsSpan(7));
        return adu;
    }

    public static byte[] CorruptLastByte(byte[] frame)
    {
        var copy = (byte[])frame.Clone();
        copy[^1] ^= 0xFF;
        return copy;
    }

    // ── A realistic RTU conversation (unit 1) with one corrupted response ──

    /// <summary>01 03 00 00 00 02 + CRC — read holding 0..1, request.</summary>
    public static byte[] RtuReadReq() =>
        ModbusCodec.WrapRtu(1, ModbusCodec.BuildReadRequestPdu(ModbusFunction.ReadHoldingRegisters, 0, 2));

    /// <summary>01 03 04 00 0A 00 0B + CRC — read holding response, registers 10/11.</summary>
    public static byte[] RtuReadResp() =>
        ModbusCodec.WrapRtu(1, ModbusCodec.BuildReadResponsePdu(ModbusFunction.ReadHoldingRegisters, new byte[] { 0x00, 0x0A, 0x00, 0x0B }));

    /// <summary>01 03 00 0A 00 01 + CRC — read holding 10, request.</summary>
    public static byte[] RtuReadReq2() =>
        ModbusCodec.WrapRtu(1, ModbusCodec.BuildReadRequestPdu(ModbusFunction.ReadHoldingRegisters, 10, 1));

    /// <summary>01 83 02 + CRC — exception "Illegal data address".</summary>
    public static byte[] RtuException() =>
        ModbusCodec.WrapRtu(1, ModbusCodec.BuildExceptionPdu(ModbusFunction.ReadHoldingRegisters, 0x02));

    /// <summary>01 06 00 05 00 2A + CRC — write single register 5 = 42, request.</summary>
    public static byte[] RtuWriteReq() =>
        ModbusCodec.WrapRtu(1, ModbusCodec.BuildWriteSingleRegisterPdu(5, 42));

    /// <summary>Write echo response with the last CRC byte flipped — a corrupted frame.</summary>
    public static byte[] RtuCorruptEcho() => CorruptLastByte(RtuWriteReq());

    /// <summary>01 03 04 00 63 00 64 + CRC — read holding response, registers 99/100.</summary>
    public static byte[] RtuReadResp2() =>
        ModbusCodec.WrapRtu(1, ModbusCodec.BuildReadResponsePdu(ModbusFunction.ReadHoldingRegisters, new byte[] { 0x00, 0x63, 0x00, 0x64 }));

    /// <summary>
    /// The canonical conversation: requests + responses + one corrupted frame,
    /// with explicit timestamps (offsets in ms from T0). Master TX, slave RX.
    /// </summary>
    public static IReadOnlyList<(double AtMs, StreamDirection Dir, byte[] Data)> RtuConversation() =>
    [
        (0, StreamDirection.Tx, RtuReadReq()),
        (5, StreamDirection.Rx, RtuReadResp()),
        (100, StreamDirection.Tx, RtuReadReq2()),
        (105, StreamDirection.Rx, RtuException()),
        (200, StreamDirection.Tx, RtuWriteReq()),
        (205, StreamDirection.Rx, RtuCorruptEcho()),
        (300, StreamDirection.Tx, RtuReadReq()),
        (305, StreamDirection.Rx, RtuReadResp2()),
    ];

    /// <summary>Frame payloads in the order a ModbusRtuLens emits them (the corrupted RX frame only drains when the next RX chunk reveals the silence gap, so it lands after the TX request at 300 ms).</summary>
    public static byte[][] RtuConversationExpectedFrameBytes() =>
    [
        RtuReadReq(), RtuReadResp(), RtuReadReq2(), RtuException(),
        RtuWriteReq(), RtuReadReq(), RtuCorruptEcho(), RtuReadResp2(),
    ];

    public static FrameStatus[] RtuConversationExpectedStatuses() =>
    [
        FrameStatus.Ok, FrameStatus.Ok, FrameStatus.Ok, FrameStatus.Warning,
        FrameStatus.Ok, FrameStatus.Ok, FrameStatus.Error, FrameStatus.Ok,
    ];
}

// ═══════════════════════════ ModbusTcpLens ═══════════════════════════

public class ModbusTcpLensTests
{
    private static readonly DateTime T0 = LensTestData.T0;

    private static byte[] ReadReqPdu() => ModbusCodec.BuildReadRequestPdu(ModbusFunction.ReadHoldingRegisters, 0, 2);

    [Fact]
    public void Feed_SingleRequestAdu_ProducesOneOkRequestFrame()
    {
        var lens = new ModbusTcpLens();
        var adu = LensTestData.Mbap(1, 1, ReadReqPdu());

        var frames = lens.Feed(LensTestData.Chunk(StreamDirection.Tx, T0, adu));

        var frame = Assert.Single(frames);
        Assert.Equal(FrameStatus.Ok, frame.Status);
        Assert.Equal(adu, frame.Bytes);
        Assert.Equal((byte)ModbusFunction.ReadHoldingRegisters, frame.FunctionCode);
        Assert.Equal((byte)1, frame.UnitId);
        Assert.Equal("U01", frame.AddressToken);
        Assert.Equal("FC03", frame.FunctionToken);
        Assert.Equal("Read holding 0–1 · request", frame.Summary);
        Assert.Null(frame.StatusTag);
    }

    [Fact]
    public void Feed_TransactionIdAndMbapFields_AreExposedOnTheFrame()
    {
        var lens = new ModbusTcpLens();
        var adu = LensTestData.Mbap(0x1234, 0x11, ReadReqPdu());

        var frame = Assert.Single(lens.Feed(LensTestData.Chunk(StreamDirection.Tx, T0, adu)));

        Assert.Equal("Transaction", frame.Fields[0].Name);
        Assert.Equal("0x1234", frame.Fields[0].Value);
        Assert.Equal(0, frame.Fields[0].Offset);
        Assert.Equal(2, frame.Fields[0].Count);
        Assert.Equal("Protocol", frame.Fields[1].Name);
        Assert.Equal("0x0000", frame.Fields[1].Value);
        Assert.Equal("Length", frame.Fields[2].Name);
        Assert.Equal("6", frame.Fields[2].Value);
        Assert.Equal("Unit ID", frame.Fields[3].Name);
        Assert.Equal("0x11", frame.Fields[3].Value);
    }

    [Fact]
    public void Feed_TwoConcatenatedAdusInOneChunk_ProducesTwoFramesWithTheirOwnTransactionIds()
    {
        var lens = new ModbusTcpLens();
        var adu1 = LensTestData.Mbap(1, 1, ReadReqPdu());
        var adu2 = LensTestData.Mbap(2, 1, ReadReqPdu());

        var frames = lens.Feed(LensTestData.Chunk(StreamDirection.Tx, T0, LensTestData.Concat(adu1, adu2)));

        Assert.Equal(2, frames.Count);
        Assert.Equal(adu1, frames[0].Bytes);
        Assert.Equal(adu2, frames[1].Bytes);
        Assert.Equal("0x0001", frames[0].Fields[0].Value);
        Assert.Equal("0x0002", frames[1].Fields[0].Value);
        Assert.All(frames, f => Assert.Equal(FrameStatus.Ok, f.Status));
    }

    [Theory]
    [InlineData(3)]  // split inside the MBAP header
    [InlineData(8)]  // split inside the PDU
    [InlineData(11)] // split just before the last byte
    public void Feed_AduSplitAcrossChunks_EmitsOnlyWhenComplete(int splitAt)
    {
        var lens = new ModbusTcpLens();
        var adu = LensTestData.Mbap(7, 1, ReadReqPdu());

        var first = lens.Feed(LensTestData.Chunk(StreamDirection.Tx, T0, adu[..splitAt]));
        Assert.Empty(first);

        var second = lens.Feed(LensTestData.Chunk(StreamDirection.Tx, T0.AddMilliseconds(2), adu[splitAt..]));
        var frame = Assert.Single(second);
        Assert.Equal(adu, frame.Bytes);
        Assert.Equal(FrameStatus.Ok, frame.Status);
        // First-byte timestamp is preserved across the chunk boundary.
        Assert.Equal(T0, frame.TimestampUtc);
    }

    [Fact]
    public void Feed_GarbagePrefix_ResyncsAndEmitsErrorFrameThenAdu()
    {
        var lens = new ModbusTcpLens();
        var garbage = new byte[] { 0xDE, 0xAD, 0xBE };
        var adu = LensTestData.Mbap(1, 1, ReadReqPdu());

        var frames = lens.Feed(LensTestData.Chunk(StreamDirection.Tx, T0, LensTestData.Concat(garbage, adu)));

        Assert.Equal(2, frames.Count);
        Assert.Equal(FrameStatus.Error, frames[0].Status);
        Assert.Equal(garbage, frames[0].Bytes);
        Assert.Equal("Not a Modbus TCP header", frames[0].Summary);
        Assert.Equal("BAD", frames[0].StatusTag);
        Assert.Equal(FrameStatus.Ok, frames[1].Status);
        Assert.Equal(adu, frames[1].Bytes);
    }

    [Fact]
    public void Feed_ExceptionResponse_IsWarningWithExceptionTag()
    {
        var lens = new ModbusTcpLens();
        var adu = LensTestData.Mbap(5, 1, ModbusCodec.BuildExceptionPdu(ModbusFunction.ReadHoldingRegisters, 0x02));

        var frame = Assert.Single(lens.Feed(LensTestData.Chunk(StreamDirection.Rx, T0, adu)));

        Assert.Equal(FrameStatus.Warning, frame.Status);
        Assert.True(frame.IsAbnormal);
        Assert.Equal("EXC 02", frame.StatusTag);
        Assert.Equal((byte)0x02, frame.ExceptionCode);
        Assert.Equal((byte)ModbusFunction.ReadHoldingRegisters, frame.FunctionCode);
        Assert.Equal("FC83", frame.FunctionToken);
        Assert.Equal("Illegal data address", frame.Summary);
    }

    [Fact]
    public void Feed_ResponseAdu_ParsesAsResponse()
    {
        var lens = new ModbusTcpLens();
        var adu = LensTestData.Mbap(9, 1, ModbusCodec.BuildReadResponsePdu(
            ModbusFunction.ReadHoldingRegisters, new byte[] { 0x00, 0x01, 0x00, 0x02 }));

        var frame = Assert.Single(lens.Feed(LensTestData.Chunk(StreamDirection.Rx, T0, adu)));

        Assert.Equal(FrameStatus.Ok, frame.Status);
        Assert.Equal("OK", frame.StatusTag);
        Assert.Equal("4 bytes · response", frame.Summary);
        Assert.Equal((byte)1, frame.UnitId);
    }

    [Fact]
    public void FlushPending_DrainsPartialAduOnlyAtStreamEnd()
    {
        var lens = new ModbusTcpLens();
        var adu = LensTestData.Mbap(3, 1, ReadReqPdu());
        Assert.Empty(lens.Feed(LensTestData.Chunk(StreamDirection.Tx, T0, adu[..5])));

        // Ordinary tick: TCP framing is deterministic, nothing flushes.
        Assert.Empty(lens.FlushPending(T0.AddSeconds(10)));

        // Stream end: leftovers drain as an error frame.
        var flushed = lens.FlushPending(DateTime.MaxValue);
        var frame = Assert.Single(flushed);
        Assert.Equal(FrameStatus.Error, frame.Status);
        Assert.Equal(adu[..5], frame.Bytes);
    }
}

// ═══════════════════════════ RawLens ═══════════════════════════

public class RawLensTests
{
    private static readonly DateTime T0 = LensTestData.T0;

    private static byte[] Ascii(string s) => System.Text.Encoding.ASCII.GetBytes(s);

    [Fact]
    public void SilenceGap_ChunksWithinGap_MergeIntoOneBlock()
    {
        var lens = new RawLens(); // SilenceGap, GapMs 20 by default

        Assert.Empty(lens.Feed(LensTestData.Chunk(StreamDirection.Rx, T0, Ascii("ABC"))));
        Assert.Empty(lens.Feed(LensTestData.Chunk(StreamDirection.Rx, T0.AddMilliseconds(10), Ascii("DEF"))));

        var frame = Assert.Single(lens.FlushPending(DateTime.MaxValue));
        Assert.Equal(Ascii("ABCDEF"), frame.Bytes);
        Assert.Equal(FrameStatus.Raw, frame.Status);
        Assert.Equal("ABCDEF", frame.Summary);
        Assert.Equal(T0, frame.TimestampUtc);
    }

    [Fact]
    public void SilenceGap_GapBeforeNextChunk_FlushesPendingBlock()
    {
        var lens = new RawLens();
        Assert.Empty(lens.Feed(LensTestData.Chunk(StreamDirection.Rx, T0, Ascii("ABC"))));

        // 50 ms pause >= 20 ms gap: the pending block closes when the next chunk arrives.
        var frames = lens.Feed(LensTestData.Chunk(StreamDirection.Rx, T0.AddMilliseconds(50), Ascii("XYZ")));

        var frame = Assert.Single(frames);
        Assert.Equal(Ascii("ABC"), frame.Bytes);
        Assert.Equal(FrameStatus.Raw, frame.Status);
        Assert.Equal(T0, frame.TimestampUtc);
    }

    [Fact]
    public void SilenceGap_FlushPending_RespectsGapExpiry()
    {
        var lens = new RawLens();
        lens.Feed(LensTestData.Chunk(StreamDirection.Rx, T0, Ascii("PENDING")));

        // Not yet expired: 10 ms < 20 ms.
        Assert.Empty(lens.FlushPending(T0.AddMilliseconds(10)));

        // Expired: 30 ms >= 20 ms.
        var frame = Assert.Single(lens.FlushPending(T0.AddMilliseconds(30)));
        Assert.Equal(Ascii("PENDING"), frame.Bytes);
    }

    [Fact]
    public void SilenceGap_TxAndRx_UseIndependentBuffers()
    {
        var lens = new RawLens();
        lens.Feed(LensTestData.Chunk(StreamDirection.Tx, T0, Ascii("tx")));
        lens.Feed(LensTestData.Chunk(StreamDirection.Rx, T0.AddMilliseconds(1), Ascii("rx")));

        var frames = lens.FlushPending(DateTime.MaxValue);
        Assert.Equal(2, frames.Count);
        Assert.Contains(frames, f => f.Direction == StreamDirection.Tx && f.Bytes.SequenceEqual(Ascii("tx")));
        Assert.Contains(frames, f => f.Direction == StreamDirection.Rx && f.Bytes.SequenceEqual(Ascii("rx")));
    }

    [Fact]
    public void FixedLength_SplitsExactBlocksAndKeepsRemainderPending()
    {
        var lens = new RawLens { SplitMode = RawSplitMode.FixedLength, FixedLength = 4 };

        var frames = lens.Feed(LensTestData.Chunk(StreamDirection.Rx, T0, Ascii("ABCDEFGHIJ")));

        Assert.Equal(2, frames.Count);
        Assert.Equal(Ascii("ABCD"), frames[0].Bytes);
        Assert.Equal(Ascii("EFGH"), frames[1].Bytes);

        // In FixedLength mode FlushPending drains the remainder regardless of time.
        var flushed = Assert.Single(lens.FlushPending(T0));
        Assert.Equal(Ascii("IJ"), flushed.Bytes);
    }

    [Fact]
    public void FixedLength_JoinsBytesAcrossChunkBoundaries()
    {
        var lens = new RawLens { SplitMode = RawSplitMode.FixedLength, FixedLength = 4 };

        lens.Feed(LensTestData.Chunk(StreamDirection.Rx, T0, Ascii("ABCDEFGHIJ")));
        var frames = lens.Feed(LensTestData.Chunk(StreamDirection.Rx, T0.AddMilliseconds(1), Ascii("KL")));

        var frame = Assert.Single(frames);
        Assert.Equal(Ascii("IJKL"), frame.Bytes);
    }

    [Fact]
    public void Terminator_SplitsOnSingleByteTerminator()
    {
        var lens = new RawLens { SplitMode = RawSplitMode.Terminator }; // default terminator 0x0A

        var frames = lens.Feed(LensTestData.Chunk(StreamDirection.Rx, T0, Ascii("AB\nCD\nEF")));

        Assert.Equal(2, frames.Count);
        Assert.Equal(Ascii("AB\n"), frames[0].Bytes);
        Assert.Equal(Ascii("CD\n"), frames[1].Bytes);
        Assert.Equal("AB·", frames[0].Summary); // control char rendered as middle dot

        var flushed = Assert.Single(lens.FlushPending(T0));
        Assert.Equal(Ascii("EF"), flushed.Bytes);
    }

    [Fact]
    public void Terminator_MultiByteTerminator_MatchesAcrossChunkBoundary()
    {
        var lens = new RawLens { SplitMode = RawSplitMode.Terminator, Terminator = [0x0D, 0x0A] };

        Assert.Empty(lens.Feed(LensTestData.Chunk(StreamDirection.Rx, T0, Ascii("OK\r"))));
        var frames = lens.Feed(LensTestData.Chunk(StreamDirection.Rx, T0.AddMilliseconds(1), Ascii("\nGO\r\n")));

        Assert.Equal(2, frames.Count);
        Assert.Equal(Ascii("OK\r\n"), frames[0].Bytes);
        Assert.Equal(Ascii("GO\r\n"), frames[1].Bytes);
    }

    [Theory]
    [InlineData((byte)0x00, "·")]
    [InlineData((byte)0x09, "·")]
    [InlineData((byte)0x1F, "·")]
    [InlineData((byte)0x20, " ")]
    [InlineData((byte)0x41, "A")]
    [InlineData((byte)0x7E, "~")]
    [InlineData((byte)0x7F, "·")]
    [InlineData((byte)0xFF, "·")]
    public void AsciiPreview_RendersControlBytesAsMiddleDots(byte value, string expected)
    {
        Assert.Equal(expected, RawLens.AsciiPreview(new[] { value }));
    }

    [Fact]
    public void AsciiPreview_MixedPrintableAndControl()
    {
        var bytes = new byte[] { 0x48, 0x69, 0x09, 0x0A, 0x21 }; // "Hi\t\n!"
        Assert.Equal("Hi··!", RawLens.AsciiPreview(bytes));
    }

    [Fact]
    public void AsciiPreview_TruncatesAtMax()
    {
        var bytes = new byte[100];
        Array.Fill(bytes, (byte)'X');
        Assert.Equal(new string('X', 64), RawLens.AsciiPreview(bytes));
        Assert.Equal("XXX", RawLens.AsciiPreview(bytes, max: 3));
    }
}

// ═══════════════════════════ ProtocolDetector ═══════════════════════════

public class ProtocolDetectorTests
{
    private static readonly DateTime T0 = LensTestData.T0;

    [Fact]
    public void PureRtuChunks_FireAfterExactlyThreeConsecutiveValidChunks()
    {
        var detector = new ProtocolDetector();
        DetectionResult? result = null;
        int fires = 0;
        detector.Detected += r => { result = r; fires++; };

        var frame = LensTestData.RtuReadReq(); // @01 FC03, CRC valid

        detector.Feed(LensTestData.Chunk(StreamDirection.Rx, T0, frame));
        Assert.False(detector.HasFired);
        detector.Feed(LensTestData.Chunk(StreamDirection.Rx, T0.AddMilliseconds(100), frame));
        Assert.False(detector.HasFired);

        detector.Feed(LensTestData.Chunk(StreamDirection.Rx, T0.AddMilliseconds(200), frame));

        Assert.True(detector.HasFired);
        Assert.Equal(1, fires);
        Assert.NotNull(result);
        Assert.Equal("modbus-rtu", result!.LensId);
        Assert.Equal("Modbus RTU", result.DisplayName);
        Assert.Equal(3, result.PassCount);
        Assert.Equal(3, result.TotalCount);
        Assert.Equal("@01 polling FC03", result.Evidence);
    }

    [Fact]
    public void RandomByteStreams_NeverFire()
    {
        var detector = new ProtocolDetector();
        detector.Detected += _ => Assert.Fail("Detector must not fire on random data.");

        var rng = new Random(20260704); // fixed seed => deterministic block contents
        for (int i = 0; i < 200; i++)
        {
            var block = new byte[rng.Next(4, 65)];
            rng.NextBytes(block);
            detector.Feed(block);
            Assert.False(detector.HasFired);
        }

        Assert.False(detector.HasFired);
    }

    [Fact]
    public void InterruptedStreak_ResetsTheConsecutiveCount()
    {
        var detector = new ProtocolDetector();
        var valid = LensTestData.RtuReadReq();
        var invalid = LensTestData.CorruptLastByte(valid); // CRC fails, length >= 4

        detector.Feed(valid);
        detector.Feed(valid);
        Assert.False(detector.HasFired);

        detector.Feed(invalid); // breaks the streak

        detector.Feed(valid);
        detector.Feed(valid);
        Assert.False(detector.HasFired); // only 2 consecutive since the interruption

        detector.Feed(valid);
        Assert.True(detector.HasFired); // third consecutive after the reset
    }

    [Fact]
    public void MbapChunks_FireModbusTcp()
    {
        var detector = new ProtocolDetector();
        DetectionResult? result = null;
        detector.Detected += r => result = r;

        var pdu = ModbusCodec.BuildReadRequestPdu(ModbusFunction.ReadHoldingRegisters, 0, 2);
        detector.Feed(LensTestData.Mbap(1, 1, pdu));
        detector.Feed(LensTestData.Mbap(2, 1, pdu));
        Assert.False(detector.HasFired);

        detector.Feed(LensTestData.Mbap(3, 1, pdu));

        Assert.True(detector.HasFired);
        Assert.NotNull(result);
        Assert.Equal("modbus-tcp", result!.LensId);
        Assert.Equal("Modbus TCP", result.DisplayName);
        Assert.Equal("MBAP headers consistent", result.Evidence);
    }

    [Fact]
    public void FiresOnlyOnce_AndResetRearms()
    {
        var detector = new ProtocolDetector();
        int fires = 0;
        detector.Detected += _ => fires++;
        var valid = LensTestData.RtuReadReq();

        for (int i = 0; i < 6; i++) detector.Feed(valid);
        Assert.Equal(1, fires); // latched after the first detection

        detector.Reset();
        Assert.False(detector.HasFired);

        detector.Feed(valid);
        detector.Feed(valid);
        detector.Feed(valid);
        Assert.Equal(2, fires);
    }
}

// ═══════════════════════════ LensReplay — retroactive switching (W1) ═══════════════════════════

public class LensReplayTests
{
    private static readonly DateTime T0 = LensTestData.T0;

    private static ByteStreamStore BuildStore()
    {
        var store = new ByteStreamStore();
        foreach (var (atMs, dir, data) in LensTestData.RtuConversation())
        {
            store.Append(T0.AddMilliseconds(atMs), dir, data);
        }

        return store;
    }

    private static List<Frame> ParseLive(ByteStreamStore store, IProtocolLens lens)
    {
        // Feed chunk-by-chunk as they were appended (live order == store order).
        var frames = new List<Frame>();
        foreach (var chunk in store.Snapshot())
        {
            frames.AddRange(lens.Feed(chunk));
        }

        frames.AddRange(lens.FlushPending(DateTime.MaxValue));
        return frames;
    }

    [Fact]
    public void LiveRtuParse_ProducesExpectedConversationFrames()
    {
        var store = BuildStore();
        var live = ParseLive(store, new ModbusRtuLens());

        var expectedBytes = LensTestData.RtuConversationExpectedFrameBytes();
        var expectedStatuses = LensTestData.RtuConversationExpectedStatuses();

        Assert.Equal(expectedBytes.Length, live.Count);
        for (int i = 0; i < live.Count; i++)
        {
            Assert.Equal(expectedBytes[i], live[i].Bytes);
            Assert.Equal(expectedStatuses[i], live[i].Status);
        }

        // Spot checks on semantics.
        Assert.Equal("Read holding 0–1", live[0].Summary);
        Assert.Equal("EXC 02", live[3].StatusTag);
        Assert.Equal("Illegal data address", live[3].Summary);
        Assert.Equal("Write single 40006 = 42", live[4].Summary);
        Assert.Equal("CRC FAIL", live[6].StatusTag);
        Assert.Equal(LensTestData.RtuCorruptEcho(), live[6].Bytes);
    }

    [Fact]
    public void Replay_FreshRtuLensOverSnapshot_MatchesLiveParse()
    {
        var store = BuildStore();

        // Live: chunks fed one by one, in arrival order, to a lens that ran during capture.
        var liveFrames = new List<Frame>();
        var liveLens = new ModbusRtuLens();
        foreach (var chunk in store.Snapshot())
        {
            liveFrames.AddRange(liveLens.Feed(chunk));
        }

        liveFrames.AddRange(liveLens.FlushPending(DateTime.MaxValue));

        // Retroactive switch: a FRESH lens replayed over the full snapshot.
        var replayed = LensReplay.Replay(new ModbusRtuLens(), store.Snapshot());

        Assert.Equal(liveFrames.Count, replayed.Count);
        for (int i = 0; i < liveFrames.Count; i++)
        {
            Assert.Equal(liveFrames[i].Bytes, replayed[i].Bytes);
            Assert.Equal(liveFrames[i].Status, replayed[i].Status);
            Assert.Equal(liveFrames[i].Summary, replayed[i].Summary);
            Assert.Equal(liveFrames[i].StatusTag, replayed[i].StatusTag);
            Assert.Equal(liveFrames[i].TimestampUtc, replayed[i].TimestampUtc);
            Assert.Equal(liveFrames[i].Direction, replayed[i].Direction);
            Assert.Equal(liveFrames[i].FunctionCode, replayed[i].FunctionCode);
            Assert.Equal(liveFrames[i].UnitId, replayed[i].UnitId);
            Assert.Equal(liveFrames[i].ExceptionCode, replayed[i].ExceptionCode);
            Assert.Equal(liveFrames[i].Role, replayed[i].Role);
            Assert.Equal(liveFrames[i].DeltaMs, replayed[i].DeltaMs);
        }
    }

    [Fact]
    public void Replay_ViaChunkAppendedEvent_MatchesReplayOverSnapshot()
    {
        // Live capture wired the realistic way: lens fed from the store's append event.
        var store = new ByteStreamStore();
        var live = new ModbusRtuLens();
        var liveFrames = new List<Frame>();
        store.ChunkAppended += c => liveFrames.AddRange(live.Feed(c));

        foreach (var (atMs, dir, data) in LensTestData.RtuConversation())
        {
            store.Append(T0.AddMilliseconds(atMs), dir, data);
        }

        liveFrames.AddRange(live.FlushPending(DateTime.MaxValue));

        var replayed = LensReplay.Replay(new ModbusRtuLens(), store.Snapshot());

        Assert.Equal(liveFrames.Count, replayed.Count);
        for (int i = 0; i < liveFrames.Count; i++)
        {
            Assert.Equal(liveFrames[i].Bytes, replayed[i].Bytes);
            Assert.Equal(liveFrames[i].Status, replayed[i].Status);
            Assert.Equal(liveFrames[i].Summary, replayed[i].Summary);
        }
    }

    [Fact]
    public void RawToRtuSwitch_ReplayParsesTheFullHistory()
    {
        var store = BuildStore();

        // Session started on the Raw lens: everything is an uninterpreted block.
        var rawFrames = ParseLive(store, new RawLens());
        Assert.NotEmpty(rawFrames);
        Assert.All(rawFrames, f => Assert.Equal(FrameStatus.Raw, f.Status));

        // Switch to Modbus RTU: the full history re-parses into protocol frames.
        var replayed = LensReplay.Replay(new ModbusRtuLens(), store.Snapshot());

        var expectedBytes = LensTestData.RtuConversationExpectedFrameBytes();
        var expectedStatuses = LensTestData.RtuConversationExpectedStatuses();
        Assert.Equal(expectedBytes.Length, replayed.Count);
        for (int i = 0; i < replayed.Count; i++)
        {
            Assert.Equal(expectedBytes[i], replayed[i].Bytes);
            Assert.Equal(expectedStatuses[i], replayed[i].Status);
        }

        // No byte lost or invented across the switch.
        Assert.Equal(
            store.Snapshot().Sum(c => c.Data.Length),
            replayed.Sum(f => f.Bytes.Length));
    }

    [Fact]
    public void Replay_ResetsADirtyLens_BeforeReparsing()
    {
        var store = BuildStore();

        var dirty = new ModbusRtuLens();
        dirty.Feed(LensTestData.Chunk(StreamDirection.Rx, T0.AddSeconds(-5), [0xDE, 0xAD, 0xBE, 0xEF]));

        var fromDirty = LensReplay.Replay(dirty, store.Snapshot());
        var fromFresh = LensReplay.Replay(new ModbusRtuLens(), store.Snapshot());

        Assert.Equal(fromFresh.Count, fromDirty.Count);
        for (int i = 0; i < fromFresh.Count; i++)
        {
            Assert.Equal(fromFresh[i].Bytes, fromDirty[i].Bytes);
            Assert.Equal(fromFresh[i].Status, fromDirty[i].Status);
            Assert.Equal(fromFresh[i].Summary, fromDirty[i].Summary);
        }
    }

    [Fact]
    public void Replay_PairsResponsesWithRequests_UsingChunkTimestamps()
    {
        var store = BuildStore();
        var replayed = LensReplay.Replay(new ModbusRtuLens(), store.Snapshot());

        // Response at T0+5 to request at T0: 5 ms latency, derived purely from stored timestamps.
        Assert.Equal(5.0, replayed[1].DeltaMs);
        Assert.Equal(FrameRole.MasterToSlave, replayed[0].Role);
        Assert.Equal(FrameRole.SlaveToMaster, replayed[1].Role);

        // Exception at T0+105 to request at T0+100 pairs too.
        Assert.Equal(5.0, replayed[3].DeltaMs);
    }
}
