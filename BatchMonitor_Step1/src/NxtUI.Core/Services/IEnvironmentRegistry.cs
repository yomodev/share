using NxtUI.Models;

namespace NxtUI.Core.Services;

/// <summary>
/// Provides the ordered list of environments known to this portal instance.
/// The default implementation reads from <c>App:Environments</c> in configuration;
/// alternative implementations can override the source (e.g. from a database or
/// a remote configuration service) without changing any callers.
/// </summary>
public interface IEnvironmentRegistry
{
    /// <summary>Returns all configured environments in declaration order.</summary>
    IReadOnlyList<EnvironmentInfo> GetAll();

    /// <summary>Returns the environment with the given id, or null if not found.</summary>
    EnvironmentInfo? Find(string id);
}
