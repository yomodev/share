namespace NxtUI.Core.Models;

using System.Text.Json.Serialization;

/// <summary>
/// Full topology snapshot: nodes (services with pipeline rows) and edges
/// (pipeline-row → pipeline-row), computed client-side from the in-memory
/// event store per design doc §8.2.
/// </summary>
public class Topology
{
    public List<TopologyNode> Nodes { get; set; } = [];
    public List<TopologyEdge> Edges { get; set; } = [];

    /// <summary>
    /// Child runs triggered by this run (see <see cref="RunDetails.Children"/>), rendered as a
    /// separate cluster of collapsed blocks rather than wired into <see cref="Nodes"/>/
    /// <see cref="Edges"/> — a child run is triggered at the RUN level, not by a specific
    /// service/pipeline, so it has no natural edge endpoint in the service flow graph. See
    /// docs/12_Custom_Layout_And_Nested_Runs.md §7.4.
    /// </summary>
    public List<RunNode> ChildRuns { get; set; } = [];

    /// <summary>
    /// Layout preferences from the run-type topology hint (see <see cref="TopologyHintFile"/>),
    /// or null when no hint applies (or the matched variant didn't specify one) — the graph
    /// then auto-picks direction by aspect ratio. Not a reliable signal for whether a blueprint
    /// was applied at all (a variant can omit Layout) — see <see cref="HasBlueprint"/> for that.
    /// </summary>
    public LayoutHint? Layout { get; set; }

    /// <summary>
    /// True when a run-type topology blueprint was applied (regardless of whether it declared
    /// a Layout). Gates "undeclared" (dashed-border) styling in the UI — with no blueprint at
    /// all, EVERY node would otherwise read as "undeclared" (IsDeclared defaults to false),
    /// which is wrong: there's no plan to be off of.
    /// </summary>
    public bool HasBlueprint { get; set; }

    /// <summary>Total unique names seen so far.</summary>
    public int TotalChunks { get; set; }

    /// <summary>Total raw events in the store.</summary>
    public int TotalEvents { get; set; }

    /// <summary>Total chunks with at least one finished event (any pipeline).</summary>
    public int TotalDone { get; set; }

    /// <summary>Total chunks currently in progress (started, not yet finished) on any pipeline.</summary>
    public int TotalInProgress { get; set; }

    /// <summary>
    /// Estimated overall progress (0-1), per §8.3:
    /// count(finished chunk×service×pipeline combos) / count(expected combos).
    /// </summary>
    public double EstimatedProgress { get; set; }
}

/// <summary>
/// A single service-type node in the flow graph — a header plus one row per pipeline.
/// </summary>
public class TopologyNode
{
    /// <summary>Stable identifier — the service name.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display label (service type name).</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Number of distinct (server, processId) instances seen for this service, across all pipelines.</summary>
    public int InstanceCount { get; set; }

    /// <summary>Pipeline rows belonging to this service, in stable order.</summary>
    public List<PipelineRow> Pipelines { get; set; } = new();

    /// <summary>
    /// Header accent state, derived from the worst/most active pipeline
    /// per the priority table in §8.2.
    /// </summary>
    public PipelineState HeaderState { get; set; } = PipelineState.NotStarted;

    // ── Topology-hint decoration (null/false when no hint applies) ──────────────

    /// <summary>"source" | "sink" | "middle" from the hint — pins layer position (layered layout).</summary>
    public string? Role { get; set; }

    /// <summary>Cluster label from the hint — same-group nodes are kept adjacent behind a band.</summary>
    public string? Group { get; set; }

    /// <summary>Header accent colour override (hex) from the hint.</summary>
    public string? Color { get; set; }

    /// <summary>Tie-break ordering within a layer from the hint (lower first).</summary>
    public int? Order { get; set; }

    /// <summary>Keep at a fixed spot across re-layouts (hint escape hatch).</summary>
    public bool Pin { get; set; }

    /// <summary>True when the run-type blueprint declared this service (matched a ServiceHint).</summary>
    public bool IsDeclared { get; set; }

    /// <summary>True once at least one real event has been seen for this service.</summary>
    public bool IsObserved { get; set; }
}

/// <summary>
/// A single pipeline row within a service node.
/// </summary>
public class PipelineRow
{
    /// <summary>Pipeline name — unique per service, one input topic. This is the raw name
    /// used as the edge/topology matching key; use <see cref="DisplayName"/> for rendering.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Shortened, display-only pipeline name (filler words stripped — see
    /// TopologyLabelFormatter). Defaults to <see cref="Name"/> when no stripping applies.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Input topic name for this pipeline (derived; used for the Kafka tooltip link).</summary>
    public string Topic { get; set; } = string.Empty;

