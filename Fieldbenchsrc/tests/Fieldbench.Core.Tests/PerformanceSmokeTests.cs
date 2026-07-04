using System.Diagnostics;
using Fieldbench.Core.Lenses;
using Fieldbench.Core.Modbus;
using Fieldbench.Core.Streams;

namespace Fieldbench.Core.Tests;

/// <summary>
/// PRD §7 smoke checks scaled for CI: sustained ingest through the full
/// lens pipeline with zero frame loss, and bounded memory for a 100k-frame
/// resident timeline.
/// </summary>
public class PerformanceSmokeTests
{
    [Fact]
    public void RtuLens_Sustains_HighRate_Ingest_WithoutLoss()
    {
        // ~115200 baud saturation is ≈ 720 request/response pairs per second.
        // Push 60k frames (30k pairs) through the lens and require both zero
        // loss and throughput far above the wire rate.
        var lens = new ModbusRtuLens { Perspective = LensPerspective.Master };
        var request = ModbusCodec.WrapRtu(1, ModbusCodec.BuildReadRequestPdu(ModbusFunction.ReadHoldingRegisters, 0, 10));
        var response = ModbusCodec.WrapRtu(1, ModbusCodec.BuildReadResponsePdu(ModbusFunction.ReadHoldingRegisters, new byte[20]));

        const int pairs = 30_000;
        long frames = 0;
        var t0 = new DateTime(2026, 7, 4, 8, 0, 0, DateTimeKind.Utc);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < pairs; i++)
        {
            var ts = t0.AddMilliseconds(i * 2);
            frames += lens.Feed(new StreamChunk(i * 2, ts, StreamDirection.Tx, request)).Count;
            frames += lens.Feed(new StreamChunk(i * 2 + 1, ts.AddMilliseconds(1), StreamDirection.Rx, response)).Count;
        }

        sw.Stop();
        Assert.Equal(pairs * 2, frames);
        double framesPerSecond = frames / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        Assert.True(framesPerSecond > 20_000, $"lens throughput {framesPerSecond:0} fps — too slow for 115200-baud saturation");
    }

    [Fact]
    public void HundredThousandResidentFrames_StayUnderMemoryBudget()
    {
        long before = GC.GetTotalMemory(forceFullCollection: true);

        var lens = new ModbusRtuLens { Perspective = LensPerspective.Master };
        var response = ModbusCodec.WrapRtu(1, ModbusCodec.BuildReadResponsePdu(ModbusFunction.ReadHoldingRegisters, new byte[20]));
        var request = ModbusCodec.WrapRtu(1, ModbusCodec.BuildReadRequestPdu(ModbusFunction.ReadHoldingRegisters, 0, 10));
        var frames = new List<Frame>(100_000);
        var t0 = new DateTime(2026, 7, 4, 8, 0, 0, DateTimeKind.Utc);
        for (int i = 0; frames.Count < 100_000; i++)
        {
            var ts = t0.AddMilliseconds(i * 2);
            frames.AddRange(lens.Feed(new StreamChunk(i * 2, ts, StreamDirection.Tx, request)));
            frames.AddRange(lens.Feed(new StreamChunk(i * 2 + 1, ts.AddMilliseconds(1), StreamDirection.Rx, response)));
        }

        long after = GC.GetTotalMemory(forceFullCollection: true);
        double mb = (after - before) / (1024.0 * 1024.0);

        // PRD budget: < 300 MB typical working set. The frame model itself must
        // stay well under that so UI overhead has room; 200 MB is the red line here.
        Assert.True(mb < 200, $"100k resident frames took {mb:0} MB");
        GC.KeepAlive(frames);
    }
}
