using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Models;

namespace NxtUI.Services;

public sealed class LogPathDiscoveryService : ILogPathDiscoveryService
{
    private readonly LogPathSettings _settings;

    // key → running or completed search task
    private readonly ConcurrentDictionary<string, Task<string?>> _cache = new();

    public event Action<string>? OnPathResolved;

    public LogPathDiscoveryService(IOptions<LogPathSettings> options)
    {
        _settings = options.Value;
    }

    public static string CacheKey(ServiceStatus svc) =>
        $"{svc.HostName}|{svc.ServiceName}|{svc.ProcessId}";

    public string? GetCachedPath(ServiceStatus svc)
    {
        if (_cache.TryGetValue(CacheKey(svc), out var task) && task.IsCompletedSuccessfully)
            return task.Result;
        return null;
    }

    public bool IsSearching(ServiceStatus svc) =>
        _cache.TryGetValue(CacheKey(svc), out var task) && !task.IsCompleted;

    public void EnsureDiscovering(ServiceStatus svc, string env)
    {
        var key = CacheKey(svc);
        _cache.GetOrAdd(key, _ => RunSearchAsync(svc, env, key));
    }

    public async Task<string?> FindNowAsync(ServiceStatus svc, string env)
    {
        var key = CacheKey(svc);

        // Reuse any in-progress search
        if (_cache.TryGetValue(key, out var existing) && !existing.IsCompleted)
            return await existing;

        // Start a fresh search (replaces a completed null result from a previous attempt)
        var task = RunSearchAsync(svc, env, key);
        _cache[key] = task;
        return await task;
    }

    // ── Internal ─────────────────────────────────────────────────────────────

    private async Task<string?> RunSearchAsync(ServiceStatus svc, string env, string key)
    {
        var result = await Task.Run(() => SearchSync(svc, env));
        if (result is not null)
            OnPathResolved?.Invoke(key);
        return result;
    }

    private string? SearchSync(ServiceStatus svc, string env)
    {
        foreach (var template in _settings.Templates)
        {
            var expanded = ExpandTemplate(template, svc, env);
            var resolved = ResolveWildcard(expanded);
            if (resolved is not null) return resolved;
        }
        return null;
    }

    private static string ExpandTemplate(string template, ServiceStatus svc, string env)
    {
        // Replace {date-1} before {date} to avoid partial match
        return template
            .Replace("{date-1}", svc.CreatedDateTime.AddDays(-1).ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}",   svc.CreatedDateTime.ToString("yyyy-MM-dd"),              StringComparison.OrdinalIgnoreCase)
            .Replace("{server}", svc.HostName,                StringComparison.OrdinalIgnoreCase)
            .Replace("{service}", svc.ServiceName,            StringComparison.OrdinalIgnoreCase)
            .Replace("{pid}",    svc.ProcessId.ToString(),    StringComparison.OrdinalIgnoreCase)
            .Replace("{env}",    env,                         StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a path that may contain * wildcards in one or more segments.
    /// Recursively expands each wildcard segment via Directory.GetDirectories.
    /// </summary>
    private static string? ResolveWildcard(string path)
    {
        if (!path.Contains('*'))
            return Directory.Exists(path) ? path : null;

        var starIdx   = path.IndexOf('*');
        var slashPrev = path.LastIndexOf('\\', starIdx);
        if (slashPrev < 0) return null;

        var basePath  = path[..slashPrev];
        var slashNext = path.IndexOf('\\', starIdx);

        string segment, remainder;
        if (slashNext < 0)
        {
            segment   = path[(slashPrev + 1)..];
            remainder = string.Empty;
        }
        else
        {
            segment   = path[(slashPrev + 1)..slashNext];
            remainder = path[slashNext..]; // includes leading backslash
        }

        if (!Directory.Exists(basePath)) return null;

        string[] matches;
        try { matches = Directory.GetDirectories(basePath, segment); }
        catch { return null; }

        foreach (var match in matches)
        {
            var candidate = string.IsNullOrEmpty(remainder) ? match : match + remainder;
            var result = ResolveWildcard(candidate);
            if (result is not null) return result;
        }
        return null;
    }
}
