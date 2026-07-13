namespace NxtUI.Configuration;

public class RunsSettings
{
    public const string SectionName = "Runs";

    /// <summary>How often the home page polls for new runs, in seconds. Default: 30.</summary>
    public int PollingIntervalSeconds { get; set; } = 30;

    /// <summary>Default look-back window. Default: 4 months.</summary>
    public int WindowMonths { get; set; } = 4;

    /// <summary>Maximum rows returned per query when the caller doesn't specify a count. Default: 100.</summary>
    public int PageSize { get; set; } = 100;

    /// <summary>Hard cap on rows returned per query, regardless of what the caller requests. Default: 1000.</summary>
    public int MaxResults { get; set; } = 1000;

    /// <summary>
    /// Filter text pre-populated in the Runs page's filter box on load. Uses the same
    /// syntax as the filter box itself (e.g. "start:&gt;=-10d"). Default: "start:&gt;=-10d".
    /// </summary>
    public string DefaultFilterText { get; set; } = "start:>=-10d";

    /// <summary>
    /// Filter text pre-populated in the Run Stats page's filter box on load — deliberately
    /// a much shorter window than the Runs page's own default, since Run Stats fans a
    /// separate request out to every selected environment. Default: "start:&gt;=-24h".
    /// </summary>
    public string RunStatsDefaultFilterText { get; set; } = "start:>=-24h";

    /// <summary>
    /// How the run-detail flow graph pins pipeline-row ports for ELK's edge routing.
    /// <c>FixedSide</c> (default): ports are pinned to a side (e.g. EAST) only — ELK can
    /// reorder them within that side to minimise edge crossings/overlaps, but an arrow
    /// no longer lands on the exact pixel row it represents. <c>FixedPos</c>: ports are
    /// pinned to the exact row they represent — arrows always connect to the right row,
    /// but ELK can't reorder them, so dense diagrams can show more overlapping edges.
    /// Any other value falls back to FixedSide.
    /// </summary>
    public string GraphPortConstraints { get; set; } = "FixedSide";

    /// <summary>
    /// How the run-detail flow graph draws edges through ELK's computed waypoints.
    /// <c>Orthogonal</c> (default): horizontal/vertical segments with softly rounded
    /// corners, matching the routing ELK actually computed (never passes through a
    /// node interior). <c>Curved</c>: a smooth spline through the same waypoints —
    /// looser/organic look, but can visually cut closer to node edges between bends.
    /// Applies regardless of <see cref="GraphPortConstraints"/>. Any other value
    /// falls back to Orthogonal.
    /// </summary>
    public string GraphEdgeStyle { get; set; } = "Orthogonal";

    /// <summary>
    /// Default flow direction for the run-detail graph when no run-type topology hint
    /// specifies one (see <c>LayoutHint.Direction</c> in docs/11_Topology_Hints.md — a
    /// hint's own direction always wins over this). <c>Horizontal</c> (default): left to
    /// right, matching typical widescreen monitors. <c>Vertical</c>: top to bottom.
    /// <c>Auto</c>: picks based on the graph panel's own aspect ratio (the old behavior,
    /// before a fixed default was introduced). Any other value falls back to Horizontal.
    /// </summary>
    public string GraphDirection { get; set; } = "Horizontal";

    /// <summary>
    /// Which layout engine computes the run-detail flow graph's node positions/edge
    /// routing. <c>Elk</c> (default): the mature, full-featured ELK.js engine (ports,
    /// multiple algorithms, battle-tested) — see docs/12_Custom_Layout_And_Nested_Runs.md.
    /// <c>Custom</c>: the in-house bm-flow-layout engine built alongside it as a Stage 1
    /// comparison (docs/12 §5) — no per-pipeline ports yet (service-to-service edges only)
    /// and a narrower feature set, but no external CDN dependency and full control over its
    /// behavior. Any other value falls back to Elk. Not currently overridable per topology
    /// hint — this is a whole-app choice, not a per-run-type one.
    /// </summary>
    public string GraphLayoutEngine { get; set; } = "Elk";

    /// <summary>
    /// How many seconds since a pipeline's last finished chunk it still counts as
    /// "recently active" (bright green/blue header and row color in the run-detail
    /// flow graph) rather than "quiet" (dim green/gray). Measured against the run's
    /// latest known event timestamp, not wall-clock time — so this applies the same
    /// way whether watching a run live or scrubbing through it in Replay. Lower it to
    /// make the graph highlight only the very latest hop; raise it if replay's
    /// timestamp-truncated snapshots make too many pipelines flicker gray. Default: 15.
    /// </summary>
    public int TopologyRecentActivityWindowSeconds { get; set; } = 15;

    /// <summary>
    /// How clicking a collapsed child-run block (docs/12_Custom_Layout_And_Nested_Runs.md
    /// §7.4) behaves. <c>InPlace</c> (default): fetches the child's own topology and expands
    /// it inline as a box within the parent's graph. <c>NewTab</c>: opens the child in its
    /// own RunDetail tab instead (the simpler original behavior). Any other value falls back
    /// to InPlace.
    /// </summary>
    public string ChildRunExpandMode { get; set; } = "InPlace";

    /// <summary>
    /// Whether a run's immediate child runs (docs/12_Custom_Layout_And_Nested_Runs.md §7.4)
    /// start expanded when first discovered, instead of collapsed. Default: <c>false</c>.
    /// Overridable per run-type via the topology hint file's own
    /// <c>expandChildrenByDefault</c> (see <c>TopologyVariant.ExpandChildrenByDefault</c>) —
    /// the hint's value always wins over this when the hint sets one. Deliberately NOT
    /// recursive: only the immediate children of the run being viewed get this treatment: a
    /// newly-expanded child's OWN children still start collapsed and need a manual click,
    /// regardless of this setting.
    /// </summary>
    public bool ExpandChildRunsByDefault { get; set; }

    /// <summary>
    /// Border/background accent color for an expanded or collapsed child-run box/card
    /// (docs/12_Custom_Layout_And_Nested_Runs.md §7.4), as a CSS color string (e.g.
    /// "#8957E5" or "rgba(137,87,229,0.8)"). Default: <c>null</c>, which keeps the original
    /// behavior of deriving the box's color from the child run's own status (Running/
    /// Completed/Failed/Unknown). Set this to give every child-run box a single distinct
    /// look regardless of status, so it reads as "this is a nested run" at a glance rather
    /// than blending in with a same-colored top-level service. Overridable per run-type via
    /// the topology hint file's own <c>childRunBoxColor</c> (see
    /// <c>TopologyVariant.ChildRunBoxColor</c>) — the hint's value always wins when set.
    /// </summary>
    public string? ChildRunBoxColor { get; set; }
}
