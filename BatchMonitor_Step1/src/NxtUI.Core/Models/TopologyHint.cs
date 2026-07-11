using System.Text.RegularExpressions;

namespace NxtUI.Core.Models;

/// <summary>
/// Optional per-run-type topology blueprint, loaded from <c>config/topology/{runType}.json</c>.
/// Describes the services a run of this type is expected to involve, how they connect (via
/// shared publish/subscribe Targets, or explicit edges), and layout preferences. Purely
/// advisory: the runtime event stream can add services/edges the file didn't mention, and a
/// declared service that never appears just renders greyed. See docs/11_Topology_Hints.md.
/// </summary>
public sealed class TopologyHintFile
{
    public string RunType { get; set; } = string.Empty;

    /// <summary>
    /// Layout variants for this run type, tried in order. The first whose <see cref="TopologyVariant.Match"/>
    /// is satisfied by a service seen in the run wins (and is then locked). If none match,
    /// <see cref="Default"/> is used.
    /// </summary>
    public List<TopologyVariant> Variants { get; set; } = [];

    /// <summary>Fallback variant when no <see cref="Variants"/> entry matches. Optional.</summary>
    public TopologyVariant? Default { get; set; }
}

/// <summary>One layout variant: a discriminator, layout prefs, and the declared services/edges.</summary>
public sealed class TopologyVariant
{
    /// <summary>Discriminator; null on the default variant. Matched against services seen so far.</summary>
    public VariantMatch? Match { get; set; }

    public LayoutHint? Layout { get; set; }

    public List<ServiceHint> Services { get; set; } = [];

    /// <summary>Explicit service→service edges, an alternative to declaring shared publish/subscribe Targets.</summary>
    public List<EdgeHint> Edges { get; set; } = [];
}

/// <summary>Variant discriminator — currently "a service matching this glob has been seen".</summary>
public sealed class VariantMatch
{
    /// <summary>Glob (<c>*</c>/<c>?</c>) matched against every service seen in the run so far.</summary>
    public string? AnyService { get; set; }
}

/// <summary>Graph-level layout preferences (author-friendly; mapped to ELK by the graph component).</summary>
public sealed class LayoutHint
{
    /// <summary>"horizontal" (→ ELK RIGHT) or "vertical" (→ ELK DOWN). Null = auto (by aspect ratio).</summary>
    public string? Direction { get; set; }

    /// <summary>"layered" (default, fans back in) or "tree" (branching hierarchy, → ELK mrtree).</summary>
    public string? Shape { get; set; }

    /// <summary>"compact" | "normal" | "airy" — scales node/edge spacing. Null = normal.</summary>
    public string? Density { get; set; }

    /// <summary>Prefer straighter edges (ELK NETWORK_SIMPLEX node placement) over tidiest packing.</summary>
    public bool? StraightenEdges { get; set; }
}

/// <summary>Per-service declaration + node hints.</summary>
public sealed class ServiceHint
{
    /// <summary>
    /// Service name or glob. A <b>literal</b> name (no <c>*</c>/<c>?</c>) pre-renders as a
    /// skeleton node from t=0 (greyed NotStarted); a <b>glob</b> only decorates matching
    /// runtime nodes (it has no concrete identity to draw before the service appears).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>"source" | "sink" | "middle" — pins layer position (layered layout only).</summary>
    public string? Role { get; set; }

    /// <summary>Cluster label — same-group services are kept adjacent behind a shared band.</summary>
    public string? Group { get; set; }

    /// <summary>Header accent override (hex). State colour still shows via border/intensity.</summary>
    public string? Color { get; set; }

    /// <summary>Tie-break ordering within a layer (lower first).</summary>
    public int? Order { get; set; }

    /// <summary>Keep at a fixed spot across re-layouts (escape hatch; fights crossing minimisation).</summary>
    public bool Pin { get; set; }

    /// <summary>Start folded.</summary>
    public bool Collapsed { get; set; }

    /// <summary>Targets this service produces to (for edge derivation by shared token).</summary>
    public List<string> Publishes { get; set; } = [];

    /// <summary>Targets this service consumes from.</summary>
    public List<string> Subscribes { get; set; } = [];

    public bool IsGlob => Name.Contains('*') || Name.Contains('?');
}

/// <summary>An explicit declared edge between two service names (may be literal or glob).</summary>
public sealed class EdgeHint
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
}

/// <summary>Case-insensitive glob (<c>*</c>=any, <c>?</c>=one) used across topology hint matching.</summary>
public static class Glob
{
    public static bool IsMatch(string input, string pattern)
    {
        var sb = new System.Text.StringBuilder("^");
        foreach (var ch in pattern)
            sb.Append(ch switch { '*' => ".*", '?' => ".", _ => Regex.Escape(ch.ToString()) });
        sb.Append('$');
        return Regex.IsMatch(input ?? string.Empty, sb.ToString(), RegexOptions.IgnoreCase);
    }
}
