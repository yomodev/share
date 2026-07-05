using NxtUI.Core.Services;
using NxtUI.Core.Models;
using System.Text;
using System.Text.Json;

namespace NxtUI.Web.Services;

/// <summary>
/// Loads the batch catalog from <c>wwwroot/data/batch-catalog.json</c> and
/// triggers jobs by POSTing a JSON body to the resolved endpoint URL.
/// Swap the HTTP call with a real client once the backend is available.
/// </summary>
public sealed class BatchCatalogService : IBatchCatalogService
{
    private readonly IWebHostEnvironment _env;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<BatchCatalogService> _log;

    private IReadOnlyList<BatchDefinition>? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BatchCatalogService(
        IWebHostEnvironment env,
        IHttpClientFactory http,
        ILogger<BatchCatalogService> log)
    {
        _env = env;
        _http = http;
        _log = log;
    }

    public async Task<IReadOnlyList<BatchDefinition>> GetBatchesAsync(CancellationToken ct = default)
    {
        if (_cache is not null) return _cache;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache is not null) return _cache;

            var path = Path.Combine(_env.WebRootPath, "data", "batch-catalog.json");
            await using var stream = File.OpenRead(path);
            var catalog = await JsonSerializer.DeserializeAsync<BatchCatalog>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
            _cache = catalog?.Batches ?? [];
            return _cache;
        }
        finally { _lock.Release(); }
    }

    public async Task<BatchTriggerResult> TriggerAsync(
        BatchDefinition batch,
        Dictionary<string, string> values,
        string env,
        CancellationToken ct = default)
    {
        // Build resolved flat values (param values take precedence, then built-in placeholders)
        var resolved = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = batch.Id,
            ["env"] = env,
            ["today"] = DateTime.Today.ToString("yyyy-MM-dd"),
            ["yesterday"] = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd"),
            ["month"] = DateTime.Today.Month.ToString("D2"),
            ["year"] = DateTime.Today.Year.ToString(),
        };

        var url = Substitute(batch.Endpoint.Url, resolved);
        var body = BuildBody(batch, values);

        _log.LogInformation("Triggering {Batch} → {Method} {Url}", batch.Id, batch.Endpoint.Method, url);

        try
        {
            var client = _http.CreateClient();
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(
                new HttpMethod(batch.Endpoint.Method), url)
            { Content = content };

            using var resp = await client.SendAsync(req, ct);
            var respBody = await resp.Content.ReadAsStringAsync(ct);

            if (resp.IsSuccessStatusCode)
            {
                var jobId = TryParseJobId(respBody);
                return new BatchTriggerResult(true, url, body,
                    JobId: jobId,
                    Message: $"HTTP {(int)resp.StatusCode} — job queued{(jobId is not null ? $" ({jobId})" : "")}");
            }

            return new BatchTriggerResult(false, url, body,
                Error: $"HTTP {(int)resp.StatusCode}: {respBody.Truncate(200)}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Trigger failed for {Batch}", batch.Id);
            // In demo mode there is no real backend — simulate success so the UI
            // shows the resolved URL/body without crashing.
            return new BatchTriggerResult(true, url, body,
                JobId: Guid.NewGuid().ToString("N")[..8],
                Message: $"[MOCK] Endpoint unreachable — simulated success. Would call: {batch.Endpoint.Method} {url}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Substitute(string template, Dictionary<string, string> values)
    {
        var sb = new StringBuilder(template);
        foreach (var (k, v) in values)
            sb.Replace($"{{{k}}}", v);
        return sb.ToString();
    }

    private static string BuildBody(BatchDefinition batch, Dictionary<string, string> values)
    {
        var props = new Dictionary<string, object?>();
        foreach (var p in batch.Parameters)
        {
            var raw = values.TryGetValue(p.Key, out var v) ? v : p.DefaultString();
            props[p.Key] = p.Type switch
            {
                "boolean" => raw is "true" or "True" or "1",
                "number" => double.TryParse(raw,
                                     System.Globalization.NumberStyles.Any,
                                     System.Globalization.CultureInfo.InvariantCulture,
                                     out var n) ? n : (object?)raw,
                "multiselect" => raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                    .Select(s => s.Trim()).ToArray(),
                _ => raw
            };
        }
        return JsonSerializer.Serialize(props, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string? TryParseJobId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var name in new[] { "jobId", "id", "runId", "correlationId" })
                if (doc.RootElement.TryGetProperty(name, out var el))
                    return el.GetString();
        }
        catch (JsonException) { }
        return null;
    }
}

file static class StringExtensions
{
    public static string Truncate(this string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
