using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fieldbench.Core.Export;
using Fieldbench.Core.Master;

namespace Fieldbench.App.ViewModels;

public enum ChartWindow
{
    Sixty,
    TenMinutes,
    All,
}

/// <summary>
/// Multi-channel live chart bound to poll-task tag histories, with the
/// anomaly → timeline jump link (PRD §6.6: value wrong → which frame → why).
/// Free tier: 2 channels.
/// </summary>
public partial class ChartPanelViewModel : ObservableObject, IDisposable
{
    private readonly SessionViewModel _session;
    private readonly MasterPanelViewModel _panel;
    private readonly DispatcherTimer _redrawTimer;

    public ChartPanelViewModel(SessionViewModel session, MasterPanelViewModel panel)
    {
        _session = session;
        _panel = panel;
        _redrawTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Redraw());
        _redrawTimer.Start();
        panel.Engine.Map.TagsChanged += () => Dispatcher.UIThread.Post(SyncChannels);
        SyncChannels();
    }

    public ObservableCollection<ChartChannelViewModel> Channels { get; } = new();

    [ObservableProperty]
    private ChartWindow _window = ChartWindow.Sixty;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private long _revision;

    /// <summary>Marker for error frames within the window (red dots on the plot).</summary>
    public List<(DateTime Time, string Label, Core.Lenses.Frame Frame)> ErrorMarkers { get; } = new();

    partial void OnWindowChanged(ChartWindow value) => Redraw();

    public TimeSpan WindowSpan => Window switch
    {
        ChartWindow.Sixty => TimeSpan.FromSeconds(60),
        ChartWindow.TenMinutes => TimeSpan.FromMinutes(10),
        _ => TimeSpan.MaxValue,
    };

    private void SyncChannels()
    {
        int limit = _session.Main.Workbench.License.MaxChartChannels;
        var numericTags = _panel.Engine.Map.Tags
            .Where(t => t.DataType != Core.Modbus.RegisterDataType.Text && !t.Name.Contains("status", StringComparison.OrdinalIgnoreCase))
            .Take(Math.Min(6, limit))
            .ToList();

        Channels.Clear();
        int i = 0;
        foreach (var tag in numericTags)
        {
            Channels.Add(new ChartChannelViewModel(tag, i++));
        }

        Redraw();
    }

    private void Redraw()
    {
        if (IsPaused) return;

        foreach (var c in Channels) c.RefreshCurrent();

        ErrorMarkers.Clear();
        var cutoff = WindowSpan == TimeSpan.MaxValue ? DateTime.MinValue : DateTime.UtcNow - WindowSpan;
        foreach (var frame in _session.Frames)
        {
            if (frame.IsAbnormal && frame.TimestampUtc >= cutoff)
            {
                ErrorMarkers.Add((frame.TimestampUtc, frame.StatusTag ?? "ERR", frame));
            }
        }

        Revision++;
    }

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    [RelayCommand]
    private void JumpToFrame(Core.Lenses.Frame frame) => _session.JumpToFrame(frame);

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var csv = ChartExporter.ToCsv(Channels.Select(c => c.Tag).ToList());
        var dir = Path.Combine(_session.Main.Workbench.SettingsStore.Directory_, "exports");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"chart-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        await File.WriteAllTextAsync(path, csv);
        if (_session.Main.DialogHost is { } host)
        {
            await host.ShowMessageAsync("Export", $"Saved {path}");
        }
    }

    public void Dispose() => _redrawTimer.Stop();
}

public partial class ChartChannelViewModel : ObservableObject
{
    /// <summary>Categorical channel palette: blue, teal, purple (field colors reused deliberately).</summary>
    public static readonly string[] Palette = ["#3E6AE1", "#00B386", "#8A56D6", "#D9870A", "#1989FA", "#1FA463"];

    public ChartChannelViewModel(RegisterTag tag, int index)
    {
        Tag = tag;
        ColorHex = Palette[index % Palette.Length];
    }

    public RegisterTag Tag { get; }

    public string Name => Tag.Name;

    public string ColorHex { get; }

    public string Unit => Tag.Unit;

    [ObservableProperty]
    private string _currentValue = "—";

    public void RefreshCurrent() => CurrentValue = Tag.FormatValue();

    public IReadOnlyList<TagSample> Samples(TimeSpan window)
    {
        var all = Tag.HistorySnapshot();
        if (window == TimeSpan.MaxValue) return all;
        var cutoff = DateTime.UtcNow - window;
        int start = 0;
        while (start < all.Count && all[start].TimestampUtc < cutoff) start++;
        return start == 0 ? all : all.Skip(start).ToList();
    }
}
