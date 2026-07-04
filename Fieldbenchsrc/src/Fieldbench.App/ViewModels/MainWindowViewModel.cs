using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fieldbench.Core.Demo;
using Fieldbench.Core.Lenses;
using Fieldbench.Core.Profiles;
using Fieldbench.Core.Sessions;
using Fieldbench.Core.Transport;

namespace Fieldbench.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(Workbench workbench)
    {
        Workbench = workbench;
        RefreshTree();
    }

    public Workbench Workbench { get; }

    public ObservableCollection<SessionViewModel> Tabs { get; } = new();

    public ObservableCollection<ConnectionNodeViewModel> Tree { get; } = new();

    public ObservableCollection<ProfileNode> Profiles { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveTab), nameof(ShowEmptyState))]
    private SessionViewModel? _activeTab;

    public bool HasActiveTab => ActiveTab is not null;

    public bool ShowEmptyState => ActiveTab is null;

    /// <summary>Set by the view to open modal dialogs; swapped in headless tests.</summary>
    public IDialogHost? DialogHost { get; set; }

    partial void OnActiveTabChanged(SessionViewModel? value)
    {
        foreach (var node in Tree)
        {
            foreach (var s in node.Sessions) s.IsActive = s.Session == value?.Session;
        }

        foreach (var tab in Tabs) tab.IsTabActive = tab == value;
    }

    public void RefreshTree()
    {
        foreach (var old in Tree) old.Detach();
        Tree.Clear();
        foreach (var connection in Workbench.Connections)
        {
            var node = new ConnectionNodeViewModel(connection, this);
            Tree.Add(node);
        }

        Profiles.Clear();
        foreach (var p in Workbench.Settings.Profiles.OrderByDescending(p => p.LastUsedUtc).Take(8))
        {
            Profiles.Add(new ProfileNode(p, this));
        }

        OnPropertyChanged(nameof(ShowEmptyState));
    }

    public SessionViewModel OpenSession(Session session)
    {
        var existing = Tabs.FirstOrDefault(t => t.Session == session);
        if (existing is not null)
        {
            ActiveTab = existing;
            return existing;
        }

        var vm = new SessionViewModel(session, this);
        Tabs.Add(vm);
        ActiveTab = vm;
        RefreshTree();
        return vm;
    }

    [RelayCommand]
    private async Task NewConnectionAsync()
    {
        if (DialogHost is null) return;
        await DialogHost.ShowNewConnectionAsync(this);
    }

    [RelayCommand]
    private void SelectTab(SessionViewModel tab) => ActiveTab = tab;

    [RelayCommand]
    private async Task CloseTabAsync(SessionViewModel tab)
    {
        Tabs.Remove(tab);
        bool isDemo = tab.IsDemoSession;
        tab.Dispose();
        var connection = tab.Session.Connection;
        tab.Session.Dispose();

        if (connection.Sessions.Count == 0)
        {
            if (isDemo)
            {
                // Demo connections belong to the orchestrator, which disposes the
                // loopback pair when its last tab goes; just drop it from the list.
                Workbench.Connections.Remove(connection);
            }
            else
            {
                await Workbench.RemoveConnectionAsync(connection);
            }
        }

        if (ActiveTab == tab) ActiveTab = Tabs.FirstOrDefault();
        RefreshTree();
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        if (DialogHost is null) return;
        await DialogHost.ShowSettingsAsync(this);
    }

    [RelayCommand]
    private async Task RunDemoAsync()
    {
        var demo = await DemoOrchestrator.StartAsync();
        Workbench.AddConnection(demo.MasterConnection);
        Workbench.AddConnection(demo.SlaveConnection);

        var masterLens = new ModbusRtuLens
        {
            Perspective = LensPerspective.Master,
            CharTimeSecondsProvider = () => demo.MasterConnection.Config.CharTimeSeconds(),
        };
        var masterSession = Workbench.CreateSession(demo.MasterConnection, SessionKind.Master, masterLens, "Master");
        var masterVm = OpenSession(masterSession);
        masterVm.AttachDemo(demo);

        var slaveLens = new ModbusRtuLens
        {
            Perspective = LensPerspective.Slave,
            CharTimeSecondsProvider = () => demo.SlaveConnection.Config.CharTimeSeconds(),
        };
        var slaveSession = Workbench.CreateSession(demo.SlaveConnection, SessionKind.Slave, slaveLens, "Slave");
        var slaveVm = OpenSession(slaveSession);
        slaveVm.AttachDemoSlave(demo);

        ActiveTab = masterVm;
        RefreshTree();
    }

    /// <summary>Create connection + first session from the dialog result.</summary>
    public async Task<SessionViewModel> CreateFromDialogAsync(ConnectionConfig config, SessionKind kind)
    {
        var connection = Workbench.AddConnection(config);
        try
        {
            await connection.OpenAsync();
        }
        catch
        {
            // Surface as disconnected; the user can hot-fix parameters and reconnect.
        }

        IProtocolLens lens = kind switch
        {
            SessionKind.Monitor => new RawLens { GapMs = Workbench.Settings.RawSplitGapMs },
            _ => MakeModbusLens(connection, kind),
        };

        var session = Workbench.CreateSession(connection, kind, lens);
        var vm = OpenSession(session);
        if (kind == SessionKind.Master) vm.EnsureMasterPanel();
        if (kind == SessionKind.Slave) vm.EnsureSlavePanel();
        RefreshTree();
        return vm;
    }

    private static IProtocolLens MakeModbusLens(Connection connection, SessionKind kind)
    {
        var perspective = kind == SessionKind.Master ? LensPerspective.Master : LensPerspective.Slave;
        return connection.Config.Kind is ConnectionKind.TcpClient or ConnectionKind.TcpServer
            ? new ModbusTcpLens { Perspective = perspective }
            : new ModbusRtuLens
            {
                Perspective = perspective,
                CharTimeSecondsProvider = () => connection.Config.CharTimeSeconds(),
            };
    }
}

