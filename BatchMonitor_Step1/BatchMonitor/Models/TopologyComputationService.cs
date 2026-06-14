using BatchMonitor.Models;

namespace BatchMonitor.Services;

/// <summary>
/// Computes a flow graph topology from the in-memory event store.
/// Infers nodes (service aggregations) and edges (data flow paths).
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

        // Phase 1: Aggregate nodes by service
        var nodesByService = new Dictionary<string, TopologyNode>();
        var chunksByService = new Dictionary<string, HashSet<string>>();

        foreach (var evt in eventStore.Values)
        {
            if (!nodesByService.TryGetValue(evt.Service, out var node))
            {
                node = new TopologyNode { Id = evt.Service, Label = evt.Service };
                nodesByService[evt.Service] = node;
                chunksByService[evt.Service] = new HashSet<string>();
            }

            // Track unique chunks
            chunksByService[evt.Service].Add(evt.ChunkId);

            // Aggregate stats
            node.ProcessedCount += evt.RecordCount;
            node.AvgDurationMs = (node.AvgDurationMs + evt.DurationMs) / 2;

            switch (evt.Status)
            {
                case "Success":
                    node.SuccessCount++;
                    break;
                case "Failed":
                    node.FailedCount++;
                    break;
                case "Skipped":
                    node.SkippedCount++;
                    break;
            }
        }

        // Update instance counts (unique ProcessIds per service)
        foreach (var service in nodesByService.Keys)
        {
            var uniqueProcessIds = eventStore.Values
                .Where(e => e.Service == service)
                .Select(e => e.ProcessId)
                .Distinct()
                .Count();
            nodesByService[service].InstanceCount = uniqueProcessIds;
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
