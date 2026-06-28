namespace NxtUI.Core.Models;

/// <summary>
/// Runtime representation of an environment.
/// Badge colours are assigned by <see cref="NxtUI.Core.Services.EnvironmentRegistry"/>
/// based on the environment id — not on the tier — so every environment
/// automatically gets a stable, distinct colour.
/// </summary>
public class EnvironmentInfo
{
    public string Id           { get; init; } = string.Empty;
    public string Label        { get; init; } = string.Empty;
    public string Tier         { get; init; } = string.Empty;
    public string BadgeColor   { get; init; } = "#546E7A";
    public string BadgeTextColor { get; init; } = "#ffffff";
}