    /// <summary>Count of chunks that have finished on this pipeline (Success or Failed).</summary>
    public int DoneCount { get; set; }

    /// <summary>Count of chunks currently in progress on this pipeline (started, no finish yet).</summary>
    public int InProgressCount { get; set; }

    /// <summary>Count of chunks that errored on this pipeline.</summary>
    public int ErrorCount { get; set; }

    /// <summary>
    /// Progress estimate (0-1) for this pipeline:
    /// DoneCount / (DoneCount + InProgressCount), or 0 if nothing seen yet.
    /// </summary>
    public double Progress { get; set; }

    /// <summary>Visual state driving the left-border colour and priority rollup (§8.2).</summary>
    public PipelineState State { get; set; } = PipelineState.NotStarted;

    /// <summary>0-1 score reflecting recent throughput/activity — drives dash-flow speed on outgoing edges.</summary>
    public double RecentThroughputScore { get; set; }

    /// <summary>Per-(server, processId) instance breakdown, for the hover tooltip.</summary>
    public List<InstanceStats> Instances { get; set; } = new();
}

/// <summary>
/// Per-instance (server + PID) event counters for a <see cref="PipelineRow"/>.
/// Sorted by server then PID for the hover tooltip per §8.2.
/// </summary>
public class InstanceStats
{
    public string Server { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public int DoneCount { get; set; }
    public int InProgressCount { get; set; }

    /// <summary>Count of chunks that errored on this specific instance.</summary>
    public int ErrorCount { get; set; }

    /// <summary>
    /// Latest Finish (or Start, if still in progress) seen across this instance's events —
    /// the actual day/time this instance was active, used to resolve its log folder
    /// (which is dated) instead of guessing from the run's overall start time.
    /// </summary>
    public DateTime LastActivity { get; set; }

    public InstanceStats() { }

    public InstanceStats(string server, int processId, int doneCount, int inProgressCount, int errorCount, DateTime lastActivity)
    {
        Server = server;
        ProcessId = processId;
        DoneCount = doneCount;
        InProgressCount = inProgressCount;
        ErrorCount = errorCount;
        LastActivity = lastActivity;
    }
}

/// <summary>
/// Visual / priority state of a pipeline row, evaluated in priority order
/// for the node header accent (§8.2, priority 1 = highest).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PipelineState
{
    /// <summary>Priority 6 (lowest) — no events seen yet for this pipeline.</summary>
    NotStarted = 0,

    /// <summary>Priority 5 — all chunks seen on this pipeline have finished, none errored.</summary>
    Completed = 1,

    /// <summary>Priority 4 — no in-progress activity, but not all chunks finished (stalled/idle).</summary>
    Idle = 2,

    /// <summary>Priority 3 — chunks currently in progress.</summary>
    InProgress = 3,

    /// <summary>Priority 2 — recently active (high throughput / recent completions).</summary>
    Active = 4,

    /// <summary>Priority 1 (highest) — at least one chunk errored on this pipeline.</summary>
    Errored = 5,
}

/// <summary>
/// A directed edge representing message handoff from one pipeline row to
/// another (possibly in a different service), at port level per §8.2.
/// </summary>
public class TopologyEdge
{
    /// <summary>Source service id.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Source pipeline name within the source service.</summary>
    public string SourcePipeline { get; set; } = string.Empty;

    /// <summary>Target service id.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Target pipeline name within the target service.</summary>
    public string TargetPipeline { get; set; } = string.Empty;

    /// <summary>Total chunks observed flowing along this edge (done = both sides observed for the chunk).</summary>
    public int DoneCount { get; set; }

    /// <summary>
    /// Estimated number of chunks "in flight" between source and target:
    /// chunks where the source pipeline has finished but the target pipeline
    /// has no event yet.
    /// </summary>
    public int WaitingEstimate { get; set; }

    /// <summary>Visual state inherited from the source pipeline row (drives edge colour).</summary>
    public PipelineState State { get; set; } = PipelineState.NotStarted;

    /// <summary>True when the run-type blueprint declared this edge.</summary>
    public bool IsDeclared { get; set; }

    /// <summary>True once at least one chunk has been observed flowing along this edge.</summary>
    public bool IsObserved { get; set; }
}
