using BatchMonitor.Models;

namespace BatchMonitor.Services;

/// <summary>
/// Computes a flow graph topology from the in-memory event store.
/// Infers service nodes with pipeline rows, and pipeline-to-pipeline edges,
/// per design doc §8.2.
/// </summary>
public class TopologyComputationService
{
    /// <summary>
    /// Recent-activity window used to decide whether a pipeline is "Active"
    /// (priority 2) vs merely "InProgress" (priority 3). A pipeline counts
    /// as Active if it has had a finish event within this window of the
    /// latest known event time.
    /// </summary>
    private static readonly TimeSpan RecentActivityWindow = TimeSpan.FromSeconds(15);

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

        var events = eventStore.Values.ToList();
        var latestEventTime = events.Max(e => e.Finish ?? e.Start);

        // ── Phase 1: group events by (service, pipeline) ────────────────
        var byServicePipeline = events
            .GroupBy(e => (e.Service, e.Pipeline))
            .ToDictionary(g => g.Key, g => g.ToList());

        var nodesByService = new Dictionary<string, TopologyNode>();
        var pipelineRowsByKey = new Dictionary<(string Service, string Pipeline), PipelineRow>();

        foreach (var ((service, pipeline), pipelineEvents) in byServicePipeline)
        {
            if (!nodesByService.TryGetValue(service, out var node))
            {
                node = new TopologyNode { Id = service, Label = service };
                nodesByService[service] = node;
            }

            var row = BuildPipelineRow(pipeline, pipelineEvents, latestEventTime);
            node.Pipelines.Add(row);
            pipelineRowsByKey[(service, pipeline)] = row;
        }

        // ── Phase 2: per-node rollups (instance count, header state) ────
        foreach (var node in nodesByService.Values)
        {
            node.Pipelines = node.Pipelines.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

            node.InstanceCount = node.Pipelines
                .SelectMany(p => p.Instances)
                .Select(i => $"{i.Server}:{i.ProcessId}")
                .Distinct()
                .Count();

            node.HeaderState = node.Pipelines.Count == 0
                ? PipelineState.NotStarted
                : node.Pipelines.Max(p => p.State); // enum ordered so highest = highest priority
        }

        topology.Nodes = nodesByService.Values.OrderBy(n => n.Label, StringComparer.OrdinalIgnoreCase).ToList();

        var allChunkIds = events.Select(e => e.ChunkId).Distinct().ToHashSet();
        topology.TotalChunks = allChunkIds.Count;
        topology.TotalDone = events.Count(e => e.IsDone);
        topology.TotalInProgress = events.Count(e => !e.IsDone);

