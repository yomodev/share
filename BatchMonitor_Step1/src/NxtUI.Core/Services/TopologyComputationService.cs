using NxtUI.Core.Models;

namespace NxtUI.Core.Services;

/// <summary>
/// Computes a flow graph topology from the in-memory event store.
/// Infers service nodes with pipeline rows, and pipeline-to-pipeline edges,
/// per design doc §8.2.
/// </summary>
public class TopologyComputationService(TimeSpan? recentActivityWindow = null)
{
    /// <summary>
    /// Recent-activity window used to decide whether a pipeline is "Active"
    /// (priority 2) vs merely "InProgress" (priority 3). A pipeline counts
    /// as Active if it has had a finish event within this window of the
    /// latest known event time. Configurable via RunsSettings.TopologyRecentActivityWindowSeconds
    /// — callers should pass that value in; defaults to 15s if omitted (e.g. in tests).
    /// </summary>
    private readonly TimeSpan _recentActivityWindow = recentActivityWindow ?? TimeSpan.FromSeconds(15);

    /// <summary>
    /// Computes topology from the event store snapshot.
    /// </summary>
    /// <param name="labelFormatter">
    /// Optional display-label shortener applied to node labels and pipeline display names
    /// (see <see cref="TopologyLabelFormatter"/>). Never affects the raw <c>Id</c>/<c>Name</c>
    /// keys used for edge matching. Null = identity (no shortening).
    /// </param>
    public Topology ComputeTopology(
        IReadOnlyDictionary<string, PerformanceEvent> eventStore,
        Func<string, string>? labelFormatter = null,
        TopologyBlueprint? blueprint = null)
    {
        var format = labelFormatter ?? (s => s);

        if (eventStore == null || eventStore.Count == 0)
        {
            // Even with no events, a blueprint still renders its skeleton (declared services
            // greyed as NotStarted) so the expected flow shows from t=0.
            var empty = new Topology { TotalChunks = 0, TotalEvents = 0 };
            if (blueprint is not null) ApplyBlueprint(empty, blueprint, format);
            return empty;
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
                node = new TopologyNode { Id = service, Label = format(service), IsObserved = true };
                nodesByService[service] = node;
            }

            var row = BuildPipelineRow(pipeline, pipelineEvents, latestEventTime, format, _recentActivityWindow);
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

        var allNames = events.Select(e => e.Name).Distinct().ToHashSet();
        topology.TotalChunks = allNames.Count;
        topology.TotalDone = events.Count(e => e.IsDone);
        // Every event is either done or not — no need for a second full scan.
        topology.TotalInProgress = events.Count - topology.TotalDone;

        // ── Phase 3: infer pipeline-to-pipeline edges from chunk flow ────
        // For each chunk, order the (service, pipeline) hops it visited by
        // start time. Consecutive distinct hops become directed edges.
        var eventsByChunk = events
            .GroupBy(e => e.Name)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Start).ToList());

        var edgesByKey = new Dictionary<(string, string, string, string), TopologyEdge>();

        // Track, per (service,pipeline), the set of names that have a
        // *finished* event there — used for the waiting estimate below.
        var doneChunksByPipeline = byServicePipeline.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Where(e => e.IsDone).Select(e => e.Name).ToHashSet());

        var anyEventChunksByPipeline = byServicePipeline.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(e => e.Name).ToHashSet());

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
                            IsObserved = true,
                        };
                        edgesByKey[key] = edge;
                    }
                    edge.DoneCount++;
                }

                prevHop = hop;
            }
        }

        // ── Phase 4: waiting estimate per edge ───────────────────────────
        // waiting ≈ count of names where source pipeline has finish set
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
        // × total unique names seen.
        var maxHops = eventsByChunk.Values.Count == 0
            ? 0
            : eventsByChunk.Values.Max(chunkEvents =>
                chunkEvents.Select(e => (e.Service, e.Pipeline)).Distinct().Count());

        var denominator = (double)maxHops * allNames.Count;
        var numerator = (double)events.Count(e => e.IsDone);

        topology.EstimatedProgress = denominator > 0
            ? Math.Clamp(numerator / denominator, 0.0, 1.0)
            : 0.0;

        if (blueprint is not null) ApplyBlueprint(topology, blueprint, format);

        return topology;
    }

    // ── Blueprint merge (run-type topology hints) ──────────────────────────────
    // Advisory overlay: decorate observed nodes with hint metadata, add greyed skeleton
    // nodes/edges for declared-but-unseen services, flag observed-but-undeclared nodes, and
    // attach the layout. Runtime data always wins on state/counts — this only adds structure.
    private static void ApplyBlueprint(Topology topology, TopologyBlueprint blueprint, Func<string, string> format)
    {
        topology.Layout = blueprint.Layout;
        topology.HasBlueprint = true;
        topology.GroupColors = new Dictionary<string, string>(blueprint.GroupColors, StringComparer.OrdinalIgnoreCase);

        // Declared names/globs are matched against each node's *stripped* form (the same
        // env-agnostic display label produced by `format`), never the raw event.Service id.
        // This lets one literal declared name match the same logical service across every
        // environment even though the raw id embeds an env-specific token.
        var nodesById = topology.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var strippedSeen = new HashSet<string>(topology.Nodes.Select(n => format(n.Id)), StringComparer.OrdinalIgnoreCase);

        // 1. Decorate observed nodes with the first matching ServiceHint (declaration order).
        foreach (var node in topology.Nodes)
        {
            var hint = blueprint.Services.FirstOrDefault(s => Glob.IsMatch(format(node.Id), s.Name));
            if (hint is not null) Decorate(node, hint);
            // else: observed but undeclared → IsDeclared stays false (dashed border in the UI).
        }

        // 2. Skeleton nodes for LITERAL declared services never seen (globs have no concrete
        //    identity to draw before the service appears, so they only decorate — step 1).
        foreach (var hint in blueprint.Services)
        {
            if (hint.IsGlob || strippedSeen.Contains(hint.Name)) continue;
            var node = new TopologyNode
            {
                Id = hint.Name,
                Label = format(hint.Name),
                HeaderState = PipelineState.NotStarted,
                IsObserved = false,
            };
            Decorate(node, hint);
            topology.Nodes.Add(node);
            nodesById[node.Id] = node;
            strippedSeen.Add(hint.Name);
        }

        // 3. Declared edges: mark matching observed edges, or add greyed skeleton edges between
        //    declared endpoints that resolve to concrete nodes.
        var existing = new HashSet<string>(
            topology.Edges.Select(e => $"{e.Source}{e.Target}"), StringComparer.OrdinalIgnoreCase);

        // Endpoints are resolved against each node's stripped form too, so edges declared
        // with env-agnostic names line up with the (possibly env-suffixed) real node ids.
        var idsByStrippedForm = topology.Nodes
            .Select(n => n.Id)
            .ToLookup(id => format(id), StringComparer.OrdinalIgnoreCase);

        foreach (var (from, to) in blueprint.DeclaredEdges)
        foreach (var src in ResolveNodes(from, idsByStrippedForm))
        foreach (var dst in ResolveNodes(to, idsByStrippedForm))
        {
            if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase)) continue;
            var key = $"{src}{dst}";
            if (existing.Contains(key))
            {
                foreach (var e in topology.Edges)
                    if (string.Equals(e.Source, src, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(e.Target, dst, StringComparison.OrdinalIgnoreCase))
                        e.IsDeclared = true;
            }
            else
            {
                topology.Edges.Add(new TopologyEdge
                {
                    Source = src, Target = dst,
                    SourcePipeline = string.Empty, TargetPipeline = string.Empty,
                    State = PipelineState.NotStarted, IsDeclared = true, IsObserved = false,
                });
                existing.Add(key);
            }
        }
    }

    private static void Decorate(TopologyNode node, ServiceHint hint)
    {
        node.IsDeclared = true;
        node.Role = hint.Role;
        node.Group = hint.Group;
        node.Color = hint.Color;
        node.Order = hint.Order;
        node.Pin = hint.Pin;
        node.Direction = hint.Direction;
        node.External = hint.External;
        node.ArriveFrom = hint.ArriveFrom;
        node.Orientation = hint.Orientation;
    }

    // A declared endpoint (literal or glob) → the concrete node ids it resolves to, matched
    // against each node's stripped form (ids grouped by that form via idsByStrippedForm).
    private static IEnumerable<string> ResolveNodes(string nameOrGlob, ILookup<string, string> idsByStrippedForm)
    {
        var isGlob = nameOrGlob.Contains('*') || nameOrGlob.Contains('?');
        return isGlob
            ? idsByStrippedForm.Where(g => Glob.IsMatch(g.Key, nameOrGlob)).SelectMany(g => g)
            : idsByStrippedForm[nameOrGlob];
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static PipelineRow BuildPipelineRow(
        string pipeline, List<PerformanceEvent> pipelineEvents, DateTime latestEventTime,
        Func<string, string> format, TimeSpan recentActivityWindow)
    {
        // Single pass instead of four separate Count() scans over the same list.
        int done = 0, inProgress = 0, errors = 0, recentDone = 0;
        foreach (var e in pipelineEvents)
        {
            if (e.IsDone) done++; else inProgress++;
            if (e.IsError) errors++;
            if (e.Finish.HasValue && (latestEventTime - e.Finish.Value) <= recentActivityWindow) recentDone++;
        }

        var row = new PipelineRow
        {
            Name = pipeline,
            DisplayName = format(pipeline),
            Topic = InferTopicName(pipeline),
            DoneCount = done,
            InProgressCount = inProgress,
            ErrorCount = errors,
            Progress = (done + inProgress) > 0 ? (double)done / (done + inProgress) : 0.0,
        };

        // Recent throughput: fraction of "done" events that finished within
        // the recent-activity window of the latest known event time.
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
                inProgressCount: g.Count(e => !e.IsDone),
                errorCount: g.Count(e => e.IsError),
                lastActivity: g.Max(e => e.Finish ?? e.Start)))
            .OrderBy(i => i.Server, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.ProcessId)
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
