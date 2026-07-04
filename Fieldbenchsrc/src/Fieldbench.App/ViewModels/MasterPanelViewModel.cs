using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fieldbench.Core.Master;
using Fieldbench.Core.Modbus;

namespace Fieldbench.App.ViewModels;

/// <summary>Master session panel: register grid, poll tasks, sender, chart.</summary>
public partial class MasterPanelViewModel : ObservableObject, IDisposable
{
    private readonly bool _ownsEngine;
    private readonly bool _ownsScheduler;

    public MasterPanelViewModel(SessionViewModel session, bool tcpFraming)
        : this(session, new ModbusMasterEngine(session.Session.Connection, tcpFraming), null)
    {
    }

    public MasterPanelViewModel(SessionViewModel session, ModbusMasterEngine engine, PollScheduler? scheduler)
    {
        Session = session;
        Engine = engine;
        Scheduler = scheduler ?? new PollScheduler(engine);
        _ownsEngine = scheduler is null;
        _ownsScheduler = scheduler is null;
        Sender = new SendPanelViewModel(session);
        Chart = new ChartPanelViewModel(session, this);

        Engine.Map.TagsChanged += () => Dispatcher.UIThread.Post(RefreshRegisters);
        Engine.Map.TagUpdated += tag => Dispatcher.UIThread.Post(() => UpdateRegisterRow(tag));
        Scheduler.TasksChanged += () => Dispatcher.UIThread.Post(RefreshTasks);

        RefreshRegisters();
        RefreshTasks();
    }

    public SessionViewModel Session { get; }

    public ModbusMasterEngine Engine { get; }

    public PollScheduler Scheduler { get; }

    public SendPanelViewModel Sender { get; }

    public ChartPanelViewModel Chart { get; }

    [ObservableProperty]
    private int _activeTabIndex;

    public ObservableCollection<RegisterRowViewModel> Registers { get; } = new();

    public ObservableCollection<PollTaskRowViewModel> Tasks { get; } = new();

    public bool HasRunningTask => Scheduler.Tasks.Any(t => t.Enabled);

    public string RunningTaskSummary
    {
        get
        {
            var running = Scheduler.Tasks.FirstOrDefault(t => t.Enabled);
            if (running is null) return "";
            return $"@{running.Unit:00} · FC{running.Function:00} · {running.Start}–{running.Start + running.Count - 1} · {running.PeriodMs} ms";
        }
    }

    public string RunningTaskCycle
    {
        get
        {
            var running = Scheduler.Tasks.FirstOrDefault(t => t.Enabled);
            return running?.LastCycleMs is { } c ? $"cycle {c:0} ms" : "";
        }
    }

    private void RefreshRegisters()
    {
        Registers.Clear();
        foreach (var tag in Engine.Map.Tags)
        {
            Registers.Add(new RegisterRowViewModel(tag, this));
        }
    }

    private void UpdateRegisterRow(RegisterTag tag)
    {
        foreach (var row in Registers)
        {
            if (row.Tag == tag)
            {
                row.Refresh();
                break;
            }
        }

        OnPropertyChanged(nameof(RunningTaskCycle));
    }

    private void RefreshTasks()
    {
        foreach (var row in Tasks) row.Detach();
        Tasks.Clear();
        foreach (var task in Scheduler.Tasks)
        {
            Tasks.Add(new PollTaskRowViewModel(task, this));
        }

        OnPropertyChanged(nameof(RunningTaskSummary));
        OnPropertyChanged(nameof(RunningTaskCycle));
        OnPropertyChanged(nameof(HasRunningTask));
    }

    [RelayCommand]
    private void AddTask()
    {
        var last = Scheduler.Tasks.LastOrDefault();
        Scheduler.Add(new PollTask
        {
            Name = $"Task {Scheduler.Tasks.Count + 1}",
            Unit = last?.Unit ?? Engine.DefaultUnit,
            Function = ModbusFunction.ReadHoldingRegisters,
            Start = 0,
            Count = 10,
            PeriodMs = 500,
        });
    }

    [RelayCommand]
    private void AddTag()
    {
        var last = Engine.Map.Tags.LastOrDefault();
        ushort next = (ushort)(last is null ? 0 : last.Address + last.RegisterCount);
        Engine.Map.AddTag(new RegisterTag
        {
            Area = RegisterArea.HoldingRegisters,
            Address = next,
            Name = $"tag_{next}",
            DataType = RegisterDataType.UInt16,
        });
    }

