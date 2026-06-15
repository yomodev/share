using BatchMonitor.Models;

namespace BatchMonitor.Services;

/// <summary>
/// Computes a flow graph topology from the in-memory event store.
/// Infers nodes (service aggregations) and edges (data flow paths).
/// Enhanced with per-instance breakdown and throughput indicators.
/// </summary>
public class TopologyComputationService
{
    /// <summary>
    /// Computes topology from the event store snapshot.
    /// </summary>
    public Topology ComputeTopology(IReadOnlyDictionary<string, PerformanceEvent> eventStore)
    {
        if (eventStore == null || eventStore.Count == 0)
        {
            return new Topology { TotalChunks = 0, TotalEvents = 0 };
        }

        var topology = new Topology { TotalEvents = eventStore.Count };

        // Phase 1: Aggregate nodes by service with per-instance breakdown
        var nodesByService = new Dictionary<string, TopologyNode>();
        var chunksByService = new Dictionary<string, HashSet<string>>();
        var instanceStats = new Dictionary<string, Dictionary<string, InstanceStats>>();

        foreach (var evt in eventStore.Values)
        {
            if (!nodesByService.TryGetValue(evt.Service, out var node))
            {
                node = new TopologyNode { Id = evt.Service, Label = evt.Service };
                nodesByService[evt.Service] = node;
                chunksByService[evt.Service] = new HashSet<string>();
                instanceStats[evt.Service] = new Dictionary<string, InstanceStats>();
            }

            // Track unique chunks
            chunksByService[evt.Service].Add(evt.ChunkId);

            // Aggregate stats
            node.ProcessedCount += evt.RecordCount;
            node.AvgDurationMs = (node.AvgDurationMs + evt.DurationMs) / 2;

            // Track per-instance breakdown
            if (!instanceStats[evt.Service].TryGetValue(evt.ProcessId, out var stats))
            {
                stats = new InstanceStats();
                instanceStats[evt.Service][evt.ProcessId] = stats;
            }
            stats.EventCount++;

            switch (evt.Status)
            {
                case "Success":
                    node.SuccessCount++;
                    stats.SuccessCount++;
                    break;
                case "Failed":
                    node.FailedCount++;
                    stats.FailureCount++;
                    break;
                case "Skipped":
                    node.SkippedCount++;
                    break;
            }
        }

        // Update instance counts and per-instance breakdown
        foreach (var service in nodesByService.Keys)
        {
            var node = nodesByService[service];
            node.InstanceCount = instanceStats[service].Count;
            node.InstanceBreakdown = instanceStats[service]
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            // Calculate recent throughput score (0-1 scale)
            // Based on success rate and event density
            var totalEvents = node.SuccessCount + node.FailedCount + node.SkippedCount;
            if (totalEvents > 0)
            {
                var successRate = (double)node.SuccessCount / totalEvents;
                var eventDensity = Math.Min(1.0, totalEvents / 100.0); // Normalize to ~100 events
                node.RecentThroughputScore = (successRate * 0.6) + (eventDensity * 0.4);
            }
        }

        topology.Nodes = nodesByService.Values.OrderBy(n => n.Label).ToList();
        topology.TotalChunks = chunksByService.Values.Sum(set => set.Count);

        // Phase 2: Infer edges from chunk flow
        var edgesByKey = new Dictionary<string, TopologyEdge>();
        var chunkLastServiceMap = new Dictionary<string, string>();

        // Sort events by chunk, then by timestamp to trace flow
        var eventsByChunk = eventStore.Values
            .GroupBy(e => e.ChunkId)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Timestamp).ToList());

        foreach (var chunkEvents in eventsByChunk.Values)
        {
            string? prevService = null;

            foreach (var evt in chunkEvents)
            {
                if (prevService != null && prevService != evt.Service)
                {
                    var edgeKey = $"{prevService}→{evt.Service}";
                    if (!edgesByKey.TryGetValue(edgeKey, out var edge))
                    {
                        edge = new TopologyEdge
                        {
                            Source = prevService,
                            Target = evt.Service,
                            MessageCount = 0,
                            PendingEstimate = 0
                        };
                        edgesByKey[edgeKey] = edge;
                    }
                    edge.MessageCount++;
                }

                prevService = evt.Service;
                chunkLastServiceMap[evt.ChunkId] = evt.Service;
            }
        }

        // Phase 3: Estimate pending messages per edge
        // A chunk is "pending" on an edge if it has left the source but hasn't reached the target yet
        foreach (var edge in edgesByKey.Values)
        {
            var chunksAtSource = chunksByService[edge.Source].Count;
            var chunksAtTarget = chunksByService[edge.Target].Count;
            edge.PendingEstimate = Math.Max(0, chunksAtSource - chunksAtTarget);
        }

        topology.Edges = edgesByKey.Values.ToList();

        return topology;
    }
}