        // ── Phase 3: infer pipeline-to-pipeline edges from chunk flow ────
        // For each chunk, order the (service, pipeline) hops it visited by
        // start time. Consecutive distinct hops become directed edges.
        var eventsByChunk = events
            .GroupBy(e => e.ChunkId)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Start).ToList());

        var edgesByKey = new Dictionary<(string, string, string, string), TopologyEdge>();

        // Track, per (service,pipeline), the set of chunkIds that have a
        // *finished* event there — used for the waiting estimate below.
        var doneChunksByPipeline = byServicePipeline.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Where(e => e.IsDone).Select(e => e.ChunkId).ToHashSet());

        var anyEventChunksByPipeline = byServicePipeline.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(e => e.ChunkId).ToHashSet());

        foreach (var chunkEvents in eventsByChunk.Values)
        {
            (string Service, string Pipeline)? prevHop = null;

            foreach (var evt in chunkEvents)
            {
                var hop = (evt.Service, evt.Pipeline);

                if (prevHop.HasValue && prevHop.Value != hop)
                {
                    var key = (prevHop.Value.Service, prevHop.Value.Pipeline, hop.Service, hop.Pipeline);
                    if (!edgesByKey.TryGetValue(key, out var edge))
                    {
                        edge = new TopologyEdge
                        {
                            Source = prevHop.Value.Service,
                            SourcePipeline = prevHop.Value.Pipeline,
                            Target = hop.Service,
                            TargetPipeline = hop.Pipeline,
                        };
                        edgesByKey[key] = edge;
                    }
                    edge.DoneCount++;
                }

                prevHop = hop;
            }
        }

        // ── Phase 4: waiting estimate per edge ───────────────────────────
        // waiting ≈ count of chunkIds where source pipeline has finish set
        // but target pipeline has no event yet (§8.2).
        foreach (var edge in edgesByKey.Values)
        {
            var sourceKey = (edge.Source, edge.SourcePipeline);
            var targetKey = (edge.Target, edge.TargetPipeline);

            var sourceDone = doneChunksByPipeline.GetValueOrDefault(sourceKey, new HashSet<string>());
            var targetSeen = anyEventChunksByPipeline.GetValueOrDefault(targetKey, new HashSet<string>());

            edge.WaitingEstimate = sourceDone.Count(c => !targetSeen.Contains(c));

            // Edge colour inherits the source pipeline row's state.
            edge.State = pipelineRowsByKey.TryGetValue(sourceKey, out var sourceRow)
                ? sourceRow.State
                : PipelineState.NotStarted;
        }

        topology.Edges = edgesByKey.Values
            .OrderBy(e => e.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.SourcePipeline, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Target, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.TargetPipeline, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // ── Phase 5: overall estimated progress (§8.3) ──────────────────
        // Denominator: max hop count observed across chunks (i.e. number of
        // distinct (service,pipeline) nodes in the longest observed chain)
        // × total unique chunkIds seen.
        var maxHops = eventsByChunk.Values.Count == 0
            ? 0
            : eventsByChunk.Values.Max(chunkEvents =>
                chunkEvents.Select(e => (e.Service, e.Pipeline)).Distinct().Count());

        var denominator = (double)maxHops * allChunkIds.Count;
        var numerator = (double)events.Count(e => e.IsDone);

        topology.EstimatedProgress = denominator > 0
            ? Math.Clamp(numerator / denominator, 0.0, 1.0)
            : 0.0;

        return topology;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static PipelineRow BuildPipelineRow(string pipeline, List<PerformanceEvent> pipelineEvents, DateTime latestEventTime)
    {
        var done = pipelineEvents.Count(e => e.IsDone);
        var inProgress = pipelineEvents.Count(e => !e.IsDone);
        var errors = pipelineEvents.Count(e => e.IsError);

        var row = new PipelineRow
        {
            Name = pipeline,
            Topic = InferTopicName(pipeline),
            DoneCount = done,
            InProgressCount = inProgress,
            ErrorCount = errors,
            Progress = (done + inProgress) > 0 ? (double)done / (done + inProgress) : 0.0,
        };

        // Recent throughput: fraction of "done" events that finished within
        // the recent-activity window of the latest known event time.
        var recentDone = pipelineEvents.Count(e =>
            e.Finish.HasValue && (latestEventTime - e.Finish.Value) <= RecentActivityWindow);
        row.RecentThroughputScore = done > 0
            ? Math.Clamp((double)recentDone / Math.Max(done, 1), 0.0, 1.0)
            : 0.0;

        row.State = DetermineState(row, recentDone);

        // Per-instance breakdown, sorted by server then PID per §8.2.
        row.Instances = pipelineEvents
            .GroupBy(e => (e.Server, e.ProcessId))
            .Select(g => new InstanceStats(
                server: g.Key.Server,
                processId: g.Key.ProcessId,
                doneCount: g.Count(e => e.IsDone),
                inProgressCount: g.Count(e => !e.IsDone)))
            .OrderBy(i => i.Server, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.ProcessId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return row;
    }

    /// <summary>
    /// Priority order per §8.2 (1 = highest):
    /// 1. Errored, 2. Active, 3. InProgress, 4. Idle, 5. Completed, 6. NotStarted.
    /// </summary>
    private static PipelineState DetermineState(PipelineRow row, int recentDone)
    {
        if (row.ErrorCount > 0) return PipelineState.Errored;
        if (row.InProgressCount == 0 && row.DoneCount == 0) return PipelineState.NotStarted;
        if (recentDone > 0) return PipelineState.Active;
        if (row.InProgressCount > 0) return PipelineState.InProgress;
        if (row.DoneCount > 0 && row.InProgressCount == 0) return PipelineState.Completed;
        return PipelineState.Idle;
    }

    /// <summary>
    /// Derives a plausible Kafka topic name from a pipeline name for the
    /// hover-tooltip link (§8.2). The real topic name will come from the
    /// backend once available — this is a presentation-layer placeholder.
    /// </summary>
    private static string InferTopicName(string pipeline) =>
        string.IsNullOrWhiteSpace(pipeline) ? string.Empty : pipeline.ToLowerInvariant().Replace(' ', '-') + "-events";
}
