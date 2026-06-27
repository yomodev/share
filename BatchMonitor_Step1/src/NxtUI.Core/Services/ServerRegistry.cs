using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Models;

namespace NxtUI.Core.Services;

/// <summary>
/// Default <see cref="IServerRegistry"/> — returns the static <c>Logs:Servers</c> list
/// from configuration when it is non-empty, otherwise falls back to the distinct set of
/// hosts currently visible in live heartbeat data.
/// </summary>
public sealed class ServerRegistry : IServerRegistry
{
    private readonly LogPathSettings _settings;

    public ServerRegistry(IOptions<LogPathSettings> settings)
    {
        _settings = settings.Value;
    }

    public IReadOnlyList<string> GetServers(EnvironmentInfo environment, IEnumerable<string> heartbeatHosts)
    {
        if (_settings.Servers.Count > 0)
            return _settings.Servers;

        return heartbeatHosts
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
