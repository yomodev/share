using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NxtUI.Core.Configuration;
using NxtUI.Core.Logging;
using NxtUI.Core.Models;
using System.Collections.Concurrent;

namespace NxtUI.Core.Services;

public sealed class LogPathDiscoveryService(IOptions<FileBrowserSettings> options, ILogger<LogPathDiscoveryService> log) : ILogPathDiscoveryService
{
    // key → running or completed search task
    private readonly ConcurrentDictionary<string, Task<string?>> _cache = new();

    // key → resolved result, populated once RunSearchAsync completes. GetCachedPath reads
    // from here instead of the task above — a cache-only lookup should never touch a Task's
    // .Result even when it's known-completed (that pattern is easy to accidentally copy
    // somewhere it DOES block; a plain dictionary read can't).
    private readonly ConcurrentDictionary<string, string?> _resolved = new();

    public event Action<string>? OnPathResolved;

    public static string CacheKey(ServiceStatus svc, string env) =>
        $"{env}|{svc.HostName}|{svc.ServiceName}|{svc.ProcessId}";

    public string? GetCachedPath(ServiceStatus svc, string env) =>
        _resolved.TryGetValue(CacheKey(svc, env), out var path) ? path : null;

    public bool IsSearching(ServiceStatus svc, string env) =>
        _cache.TryGetValue(CacheKey(svc, env), out var task) && !task.IsCompleted;

    public void EnsureDiscovering(ServiceStatus svc, string env)
    {
        var key = CacheKey(svc, env);
        var added = false;
        _cache.GetOrAdd(key, _ => { added = true; return RunSearchAsync(svc, env, key); });
        if (added)
            log.LogTrace("discovery [{Env}]: starting search for {Svc}@{Host} pid={Pid}", env, svc.ServiceName, svc.HostName, svc.ProcessId);
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

    public Task<string?> FindServiceParentFolderAsync(ServiceStatus svc, string env) =>
        Task.Run(() =>
        {
            foreach (var template in options.Value.ServiceTemplates)
            {
                // Drop the path segment containing {pid} entirely — not just the PID
                // itself — so this doesn't depend on any specific instance having ever
                // logged (the parent, e.g. "{service}_{env}" for the day, is expected to
                // always exist once the service has run at all that day).
                var pidIdx = template.IndexOf("{pid}", StringComparison.OrdinalIgnoreCase);
                if (pidIdx < 0) continue;
                var slashBeforePid = template.LastIndexOf('\\', pidIdx);
                if (slashBeforePid < 0) continue;

                var parentTemplate = template[..slashBeforePid];
                var expanded = ExpandTemplate(parentTemplate, svc, env);
                var resolved = ResolveWildcard(expanded);
                if (resolved is not null) return resolved;
            }
            return null;
        });

    public void PruneStaleEntries(string env, IReadOnlySet<string> activeKeys)
    {
        var prefix = env + "|";
        var removed = 0;
        foreach (var key in _cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && !activeKeys.Contains(k)).ToList())
        {
            if (_cache.TryRemove(key, out _)) removed++;
            _resolved.TryRemove(key, out _);
        }
        if (removed > 0)
            log.LogDebug("discovery [{Env}]: pruned {Count} stale cache entr{Suffix}", env, removed, removed == 1 ? "y" : "ies");
    }

    public void ClearEnv(string env)
    {
        var prefix = env + "|";
        foreach (var key in _cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _cache.TryRemove(key, out _);
        foreach (var key in _resolved.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _resolved.TryRemove(key, out _);
    }

    // ── Internal ─────────────────────────────────────────────────────────────

    private async Task<string?> RunSearchAsync(ServiceStatus svc, string env, string key)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? result;
        try
        {
            result = await Task.Run(() => SearchSync(svc, env));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "discovery [{Env}]: search failed for {Svc}@{Host} pid={Pid} after {Ms}ms",
                env, svc.ServiceName, svc.HostName, svc.ProcessId, sw.ElapsedMilliseconds);
            throw;
        }

        _resolved[key] = result;

        if (result is not null)
        {
            log.LogTrace("discovery [{Env}]: found folder for {Svc}@{Host} -> {Path} in {Ms}ms",
                env, svc.ServiceName, svc.HostName, result, sw.ElapsedMilliseconds);
            OnPathResolved?.Invoke(key);
        }
        else
        {
            log.LogTrace("discovery [{Env}]: no folder found for {Svc}@{Host} pid={Pid} after {Ms}ms",
                env, svc.ServiceName, svc.HostName, svc.ProcessId, sw.ElapsedMilliseconds);
        }
        return result;
    }

    private string? SearchSync(ServiceStatus svc, string env)
    {
        foreach (var template in options.Value.ServiceTemplates)
        {
            var expanded = ExpandTemplate(template, svc, env);
            log.LogTrace("discovery [{Env}]: trying {Expanded}", env, expanded);
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

        var starIdx = path.IndexOf('*');
        var slashPrev = path.LastIndexOf('\\', starIdx);
        if (slashPrev < 0) return null;

        var basePath = path[..slashPrev];
        var slashNext = path.IndexOf('\\', starIdx);

        string segment, remainder;
        if (slashNext < 0)
        {
            segment = path[(slashPrev + 1)..];
            remainder = string.Empty;
        }
        else
        {
            segment = path[(slashPrev + 1)..slashNext];
            remainder = path[slashNext..]; // includes leading backslash
        }

        if (!Directory.Exists(basePath)) return null;

        string[] matches;
        try { matches = Directory.GetDirectories(basePath, segment); }
        catch { return null; }

        // Pick the most-recently-modified directory first so active log sessions
        // win over stale folders from previous runs that sort earlier alphabetically.
        matches = [.. matches.OrderByDescending(Directory.GetLastWriteTimeUtc)];

        foreach (var match in matches)
        {
            var candidate = string.IsNullOrEmpty(remainder) ? match : match + remainder;
            var result = ResolveWildcard(candidate);
            if (result is not null) return result;
        }
        return null;
    }
}
