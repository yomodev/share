using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Models;
using NxtUI.Services;

namespace NxtUI.Core.Services;

/// <summary>
/// Default <see cref="IServerRegistry"/> — returns <c>Logs:Servers</c> from config when
/// non-empty; otherwise queries the heartbeat service to discover hosts for the environment.
/// </summary>
public sealed class ServerRegistry : IServerRegistry
{
    private readonly LogPathSettings  _settings;
    private readonly IHeartbeatService _heartbeat;

    public ServerRegistry(IOptions<LogPathSettings> settings, IHeartbeatService heartbeat)
    {
        _settings  = settings.Value;
        _heartbeat = heartbeat;
    }

    public async Task<IReadOnlyList<string>> GetServersAsync(EnvironmentInfo environment, CancellationToken ct = default)
    {
        if (_settings.Servers.Count > 0)
            return _settings.Servers;

        var statuses = await _heartbeat.GetServiceStatusesAsync(environment.Id, ct);
        return statuses
            .Select(s => s.HostName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
