using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fieldbench.Core.Ai;
using Fieldbench.Core.Lenses;

namespace Fieldbench.App.ViewModels;

/// <summary>
/// AI explain panel: empty → streaming → done, with trial quota footer.
/// The inline row icon and this panel are the product's main conversion spot.
/// </summary>
public partial class AiPanelViewModel : ObservableObject
{
    private readonly SessionViewModel _session;
    private CancellationTokenSource? _cts;

    public AiPanelViewModel(SessionViewModel session)
    {
        _session = session;
    }

    public ObservableCollection<AiCauseRow> Causes { get; } = new();

    public ObservableCollection<AiCheckRow> Checks { get; } = new();

    public ObservableCollection<FieldNoteViewModel> FieldNotes { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasContent))]
    private bool _hasResult;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private string _verdict = "";

    [ObservableProperty]
    private string _frameLabel = "";

    [ObservableProperty]
    private string _question = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoticeText), nameof(HasNotice))]
    private string? _notice;

    public bool HasNotice => Notice is not null;

    public string? NoticeText => Notice switch
    {
        "offline" => Loc("L.AiOffline", "AI needs a network connection — core features are unaffected."),
        "quota" => Loc("L.AiQuotaOut", "Trial quota exhausted — subscribe for 1000 explanations / month."),
        _ => null,
    };

    private static string Loc(string key, string fallback)
    {
        var app = Avalonia.Application.Current;
        return app is not null && app.TryGetResource(key, null, out var value) && value is string s ? s : fallback;
    }

    public bool IsEmpty => !HasResult;

    public bool HasContent => HasResult;

    public string QuotaLabel
    {
        get
        {
            var q = _session.Main.Workbench.Settings.AiQuota;
            return q.Subscribed ? $"{q.ExplainsLimit - q.ExplainsUsed} / {q.ExplainsLimit}" : $"{q.ExplainsLeft} / {q.ExplainsLimit}";
        }
    }

    public void OnSelectionChanged()
    {
        var selected = _session.SelectedFrames;
        FrameLabel = selected.Count switch
        {
            0 => "",
            1 => $"#{selected[0].Id:0000}",
            _ => $"{selected.Count} frames",
        };
        ExplainCommand.NotifyCanExecuteChanged();
    }

    private bool CanExplain() => _session.SelectedFrames.Count > 0 && !IsStreaming;

    [RelayCommand(CanExecute = nameof(CanExplain))]
    private async Task ExplainAsync()
    {
        var workbench = _session.Main.Workbench;
        if (!await AiConsent.EnsureAsync(workbench, _session.Main.DialogHost)) return;
        if (!workbench.TryConsumeExplain())
        {
            Notice = "quota";
            return;
        }

        Notice = null;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        Verdict = "";
        Causes.Clear();
        Checks.Clear();
        FieldNotes.Clear();
        HasResult = true;
        IsStreaming = true;
        OnPropertyChanged(nameof(QuotaLabel));

        var context = BuildContext();
        try
        {
            await foreach (var chunk in workbench.AiClient.ExplainAsync(context, ct))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (chunk.VerdictDelta is { } delta) Verdict += delta;
                    if (chunk.Cause is { } cause) Causes.Add(new AiCauseRow(Causes.Count + 1, cause));
                    if (chunk.Check is { } check) Checks.Add(new AiCheckRow((Checks.Count + 1).ToString("00"), check));
                    if (chunk.FieldNote is { } note) FieldNotes.Add(new FieldNoteViewModel(note.Field, note.Meaning));
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            Notice = "offline";
        }
        finally
        {
            IsStreaming = false;
            ExplainCommand.NotifyCanExecuteChanged();
        }
    }

    public ExplainContext BuildContext()
    {
        var frames = _session.Frames;
        var selected = _session.SelectedFrames.ToArray();
        var contextSummaries = new List<string>();
        if (selected.Length > 0)
        {
            int anchor = frames.IndexOf(selected[^1]);
            if (anchor >= 0)
            {
                int from = Math.Max(0, anchor - 10);
                int to = Math.Min(frames.Count - 1, anchor + 10);
                for (int i = from; i <= to; i++)
                {
                    var f = frames[i];
                    contextSummaries.Add($"{f.Direction} {f.AddressToken} {f.FunctionToken} {f.Summary} {f.StatusTag}".Trim());
                }
            }
        }

        int recent = Math.Min(frames.Count, 200);
        int errors = 0;
        for (int i = frames.Count - recent; i < frames.Count; i++)
        {
            if (frames[i].IsAbnormal) errors++;
        }

        return new ExplainContext
        {
            Protocol = _session.Session.Lens.DisplayName,
            ConnectionParams = $"{_session.PortLabel} · {_session.Session.Connection.Config.ParamSummary()}",
            SelectedFrames = selected,
            ContextSummaries = contextSummaries,
            UserQuestion = string.IsNullOrWhiteSpace(Question) ? null : Question,
            RecentErrorRatio = recent == 0 ? 0 : (double)errors / recent,
        };
    }

    /// <summary>Inline ✦ icon on an abnormal row: select that frame and explain.</summary>
    public async Task ExplainFrameAsync(Frame frame)
    {
        _session.OnSelectionChanged([frame]);
        if (ExplainCommand.CanExecute(null))
        {
            await ExplainAsync();
        }
    }
}

public sealed record FieldNoteViewModel(string Field, string Meaning);

public sealed record AiCauseRow(int Rank, AiCause Cause)
{
    public string Text => Cause.Text;

    public double Likelihood => Cause.Likelihood;

    public string Level => Cause.Level.ToUpperInvariant();
}

public sealed record AiCheckRow(string Number, AiCheck Check)
{
    public string Text => Check.Text;

    public string Action => Check.Action;
}
