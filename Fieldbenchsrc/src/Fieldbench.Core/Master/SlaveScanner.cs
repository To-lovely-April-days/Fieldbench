using Fieldbench.Core.Modbus;
using Fieldbench.Core.Transport;

namespace Fieldbench.Core.Master;

public sealed class ScanHit
{
    public required byte Unit { get; init; }

    public required double ResponseMs { get; init; }

    /// <summary>An exception response still proves the slave is alive.</summary>
    public byte? ExceptionCode { get; init; }

    public int? BaudRate { get; init; }

    public System.IO.Ports.Parity? Parity { get; init; }

    public string ResultLabel => ExceptionCode is { } c ? $"EXC {c:00} · alive" : "OK";
}

public sealed class ScanProgress
{
    public required byte CurrentUnit { get; init; }
    public required int Done { get; init; }
    public required int Total { get; init; }
    public TimeSpan Elapsed { get; init; }
    public (int Baud, System.IO.Ports.Parity Parity)? CurrentParams { get; init; }
}

/// <summary>
/// Slave address scan 1–247 with a configurable probe FC and short timeout;
/// optionally sweeps common serial parameter combinations (baud × parity).
/// The first move when a poll times out (PRD §6.4).
/// </summary>
public sealed class SlaveScanner
{
    private readonly ModbusMasterEngine _engine;

    public SlaveScanner(ModbusMasterEngine engine)
    {
        _engine = engine;
    }

    public byte From { get; set; } = 1;

    public byte To { get; set; } = 247;

    public byte ProbeFunction { get; set; } = ModbusFunction.ReadHoldingRegisters;

    public int TimeoutMs { get; set; } = 200;

    public bool SweepSerialParams { get; set; }

    public static readonly int[] SweepBauds = [9600, 19200, 38400, 57600, 115200];

    public static readonly System.IO.Ports.Parity[] SweepParities = [System.IO.Ports.Parity.None, System.IO.Ports.Parity.Even];

    public event Action<ScanHit>? Found;

    public event Action<ScanProgress>? Progress;

    public async Task<IReadOnlyList<ScanHit>> RunAsync(CancellationToken ct = default)
    {
        var hits = new List<ScanHit>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var combos = BuildCombos();
        int total = combos.Count * (To - From + 1);
        int done = 0;

        var originalConfig = _engine.Connection.Config.Clone();
        try
        {
            foreach (var combo in combos)
            {
                if (combo is { } p)
                {
                    var cfg = _engine.Connection.Config.Clone();
                    cfg.BaudRate = p.Baud;
                    cfg.Parity = p.Parity;
                    await _engine.Connection.ApplyConfigAsync(cfg).ConfigureAwait(false);
                }

                for (int unit = From; unit <= To; unit++)
                {
                    ct.ThrowIfCancellationRequested();
                    var pdu = ModbusCodec.BuildReadRequestPdu(ProbeFunction, 0, 1);
                    var result = await _engine.ExecuteAsync((byte)unit, pdu, ct, timeoutMs: TimeoutMs, retries: 1).ConfigureAwait(false);
                    done++;
                    Progress?.Invoke(new ScanProgress
                    {
                        CurrentUnit = (byte)unit,
                        Done = done,
                        Total = total,
                        Elapsed = sw.Elapsed,
                        CurrentParams = combo,
                    });

                    if (result.Response is not null)
                    {
                        var hit = new ScanHit
                        {
                            Unit = (byte)unit,
                            ResponseMs = result.ElapsedMs,
                            ExceptionCode = result.ExceptionCode,
                            BaudRate = combo?.Baud,
                            Parity = combo?.Parity,
                        };
                        hits.Add(hit);
                        Found?.Invoke(hit);
                    }
                }
            }
        }
        finally
        {
            if (SweepSerialParams && _engine.Connection.Config.Kind == ConnectionKind.Serial)
            {
                await _engine.Connection.ApplyConfigAsync(originalConfig).ConfigureAwait(false);
            }
        }

        return hits;
    }

    private List<(int Baud, System.IO.Ports.Parity Parity)?> BuildCombos()
    {
        var combos = new List<(int, System.IO.Ports.Parity)?> { null };
        if (SweepSerialParams && _engine.Connection.Config.Kind == ConnectionKind.Serial)
        {
            foreach (var baud in SweepBauds)
            {
                foreach (var parity in SweepParities)
                {
                    if (baud == _engine.Connection.Config.BaudRate && parity == _engine.Connection.Config.Parity) continue;
                    combos.Add((baud, parity));
                }
            }
        }

        return combos;
    }
}
