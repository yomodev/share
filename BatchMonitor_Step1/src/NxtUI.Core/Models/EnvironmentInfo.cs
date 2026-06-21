namespace BatchMonitor.Models;

/// <summary>
/// Runtime representation of an environment with resolved badge colour.
/// Derived from <see cref="Configuration.EnvironmentDefinition"/>.
/// </summary>
public class EnvironmentInfo
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Tier { get; init; } = string.Empty;

    /// <summary>MudBlazor colour name for the environment badge.</summary>
    public string BadgeColor => Tier switch
    {
        "Development" => "#00BCD4",   // Teal/Cyan
        "UAT"         => "#FFB300",   // Amber
        "Staging"     => "#7B1FA2",   // Purple
        "Production"  => "#E64A19",   // Deep Orange
        _             => "#546E7A"    // Blue Grey
    };

    /// <summary>Text colour to use on top of the badge background.</summary>
    public string BadgeTextColor => Tier switch
    {
        "UAT" => "#1a1a1a",
        _     => "#ffffff"
    };
}
