using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Logging;
using NxtUI.Core.Models;

namespace NxtUI.Core.Services;

public sealed class LogPathDiscoveryService : ILogPathDiscoveryService
{
    private readonly LogPathSettings                 _settings;
    private readonly ILogger<LogPathDiscoveryService> _log;

    // key → running or completed search task
    private readonly ConcurrentDictionary<string, Task<string?>> _cache = new();

    public event Action<string>? OnPathResolved;

    public LogPathDiscoveryService(IOptions<LogPathSettings> options, ILogger<LogPathDiscoveryService> log)
    {
        _settings = options.Value;
        _log      = log;
    }

    public static string CacheKey(ServiceStatus svc, string env) =>
        $"{env}|{svc.HostName}|{svc.ServiceName}|{svc.ProcessId}";

    public string? GetCachedPath(ServiceStatus svc, string env)
    {
        if (_cache.TryGetValue(CacheKey(svc, env), out var task) && task.IsCompletedSuccessfully)
            return task.Result;
        return null;
    }

    public bool IsSearching(ServiceStatus svc, string env) =>
        _cache.TryGetValue(CacheKey(svc, env), out var task) && !task.IsCompleted;

    public void EnsureDiscovering(ServiceStatus svc, string env)
    {
        var key = CacheKey(svc, env);
        var added = false;
        _cache.GetOrAdd(key, _ => { added = true; return RunSearchAsync(svc, env, key); });
        if (added)
            _log.LogDebug("discovery [{Env}]: starting search for {Svc}@{Host} pid={Pid}", env, svc.ServiceName, svc.HostName, svc.ProcessId);
    }

    public async Task<string?> FindNowAsync(ServiceStatus svc, string env)
    {
        var key = CacheKey(svc, env);

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
        {
            _log.LogInformation("discovery [{Env}]: found folder for {Svc}@{Host} → {Path}", env, svc.ServiceName, svc.HostName, result);
            OnPathResolved?.Invoke(key);
        }
        else
        {
            _log.LogDebug("discovery [{Env}]: no folder found for {Svc}@{Host} pid={Pid}", env, svc.ServiceName, svc.HostName, svc.ProcessId);
        }
        return result;
    }

    private string? SearchSync(ServiceStatus svc, string env)
    {
        foreach (var template in _settings.ServiceTemplates)
        {
            var expanded = ExpandTemplate(template, svc, env);
            _log.LogDebug("discovery [{Env}]: trying {Expanded}", env, expanded);
            var resolved = ResolveWildcard(expanded);
            if (resolved is not null) return resolved;
        }
        return null;
    }

    private static string ExpandTemplate(string template, ServiceStatus svc, string env) =>
        LogPathTemplate.Expand(template, svc, env);

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

        // Pick the most-recently-modified directory first so active log sessions
        // win over stale folders from previous runs that sort earlier alphabetically.
        matches = matches.OrderByDescending(Directory.GetLastWriteTimeUtc).ToArray();

        foreach (var match in matches)
        {
            var candidate = string.IsNullOrEmpty(remainder) ? match : match + remainder;
            var result = ResolveWildcard(candidate);
            if (result is not null) return result;
        }
        return null;
    }
}
