using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fieldbench.Core.Ai;
using Fieldbench.Core.Master;
using Fieldbench.Core.Modbus;
using Fieldbench.Core.Profiles;
using Fieldbench.Core.Sessions;
using Fieldbench.Core.Transport;

namespace Fieldbench.App.ViewModels;

// ─────────────────────────── New connection ───────────────────────────

public partial class NewConnectionViewModel : ObservableObject
{
    public NewConnectionViewModel(MainWindowViewModel main)
    {
        Main = main;
        RefreshPorts();
    }

    public MainWindowViewModel Main { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSerial), nameof(IsTcpClient), nameof(IsTcpServer))]
    private int _kindIndex;

    public bool IsSerial => KindIndex == 0;

    public bool IsTcpClient => KindIndex == 1;

    public bool IsTcpServer => KindIndex == 2;

    public ObservableCollection<string> Ports { get; } = new();

    [ObservableProperty]
    private string? _selectedPort;

    [ObservableProperty]
    private int _baudRate = 9600;

    public static int[] BaudRates => SessionViewModel.StandardBauds;

    [ObservableProperty]
    private int _dataBits = 8;

    public static int[] DataBitsOptions { get; } = [5, 6, 7, 8];

    [ObservableProperty]
    private System.IO.Ports.Parity _parity = System.IO.Ports.Parity.None;

    public static System.IO.Ports.Parity[] ParityOptions { get; } =
        [System.IO.Ports.Parity.None, System.IO.Ports.Parity.Even, System.IO.Ports.Parity.Odd, System.IO.Ports.Parity.Mark, System.IO.Ports.Parity.Space];

    [ObservableProperty]
    private System.IO.Ports.StopBits _stopBits = System.IO.Ports.StopBits.One;

    public static System.IO.Ports.StopBits[] StopBitsOptions { get; } =
        [System.IO.Ports.StopBits.One, System.IO.Ports.StopBits.OnePointFive, System.IO.Ports.StopBits.Two];

    [ObservableProperty]
    private System.IO.Ports.Handshake _flowControl = System.IO.Ports.Handshake.None;

    public static System.IO.Ports.Handshake[] FlowOptions { get; } =
        [System.IO.Ports.Handshake.None, System.IO.Ports.Handshake.RequestToSend, System.IO.Ports.Handshake.XOnXOff];

    [ObservableProperty]
    private string _host = "192.168.1.50";

    [ObservableProperty]
    private int _port = 502;

    [ObservableProperty]
    private int _listenPort = 502;

    /// <summary>0 Monitor · 1 Master (default) · 2 Slave.</summary>
    [ObservableProperty]
    private int _startAs = 1;

    [ObservableProperty]
    private string? _error;

    public event Action<SessionViewModel?>? Completed;

    public void RefreshPorts()
    {
        Ports.Clear();
        foreach (var (name, friendly) in SerialTransport.ListPorts())
        {
            Ports.Add(string.IsNullOrEmpty(friendly) ? name : $"{name} — {friendly}");
        }

        // USB serial adapters enumerate first-come; preselect the first (PRD S0).
        SelectedPort = Ports.FirstOrDefault();
    }

    public ConnectionConfig BuildConfig()
    {
        var portName = SelectedPort?.Split(" — ")[0] ?? "";
        return new ConnectionConfig
        {
            Kind = IsSerial ? ConnectionKind.Serial : IsTcpClient ? ConnectionKind.TcpClient : ConnectionKind.TcpServer,
            PortName = portName,
            PortFriendlyName = SelectedPort?.Contains(" — ") == true ? SelectedPort.Split(" — ")[1] : "",
            BaudRate = BaudRate,
            DataBits = DataBits,
            Parity = Parity,
            StopBits = StopBits,
            FlowControl = FlowControl,
            Host = Host,
            Port = Port,
            ListenPort = ListenPort,
        };
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        Error = null;
        if (IsSerial && string.IsNullOrEmpty(SelectedPort))
        {
            Error = "No serial port found — plug in a USB adapter or pick TCP.";
            return;
        }

        var kind = StartAs switch
        {
            0 => SessionKind.Monitor,
            2 => SessionKind.Slave,
            _ => SessionKind.Master,
        };

        try
        {
            var config = BuildConfig();
            var vm = await Main.CreateFromDialogAsync(config, kind);
            SaveProfile(config, StartAs);
            Completed?.Invoke(vm);
        }
        catch (ProFeatureException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>Named save + recent list: reconnecting tomorrow is one click (PRD §6.1).</summary>
    private void SaveProfile(ConnectionConfig config, int startAs)
    {
        var settings = Main.Workbench.Settings;
        var name = config.ShortLabel();
        var existing = settings.Profiles.FirstOrDefault(p => p.Name == name && p.StartAs == startAs);
        if (existing is null)
        {
            settings.Profiles.Insert(0, new ConnectionProfile
            {
                Name = name,
                Config = config.Clone(),
                StartAs = startAs,
                LastUsedUtc = DateTime.UtcNow,
            });
            while (settings.Profiles.Count > 8) settings.Profiles.RemoveAt(settings.Profiles.Count - 1);
        }
        else
        {
            existing.Config = config.Clone();
            existing.LastUsedUtc = DateTime.UtcNow;
        }

        Main.Workbench.SettingsStore.Save();
        Main.RefreshTree();
    }

    [RelayCommand]
    private async Task RunDemoAsync()
    {
        await Main.RunDemoCommand.ExecuteAsync(null);
        Completed?.Invoke(Main.ActiveTab);
    }

    [RelayCommand]
    private void Cancel() => Completed?.Invoke(null);
}

// ─────────────────────────── Scan slaves ───────────────────────────

public partial class ScanViewModel : ObservableObject
{
    private readonly SessionViewModel _session;
    private CancellationTokenSource? _cts;

    public ScanViewModel(SessionViewModel session)
    {
        _session = session;
    }

    [ObservableProperty]
    private int _from = 1;

    [ObservableProperty]
    private int _to = 247;

    [ObservableProperty]
    private byte _probeFunction = ModbusFunction.ReadHoldingRegisters;

    public static byte[] ProbeFunctions { get; } = [0x01, 0x02, 0x03, 0x04];

    [ObservableProperty]
    private int _timeoutMs = 200;

    public static int[] TimeoutOptions { get; } = [100, 200, 500, 1000];

    [ObservableProperty]
    private bool _sweepParams;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _progressLabel = "";

    [ObservableProperty]
    private string _currentUnit = "";

    public ObservableCollection<ScanHit> Hits { get; } = new();

    public event Action<byte>? OpenAsMasterRequested;

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning)
        {
            _cts?.Cancel();
            return;
        }

        if (_session.MasterPanel is null) _session.EnsureMasterPanel();
        var engine = _session.MasterPanel!.Engine;

        Hits.Clear();
        IsRunning = true;
        _cts = new CancellationTokenSource();

        var scanner = new SlaveScanner(engine)
        {
            From = (byte)Math.Clamp(From, 1, 247),
            To = (byte)Math.Clamp(To, 1, 247),
            ProbeFunction = ProbeFunction,
            TimeoutMs = TimeoutMs,
            SweepSerialParams = SweepParams,
        };
        scanner.Found += hit => Dispatcher.UIThread.Post(() => Hits.Add(hit));
        scanner.Progress += p => Dispatcher.UIThread.Post(() =>
        {
            Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
            CurrentUnit = $"@{p.CurrentUnit}";
            ProgressLabel = $"{p.Done} / {p.Total} · {p.Elapsed:mm\\:ss} elapsed";
        });

        try
        {
            await scanner.RunAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void OpenAsMaster(ScanHit hit)
    {
        if (_session.MasterPanel is { } panel)
        {
            panel.Engine.DefaultUnit = hit.Unit;
        }

        OpenAsMasterRequested?.Invoke(hit.Unit);
    }

    public void Stop() => _cts?.Cancel();
}

// ─────────────────────────── Point-map import review ───────────────────────────

public partial class ImportReviewViewModel : ObservableObject
{
    private readonly SessionViewModel _session;

    public ImportReviewViewModel(SessionViewModel session)
    {
        _session = session;
        ProfileFileName = "device-profile.json";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows), nameof(ShowPasteStage))]
    private bool _extracted;

    public bool HasRows => Extracted && Rows.Count > 0;

    public bool ShowPasteStage => !Extracted;

    [ObservableProperty]
    private string _pasteText = "";

    [ObservableProperty]
    private bool _isExtracting;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBase1))]
    [NotifyPropertyChangedFor(nameof(IsBase0))]
    private AddressBase _addressBase = AddressBase.Plc1Based;

    public bool IsBase1
    {
        get => AddressBase == AddressBase.Plc1Based;
        set { if (value) AddressBase = AddressBase.Plc1Based; }
    }

    public bool IsBase0
    {
        get => AddressBase == AddressBase.Protocol0Based;
        set { if (value) AddressBase = AddressBase.Protocol0Based; }
    }

    partial void OnAddressBaseChanged(AddressBase value) => ReResolve();

    [ObservableProperty]
    private string _aiSuggestion = "";

    [ObservableProperty]
    private bool _saveProfile = true;

    [ObservableProperty]
    private string _profileFileName;

    public ObservableCollection<PointRowViewModel> Rows { get; } = new();

    public string SummaryLabel
    {
        get
        {
            int total = Rows.Count;
            int ready = Rows.Count(r => r.Row.Status == PointStatus.Ready);
            int warn = total - ready;
            return $"{total} points extracted · {ready} ready · {warn} need review";
        }
    }

    public int SelectedCount => Rows.Count(r => r.IsSelected && r.Row.Status != PointStatus.Invalid);

    public string ImportButtonLabel => $"Import {SelectedCount} points";

    public string SegmentNote
    {
        get
        {
            var tasks = PointMapReview.BuildPollTasks(Rows.Where(r => r.IsSelected).Select(r => r.Row),
                _session.MasterPanel?.Engine.DefaultUnit ?? 1);
            return $"Creates {tasks.Count} segmented poll task{(tasks.Count == 1 ? "" : "s")} · ≤125 registers each";
        }
    }

    public string QuotaLabel
    {
        get
        {
            var q = _session.Main.Workbench.Settings.AiQuota;
            return $"{q.ExtractionsLeft} / {q.ExtractionsLimit}";
        }
    }

    /// <summary>Manual-page screenshot pasted from the clipboard (vision path via gateway).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage), nameof(ImageInfo))]
    private byte[]? _imageBytes;

    public bool HasImage => ImageBytes is not null;

    public string ImageInfo => ImageBytes is { } b ? $"Image attached · {b.Length / 1024.0:0.#} KB" : "";

    public void SetImage(byte[]? bytes) => ImageBytes = bytes;

    [RelayCommand]
    private async Task ExtractAsync()
    {
        if (string.IsNullOrWhiteSpace(PasteText) && ImageBytes is null)
        {
            Error = "Paste a register table (text) or a manual screenshot first.";
            return;
        }

        var workbench = _session.Main.Workbench;
        if (!await AiConsent.EnsureAsync(workbench, _session.Main.DialogHost)) return;
        if (workbench.Settings.AiQuota.ExtractionsLeft <= 0)
        {
            Error = "Extraction quota exhausted — subscribe for 30 / month.";
            return;
        }

        Error = null;
        IsExtracting = true;
        try
        {
            var extraction = await workbench.AiClient.ExtractPointMapAsync(
                string.IsNullOrWhiteSpace(PasteText) ? null : PasteText, ImageBytes);
            if (extraction.Error is { } err)
            {
                Error = err;
                return;
            }

            // Success only consumes the (expensive) extraction credit.
            workbench.TryConsumeExtraction();

            Rows.Clear();
            foreach (var row in extraction.Rows)
            {
                Rows.Add(new PointRowViewModel(row, this));
            }

            AddressBase = extraction.SuggestedBase;
            AiSuggestion = extraction.SuggestedBase == AddressBase.Plc1Based ? "AI suggests: 4xxxx" : "AI suggests: 0-based";
            ReResolve();
            Extracted = true;
        }
        finally
        {
            IsExtracting = false;
            OnPropertyChanged(nameof(QuotaLabel));
        }
    }

    public void ReResolve()
    {
        PointMapReview.Resolve(Rows.Select(r => r.Row).ToList(), AddressBase);
        foreach (var r in Rows) r.Refresh();
        OnPropertyChanged(nameof(SummaryLabel));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(ImportButtonLabel));
        OnPropertyChanged(nameof(SegmentNote));
    }

    public event Action<bool>? Completed;

    [RelayCommand]
    private void Import()
    {
        _session.EnsureMasterPanel();
        var panel = _session.MasterPanel!;
        var selectedRows = Rows.Where(r => r.IsSelected && r.Row.Status != PointStatus.Invalid).Select(r => r.Row).ToList();

        foreach (var row in selectedRows)
        {
            panel.Engine.Map.AddTag(row.ToTag());
        }

        foreach (var task in PointMapReview.BuildPollTasks(selectedRows, panel.Engine.DefaultUnit))
        {
            panel.Scheduler.Add(task);
        }

        if (SaveProfile)
        {
            var profile = new DeviceProfile
            {
                Device = Path.GetFileNameWithoutExtension(ProfileFileName),
                Source = "AI point-map import",
                CreatedUtc = DateTime.UtcNow,
                Points = selectedRows.Select(r => new DeviceProfilePoint
                {
                    Name = r.Name,
                    Area = r.Area,
                    Address = r.ProtocolAddress,
                    Type = r.DataType,
                    Order = r.WordOrder,
                    Scale = r.Scale,
                    Offset = r.Offset,
                    Unit = r.Unit,
                    Writable = r.Writable,
                    Notes = r.Notes,
                }).ToList(),
            };
            _session.Main.Workbench.SettingsStore.SaveDeviceProfile(profile,
                string.IsNullOrWhiteSpace(ProfileFileName) ? "device-profile.json" : ProfileFileName);
        }

        Completed?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => Completed?.Invoke(false);
}

