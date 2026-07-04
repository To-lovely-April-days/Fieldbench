using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fieldbench.Core.Demo;
using Fieldbench.Core.Lenses;
using Fieldbench.Core.Sessions;
using Fieldbench.Core.Streams;
using Fieldbench.Core.Transport;

namespace Fieldbench.App.ViewModels;

/// <summary>
/// One session tab: timeline (batched, filterable, bounded), toolbar state,
/// detection chip, inspector + AI panels, role-specific protocol panel.
/// Capture never pauses — only rendering does (PRD hard rule).
/// </summary>
public partial class SessionViewModel : ObservableObject, IDisposable
{
    private readonly ConcurrentQueue<Frame> _incoming = new();
    private readonly DispatcherTimer _drainTimer;
    private readonly DispatcherTimer _clockTimer;
    private DemoOrchestrator? _demo;
    private readonly object _recordGate = new();
    private FileStream? _recordFile;
    private int _framesThisSecond;
    private DateTime _fpsWindowStart = DateTime.UtcNow;

    public SessionViewModel(Session session, MainWindowViewModel main)
    {
        Session = session;
        Main = main;
        Inspector = new InspectorViewModel();
        Ai = new AiPanelViewModel(this);

        session.FramesAdded += OnFramesAdded;
        session.FramesReset += OnFramesReset;
        session.Connection.StateChanged += OnConnectionStateChanged;
        session.Connection.ClientsChanged += OnClientsChanged;
        session.Connection.DeviceLost += OnDeviceLost;
        if (session.Detector is { } det) det.Detected += OnDetected;

        if (session.Kind == SessionKind.Master) EnsureMasterPanel();
        if (session.Kind == SessionKind.Slave) EnsureSlavePanel();
        if (session.Kind == SessionKind.Monitor) Sender = new SendPanelViewModel(this);

        _drainTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => Drain());
        _drainTimer.Start();
        _clockTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => TickClock());
        _clockTimer.Start();
    }

    public Session Session { get; }

    public MainWindowViewModel Main { get; }

    public InspectorViewModel Inspector { get; }

    public AiPanelViewModel Ai { get; }

    public MasterPanelViewModel? MasterPanel { get; private set; }

    public SlavePanelViewModel? SlavePanel { get; private set; }

    public SendPanelViewModel? Sender { get; private set; }

    public ObservableCollection<Frame> Frames { get; } = new();

    public ObservableCollection<Frame> SelectedFrames { get; } = new();

    // ── tab header ──

    public string TabTitle => Session.Connection.Name.Split(" — ")[0];

    public string TabKind => Session.Kind switch
    {
        SessionKind.Monitor => "mon",
        SessionKind.Master => Session.Lens.Id == "modbus-tcp" ? "tcp·m" : "rtu·m",
        _ => Session.Lens.Id == "modbus-tcp" ? "tcp·s" : "slv",
    };

    public bool IsMonitor => Session.Kind == SessionKind.Monitor;

    public bool IsMaster => Session.Kind == SessionKind.Master;

    public bool IsSlave => Session.Kind == SessionKind.Slave;

    public bool IsSerial => Session.Connection.Config.Kind == ConnectionKind.Serial;

    public bool IsTcpServer => Session.Connection.Config.Kind == ConnectionKind.TcpServer;

    // ── toolbar ──

    public bool IsConnected => Session.Connection.State is TransportState.Open or TransportState.Listening;

    public string ConnectionStateLabel => Session.Connection.State switch
    {
        TransportState.Open => Loc("L.Connected", "Connected"),
        TransportState.Listening => Loc("L.Listening", "Listening"),
        TransportState.Faulted => Loc("L.DeviceLost", "Device lost"),
        _ => Loc("L.Disconnected", "Disconnected"),
    };

    private static string Loc(string key, string fallback)
    {
        var app = Avalonia.Application.Current;
        return app is not null && app.TryGetResource(key, null, out var value) && value is string s ? s : fallback;
    }

    public string PortLabel => Session.Connection.Config.ShortLabel();

    public string ParamsLabel => Session.Connection.Config.Kind == ConnectionKind.Serial
        ? $"{Session.Connection.Config.DataBits}-{Session.Connection.Config.ParityChar()}-{Session.Connection.Config.StopBitsLabel()}"
        : Session.Connection.Config.ParamSummary();

    public int BaudRate
    {
        get => Session.Connection.Config.BaudRate;
        set
        {
            if (value <= 0 || value == Session.Connection.Config.BaudRate) return;
            var cfg = Session.Connection.Config.Clone();
            cfg.BaudRate = value;
            _ = ApplyConfigAsync(cfg);
        }
    }

    public static int[] StandardBauds { get; } = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600];

    /// <summary>Hot parameter apply: session + captured stream always survive.</summary>
    public async Task ApplyConfigAsync(ConnectionConfig cfg)
    {
        await Session.Connection.ApplyConfigAsync(cfg);
        OnPropertyChanged(nameof(BaudRate));
        OnPropertyChanged(nameof(ParamsLabel));
        OnPropertyChanged(nameof(PortLabel));
    }

    [ObservableProperty]
    private string _connectedFor = "";

    [ObservableProperty]
    private bool _isTabActive;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _isRecording;

    partial void OnIsRecordingChanged(bool value)
    {
        if (value) StartRecording();
        else StopRecording();
    }

    // ── lens ──

    public bool IsRawLensActive => Session.Lens.Id == "raw";

    public bool IsModbusLensActive => Session.Lens.Id.StartsWith("modbus");

    public string ModbusLensLabel
    {
        get
        {
            string proto = Session.Connection.Config.Kind is ConnectionKind.TcpClient or ConnectionKind.TcpServer
                ? "Modbus TCP" : "Modbus RTU";
            return Session.Kind switch
            {
                SessionKind.Master => $"{proto} · Master",
                SessionKind.Slave => $"{proto} · Slave",
                _ => proto,
            };
        }
    }

    [RelayCommand]
    private void SwitchToRawLens()
    {
        if (IsRawLensActive) return;
        Session.SwitchLens(new RawLens { GapMs = Main.Workbench.Settings.RawSplitGapMs });
        NotifyLensChanged();
    }

    [RelayCommand]
    private void SwitchToModbusLens()
    {
        if (IsModbusLensActive) return;
        var cfg = Session.Connection.Config;
        IProtocolLens lens = cfg.Kind is ConnectionKind.TcpClient or ConnectionKind.TcpServer
            ? new ModbusTcpLens { Perspective = PerspectiveForKind() }
            : new ModbusRtuLens { Perspective = PerspectiveForKind(), CharTimeSecondsProvider = () => cfg.CharTimeSeconds() };
        Session.SwitchLens(lens);
        NotifyLensChanged();
    }

    private LensPerspective PerspectiveForKind() => Session.Kind switch
    {
        SessionKind.Master => LensPerspective.Master,
        SessionKind.Slave => LensPerspective.Slave,
        _ => LensPerspective.Monitor,
    };

    private void NotifyLensChanged()
    {
        OnPropertyChanged(nameof(IsRawLensActive));
        OnPropertyChanged(nameof(IsModbusLensActive));
        OnPropertyChanged(nameof(TabKind));
        OnPropertyChanged(nameof(ShowAsciiColumn));
    }

    public bool ShowAsciiColumn => IsRawLensActive;

    /// <summary>Raw split gap (ms) — visible for raw/monitor sessions.</summary>
    public double SplitGapMs
    {
        get => (Session.Lens as RawLens)?.GapMs ?? Main.Workbench.Settings.RawSplitGapMs;
        set
        {
            if (Session.Lens is RawLens raw && Math.Abs(raw.GapMs - value) > 0.01)
            {
                raw.GapMs = value;
                Main.Workbench.Settings.RawSplitGapMs = value;
                Session.Reparse();
            }
        }
    }

    // ── detection chip ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetection), nameof(DetectionTitle), nameof(DetectionMeta))]
    private DetectionResult? _detection;

    public bool HasDetection => Detection is not null;

    public string DetectionTitle => Detection is { } d
        ? $"{d.DisplayName} {Loc("L.Detected", "detected")}"
        : "";

    public string DetectionMeta => Detection is { } d
        ? $"{d.PassCount} / {d.TotalCount} CRC-16 ✓ · {d.Evidence}"
        : "";

    private void OnDetected(DetectionResult result) =>
        Dispatcher.UIThread.Post(() => Detection = result);

    [RelayCommand]
    private void ApplyDetectedLens()
    {
        if (Detection is null) return;
        SwitchToModbusLens();
        Detection = null;
    }

    [RelayCommand]
    private void DismissDetection() => Detection = null;

    // ── filters ──

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private bool _filterErrorsOnly;

    /// <summary>0 all · 1 TX only · 2 RX only.</summary>
    [ObservableProperty]
    private int _filterDirection;

    public static string[] DirectionFilters { get; } = ["All", "TX", "RX"];

    public string FilterDirectionLabel
    {
        get => DirectionFilters[Math.Clamp(FilterDirection, 0, 2)];
        set => FilterDirection = Array.IndexOf(DirectionFilters, value) is var i and >= 0 ? i : 0;
    }

    partial void OnFilterTextChanged(string value) => RebuildFrames();

    partial void OnFilterErrorsOnlyChanged(bool value) => RebuildFrames();

    partial void OnFilterDirectionChanged(int value)
    {
        OnPropertyChanged(nameof(FilterDirectionLabel));
        RebuildFrames();
    }

    private bool PassesFilter(Frame f)
    {
        if (FilterErrorsOnly && !f.IsAbnormal) return false;
        if (FilterDirection == 1 && f.Direction != StreamDirection.Tx) return false;
        if (FilterDirection == 2 && f.Direction != StreamDirection.Rx) return false;
        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var t = FilterText.Trim();
            return f.HexString().Contains(t, StringComparison.OrdinalIgnoreCase)
                   || f.Summary.Contains(t, StringComparison.OrdinalIgnoreCase)
                   || (f.FunctionToken?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
                   || (f.AddressToken?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
                   || RawLens.AsciiPreview(f.Bytes).Contains(t, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    // ── frame flow ──

    private void OnFramesAdded(IReadOnlyList<Frame> frames)
    {
        foreach (var f in frames) _incoming.Enqueue(f);
        if (_recordFile is not null) RecordFrames(frames);
    }

    private void OnFramesReset() => Dispatcher.UIThread.Post(RebuildFrames);

    private void Drain()
    {
        if (IsPaused)
        {
            // Rendering paused; capture continues into Session. Drop our queue —
            // the resume path rebuilds from the session snapshot.
            while (_incoming.TryDequeue(out _)) { }
            return;
        }

        bool added = false;
        int drained = 0;
        while (drained < 2000 && _incoming.TryDequeue(out var frame))
        {
            drained++;
            _framesThisSecond++;
            if (PassesFilter(frame))
            {
                Frames.Add(frame);
                added = true;
            }
        }

        if (added)
        {
            int overflow = Frames.Count - Session.MaxFrames;
            if (overflow > 0)
            {
                for (int i = 0; i < overflow; i++) Frames.RemoveAt(0);
            }

            TailChanged?.Invoke();
            UpdateStats();
        }
    }

    partial void OnIsPausedChanged(bool value)
    {
        if (!value) RebuildFrames();
    }

    private void RebuildFrames()
    {
        Frames.Clear();
        foreach (var f in Session.SnapshotFrames())
        {
            if (PassesFilter(f)) Frames.Add(f);
        }

        while (_incoming.TryDequeue(out _)) { }
        TailChanged?.Invoke();
        UpdateStats();
    }

    /// <summary>The view auto-scrolls to tail on this signal.</summary>
    public event Action? TailChanged;

    /// <summary>Chart → timeline jump: view scrolls to and selects the frame.</summary>
    public event Action<Frame>? JumpRequested;

    public void JumpToFrame(Frame frame) => JumpRequested?.Invoke(frame);

    public void JumpToTime(DateTime utc)
    {
        var frame = Frames.LastOrDefault(f => f.TimestampUtc <= utc) ?? Frames.FirstOrDefault();
        if (frame is not null) JumpRequested?.Invoke(frame);
    }

    // ── selection → inspector + AI ──

    public void OnSelectionChanged(IReadOnlyList<Frame> selected)
    {
        SelectedFrames.Clear();
        foreach (var f in selected) SelectedFrames.Add(f);
        Inspector.SetFrame(selected.Count > 0 ? selected[^1] : null, IsRawLensActive, Detection is not null);
        Ai.OnSelectionChanged();
    }

    // ── status bar ──

    [ObservableProperty]
    private string _txBytes = "0 B";

    [ObservableProperty]
    private string _rxBytes = "0 B";

    [ObservableProperty]
    private string _frameCount = "0";

    [ObservableProperty]
    private string _errorCount = "0";

    [ObservableProperty]
    private double _bufferUsage;

    [ObservableProperty]
    private string _bufferPercent = "0%";

    [ObservableProperty]
    private string _fps = "60";

    private void UpdateStats()
    {
        var store = Session.Connection.Store;
        TxBytes = FormatBytes(store.TotalTxBytes);
        RxBytes = FormatBytes(store.TotalRxBytes);
        FrameCount = Session.TotalFrames.ToString("N0");
        ErrorCount = Session.ErrorFrames.ToString("N0");
        BufferUsage = Session.BufferUsage;
        BufferPercent = $"{Session.BufferUsage * 100:0}%";
    }

    private static string FormatBytes(long n) => n switch
    {
        < 1024 => $"{n} B",
        < 1024 * 1024 => $"{n / 1024.0:0.#} KB",
        _ => $"{n / (1024.0 * 1024.0):0.#} MB",
    };

    private void TickClock()
    {
        if (Session.Connection.ConnectedAtUtc is { } start)
        {
            ConnectedFor = (DateTime.UtcNow - start).ToString(@"hh\:mm\:ss");
        }

        var elapsed = (DateTime.UtcNow - _fpsWindowStart).TotalSeconds;
        if (elapsed >= 1)
        {
            var rate = _framesThisSecond / elapsed;
            Fps = rate > 55 ? "60" : Math.Max(1, rate).ToString("0");
            _framesThisSecond = 0;
            _fpsWindowStart = DateTime.UtcNow;
            if (rate < 1) Fps = "60";
        }

        UpdateStats();
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectionStateLabel));
    }

    // ── connect / disconnect ──

    [RelayCommand]
    private async Task ToggleConnectionAsync()
    {
        if (IsConnected)
        {
            await Session.Connection.CloseAsync();
        }
        else
        {
            try
            {
                await Session.Connection.OpenAsync();
            }
            catch (Exception ex)
            {
                if (Main.DialogHost is { } host) await host.ShowMessageAsync("Connection failed", ex.Message);
            }
        }

        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectionStateLabel));
    }

    [RelayCommand]
    private void ClearTimeline()
    {
        Session.Connection.Store.Clear();
        Frames.Clear();
        UpdateStats();
    }

    [RelayCommand]
    private async Task ScanSlavesAsync()
    {
        if (Main.DialogHost is { } host) await host.ShowScanAsync(this);
    }

    /// <summary>Export the selection when one exists, otherwise the full timeline (PRD §6.2).</summary>
    [RelayCommand]
    private async Task ExportAsync(string format)
    {
        var fmt = format.ToLowerInvariant() switch
        {
            "json" => Core.Export.ExportFormat.Json,
            "bin" => Core.Export.ExportFormat.Bin,
            "txt" => Core.Export.ExportFormat.Txt,
            _ => Core.Export.ExportFormat.Csv,
        };

        IReadOnlyList<Frame> frames = SelectedFrames.Count > 0 ? SelectedFrames.ToArray() : Session.SnapshotFrames();
        var bytes = Core.Export.FrameExporter.Export(frames, fmt);
        var dir = Path.Combine(Main.Workbench.SettingsStore.Directory_, "exports");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"frames-{DateTime.Now:yyyyMMdd-HHmmss}.{Core.Export.FrameExporter.DefaultExtension(fmt)}");
        await File.WriteAllBytesAsync(path, bytes);
        if (Main.DialogHost is { } host)
        {
            await host.ShowMessageAsync("Export", $"{frames.Count} frames → {path}");
        }
    }

    [RelayCommand]
    private async Task ImportPointMapAsync()
    {
        if (Main.DialogHost is { } host) await host.ShowImportAsync(this);
    }

    private void OnConnectionStateChanged() => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectionStateLabel));
    });

    private void OnClientsChanged() => Dispatcher.UIThread.Post(() => SlavePanel?.RefreshClients());

    private void OnDeviceLost(Exception ex) => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectionStateLabel));
    });

    // ── record to disk ──

    private void StartRecording()
    {
        try
        {
            var dir = Path.Combine(Main.Workbench.SettingsStore.Directory_, "captures");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            lock (_recordGate)
            {
                _recordFile = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            }
        }
        catch
        {
            IsRecording = false;
        }
    }

    private void RecordFrames(IReadOnlyList<Frame> frames)
    {
        // Producers are transport threads + the flush timer; teardown is UI-side.
        lock (_recordGate)
        {
            if (_recordFile is null) return;
            try
            {
                var text = Core.Export.FrameExporter.ToTxt(frames);
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                _recordFile.Write(bytes);
                _recordFile.Flush();
            }
            catch
            {
                // Disk full / removed: close the file and drop the REC state; capture continues in memory.
                try { _recordFile.Dispose(); } catch { }
                _recordFile = null;
                Dispatcher.UIThread.Post(() => IsRecording = false);
            }
        }
    }

    private void StopRecording()
    {
        lock (_recordGate)
        {
            _recordFile?.Dispose();
            _recordFile = null;
        }
    }

    // ── panels ──

    public void EnsureMasterPanel()
    {
        if (MasterPanel is not null) return;
        bool tcp = Session.Connection.Config.Kind is ConnectionKind.TcpClient or ConnectionKind.TcpServer;
        MasterPanel = new MasterPanelViewModel(this, tcp);
        Sender = MasterPanel.Sender;
        OnPropertyChanged(nameof(MasterPanel));
        OnPropertyChanged(nameof(Sender));
    }

    public void EnsureSlavePanel()
    {
        if (SlavePanel is not null) return;
        bool tcp = Session.Connection.Config.Kind is ConnectionKind.TcpClient or ConnectionKind.TcpServer;
        SlavePanel = new SlavePanelViewModel(this, tcp);
        OnPropertyChanged(nameof(SlavePanel));
    }

    public void AttachDemo(DemoOrchestrator demo)
    {
        _demo = demo;
        EnsureMasterPanelFrom(demo);
    }

    private void EnsureMasterPanelFrom(DemoOrchestrator demo)
    {
        // The constructor may have auto-created a plain engine; replace it with
        // the demo's shared engine and release the placeholder.
        MasterPanel?.Dispose();
        MasterPanel = new MasterPanelViewModel(this, demo.Master, demo.Scheduler);
        Sender = MasterPanel.Sender;
        OnPropertyChanged(nameof(MasterPanel));
        OnPropertyChanged(nameof(Sender));
    }

    public void AttachDemoSlave(DemoOrchestrator demo)
    {
        _demo = demo;
        SlavePanel?.Dispose();
        SlavePanel = new SlavePanelViewModel(this, demo.Slave);
        OnPropertyChanged(nameof(SlavePanel));
    }

    /// <summary>True when this tab belongs to the in-process demo pair.</summary>
    public bool IsDemoSession => _demo is not null;

    public void Dispose()
    {
        _drainTimer.Stop();
        _clockTimer.Stop();
        StopRecording();
        Sender?.StopCycle();
        MasterPanel?.Sender.StopCycle();
        Session.FramesAdded -= OnFramesAdded;
        Session.FramesReset -= OnFramesReset;
        Session.Connection.StateChanged -= OnConnectionStateChanged;
        Session.Connection.ClientsChanged -= OnClientsChanged;
        Session.Connection.DeviceLost -= OnDeviceLost;
        MasterPanel?.Dispose();
        SlavePanel?.Dispose();
        if (_demo is { } demo && Main.Tabs.All(t => t._demo != demo))
        {
            _ = demo.DisposeAsync();
        }
    }
}
