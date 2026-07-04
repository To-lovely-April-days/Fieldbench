using Fieldbench.Core.Modbus;

namespace Fieldbench.Core.Master;

/// <summary>One recurring read: slave + FC + range + period. Runtime stats included.</summary>
public sealed class PollTask
{
    public Guid Id { get; } = Guid.NewGuid();

    public string Name { get; set; } = "Task";

    public byte Unit { get; set; } = 1;

    public byte Function { get; set; } = ModbusFunction.ReadHoldingRegisters;

    public ushort Start { get; set; }

    public ushort Count { get; set; } = 10;

    public int PeriodMs { get; set; } = 500;

    public bool Enabled { get; set; }

    // Runtime
    public double? LastCycleMs { get; internal set; }

    public int ErrorCount { get; internal set; }

    public DateTime? LastRunUtc { get; internal set; }

    public event Action? Changed;

    internal void NotifyChanged() => Changed?.Invoke();

    public RegisterArea Area => RegisterAreaInfo.AreaForReadFc(Function);

    public string TargetLabel() => $"{RegisterAreaInfo.AreaForReadFc(Function) switch
    {
        RegisterArea.Coils => "Coils",
        RegisterArea.DiscreteInputs => "Discrete",
        RegisterArea.InputRegisters => "Input",
        _ => "Holding",
    }} {Start}–{Start + Math.Max(1, (int)Count) - 1}";
}

/// <summary>
/// Runs enabled poll tasks concurrently; the engine's per-connection semaphore
/// interleaves them serially on the wire.
/// </summary>
public sealed class PollScheduler : IDisposable
{
    private readonly ModbusMasterEngine _engine;
    private readonly object _gate = new();
    private readonly List<PollTask> _tasks = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _running = new();
    private bool _disposed;

    public PollScheduler(ModbusMasterEngine engine)
    {
        _engine = engine;
    }

    public event Action? TasksChanged;

    public IReadOnlyList<PollTask> Tasks
    {
        get { lock (_gate) return _tasks.ToArray(); }
    }

    public int RunningCount
    {
        get { lock (_gate) return _running.Count; }
    }

    public PollTask Add(PollTask task)
    {
        lock (_gate) _tasks.Add(task);
        TasksChanged?.Invoke();
        if (task.Enabled) Start(task);
        return task;
    }

    public void Remove(PollTask task)
    {
        Stop(task);
        lock (_gate) _tasks.Remove(task);
        TasksChanged?.Invoke();
    }

    public void Start(PollTask task)
    {
        lock (_gate)
        {
            if (_disposed || _running.ContainsKey(task.Id)) return;
            var cts = new CancellationTokenSource();
            _running[task.Id] = cts;
            task.Enabled = true;
            _ = RunLoopAsync(task, cts.Token);
        }

        task.NotifyChanged();
        TasksChanged?.Invoke();
    }

    public void Stop(PollTask task)
    {
        lock (_gate)
        {
            if (_running.Remove(task.Id, out var cts)) cts.Cancel();
            task.Enabled = false;
        }

        task.NotifyChanged();
        TasksChanged?.Invoke();
    }

    public bool IsRunning(PollTask task)
    {
        lock (_gate) return _running.ContainsKey(task.Id);
    }

    /// <summary>Single-shot execution (the ▶ button on a stopped task).</summary>
    public Task<ModbusRequestResult> RunOnceAsync(PollTask task, CancellationToken ct = default) => ExecuteAsync(task, ct);

    private async Task RunLoopAsync(PollTask task, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(20, task.PeriodMs)));
            await ExecuteAsync(task, ct).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await ExecuteAsync(task, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<ModbusRequestResult> ExecuteAsync(PollTask task, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        ModbusRequestResult result;
        try
        {
            result = await _engine.ReadAsync(task.Area, task.Start, task.Count, task.Unit, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A transport/engine fault must never silently kill the poll loop.
            result = new ModbusRequestResult { Success = false, Error = ex.Message };
        }

        task.LastRunUtc = started;
        task.LastCycleMs = result.ElapsedMs;
        if (!result.Success) task.ErrorCount++;
        task.NotifyChanged();
        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var cts in _running.Values) cts.Cancel();
            _running.Clear();
        }
    }
}
