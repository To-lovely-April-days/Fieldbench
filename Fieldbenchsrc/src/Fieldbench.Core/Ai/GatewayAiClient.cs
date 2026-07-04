using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Fieldbench.Core.Ai;

/// <summary>
/// Thin client for the Cloudflare Workers gateway (license auth → quota →
/// model forward; nothing stored server-side). Falls back to the offline
/// engine when unreachable so tests and demos never require network.
/// </summary>
public sealed class GatewayAiClient : IAiClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly OfflineAiEngine _fallback = new();

    public GatewayAiClient(string baseUrl, string? licenseKey)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrEmpty(licenseKey))
        {
            _http.DefaultRequestHeaders.Add("X-License-Key", licenseKey);
        }
    }

    public bool IsOnline { get; private set; } = true;

    public bool FallbackToOffline { get; set; } = true;

    public async IAsyncEnumerable<AiChunk> ExplainAsync(ExplainContext context, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Stream? stream = null;
        try
        {
            var response = await _http.PostAsJsonAsync("explain", new { prompt = context.ToPromptText() }, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            IsOnline = true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            IsOnline = false;
        }

        if (stream is null)
        {
            if (!FallbackToOffline) yield break;
            await foreach (var chunk in _fallback.ExplainAsync(context, ct).ConfigureAwait(false))
            {
                yield return chunk;
            }

            yield break;
        }

        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            AiChunk? chunk = null;
            try
            {
                chunk = JsonSerializer.Deserialize<AiChunk>(line, JsonOpts);
            }
            catch (JsonException)
            {
                // Non-JSON line (SSE keep-alive) — skip.
            }

            if (chunk is not null) yield return chunk;
        }

        yield return new AiChunk(Done: true);
    }

    public async Task<PointMapExtraction> ExtractPointMapAsync(string? tableText, byte[]? image, CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                text = tableText,
                image = image is null ? null : Convert.ToBase64String(image),
            };
            var response = await _http.PostAsJsonAsync("extract", payload, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var extraction = await response.Content.ReadFromJsonAsync<PointMapExtraction>(JsonOpts, ct).ConfigureAwait(false);
            IsOnline = true;
            return extraction ?? new PointMapExtraction { Error = "Empty gateway response." };
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            IsOnline = false;
            if (FallbackToOffline)
            {
                return await _fallback.ExtractPointMapAsync(tableText, image, ct).ConfigureAwait(false);
            }

            return new PointMapExtraction { Error = "AI gateway unreachable — check your network. Core features are unaffected." };
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public void Dispose() => _http.Dispose();
}
