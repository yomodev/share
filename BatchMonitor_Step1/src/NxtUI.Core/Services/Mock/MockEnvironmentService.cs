using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Core.Models;

namespace NxtUI.Core.Services.Mock;

public sealed class MockEnvironmentService : IEnvironmentService
{
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
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _servers;

    public MockEnvironmentService(IOptions<AppSettings> settings)
    {
        var envs = settings.Value.Environments;

        _environments = envs.Select(e =>
        {
            var (bg, text) = PickPair(e.Id);
            return new EnvironmentInfo
            {
                Id = e.Id,
                Label = string.IsNullOrWhiteSpace(e.Label) ? e.Id : e.Label,
                Tier = e.Tier,
                BadgeColor = bg,
                BadgeTextColor = text,
            };
        }).ToList();

        _servers = envs.ToDictionary(
            e => e.Id,
            e => (IReadOnlyList<string>)e.Servers.AsReadOnly(),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<EnvironmentInfo> GetAll() => _environments;
    public EnvironmentInfo? GetById(string id) => _environments.FirstOrDefault(e =>
        e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    public IReadOnlyList<string> GetServers(string environmentId) =>
        _servers.TryGetValue(environmentId, out var list) ? list : [];

    private static (string Bg, string Text) PickPair(string id)
    {
        var hash = (uint)id.ToUpperInvariant().Aggregate(0, (acc, c) => acc * 31 + c);
        return Palette[hash % (uint)Palette.Length];
    }
}
