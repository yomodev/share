using NxtUI.Core.Models;

namespace NxtUI.Core.Services;

public interface ILogPathDiscoveryService
{
    /// <summary>
    /// Returns the resolved path from cache, or null if discovery hasn't completed
    /// or the path was not found. Keyed per service AND environment (the path
    /// template includes {env}, so the same service resolves differently per env).
    /// </summary>
    string? GetCachedPath(ServiceStatus svc, string env);

    /// <summary>True while background discovery is still running for this service+env.</summary>
    bool IsSearching(ServiceStatus svc, string env);

    /// <summary>
    /// Kicks off background path discovery for this service if not already started.
    /// Safe to call repeatedly — only one search per service is ever running.
    /// </summary>
    void EnsureDiscovering(ServiceStatus svc, string env);

    /// <summary>
    /// Immediately searches for the path, awaiting any in-progress search or
    /// starting a fresh one. Returns the path or null if nothing matched.
    /// </summary>
    Task<string?> FindNowAsync(ServiceStatus svc, string env);

    /// <summary>
    /// Resolves the folder one level up from the PID-specific instance folder —
    /// i.e. the same templates with the path segment containing {pid} dropped
    /// entirely, not wildcarded. For when the caller only knows the service and
    /// server (e.g. a historical run's log instance) and can't find (or doesn't
    /// need) the exact PID's folder: this parent is expected to always exist once
    /// the service has logged at all that day, independent of any specific PID.
    /// Not cached — callers already have a specific PID's result cached/failed.
    /// </summary>
    Task<string?> FindServiceParentFolderAsync(ServiceStatus svc, string env);

    /// <summary>
    /// Fires when a path is successfully resolved. Argument is the cache key
    /// so subscribers can call StateHasChanged selectively.
    /// </summary>
    event Action<string>? OnPathResolved;

    /// <summary>
    /// Removes cached entries for <paramref name="env"/> whose key isn't in
    /// <paramref name="activeKeys"/> (a service no longer reported by heartbeat).
    /// Without this the cache — keyed by (env, host, service, PID) — grows without
    /// bound for the lifetime of the process as services restart with new PIDs.
    /// </summary>
    void PruneStaleEntries(string env, IReadOnlySet<string> activeKeys);

    /// <summary>Removes every cached entry for an environment (e.g. once it's gone fully idle).</summary>
    void ClearEnv(string env);
}
