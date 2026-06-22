using NxtUI.Models;

namespace NxtUI.Services;

public interface ILogPathDiscoveryService
{
    /// <summary>
    /// Returns the resolved path from cache, or null if discovery hasn't completed
    /// or the path was not found.
    /// </summary>
    string? GetCachedPath(ServiceStatus svc);

    /// <summary>True while background discovery is still running for this service.</summary>
    bool IsSearching(ServiceStatus svc);

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
    /// Fires when a path is successfully resolved. Argument is the cache key
    /// so subscribers can call StateHasChanged selectively.
    /// </summary>
    event Action<string>? OnPathResolved;
}
