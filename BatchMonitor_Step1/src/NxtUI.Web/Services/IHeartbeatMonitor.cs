using NxtUI.Core.Models;

namespace NxtUI.Web.Services;

/// <summary>
/// Server-side heartbeat cache. A single instance polls MongoDB (via IHeartbeatService)
/// for each subscribed environment; polling stops and memory is released after
/// <c>IdleReleaseMinutes</c> of no subscribers.
/// </summary>
public interface IHeartbeatMonitor
{
    /// <summary>
    /// Register interest in <paramref name="env"/>. Disposes releases the subscription.
    /// Polling starts on the first subscriber; an immediate poll is triggered so the
    /// cache is warm before the first <see cref="OnServicesUpdated"/> event fires.
    /// </summary>
    IDisposable Subscribe(string env);

    /// <summary>
    /// Returns the most recently fetched service list for <paramref name="env"/>,
    /// or <c>null</c> if no data has been received yet.
    /// Always returns from an in-memory cache — never blocks.
    /// </summary>
    IReadOnlyList<ServiceStatus>? GetServices(string env);

    /// <summary>Fired (on a thread-pool thread) after every successful poll for the given env.</summary>
    event Action<string>? OnServicesUpdated;
}
