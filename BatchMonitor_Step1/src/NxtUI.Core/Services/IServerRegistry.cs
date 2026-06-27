using NxtUI.Models;

namespace NxtUI.Core.Services;

/// <summary>
/// Resolves the set of server hostnames relevant for a given environment.
/// The default implementation prefers the static <c>Logs:Servers</c> config list
/// and falls back to live heartbeat hosts when the list is empty.
/// Alternative implementations can resolve servers dynamically (CMDB, per-environment
/// config, etc.) without changing any callers.
/// </summary>
public interface IServerRegistry
{
    /// <summary>
    /// Returns the ordered, deduplicated hostnames of servers to query for the
    /// given environment.
    /// </summary>
    Task<IReadOnlyList<string>> GetServersAsync(EnvironmentInfo environment, CancellationToken ct = default);
}
