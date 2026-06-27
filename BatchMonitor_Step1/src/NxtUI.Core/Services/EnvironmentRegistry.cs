using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Models;

namespace NxtUI.Core.Services;

/// <summary>
/// Default <see cref="IEnvironmentRegistry"/> — reads from <c>App:Environments</c>
/// in configuration and projects each <see cref="EnvironmentDefinition"/> to an
/// <see cref="EnvironmentInfo"/> (which adds derived display properties such as badge colour).
/// </summary>
public sealed class EnvironmentRegistry : IEnvironmentRegistry
{
    private readonly IReadOnlyList<EnvironmentInfo> _environments;

    public EnvironmentRegistry(IOptions<AppSettings> settings)
    {
        _environments = settings.Value.Environments
            .Select(e => new EnvironmentInfo
            {
                Id    = e.Id,
                Label = string.IsNullOrWhiteSpace(e.Label) ? e.Id : e.Label,
                Tier  = e.Tier,
            })
            .ToList();
    }

    public IReadOnlyList<EnvironmentInfo> GetAll() => _environments;

    public EnvironmentInfo? Find(string id) =>
        _environments.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