public partial class PointRowViewModel : ObservableObject
{
    private readonly ImportReviewViewModel _owner;

    public PointRowViewModel(PointRow row, ImportReviewViewModel owner)
    {
        Row = row;
        _owner = owner;
        _isSelected = row.Selected;
        _name = row.Name;
        _rawAddress = row.RawAddress;
    }

    public PointRow Row { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        Row.Selected = value;
        _owner.ReResolve();
    }

    [ObservableProperty]
    private string _name;

    partial void OnNameChanged(string value) => Row.Name = value;

    [ObservableProperty]
    private string _rawAddress;

    partial void OnRawAddressChanged(string value)
    {
        Row.RawAddress = value.Trim();
        _owner.ReResolve();
    }

    public string TypeLabel => Row.DataType.Label();

    public string OrderLabel => Row.RegisterCount > 1 ? Row.WordOrder.Label() : "—";

    public string ScaleLabel => Math.Abs(Row.Scale - 1) > double.Epsilon
        ? $"×{Row.Scale.ToString("0.####", CultureInfo.InvariantCulture)}"
        : "—";

    public string Unit => string.IsNullOrEmpty(Row.Unit) ? "—" : Row.Unit;

    public string RwLabel => Row.Writable ? "RW" : "R";

    public bool IsWarning => Row.Status == PointStatus.Warning;

