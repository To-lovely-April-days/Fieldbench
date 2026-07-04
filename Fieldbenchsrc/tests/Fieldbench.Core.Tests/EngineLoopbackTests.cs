using System.Diagnostics;
using Fieldbench.Core.Master;
using Fieldbench.Core.Modbus;
using Fieldbench.Core.Sessions;
using Fieldbench.Core.Slave;
using Fieldbench.Core.Streams;
using Fieldbench.Core.Transport;

namespace Fieldbench.Core.Tests;

/// <summary>
/// End-to-end master ↔ slave over the in-process LoopbackHub (RTU framing),
/// the PRD self-loop acceptance: every supported function code round-trips,
/// exceptions come back, timeouts fire for absent units, and the higher-level
/// blocks (RegisterMap, PollScheduler, SlaveScanner) work over the same loop.
/// </summary>
public class EngineLoopbackTests
{
    // ── plumbing ──

    private sealed class Loop : IAsyncDisposable
    {
        public required LoopbackHub Hub { get; init; }
        public required Connection MasterConn { get; init; }
        public required Connection SlaveConn { get; init; }
        public required ModbusMasterEngine Master { get; init; }
        public required ModbusSlaveEngine Slave { get; init; }

        public static async Task<Loop> CreateAsync(int timeoutMs = 2000)
        {
            var hub = new LoopbackHub { Latency = TimeSpan.Zero };
            var masterConn = new Connection(hub.A, new ConnectionConfig { Kind = ConnectionKind.Loopback }, "master");
            var slaveConn = new Connection(hub.B, new ConnectionConfig { Kind = ConnectionKind.Loopback }, "slave");
            await masterConn.OpenAsync();
            await slaveConn.OpenAsync();
            var master = new ModbusMasterEngine(masterConn, tcpFraming: false)
            {
                TimeoutMs = timeoutMs,
                Retries = 1,
                DefaultUnit = 1,
            };
            var slave = new ModbusSlaveEngine(slaveConn, tcpFraming: false) { UnitId = 1 };
            return new Loop { Hub = hub, MasterConn = masterConn, SlaveConn = slaveConn, Master = master, Slave = slave };
        }

        public async ValueTask DisposeAsync()
        {
            Master.Dispose();
            Slave.Dispose();
            await MasterConn.DisposeAsync();
            await SlaveConn.DisposeAsync();
        }
    }

    /// <summary>Poll a condition instead of sleeping a fixed interval; returns true once it holds.</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 4000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }

