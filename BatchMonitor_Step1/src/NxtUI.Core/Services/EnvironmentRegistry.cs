using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Models;

namespace NxtUI.Core.Services;

/// <summary>
/// Default <see cref="IEnvironmentRegistry"/> — reads from <c>App:Environments</c>
/// in configuration. Each environment is assigned a badge colour by hashing its id
/// against a palette of MudBlazor CSS variable pairs, so badges adapt to the active
/// theme and new environments get a colour automatically.
/// </summary>
public sealed class EnvironmentRegistry : IEnvironmentRegistry
{
    // Each entry is (background CSS, text CSS) using MudBlazor palette variables.
    // Pairs are ordered for maximum visual distinction between consecutive entries.
    private static readonly (string Bg, string Text)[] Palette =
    [
        ("var(--mud-palette-primary)",          "var(--mud-palette-primary-text)"),
        ("var(--mud-palette-error)",            "var(--mud-palette-error-text)"),
        ("var(--mud-palette-success)",          "var(--mud-palette-success-text)"),
        ("var(--mud-palette-warning)",          "var(--mud-palette-warning-text)"),
        ("var(--mud-palette-info)",             "var(--mud-palette-info-text)"),
        ("var(--mud-palette-secondary)",        "var(--mud-palette-secondary-text)"),
        ("var(--mud-palette-tertiary)",         "var(--mud-palette-tertiary-text)"),
        ("var(--mud-palette-dark)",             "var(--mud-palette-dark-text)"),
        ("var(--mud-palette-primary-darken)",   "var(--mud-palette-primary-text)"),
        ("var(--mud-palette-error-darken)",     "var(--mud-palette-error-text)"),
        ("var(--mud-palette-success-darken)",   "var(--mud-palette-success-text)"),
        ("var(--mud-palette-warning-darken)",   "var(--mud-palette-warning-text)"),
        ("var(--mud-palette-info-darken)",      "var(--mud-palette-info-text)"),
        ("var(--mud-palette-secondary-darken)", "var(--mud-palette-secondary-text)"),
        ("var(--mud-palette-tertiary-darken)",  "var(--mud-palette-tertiary-text)"),
        ("var(--mud-palette-primary-lighten)",  "var(--mud-palette-primary-text)"),
    ];

    private readonly IReadOnlyList<EnvironmentInfo> _environments;

    public EnvironmentRegistry(IOptions<AppSettings> settings)
    {
        _environments = settings.Value.Environments
            .Select(e =>
            {
                var (bg, text) = PickPair(e.Id);
                return new EnvironmentInfo
                {
                    Id             = e.Id,
                    Label          = string.IsNullOrWhiteSpace(e.Label) ? e.Id : e.Label,
                    Tier           = e.Tier,
                    BadgeColor     = bg,
                    BadgeTextColor = text,
                };
            })
            .ToList();
    }

    public IReadOnlyList<EnvironmentInfo> GetAll() => _environments;

    public EnvironmentInfo? Find(string id) =>
        _environments.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static (string Bg, string Text) PickPair(string id)
    {
        var hash = (uint)id.ToUpperInvariant().Aggregate(0, (acc, c) => acc * 31 + c);
        return Palette[hash % (uint)Palette.Length];
    }
}