    public void Dispose()
    {
        Chart.Dispose();
        foreach (var row in Tasks) row.Detach();
        // A demo panel shares the orchestrator's scheduler/engine — never kill those here.
        if (_ownsScheduler) Scheduler.Dispose();
        if (_ownsEngine) Engine.Dispose();
    }
}

/// <summary>One register grid row with inline editing → auto write FC.</summary>
public partial class RegisterRowViewModel : ObservableObject
{
    private readonly MasterPanelViewModel _panel;

    public RegisterRowViewModel(RegisterTag tag, MasterPanelViewModel panel)
    {
        Tag = tag;
        _panel = panel;
    }

    public RegisterTag Tag { get; }

    public string Address => Tag.DisplayAddress;

    public string Name => Tag.Name;

    public string TypeLabel => Tag.TypeLabel;

    public string Value => Tag.FormatValue();

    public string Unit => Tag.Unit;

    public string RawHex => Tag.RawHex();

    public string RwLabel => Tag.Writable ? "RW" : "R";

    public bool IsWritable => Tag.Writable;

    public IReadOnlyList<double> Sparkline
    {
        get
        {
            var history = Tag.HistorySnapshot();
            int take = Math.Min(history.Count, 48);
            var values = new double[take];
            for (int i = 0; i < take; i++) values[i] = history[history.Count - take + i].Value;
            return values;
        }
    }

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editValue = "";

    [ObservableProperty]
    private string? _writeError;

    [RelayCommand]
    private void BeginEdit()
    {
        if (!IsWritable) return;
        EditValue = Tag.ScaledValue?.ToString(CultureInfo.InvariantCulture) ?? "";
        IsEditing = true;
    }

    [RelayCommand]
    private async Task CommitEditAsync()
    {
        if (!double.TryParse(EditValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            IsEditing = false;
            return;
        }

        IsEditing = false;
        var result = await _panel.Engine.WriteTagAsync(Tag, value);
        WriteError = result.Success ? null : result.HumanError;
        Refresh();
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(RawHex));
        OnPropertyChanged(nameof(Sparkline));
    }
}

/// <summary>One poll task row: enable switch, live cycle, error count, actions.</summary>
public partial class PollTaskRowViewModel : ObservableObject
{
    private readonly MasterPanelViewModel _panel;
    private readonly Action _changedHandler;

    public PollTaskRowViewModel(PollTask task, MasterPanelViewModel panel)
    {
        Task_ = task;
        _panel = panel;
        _changedHandler = () => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(LastCycle));
            OnPropertyChanged(nameof(Errors));
            OnPropertyChanged(nameof(HasErrors));
            OnPropertyChanged(nameof(IsEnabled));
            OnPropertyChanged(nameof(IsRunning));
        });
        task.Changed += _changedHandler;
    }

    /// <summary>Rows are rebuilt on every TasksChanged — release the model subscription.</summary>
    public void Detach() => Task_.Changed -= _changedHandler;

    public PollTask Task_ { get; }

    public string Name => Task_.Name;

    public string UnitToken => $"@{Task_.Unit:00}";

    public string FcToken => $"FC{Task_.Function:00}";

    public string Target => Task_.TargetLabel();

    public string Interval => $"{Task_.PeriodMs} ms";

    public string LastCycle => Task_.LastCycleMs is { } c ? $"{c:0} ms" : "—";

    public string Errors => Task_.ErrorCount.ToString();

    public bool HasErrors => Task_.ErrorCount > 0;

    public bool IsRunning => _panel.Scheduler.IsRunning(Task_);

    public bool IsEnabled
    {
        get => Task_.Enabled;
        set
        {
            if (value) _panel.Scheduler.Start(Task_);
            else _panel.Scheduler.Stop(Task_);
            OnPropertyChanged(nameof(IsRunning));
        }
    }

    [RelayCommand]
    private Task RunOnceAsync() => _panel.Scheduler.RunOnceAsync(Task_);

    [RelayCommand]
    private void Remove() => _panel.Scheduler.Remove(Task_);
}
