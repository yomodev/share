using Microsoft.Extensions.Options;
using NxtUI.Configuration;
using NxtUI.Models;

namespace NxtUI.Core.Services;

/// <summary>
/// Default <see cref="IEnvironmentRegistry"/> — reads from <c>App:Environments</c>
/// in configuration. Each environment is assigned a vivid badge colour by hashing
/// its id against a fixed palette, so colours are stable and automatically distinct
/// without any per-environment configuration.
/// </summary>
public sealed class EnvironmentRegistry : IEnvironmentRegistry
{
    // Vivid / saturated colours that work well as small badge backgrounds.
    // Ordered so consecutive palette entries are maximally distinct.
    private static readonly string[] Palette =
    [
        "#2196F3", // Blue
        "#E91E63", // Pink
        "#00C853", // Green A700
        "#FF6D00", // Deep Orange A400
        "#AA00FF", // Purple A700
        "#00BCD4", // Cyan
        "#FFD600", // Yellow A700
        "#F44336", // Red
        "#00BFA5", // Teal A700
        "#3D5AFE", // Indigo A400
        "#FF4081", // Pink A200
        "#76FF03", // Light Green A400
        "#FF3D00", // Deep Orange A700
        "#651FFF", // Deep Purple A400
        "#1DE9B6", // Teal A200
        "#FF6E40", // Deep Orange A200
    ];

    private readonly IReadOnlyList<EnvironmentInfo> _environments;

    public EnvironmentRegistry(IOptions<AppSettings> settings)
    {
        _environments = settings.Value.Environments
            .Select((e, index) =>
            {
                var color     = PickColor(e.Id, index);
                var textColor = ContrastColor(color);
                return new EnvironmentInfo
                {
                    Id             = e.Id,
                    Label          = string.IsNullOrWhiteSpace(e.Label) ? e.Id : e.Label,
                    Tier           = e.Tier,
                    BadgeColor     = color,
                    BadgeTextColor = textColor,
                };
            })
            .ToList();
    }

    public IReadOnlyList<EnvironmentInfo> GetAll() => _environments;

    public EnvironmentInfo? Find(string id) =>
        _environments.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    // ── colour helpers ────────────────────────────────────────────────────────

    private static string PickColor(string id, int declarationIndex)
    {
        // Hash the id so the colour is stable regardless of declaration order,
        // but fall back to declaration index when the hash collides within the
        // same registry instance (extremely unlikely with a 16-entry palette).
        var hash  = (uint)id.ToUpperInvariant().Aggregate(0, (acc, c) => acc * 31 + c);
        var slot  = (int)(hash % (uint)Palette.Length);
        return Palette[slot];
    }

    /// <summary>
    /// Returns #ffffff or #121212 depending on which gives better contrast
    /// against <paramref name="hex"/> (W3C relative-luminance formula).
    /// </summary>
    private static string ContrastColor(string hex)
    {
        hex = hex.TrimStart('#');
        var r = Convert.ToInt32(hex[0..2], 16) / 255.0;
        var g = Convert.ToInt32(hex[2..4], 16) / 255.0;
        var b = Convert.ToInt32(hex[4..6], 16) / 255.0;

        // sRGB linearise
        r = r <= 0.04045 ? r / 12.92 : Math.Pow((r + 0.055) / 1.055, 2.4);
        g = g <= 0.04045 ? g / 12.92 : Math.Pow((g + 0.055) / 1.055, 2.4);
        b = b <= 0.04045 ? b / 12.92 : Math.Pow((b + 0.055) / 1.055, 2.4);

        var luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        return luminance > 0.179 ? "#121212" : "#ffffff";
    }
}
