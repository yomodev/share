namespace NxtUI.Core.Configuration;

public class UiSettings
{
    public const string SectionName = "Ui";

    /// <summary>
    /// Default debounce interval (ms) for FilterBox inputs — how long to wait after the
    /// user stops typing before firing OnFilterChanged (and the resulting Mongo/SQL/Kafka
    /// query). Individual FilterBox call sites can still override this explicitly.
    /// </summary>
    public int FilterDebounceMs { get; set; } = 500;

    /// <summary>
    /// Whether the Home page shows the Memory treemap section. Disable if memory metrics
    /// aren't wired up for an environment/deployment and the section would just be dead
    /// weight. Default: true.
    /// </summary>
    public bool ShowMemoryDashboard { get; set; } = true;

    /// <summary>
    /// Words removed from displayed service/pipeline labels to save horizontal space —
    /// used by the run-detail flow graph (node + pipeline-row labels) and the Services
    /// page card view (service name). Display only: the underlying names used for
    /// edge/topology matching and log-path discovery are untouched. Matching is
    /// case-insensitive. The special token <c>{EnvID}</c> is replaced with the current
    /// environment id before removal. After removal, leftover doubled separators are
    /// collapsed and leading/trailing <c>-</c>/<c>_</c>/spaces are trimmed.
    /// </summary>
    public string[] LabelStripWords { get; set; } = ["Pipeline", "ABC", "{EnvID}"];

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
}
