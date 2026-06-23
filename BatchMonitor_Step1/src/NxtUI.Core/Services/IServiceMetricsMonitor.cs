using NxtUI.Logging;
using NxtUI.Models;

namespace NxtUI.Services;

/// <summary>
/// Watches the memory-metrics log files of services and caches the parsed numbers,
/// but only while there is at least one subscriber for the environment. A Services
/// tab subscribes for its environment; when the last tab for an environment closes,
/// monitoring for that environment stops and its cached data is dropped.
/// </summary>
public interface IServiceMetricsMonitor
{
    /// <summary>
    /// Registers interest in an environment. Monitoring starts (if not already) and
    /// keeps running until every returned token is disposed. Ref-counted per env.
    /// </summary>
    IDisposable Subscribe(string env);

    /// <summary>Most recent parsed sample for a service in an env, or null.</summary>
    MetricsSample? GetLatest(string env, ServiceStatus svc);

    /// <summary>Bounded history of parsed samples (oldest first); empty if none.</summary>
    IReadOnlyList<MetricsSample> GetHistory(string env, ServiceStatus svc);

    /// <summary>Fires (with the env) after a poll that produced new samples.</summary>
    event Action<string>? OnMetricsUpdated;
}