        return condition();
    }

    /// <summary>Big-endian response payload → register words.</summary>
    private static ushort[] Words(byte[] data)
    {
        var words = new ushort[data.Length / 2];
        for (int i = 0; i < words.Length; i++) words[i] = (ushort)((data[i * 2] << 8) | data[i * 2 + 1]);
        return words;
    }

    /// <summary>Function-code bytes of every request the master transmitted (ADU byte 1 in RTU).</summary>
    private static byte[] SentFunctionCodes(Loop loop) =>
        loop.MasterConn.Store.Snapshot()
            .Where(c => c.Direction == StreamDirection.Tx && c.Data.Length >= 2)
            .Select(c => c.Data[1])
            .ToArray();

    // ── FC03 / FC04: word reads ──

    [Fact]
    public async Task Fc03_ReadHoldingRegisters_RoundTripsSeededValues()
    {
        await using var loop = await Loop.CreateAsync();
        loop.Slave.Store.DefineWords(RegisterArea.HoldingRegisters, 0, 5);
        loop.Slave.Store.WriteWords(RegisterArea.HoldingRegisters, 0, new ushort[] { 100, 200, 300, 40000, 5 });

        var result = await loop.Master.ReadAsync(RegisterArea.HoldingRegisters, 0, 5);

        Assert.True(result.Success);
        Assert.False(result.TimedOut);
        Assert.Null(result.ExceptionCode);
        Assert.Equal(1, result.Attempts);
        Assert.NotNull(result.Response);
        Assert.Equal(ModbusMessageKind.Response, result.Response!.Kind);
        Assert.Equal(ModbusFunction.ReadHoldingRegisters, result.Response.Function);
        Assert.Equal(new ushort[] { 100, 200, 300, 40000, 5 }, Words(result.Response.Data));
    }

    [Fact]
    public async Task Fc04_ReadInputRegisters_RoundTripsSeededValues()
    {
        await using var loop = await Loop.CreateAsync();
        loop.Slave.Store.DefineWords(RegisterArea.InputRegisters, 10, 3);
        loop.Slave.Store.WriteWords(RegisterArea.InputRegisters, 10, new ushort[] { 1, 65535, 4242 });

        var result = await loop.Master.ReadAsync(RegisterArea.InputRegisters, 10, 3);

        Assert.True(result.Success);
        Assert.Equal(ModbusFunction.ReadInputRegisters, result.Response!.Function);
        Assert.Equal(new ushort[] { 1, 65535, 4242 }, Words(result.Response.Data));
    }

    // ── FC01 / FC02: bit reads ──

    [Fact]
    public async Task Fc01_ReadCoils_RoundTripsSeededPattern()
    {
        await using var loop = await Loop.CreateAsync();
        var pattern = new[] { true, false, true, true, false, false, true, false }; // LSB-first → 0x4D
        loop.Slave.Store.DefineBits(RegisterArea.Coils, 0, 8);
        loop.Slave.Store.WriteBits(RegisterArea.Coils, 0, pattern);

        var result = await loop.Master.ReadAsync(RegisterArea.Coils, 0, 8);

        Assert.True(result.Success);
        Assert.Equal(ModbusFunction.ReadCoils, result.Response!.Function);
        Assert.Single(result.Response.Data);
        Assert.Equal(0x4D, result.Response.Data[0]);
    }

    [Fact]
    public async Task Fc02_ReadDiscreteInputs_RoundTripsSeededPattern()
    {
        await using var loop = await Loop.CreateAsync();
        var pattern = new[] { true, true, false, false, true, false, false, true }; // LSB-first → 0x93
        loop.Slave.Store.DefineBits(RegisterArea.DiscreteInputs, 4, 8);
        loop.Slave.Store.WriteBits(RegisterArea.DiscreteInputs, 4, pattern);

        var result = await loop.Master.ReadAsync(RegisterArea.DiscreteInputs, 4, 8);

        Assert.True(result.Success);
        Assert.Equal(ModbusFunction.ReadDiscreteInputs, result.Response!.Function);
        Assert.Equal(0x93, result.Response.Data[0]);
    }

    // ── FC05 / FC06: single writes ──

    [Fact]
    public async Task Fc05_WriteSingleCoil_EchoesAndUpdatesSlaveStore()
    {
        await using var loop = await Loop.CreateAsync();
        loop.Slave.Store.DefineBits(RegisterArea.Coils, 3, 1);

        var on = await loop.Master.ExecuteAsync(1, ModbusCodec.BuildWriteSingleCoilPdu(3, true));
        Assert.True(on.Success);
        Assert.Equal((ushort)3, on.Response!.Address);
        Assert.Equal((ushort)0xFF00, on.Response.Quantity);
        Assert.True(loop.Slave.Store.ReadBits(RegisterArea.Coils, 3, 1)[0]);

        var readBack = await loop.Master.ReadAsync(RegisterArea.Coils, 3, 1);
        Assert.True(readBack.Success);
        Assert.Equal(0x01, readBack.Response!.Data[0]);

        var off = await loop.Master.ExecuteAsync(1, ModbusCodec.BuildWriteSingleCoilPdu(3, false));
        Assert.True(off.Success);
        Assert.False(loop.Slave.Store.ReadBits(RegisterArea.Coils, 3, 1)[0]);
    }

    [Fact]
    public async Task Fc06_WriteSingleRegister_EchoesAndUpdatesSlaveStore()
    {
        await using var loop = await Loop.CreateAsync();
        loop.Slave.Store.DefineWords(RegisterArea.HoldingRegisters, 10, 1);

        var result = await loop.Master.ExecuteAsync(1, ModbusCodec.BuildWriteSingleRegisterPdu(10, 1234));

        Assert.True(result.Success);
        Assert.Equal((ushort)10, result.Response!.Address);
        Assert.Equal((ushort)1234, result.Response.Quantity);
        Assert.Equal(new ushort[] { 1234 }, loop.Slave.Store.ReadWords(RegisterArea.HoldingRegisters, 10, 1));

        var readBack = await loop.Master.ReadAsync(RegisterArea.HoldingRegisters, 10, 1);
        Assert.True(readBack.Success);
        Assert.Equal(new ushort[] { 1234 }, Words(readBack.Response!.Data));
    }

    // ── FC0F / FC10: multiple writes ──

    [Fact]
    public async Task Fc0F_WriteMultipleCoils_UpdatesSlaveStoreAndReadsBack()
    {
        await using var loop = await Loop.CreateAsync();
        loop.Slave.Store.DefineBits(RegisterArea.Coils, 0, 10);
        var values = new[] { true, false, true, false, false, true, false, false, false, true };

        var result = await loop.Master.ExecuteAsync(1, ModbusCodec.BuildWriteMultipleCoilsPdu(0, values));

        Assert.True(result.Success);
        Assert.Equal((ushort)0, result.Response!.Address);
        Assert.Equal((ushort)10, result.Response.Quantity);
        Assert.Equal(values, loop.Slave.Store.ReadBits(RegisterArea.Coils, 0, 10));

        var readBack = await loop.Master.ReadAsync(RegisterArea.Coils, 0, 10);
        Assert.True(readBack.Success);
        Assert.Equal(2, readBack.Response!.Data.Length);
        Assert.Equal(0x25, readBack.Response.Data[0]); // bits 0,2,5
        Assert.Equal(0x02, readBack.Response.Data[1]); // bit 9
    }

    [Fact]
    public async Task Fc10_WriteMultipleRegisters_UpdatesSlaveStoreAndReadsBack()
    {
        await using var loop = await Loop.CreateAsync();
        loop.Slave.Store.DefineWords(RegisterArea.HoldingRegisters, 20, 4);
        var values = new ushort[] { 1, 2, 65535, 42 };

        var result = await loop.Master.ExecuteAsync(1, ModbusCodec.BuildWriteMultipleRegistersPdu(20, values));

        Assert.True(result.Success);
        Assert.Equal((ushort)20, result.Response!.Address);
        Assert.Equal((ushort)4, result.Response.Quantity);
        Assert.Equal(values, loop.Slave.Store.ReadWords(RegisterArea.HoldingRegisters, 20, 4));

        var readBack = await loop.Master.ReadAsync(RegisterArea.HoldingRegisters, 20, 4);
        Assert.True(readBack.Success);
        Assert.Equal(values, Words(readBack.Response!.Data));
    }

    // ── exception paths ──

    [Fact]
    public async Task Read_UndefinedAddress_ReturnsIllegalDataAddressException()
    {
        await using var loop = await Loop.CreateAsync();
        // Nothing defined at 500 → EXC 02.
        var result = await loop.Master.ReadAsync(RegisterArea.HoldingRegisters, 500, 1);

        Assert.False(result.Success);
        Assert.False(result.TimedOut);
        Assert.Equal((byte)0x02, result.ExceptionCode);
        Assert.NotNull(result.Response);
        Assert.Equal(ModbusMessageKind.Exception, result.Response!.Kind);
        Assert.Contains("Illegal data address", result.HumanError);
    }

    [Theory]
    [InlineData(0x03, 126)]   // > MaxReadRegisters (125)
    [InlineData(0x03, 0)]     // zero quantity
    [InlineData(0x04, 126)]   // input registers, oversized
    [InlineData(0x01, 2001)]  // > MaxReadCoils (2000)
    public async Task Read_BadQuantity_ReturnsIllegalDataValueException(int fc, int quantity)
    {
        await using var loop = await Loop.CreateAsync();
        var pdu = ModbusCodec.BuildReadRequestPdu((byte)fc, 0, (ushort)quantity);

        var result = await loop.Master.ExecuteAsync(1, pdu);

        Assert.False(result.Success);
        Assert.False(result.TimedOut);
        Assert.Equal((byte)0x03, result.ExceptionCode);
        Assert.Equal(ModbusMessageKind.Exception, result.Response!.Kind);
        Assert.Equal((byte)fc, result.Response.Function);
    }

    [Fact]
    public async Task UnsupportedFunction_RawPdu_ReturnsExceptionResponse()
    {
        await using var loop = await Loop.CreateAsync();

        // FC 0x2B (Read Device Identification) is not implemented by the slave.
        var result = await loop.Master.SendRawPduAsync(1, new byte[] { 0x2B, 0x0E, 0x01, 0x00 });

        Assert.False(result.Success);
        Assert.False(result.TimedOut);
        Assert.NotNull(result.Response);
        Assert.Equal(ModbusMessageKind.Exception, result.Response!.Kind);
        Assert.Equal((byte)0x2B, result.Response.Function);

        // Unknown function codes answer EXC 01 "Illegal function" per the spec.
        Assert.Equal((byte)0x01, result.ExceptionCode);
        Assert.True(loop.Slave.ExceptionsSent >= 1);
    }

    // ── addressing / broadcast ──

    [Fact]
    public async Task RequestToWrongUnitId_TimesOut()
    {
        await using var loop = await Loop.CreateAsync();
        loop.Slave.Store.DefineWords(RegisterArea.HoldingRegisters, 0, 1);

        var pdu = ModbusCodec.BuildReadRequestPdu(ModbusFunction.ReadHoldingRegisters, 0, 1);
        var result = await loop.Master.ExecuteAsync(9, pdu, timeoutMs: 200, retries: 1);

        Assert.False(result.Success);
        Assert.True(result.TimedOut);
        Assert.Null(result.Response);
        Assert.Null(result.ExceptionCode);
        Assert.Equal(1, result.Attempts);
        Assert.Contains("No response", result.HumanError);
    }

    [Fact]
    public async Task BroadcastWrite_ExecutesOnSlave_ButGetsNoResponse()
    {
        await using var loop = await Loop.CreateAsync();
        loop.Slave.Store.DefineWords(RegisterArea.HoldingRegisters, 30, 1);

        var result = await loop.Master.ExecuteAsync(0, ModbusCodec.BuildWriteSingleRegisterPdu(30, 777), timeoutMs: 200, retries: 1);

        // Broadcast never gets an answer per the spec — the master fires once
        // and reports success immediately instead of burning the timeout.
        Assert.True(result.Success);
        Assert.False(result.TimedOut);
        Assert.Null(result.Response);
        Assert.Equal(1, result.Attempts);

        // The slave store did take the write.
        Assert.True(await WaitUntilAsync(() =>
            loop.Slave.Store.ReadWords(RegisterArea.HoldingRegisters, 30, 1)[0] == 777));
    }

    // ── RegisterMap integration ──

    [Fact]
    public async Task ReadAsync_UpdatesMappedTag_WithScaleApplied()
    {
        await using var loop = await Loop.CreateAsync();
        loop.Slave.Store.DefineWords(RegisterArea.HoldingRegisters, 5, 1);
        loop.Slave.Store.WriteWords(RegisterArea.HoldingRegisters, 5, new ushort[] { 229 });

        var tag = new RegisterTag
        {
            Area = RegisterArea.HoldingRegisters,
            Address = 5,
            DataType = RegisterDataType.UInt16,
            Scale = 0.1,
            Name = "Temperature",
        };
        loop.Master.Map.AddTag(tag);

        var result = await loop.Master.ReadAsync(RegisterArea.HoldingRegisters, 5, 1);

        Assert.True(result.Success);
        Assert.NotNull(tag.ScaledValue);
        Assert.Equal(22.9, tag.ScaledValue!.Value, 6);
        Assert.Equal(229, tag.RawNumeric!.Value, 6);
        Assert.Equal(new ushort[] { 229 }, tag.RawWords);
        Assert.NotNull(tag.LastUpdateUtc);
        Assert.Single(tag.HistorySnapshot());
    }

    // ── WriteTagAsync FC selection ──

    [Fact]
    public async Task WriteTagAsync_SingleRegisterTag_UsesFc06()
    {
        await using var loop = await Loop.CreateAsync();
        loop.Slave.Store.DefineWords(RegisterArea.HoldingRegisters, 40, 1);
        var tag = new RegisterTag
        {
            Area = RegisterArea.HoldingRegisters,
            Address = 40,
            DataType = RegisterDataType.UInt16,
        };

        var result = await loop.Master.WriteTagAsync(tag, 1234);

        Assert.True(result.Success);
        Assert.Equal(new ushort[] { 1234 }, loop.Slave.Store.ReadWords(RegisterArea.HoldingRegisters, 40, 1));

        var sentFcs = SentFunctionCodes(loop);
        Assert.Contains(ModbusFunction.WriteSingleRegister, sentFcs);
        Assert.DoesNotContain(ModbusFunction.WriteMultipleRegisters, sentFcs);
    }

    [Fact]
    public async Task WriteTagAsync_Float32Tag_UsesFc10()
    {
        await using var loop = await Loop.CreateAsync();
        loop.Slave.Store.DefineWords(RegisterArea.HoldingRegisters, 50, 2);
        var tag = new RegisterTag
        {
            Area = RegisterArea.HoldingRegisters,
            Address = 50,
            DataType = RegisterDataType.Float32,
            WordOrder = WordOrder.ABCD,
        };

        var result = await loop.Master.WriteTagAsync(tag, 3.5);

        Assert.True(result.Success);
        var expected = RegisterValueCodec.EncodeNumeric(3.5, RegisterDataType.Float32, WordOrder.ABCD);
        Assert.Equal(expected, loop.Slave.Store.ReadWords(RegisterArea.HoldingRegisters, 50, 2));

        var sentFcs = SentFunctionCodes(loop);
        Assert.Contains(ModbusFunction.WriteMultipleRegisters, sentFcs);
        Assert.DoesNotContain(ModbusFunction.WriteSingleRegister, sentFcs);
    }

    // ── PollScheduler ──

    [Fact]
    public async Task PollScheduler_PollsPeriodically_ThenStopsCleanly()
    {
        await using var loop = await Loop.CreateAsync();
        loop.Slave.Store.DefineWords(RegisterArea.HoldingRegisters, 0, 1);
        loop.Slave.Store.WriteWords(RegisterArea.HoldingRegisters, 0, new ushort[] { 111 });

        var tag = new RegisterTag { Area = RegisterArea.HoldingRegisters, Address = 0, DataType = RegisterDataType.UInt16 };
        loop.Master.Map.AddTag(tag);

        using var scheduler = new PollScheduler(loop.Master);
        var task = scheduler.Add(new PollTask
        {
            Unit = 1,
            Function = ModbusFunction.ReadHoldingRegisters,
            Start = 0,
            Count = 1,
            PeriodMs = 50,
            Enabled = true,
        });

        // First cycle: stats recorded and the map picked up the seeded value.
        Assert.True(await WaitUntilAsync(() => task.LastCycleMs is not null && tag.ScaledValue == 111));
        Assert.True(scheduler.IsRunning(task));
        Assert.NotNull(task.LastRunUtc);
        Assert.Equal(0, task.ErrorCount);

        // The loop keeps polling: a store change shows up in the map on a later cycle.
        loop.Slave.Store.WriteWords(RegisterArea.HoldingRegisters, 0, new ushort[] { 222 });
        Assert.True(await WaitUntilAsync(() => tag.ScaledValue == 222));

        scheduler.Stop(task);
        Assert.False(scheduler.IsRunning(task));
        Assert.False(task.Enabled);

        // Let any in-flight cycle drain, then verify no further polls happen.
        await Task.Delay(120);
        var lastRun = task.LastRunUtc;
        loop.Slave.Store.WriteWords(RegisterArea.HoldingRegisters, 0, new ushort[] { 333 });
        await Task.Delay(200);
        Assert.Equal(lastRun, task.LastRunUtc);
        Assert.Equal(222, tag.ScaledValue);
    }

    // ── SlaveScanner ──

    [Fact]
    public async Task SlaveScanner_FindsExactlyUnit1_AndReportsProgress()
    {
        await using var loop = await Loop.CreateAsync();
        loop.Slave.Store.DefineWords(RegisterArea.HoldingRegisters, 0, 1, initial: 7);

        var scanner = new SlaveScanner(loop.Master)
        {
            From = 1,
            To = 3,
            ProbeFunction = ModbusFunction.ReadHoldingRegisters,
            TimeoutMs = 150,
        };
        var progress = new List<ScanProgress>();
        var found = new List<ScanHit>();
        scanner.Progress += p => progress.Add(p);
        scanner.Found += h => found.Add(h);

        var hits = await scanner.RunAsync();

        var hit = Assert.Single(hits);
        Assert.Equal((byte)1, hit.Unit);
        Assert.Null(hit.ExceptionCode); // address 0 is defined → clean OK, not "EXC alive"
        Assert.Equal("OK", hit.ResultLabel);
        Assert.True(hit.ResponseMs >= 0);

        Assert.Single(found);
        Assert.Equal((byte)1, found[0].Unit);

        Assert.Equal(3, progress.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, progress.Select(p => p.CurrentUnit).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, progress.Select(p => p.Done).ToArray());
        Assert.All(progress, p => Assert.Equal(3, p.Total));
    }
}
