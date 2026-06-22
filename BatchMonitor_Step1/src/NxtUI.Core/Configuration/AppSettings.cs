namespace NxtUI.Configuration;

/// <summary>
/// General application settings — bound from appsettings.json "App" section.
/// </summary>
public class AppSettings
{
    public const string SectionName = "App";

    /// <summary>Environment selected by default when the portal first loads.</summary>
    public string DefaultEnvironment { get; set; } = "DEV1";

    /// <summary>Default number of batch rows shown per page in the Batches dashboard.</summary>
    public int DefaultPageSize { get; set; } = 25;

    /// <summary>All environments known to this portal instance.</summary>
    public List<EnvironmentDefinition> Environments { get; set; } = new();
}

/// <summary>
/// Defines a single environment entry as configured in appsettings.json.
/// </summary>
public class EnvironmentDefinition
{
    /// <summary>Short identifier used in API calls and tab IDs, e.g. "UAT1".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable label shown in dropdowns, e.g. "UAT 1".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Tier name used to derive badge colour: Development | UAT | Staging | Production.</summary>
    public string Tier { get; set; } = string.Empty;
}
