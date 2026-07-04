using Fieldbench.Core.Lenses;
using Fieldbench.Core.Master;
using Fieldbench.Core.Modbus;
using Fieldbench.Core.Sessions;
using Fieldbench.Core.Slave;
using Fieldbench.Core.Transport;

namespace Fieldbench.Core.Demo;

/// <summary>
/// One-click demo (PRD §6.11): an in-process loopback pair runs a live
/// master ↔ slave conversation with a sine-wave boiler simulation, periodic
/// wire corruption (CRC FAIL rows) and an out-of-map poll (EXC 02 rows), so
/// every headline feature — timeline, coloring, registers, chart, AI explain —
/// shows real data within seconds of install, no hardware needed.
/// </summary>
public sealed class DemoOrchestrator : IAsyncDisposable
{
    private int _frameCounter;

    private DemoOrchestrator(Connection masterConn, Connection slaveConn, ModbusMasterEngine master, ModbusSlaveEngine slave, PollScheduler scheduler)
    {
        MasterConnection = masterConn;
        SlaveConnection = slaveConn;
        Master = master;
        Slave = slave;
        Scheduler = scheduler;
    }

    public Connection MasterConnection { get; }

    public Connection SlaveConnection { get; }

    public ModbusMasterEngine Master { get; }

    public ModbusSlaveEngine Slave { get; }

    public PollScheduler Scheduler { get; }

    public static async Task<DemoOrchestrator> StartAsync()
    {
        var hub = new LoopbackHub { Latency = TimeSpan.FromMilliseconds(6) };

        var masterConfig = new ConnectionConfig { Kind = ConnectionKind.Loopback, BaudRate = 9600 };
        var slaveConfig = new ConnectionConfig { Kind = ConnectionKind.Loopback, BaudRate = 9600 };
        var masterConn = new Connection(hub.A, masterConfig, "Demo loop — master");
        var slaveConn = new Connection(hub.B, slaveConfig, "Demo loop — slave");

        await masterConn.OpenAsync().ConfigureAwait(false);
        await slaveConn.OpenAsync().ConfigureAwait(false);

        // ── slave side: the simulated boiler ──
        var slave = new ModbusSlaveEngine(slaveConn, tcpFraming: false) { UnitId = 1 };
        slave.AddTag(new SlaveTag
        {
            Tag = new RegisterTag { Address = 0, Name = "temp_pv", DataType = RegisterDataType.UInt16, Scale = 0.1, Unit = "°C" },
            Generator = new ValueGenerator { Kind = GeneratorKind.Sine, SineMin = 20, SineMax = 30, PeriodSeconds = 10 },
        });
        slave.AddTag(new SlaveTag
        {
            Tag = new RegisterTag { Address = 1, Name = "press_pv", DataType = RegisterDataType.UInt16, Scale = 0.001, Unit = "bar" },
            Generator = new ValueGenerator { Kind = GeneratorKind.RandomRange, Min = 1.010, Max = 1.016 },
        });
        slave.AddTag(new SlaveTag
        {
            Tag = new RegisterTag { Address = 2, Name = "flow_pv", DataType = RegisterDataType.UInt16, Scale = 0.1, Unit = "L/min" },
            Generator = new ValueGenerator { Kind = GeneratorKind.Increment, IncrementPerSecond = 0.4, WrapMin = 20, WrapMax = 45 },
        });
        slave.AddTag(new SlaveTag
        {
            Tag = new RegisterTag { Address = 4, Name = "temp_sp", DataType = RegisterDataType.Float32, Scale = 1, Unit = "°C", Writable = true },
        });
        slave.AddTag(new SlaveTag
        {
            Tag = new RegisterTag { Address = 8, Name = "status", DataType = RegisterDataType.UInt16 },
        });
        // The simulated device exposes the whole 0–9 block (gaps read as 0) so
        // the demo poll task succeeds; the out-of-map task below still EXCs.
        slave.Store.DefineWords(RegisterArea.HoldingRegisters, 0, 10);

        // Seed static values.
        foreach (var st in slave.Tags)
        {
            switch (st.Tag.Name)
            {
                case "temp_sp": slave.SetTagValue(st, 25.0); break;
                case "status": slave.SetTagValue(st, 3); break;
                case "press_pv": slave.SetTagValue(st, 1.013); break;
            }
        }

        // ── wire corruption: every ~29th relayed payload gets one bit flipped
        // (a realistic single-bit CRC failure for the AI to explain) ──
        var self = default(DemoOrchestrator);
        hub.WireMutator = data =>
        {
            var counter = self is null ? 0 : Interlocked.Increment(ref self._frameCounter);
            if (counter > 0 && counter % 29 == 0 && data.Length > 6)
            {
                var corrupted = (byte[])data.Clone();
                corrupted[4] ^= 0x01;
                return corrupted;
            }

            return data;
        };

        // ── master side ──
        var master = new ModbusMasterEngine(masterConn, tcpFraming: false) { DefaultUnit = 1, TimeoutMs = 500, Retries = 1 };
        foreach (var st in slave.Tags)
        {
            var t = st.Tag;
            master.Map.AddTag(new RegisterTag
            {
                Area = t.Area,
                Address = t.Address,
                Name = t.Name,
                DataType = t.DataType,
                WordOrder = t.WordOrder,
                Scale = t.Scale,
                Offset = t.Offset,
                Unit = t.Unit,
                Writable = t.Writable,
            });
        }

        var scheduler = new PollScheduler(master);
        scheduler.Add(new PollTask
        {
            Name = "Task 1",
            Unit = 1,
            Function = ModbusFunction.ReadHoldingRegisters,
            Start = 0,
            Count = 10,
            PeriodMs = 500,
            Enabled = true,
        });
        // Out-of-map poll → periodic EXC 02 rows, showing exception translation.
        scheduler.Add(new PollTask
        {
            Name = "Task 2",
            Unit = 1,
            Function = ModbusFunction.ReadInputRegisters,
            Start = 100,
            Count = 4,
            PeriodMs = 5000,
            Enabled = true,
        });

        var demo = new DemoOrchestrator(masterConn, slaveConn, master, slave, scheduler);
        self = demo;
        scheduler.Start(scheduler.Tasks[0]);
        scheduler.Start(scheduler.Tasks[1]);
        return demo;
    }

    public async ValueTask DisposeAsync()
    {
        Scheduler.Dispose();
        Master.Dispose();
        Slave.Dispose();
        await MasterConnection.DisposeAsync().ConfigureAwait(false);
        await SlaveConnection.DisposeAsync().ConfigureAwait(false);
    }
}
