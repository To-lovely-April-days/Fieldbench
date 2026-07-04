using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fieldbench.Core.Master;
using Fieldbench.Core.Modbus;
using Fieldbench.Core.Slave;
using Fieldbench.Core.Transport;

namespace Fieldbench.App.ViewModels;

/// <summary>Slave session panel: four register area tabs, value generators, client list.</summary>
public partial class SlavePanelViewModel : ObservableObject, IDisposable
{
    private readonly bool _ownsEngine;

    public SlavePanelViewModel(SessionViewModel session, bool tcpFraming)
        : this(session, new ModbusSlaveEngine(session.Session.Connection, tcpFraming))
    {
        _ownsEngine = true;
    }

    public SlavePanelViewModel(SessionViewModel session, ModbusSlaveEngine engine)
    {
        Session = session;
        Engine = engine;
        engine.TagsChanged += () => Dispatcher.UIThread.Post(RefreshRows);
        engine.Store.RangeChanged += (_, _, _) => Dispatcher.UIThread.Post(RefreshValues);
        engine.Stats += () => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(ServedRequests));
            OnPropertyChanged(nameof(ExceptionsSent));
        });
        RefreshRows();
        RefreshClients();
    }

    public SessionViewModel Session { get; }

    public ModbusSlaveEngine Engine { get; }

    [ObservableProperty]
    private RegisterArea _activeArea = RegisterArea.HoldingRegisters;

    partial void OnActiveAreaChanged(RegisterArea value) => RefreshRows();

    public long ServedRequests => Engine.ServedRequests;

    public long ExceptionsSent => Engine.ExceptionsSent;

    public byte UnitId
    {
        get => Engine.UnitId;
        set
        {
            Engine.UnitId = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<SlaveRowViewModel> Rows { get; } = new();

    public ObservableCollection<ClientRowViewModel> Clients { get; } = new();

    public bool HasClients => Clients.Count > 0;

    private void RefreshRows()
    {
        foreach (var row in Rows) row.Detach();
        Rows.Clear();
        foreach (var st in Engine.Tags.Where(t => t.Tag.Area == ActiveArea))
        {
            Rows.Add(new SlaveRowViewModel(st, this));
        }
    }

    private void RefreshValues()
    {
        foreach (var row in Rows) row.Refresh();
    }

    public void RefreshClients()
    {
        Clients.Clear();
        foreach (var client in Session.Session.Connection.Transport.Clients)
        {
            Clients.Add(new ClientRowViewModel(client));
        }

        OnPropertyChanged(nameof(HasClients));
        OnPropertyChanged(nameof(ClientCountLabel));
    }

    public string ClientCountLabel
    {
        get
        {
            int n = Clients.Count;
            return n == 1 ? "1 connected" : $"{n} connected";
        }
    }

    public void Dispose()
    {
        if (_ownsEngine) Engine.Dispose();
    }

    [RelayCommand]
    private void AddTag()
    {
        var existing = Engine.Tags.Where(t => t.Tag.Area == ActiveArea).ToList();
        ushort next = (ushort)(existing.Count == 0 ? 0 : existing[^1].Tag.Address + existing[^1].Tag.RegisterCount);
        Engine.AddTag(new SlaveTag
        {
            Tag = new RegisterTag
            {
                Area = ActiveArea,
                Address = next,
                Name = $"tag_{next}",
                DataType = ActiveArea.IsBitArea() ? RegisterDataType.Bit : RegisterDataType.UInt16,
            },
        });
    }
}

public partial class SlaveRowViewModel : ObservableObject
{
    private readonly SlavePanelViewModel _panel;
    private readonly Action<RegisterTag> _updatedHandler;

    public SlaveRowViewModel(SlaveTag slaveTag, SlavePanelViewModel panel)
    {
        SlaveTag = slaveTag;
        _panel = panel;
        _updatedHandler = _ => Dispatcher.UIThread.Post(Refresh);
        slaveTag.Tag.Updated += _updatedHandler;
    }

    /// <summary>Rows are rebuilt on area switch / tag change — release the model subscription.</summary>
    public void Detach() => SlaveTag.Tag.Updated -= _updatedHandler;

    public SlaveTag SlaveTag { get; }

    public RegisterTag Tag => SlaveTag.Tag;

    public string Address => Tag.DisplayAddress;

    public string Name => Tag.Name;

    public string TypeLabel => Tag.TypeLabel;

    public string Value => Tag.FormatValue();

    public string RawHex => Tag.RawHex();

    public string GeneratorLabel => SlaveTag.WrittenByClient && SlaveTag.Generator.Kind == GeneratorKind.Static
        ? "Static · written by client"
        : SlaveTag.Generator.Label();

    public bool IsGenerated => SlaveTag.Generator.Kind != GeneratorKind.Static;

    public GeneratorKind GeneratorKind
    {
        get => SlaveTag.Generator.Kind;
        set
        {
            SlaveTag.Generator.Kind = value;
            OnPropertyChanged(nameof(GeneratorLabel));
            OnPropertyChanged(nameof(IsGenerated));
            OnPropertyChanged(nameof(IsSine));
            OnPropertyChanged(nameof(IsRandom));
            OnPropertyChanged(nameof(IsIncrement));
        }
    }

    public static GeneratorKind[] GeneratorKinds { get; } =
        [GeneratorKind.Static, GeneratorKind.Increment, GeneratorKind.RandomRange, GeneratorKind.Sine];

    // ── generator parameter proxies (PRD §6.5: 幅值/周期/范围/步长可配) ──

    public bool IsSine => SlaveTag.Generator.Kind == GeneratorKind.Sine;

    public bool IsRandom => SlaveTag.Generator.Kind == GeneratorKind.RandomRange;

    public bool IsIncrement => SlaveTag.Generator.Kind == GeneratorKind.Increment;

    public decimal SineMin
    {
        get => (decimal)SlaveTag.Generator.SineMin;
        set { SlaveTag.Generator.SineMin = (double)value; OnPropertyChanged(nameof(GeneratorLabel)); }
    }

    public decimal SineMax
    {
        get => (decimal)SlaveTag.Generator.SineMax;
        set { SlaveTag.Generator.SineMax = (double)value; OnPropertyChanged(nameof(GeneratorLabel)); }
    }

    public decimal PeriodSeconds
    {
        get => (decimal)SlaveTag.Generator.PeriodSeconds;
        set { SlaveTag.Generator.PeriodSeconds = Math.Max(0.1, (double)value); OnPropertyChanged(nameof(GeneratorLabel)); }
    }

    public decimal RandMin
    {
        get => (decimal)SlaveTag.Generator.Min;
        set { SlaveTag.Generator.Min = (double)value; OnPropertyChanged(nameof(GeneratorLabel)); }
    }

    public decimal RandMax
    {
        get => (decimal)SlaveTag.Generator.Max;
        set { SlaveTag.Generator.Max = (double)value; OnPropertyChanged(nameof(GeneratorLabel)); }
    }

    public decimal StepPerSecond
    {
        get => (decimal)SlaveTag.Generator.IncrementPerSecond;
        set { SlaveTag.Generator.IncrementPerSecond = (double)value; OnPropertyChanged(nameof(GeneratorLabel)); }
    }

    public decimal WrapMin
    {
        get => (decimal)SlaveTag.Generator.WrapMin;
        set => SlaveTag.Generator.WrapMin = (double)value;
    }

    public decimal WrapMax
    {
        get => (decimal)SlaveTag.Generator.WrapMax;
        set => SlaveTag.Generator.WrapMax = (double)value;
    }

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editValue = "";

    [RelayCommand]
    private void BeginEdit()
    {
        EditValue = Tag.ScaledValue?.ToString(CultureInfo.InvariantCulture) ?? "";
        IsEditing = true;
    }

    [RelayCommand]
    private void CommitEdit()
    {
        if (double.TryParse(EditValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            _panel.Engine.SetTagValue(SlaveTag, value);
        }

        IsEditing = false;
        Refresh();
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(RawHex));
        OnPropertyChanged(nameof(GeneratorLabel));
    }
}

public sealed class ClientRowViewModel(TransportClientInfo info)
{
    public string Endpoint => info.RemoteEndpoint;

    public string Meta
    {
        get
        {
            var dur = DateTime.UtcNow - info.ConnectedAtUtc;
            return $"req {info.RequestCount} · {dur:hh\\:mm\\:ss}";
        }
    }
}
