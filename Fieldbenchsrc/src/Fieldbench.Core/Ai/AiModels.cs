using Fieldbench.Core.Lenses;

namespace Fieldbench.Core.Ai;

/// <summary>One ranked probable cause with a 0..1 likelihood for the meter bar.</summary>
public sealed record AiCause(string Text, double Likelihood)
{
    public string Level => Likelihood switch
    {
        >= 0.6 => "likely",
        >= 0.3 => "possible",
        _ => "low",
    };
}

public sealed record AiCheck(string Text, string Action = "→");

/// <summary>
/// Structured explanation: verdict paragraph → ranked causes → next-step checks.
/// Matches the AI panel layout in the design (fieldbench-ui-v3 right rail).
/// </summary>
public sealed class AiExplanation
{
    public string Verdict { get; set; } = "";

    public List<AiCause> Causes { get; } = new();

    public List<AiCheck> Checks { get; } = new();

    /// <summary>Optional per-field walkthrough for normal frames.</summary>
    public List<(string Field, string Meaning)> FieldNotes { get; } = new();
}

/// <summary>Streaming delta for the panel's progressive render.</summary>
public sealed record AiChunk(string? VerdictDelta = null, AiCause? Cause = null, AiCheck? Check = null, (string Field, string Meaning)? FieldNote = null, bool Done = false);

/// <summary>Everything the AI sees. The privacy preview renders exactly this.</summary>
public sealed class ExplainContext
{
    public required string Protocol { get; init; }

    public required string ConnectionParams { get; init; }

    public required IReadOnlyList<Frame> SelectedFrames { get; init; }

    /// <summary>Summaries of N frames around the selection (default 20, configurable).</summary>
    public IReadOnlyList<string> ContextSummaries { get; init; } = Array.Empty<string>();

    public string? UserQuestion { get; init; }

    /// <summary>Recent error density, for garbage/baud diagnostics.</summary>
    public double RecentErrorRatio { get; init; }

    public string ToPromptText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Protocol: {Protocol}");
        sb.AppendLine($"Connection: {ConnectionParams}");
        sb.AppendLine($"Recent error ratio: {RecentErrorRatio:P0}");
        sb.AppendLine("Selected frames:");
        foreach (var f in SelectedFrames)
        {
            sb.AppendLine($"  [{f.Direction}] {f.HexString()} — {f.Summary} {f.StatusTag}");
            foreach (var field in f.Fields)
            {
                sb.AppendLine($"    {field.Name}: {field.Value}{(field.Detail is null ? "" : $" ({field.Detail})")}");
            }
        }

        if (ContextSummaries.Count > 0)
        {
            sb.AppendLine("Surrounding frames:");
            foreach (var s in ContextSummaries) sb.AppendLine($"  {s}");
        }

        if (!string.IsNullOrWhiteSpace(UserQuestion)) sb.AppendLine($"User question: {UserQuestion}");
        return sb.ToString();
    }
}

public sealed class AiQuota
{
    public int ExplainsUsed { get; set; }

    public int ExplainsLimit { get; set; } = 30;

    public int ExtractionsUsed { get; set; }

    public int ExtractionsLimit { get; set; } = 3;

    public bool Subscribed { get; set; }

    public int ExplainsLeft => Math.Max(0, ExplainsLimit - ExplainsUsed);

    public int ExtractionsLeft => Math.Max(0, ExtractionsLimit - ExtractionsUsed);
}

public interface IAiClient
{
    /// <summary>Streamed explanation of the selected frames.</summary>
    IAsyncEnumerable<AiChunk> ExplainAsync(ExplainContext context, CancellationToken ct = default);

    /// <summary>Extract a structured point map from pasted table text or an image.</summary>
    Task<PointMapExtraction> ExtractPointMapAsync(string? tableText, byte[]? image, CancellationToken ct = default);

    bool IsOnline { get; }
}
