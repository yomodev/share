using NxtUI.Models;

namespace NxtUI.Core.Services;

/// <summary>
/// Resolves the set of server hostnames that are relevant for a given environment.
/// The default implementation uses the static <c>Logs:Servers</c> list from config,
/// falling back to hosts seen in live heartbeats when the list is empty.
/// Alternative implementations can resolve servers dynamically (e.g. from a CMDB
/// or by querying the heartbeat collection) without changing any callers.
/// </summary>
public interface IServerRegistry
{
    /// <summary>
    /// Returns the hostnames of servers that should be queried for the given
    /// environment. The list is ordered and deduplicated.
    /// <para>
    /// <paramref name="heartbeatHosts"/> is the set of hosts currently seen in
    /// live heartbeat data — used as a fallback when no static list is configured.
    /// Pass an empty enumerable if heartbeat data is not available.
    /// </para>
    /// </summary>
    IReadOnlyList<string> GetServers(EnvironmentInfo environment, IEnumerable<string> heartbeatHosts);
}