    public bool IsInvalid => Row.Status == PointStatus.Invalid;

    public bool IsReady => Row.Status == PointStatus.Ready;

    public string StatusLabel => Row.Status switch
    {
        PointStatus.Ready => "✓ ready",
        PointStatus.Warning => $"⚠ {Row.StatusNote}",
        _ => $"⚠ {Row.StatusNote}",
    };

    public void Refresh()
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(IsInvalid));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(OrderLabel));
        OnPropertyChanged(nameof(ScaleLabel));
    }
}

// ─────────────────────────── Settings ───────────────────────────

public partial class SettingsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;

    public SettingsViewModel(MainWindowViewModel main)
    {
        _main = main;
    }

    public Workbench Workbench => _main.Workbench;

    [ObservableProperty]
    private int _sectionIndex = 3; // License, per the design default

    public bool IsDarkTheme
    {
        get => Workbench.Settings.Theme == "Dark";
        set
        {
            App.Current?.ApplyTheme(value ? "Dark" : "Light");
            OnPropertyChanged();
            Workbench.SettingsStore.Save();
        }
    }

    public bool IsChinese
    {
        get => Workbench.Settings.Language == "zh";
        set
        {
            App.Current?.ApplyLanguage(value ? "zh" : "en");
            OnPropertyChanged();
            Workbench.SettingsStore.Save();
        }
    }

    public int BufferFrames
    {
        get => Workbench.Settings.TimelineBufferFrames;
        set
        {
            Workbench.Settings.TimelineBufferFrames = Math.Clamp(value, 1000, 2_000_000);
            Workbench.SettingsStore.Save();
            OnPropertyChanged();
        }
    }

    public bool TelemetryOptIn
    {
        get => Workbench.Settings.TelemetryOptIn;
        set
        {
            Workbench.Settings.TelemetryOptIn = value;
            Workbench.SettingsStore.Save();
            OnPropertyChanged();
        }
    }

    public bool CheckUpdates
    {
        get => Workbench.Settings.CheckUpdates;
        set
        {
            Workbench.Settings.CheckUpdates = value;
            Workbench.SettingsStore.Save();
            OnPropertyChanged();
        }
    }

    // ── license ──

    public string TierLabel => Workbench.License.Tier switch
    {
        Core.Licensing.LicenseTier.Pro => "Activated",
        Core.Licensing.LicenseTier.TrialPro => $"Pro trial · {Workbench.License.TrialDaysLeft} days left",
        _ => "Free",
    };

    public bool IsPro => Workbench.License.Tier == Core.Licensing.LicenseTier.Pro;

    public bool CanStartTrial => Workbench.License.Tier == Core.Licensing.LicenseTier.Free
                                 && Workbench.License.TrialStartedUtc is null;

    public string LicenseKeyMasked
    {
        get
        {
            var key = Workbench.License.Active?.Key ?? "";
            return key.Length > 4 ? $"FB1-••••-••••-{key[^4..]}" : key;
        }
    }

    public string LicensedTo => Workbench.License.Active?.Email ?? "";

    public string MachineCode => Core.Licensing.LicenseManager.MachineCode();

    [ObservableProperty]
    private string? _activationMessage;

    [ObservableProperty]
    private string _licenseKeyInput = "";

    [ObservableProperty]
    private bool _isActivating;

    /// <summary>
    /// Online one-click activation (PRD §6.9): exchange license key + machine
    /// code for a signed activation file at the website API, then import it —
    /// the exact same file the offline self-service page hands out.
    /// </summary>
    [RelayCommand]
    private async Task ActivateOnlineAsync()
    {
        var key = LicenseKeyInput.Trim();
        if (key.Length == 0)
        {
            ActivationMessage = "Enter the license key from your purchase email.";
            return;
        }

        IsActivating = true;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var response = await http.PostAsJsonAsync(Workbench.Settings.ActivationApiUrl,
                new { key, machine = MachineCode });
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                ActivationMessage = $"Activation server said {(int)response.StatusCode}: {(body.Length > 0 && body.Length < 200 ? body : response.ReasonPhrase)}";
                return;
            }

            var activationFile = await response.Content.ReadAsStringAsync();
            ImportActivationFile(activationFile);
        }
        catch (Exception)
        {
            ActivationMessage = "Could not reach the activation server — use offline activation below (it needs no connectivity on this machine).";
        }
        finally
        {
            IsActivating = false;
        }
    }

    [RelayCommand]
    private void StartTrial()
    {
        Workbench.License.StartTrial();
        OnPropertyChanged(nameof(TierLabel));
        OnPropertyChanged(nameof(CanStartTrial));
    }

    public (bool Ok, string Message) ImportActivationFile(string json)
    {
        var result = Workbench.License.Activate(json);
        ActivationMessage = result.Message;
        OnPropertyChanged(nameof(TierLabel));
        OnPropertyChanged(nameof(IsPro));
        OnPropertyChanged(nameof(LicenseKeyMasked));
        OnPropertyChanged(nameof(LicensedTo));
        return result;
    }

    // ── AI ──

    public string ExplainUsage => $"{Workbench.Settings.AiQuota.ExplainsUsed} / {Workbench.Settings.AiQuota.ExplainsLimit} this month";

    public double ExplainRatio => (double)Workbench.Settings.AiQuota.ExplainsUsed / Math.Max(1, Workbench.Settings.AiQuota.ExplainsLimit);

    public string ExtractUsage => $"{Workbench.Settings.AiQuota.ExtractionsUsed} / {Workbench.Settings.AiQuota.ExtractionsLimit} this month";

    public double ExtractRatio => (double)Workbench.Settings.AiQuota.ExtractionsUsed / Math.Max(1, Workbench.Settings.AiQuota.ExtractionsLimit);

    public string AppVersion => "1.0.0";

    [ObservableProperty]
    private string? _privacyPreview;

    [RelayCommand]
    private void PreviewPrivacy()
    {
        var tab = _main.ActiveTab;
        PrivacyPreview = tab is null || tab.SelectedFrames.Count == 0
            ? "Select frames in a session, then preview: protocol, connection parameters, the selected frames (hex + parsed fields), ~20 surrounding frame summaries, and your optional question. Nothing else."
            : tab.Ai.BuildContext().ToPromptText();
    }
}
