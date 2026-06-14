namespace BatchMonitor.Models;

/// <summary>
/// Represents a single service node in the flow graph.
/// Inferred from events: aggregates by Service name.
/// </summary>
public class TopologyNode
{
    /// <summary>Unique node ID (e.g., "DataProcessor", "Transformer").</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display label (service name).</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Number of unique instances (distinct ProcessIds) for this service.</summary>
    public int InstanceCount { get; set; }

    /// <summary>Total records processed by this service.</summary>
    public int ProcessedCount { get; set; }

    /// <summary>Number of events with Status="Success".</summary>
    public int SuccessCount { get; set; }

    /// <summary>Number of events with Status="Failed".</summary>
    public int FailedCount { get; set; }

    /// <summary>Number of events with Status="Skipped".</summary>
    public int SkippedCount { get; set; }

    /// <summary>Average duration of operations in ms.</summary>
    public long AvgDurationMs { get; set; }

    /// <summary>
    /// Per-instance breakdown: maps ProcessId to (event count, success count, failure count).
    /// Used for hover tooltips showing instance-level details.
    /// </summary>
    public Dictionary<string, (int EventCount, int SuccessCount, int FailureCount)> InstanceBreakdown { get; set; } = new();

    /// <summary>
    /// Recent throughput score (0-1 scale).
    /// Reflects the intensity of processing activity for this node.
    /// Used to determine pulse frequency and animation intensity.
    /// </summary>
    public double RecentThroughputScore { get; set; } = 0;
}

/// <summary>
/// Represents a directed edge in the flow graph (from one service to another).
/// Inferred from event sequence: if Service A's events precede Service B's, there is an edge A→B.
/// </summary>
public class TopologyEdge
{
    /// <summary>Source service ID.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Target service ID.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Number of unique chunks (records) flowing from source to target.</summary>
    public int MessageCount { get; set; }

    /// <summary>Estimated pending messages (chunks seen at source but not yet at target).</summary>
    public int PendingEstimate { get; set; }
}

/// <summary>
/// Complete topology snapshot: nodes and edges inferred from event store.
/// </summary>
public class Topology
{
    /// <summary>List of service nodes.</summary>
    public List<TopologyNode> Nodes { get; set; } = new();

    /// <summary>List of directed edges.</summary>
    public List<TopologyEdge> Edges { get; set; } = new();

    /// <summary>Timestamp when this topology was computed.</summary>
    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Total unique chunks across all services.</summary>
    public int TotalChunks { get; set; }

    /// <summary>Total events in the underlying store.</summary>
    public int TotalEvents { get; set; }
}