/// <summary>Connection row + its session children in the left rail.</summary>
public partial class ConnectionNodeViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;
    private readonly Action _stateChangedHandler;

    public ConnectionNodeViewModel(Connection connection, MainWindowViewModel main)
    {
        Connection = connection;
        _main = main;
        Sessions = new ObservableCollection<SessionNodeViewModel>(
            connection.Sessions.Select(s => new SessionNodeViewModel(s, main)));
        _stateChangedHandler = () => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(IsOnline)));
        connection.StateChanged += _stateChangedHandler;
    }

    /// <summary>Tree rows are rebuilt on every refresh — drop the model subscription.</summary>
    public void Detach() => Connection.StateChanged -= _stateChangedHandler;

    public Connection Connection { get; }

    public string Name => Connection.Name;

    public string Meta => Connection.Config.Kind switch
    {
        ConnectionKind.Serial => Connection.Config.BaudRate.ToString(),
        ConnectionKind.TcpClient => $":{Connection.Config.Port}",
        ConnectionKind.TcpServer => $":{Connection.Config.ListenPort}",
        _ => "demo",
    };

    public bool IsOnline => Connection.State is TransportState.Open or TransportState.Listening;

    public ObservableCollection<SessionNodeViewModel> Sessions { get; }
}

public partial class SessionNodeViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;

    public SessionNodeViewModel(Session session, MainWindowViewModel main)
    {
        Session = session;
        _main = main;
    }

    public Session Session { get; }

    public string Name => Session.Kind switch
    {
        SessionKind.Monitor => "Monitor",
        SessionKind.Master => "Master",
        _ => "Slave",
    };

    public string KindTag => Session.Lens.Id switch
    {
        "modbus-rtu" => "rtu",
        "modbus-tcp" => "tcp",
        _ => "raw",
    };

    [ObservableProperty]
    private bool _isActive;

    [RelayCommand]
    private void Open() => _main.OpenSession(Session);
}

/// <summary>Left-rail profile row: one click reconnects with the saved config + session kind.</summary>
public partial class ProfileNode(ConnectionProfile profile, MainWindowViewModel main) : ObservableObject
{
    public string Name => profile.Name;

    [RelayCommand]
    private async Task OpenAsync()
    {
        var kind = profile.StartAs switch
        {
            0 => SessionKind.Monitor,
            2 => SessionKind.Slave,
            _ => SessionKind.Master,
        };
        try
        {
            await main.CreateFromDialogAsync(profile.Config.Clone(), kind);
            profile.LastUsedUtc = DateTime.UtcNow;
            main.Workbench.SettingsStore.Save();
        }
        catch (Exception ex)
        {
            if (main.DialogHost is { } host) await host.ShowMessageAsync("Reconnect failed", ex.Message);
        }
    }
}

/// <summary>Implemented by MainWindow (real dialogs) and by test doubles.</summary>
public interface IDialogHost
{
    Task ShowNewConnectionAsync(MainWindowViewModel main);

    Task ShowSettingsAsync(MainWindowViewModel main);

    Task ShowScanAsync(SessionViewModel session);

    Task ShowImportAsync(SessionViewModel session);

    Task ShowMessageAsync(string title, string message);

    /// <summary>First-use AI privacy consent (PRD §6.7); true = user accepted.</summary>
    Task<bool> ConfirmAsync(string title, string message, string accept, string cancel);
}

/// <summary>Gate shared by AI explain and point-map extraction.</summary>
public static class AiConsent
{
    public const string Message =
        "When you press Explain or Extract, Fieldbench sends to the AI gateway: the protocol name, " +
        "connection parameters (port, baud), the frames you selected (hex bytes + parsed fields), " +
        "summaries of ~20 surrounding frames, and your optional question. Nothing else — and nothing " +
        "is stored on the gateway. You can preview the exact payload anytime in Settings → AI assistant.";

    public static async Task<bool> EnsureAsync(Workbench workbench, IDialogHost? host)
    {
        if (workbench.Settings.AiPrivacyAccepted) return true;
        if (host is null) return false;
        bool accepted = await host.ConfirmAsync("Before the first AI call", Message, "Agree and continue", "Cancel");
        if (accepted)
        {
            workbench.Settings.AiPrivacyAccepted = true;
            workbench.SettingsStore.Save();
        }

        return accepted;
    }
}
