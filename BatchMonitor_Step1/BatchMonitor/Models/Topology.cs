namespace BatchMonitor.Models;

/// <summary>
/// Full topology snapshot: nodes (services) and edges (flow between services),
/// computed client-side from the in-memory event store.
/// </summary>
public class Topology
{
    public List<TopologyNode> Nodes { get; set; } = new();
    public List<TopologyEdge> Edges { get; set; } = new();

    /// <summary>Total unique chunkIds seen so far.</summary>
    public int TotalChunks { get; set; }

    /// <summary>Total raw events in the store.</summary>
    public int TotalEvents { get; set; }
}

/// <summary>
/// A single service-type node in the flow graph.
/// </summary>
public class TopologyNode
{
    /// <summary>Stable identifier — the service name.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display label (service type name).</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Number of distinct process instances seen for this service.</summary>
    public int InstanceCount { get; set; }

    /// <summary>Total records processed (aggregate across all instances).</summary>
    public int ProcessedCount { get; set; }

    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public int SkippedCount { get; set; }

    /// <summary>Running average duration in ms (rough, exponential-ish).</summary>
    public double AvgDurationMs { get; set; }

    /// <summary>0-1 score reflecting recent throughput/activity — drives node brightness/pulse.</summary>
    public double RecentThroughputScore { get; set; }

    /// <summary>Per-instance breakdown keyed by ProcessId.</summary>
    public Dictionary<string, InstanceStats> InstanceBreakdown { get; set; } = new();
}

/// <summary>
/// Per-process-instance event counters for a <see cref="TopologyNode"/>.
/// A plain class (not a ValueTuple) so it serializes cleanly to JSON for
/// the D3 flow graph's hover tooltip.
/// </summary>
public class InstanceStats
{
    public int EventCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }

    public InstanceStats() { }

    public InstanceStats(int eventCount, int successCount, int failureCount)
    {
        EventCount = eventCount;
        SuccessCount = successCount;
        FailureCount = failureCount;
    }
}

/// <summary>
/// A directed edge representing message handoff from one service to another.
/// </summary>
public class TopologyEdge
{
    public string Source { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;

    /// <summary>Total messages observed flowing along this edge.</summary>
    public int MessageCount { get; set; }

    /// <summary>Estimated number of chunks "in flight" between source and target.</summary>
    public int PendingEstimate { get; set; }
}
