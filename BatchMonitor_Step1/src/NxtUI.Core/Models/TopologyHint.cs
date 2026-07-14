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

    /// <summary>
    /// Optional per-group colour, upgrading a <see cref="ServiceHint.Group"/> cluster from
    /// the default cosmetic band (drawn for ANY shared group tag, no declaration needed) to
    /// a real bordered box in that colour. A group not listed here keeps the plain band —
    /// see docs/12_Custom_Layout_And_Nested_Runs.md §6/"Groups: cosmetic band vs. real box".
    /// </summary>
    public List<GroupHint> Groups { get; set; } = [];

    /// <summary>
    /// Per-run-type override of <c>RunsSettings.ExpandChildRunsByDefault</c> — whether this
    /// run type's immediate child runs start expanded when discovered. Null (default):
    /// inherit the app-wide setting. Set explicitly to override it either way for this run
    /// type specifically. See docs/12_Custom_Layout_And_Nested_Runs.md §7.4.
    /// </summary>
    public bool? ExpandChildrenByDefault { get; set; }

    /// <summary>
    /// Per-run-type override of <c>RunsSettings.ChildRunBoxColor</c> — the border/background
    /// accent color for this run type's own child-run boxes/cards, as a CSS color string.
    /// Null (default): inherit the app-wide setting (which itself may be null, keeping the
    /// original status-derived color). Set explicitly to override it either way for this run
    /// type specifically. See docs/12_Custom_Layout_And_Nested_Runs.md §7.4.
    /// </summary>
    public string? ChildRunBoxColor { get; set; }
}

/// <summary>Upgrades one named <see cref="ServiceHint.Group"/> cluster to a real bordered box
/// (see <see cref="TopologyVariant.Groups"/>). Always has a border — there's no separate
/// on/off flag: the border is a more saturated/opaque shade of the same <see cref="Color"/>
/// used for the box's translucent fill, so declaring a colour is what turns the box on.</summary>
public sealed class GroupHint
{
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
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

    /// <summary>
    /// Explicit position hint, ELK layout only (RunsSettings.GraphLayoutEngine = "Elk", the
    /// default) — same coordinate space ELK lays the whole graph out in, so getting exact
    /// values usually means iterating: set a rough guess, look at the result, adjust. Passed
    /// to ELK as a strong bias via its interactive layering/crossing-minimization strategies,
    /// not a hard pixel guarantee — ELK still avoids overlapping nodes and keeps edges
    /// sensible, so a pinned node can still shift somewhat from the exact requested spot.
    /// Set either or both; an unset axis is left to ELK's normal automatic layout.
    /// </summary>
    public double? PinX { get; set; }

    /// <summary>See <see cref="PinX"/>.</summary>
    public double? PinY { get; set; }

    /// <summary>
    /// "left" | "right" | "above" | "below" — soft placement preference for THIS node's
    /// successor(s) in the flow (Custom layout engine only — bm-flow-layout's own
    /// <c>placeSuccessor</c> hint). A successor node's own <see cref="Direction"/> (its
    /// self-declared <c>placement</c>) always wins over this if both are set. Ignored by the
    /// Elk engine. See docs/12_Custom_Layout_And_Nested_Runs.md §6 "direction".
    /// </summary>
    public string? Direction { get; set; }

    /// <summary>
    /// Custom layout engine only. Place this node outside the cluster of peer siblings
    /// converging on the same downstream target, pinned to the layer-edge named by
    /// <see cref="ArriveFrom"/>, instead of letting the ordinary median-based ordering
    /// interleave it among them. Meaningless without <see cref="ArriveFrom"/> also set
    /// (ignored with a warning). See docs/12_Custom_Layout_And_Nested_Runs.md §6 "external".
    /// </summary>
    public bool External { get; set; }

    /// <summary>
    /// "left" | "right" | "above" | "below" — which side of the layer an
    /// <see cref="External"/> node is pinned to; its incoming edge visually approaches from
    /// that side as a side effect of the engine's own orthogonal routing, not a separate
    /// rule. Only "above"/"below" are valid in a horizontal flow, only "left"/"right" in a
    /// vertical one (the flow axis itself is already fixed by layer) — an invalid side is
    /// dropped with a warning. See <see cref="External"/>.
    /// </summary>
    public string? ArriveFrom { get; set; }

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
