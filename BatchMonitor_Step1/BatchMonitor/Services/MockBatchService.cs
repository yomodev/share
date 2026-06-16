using BatchMonitor.Models;

namespace BatchMonitor.Services;

/// <summary>
/// Mock implementation of <see cref="IBatchService"/>.
/// Generates deterministic fake data so the UI works without a real backend.
/// Replace with <see cref="MongoBatchService"/> (Step 2 real impl) when ready.
/// </summary>
public class MockBatchService : IBatchService
{
    private static readonly string[] Types = { "FullLoad", "DeltaSync", "Reconcile", "Archive" };
    private static readonly string[] Entities = { "Customers", "Orders", "Products", "Inventory", "Pricing", "Contracts", "Shipments", "Invoices" };

    /// <summary>
    /// Each service has one or more pipelines (unique per service, one input topic each).
    /// The list order also defines the default flow order for the mock data generator.
    /// </summary>
    private static readonly (string Service, string[] Pipelines)[] ServicePipelines =
    {
        ("Ingester",    new[] { "ingest-main" }),
        ("Validator",   new[] { "validate-main", "validate-retry" }),
        ("Transformer", new[] { "transform-main" }),
        ("Enricher",    new[] { "enrich-main", "enrich-lookup" }),
        ("Loader",      new[] { "load-main" }),
    };

    private static readonly string[] Servers = { "server01", "server02" };
    private static readonly string[] ProcessIds = { "4821", "4822", "5103", "5104" };

    private readonly List<BatchSummary> _store;
    private readonly Dictionary<string, List<PerformanceEvent>> _eventsByRunId = new();
    private readonly TopologyComputationService _topologyService = new();

    public MockBatchService()
    {
        var rng = new Random(42);
        _store = Enumerable.Range(1, 200).Select(i =>
        {
            var type   = Types[i % Types.Length];
            var entity = Entities[i % Entities.Length];
            var start  = DateTime.UtcNow.AddMinutes(-(i * 7 + rng.Next(0, 5)));
            var status = i switch { 1 => BatchStatus.Running, 2 => BatchStatus.Running, 3 => BatchStatus.Failed, 7 => BatchStatus.Failed, _ => BatchStatus.Completed };
            return new BatchSummary
            {
                RunId     = $"RUN-{DateTime.UtcNow:yyyyMMdd}-{i:D3}",
                BatchName = $"{type}_{entity}",
                Type      = type,
                Status    = status,
                Start     = start,
                End       = status != BatchStatus.Running ? start.AddSeconds(rng.Next(60, 1800)) : null
            };
        })
        .OrderByDescending(b => b.Start)
        .ToList();

        // Pre-generate mock events for demo
        GenerateMockEvents(rng);
    }

    public Task<List<BatchSummary>> GetBatchesAsync(
        string env, DateTime before, int count,
        BatchFilter? filter = null, CancellationToken ct = default)
    {
        var query = _store.Where(b => b.Start < before);

        if (filter is not null && !filter.IsEmpty)
        {
            if (!string.IsNullOrWhiteSpace(filter.SearchText))
            {
                var text = filter.SearchText.Trim().ToLowerInvariant();
                query = query.Where(b =>
                    b.RunId.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    b.BatchName.Contains(text, StringComparison.OrdinalIgnoreCase));
            }
            if (filter.Statuses?.Count > 0)
                query = query.Where(b => filter.Statuses.Contains(b.Status));
            if (filter.Types?.Count > 0)
                query = query.Where(b => filter.Types.Contains(b.Type, StringComparer.OrdinalIgnoreCase));
        }

        return Task.FromResult(query.Take(count).ToList());
    }

    public Task<bool> CancelBatchAsync(string env, string runId, CancellationToken ct = default)
    {
        var batch = _store.FirstOrDefault(b => b.RunId == runId);
        if (batch is not null && batch.Status == BatchStatus.Running)
        {
            batch.Status = BatchStatus.Failed;
            batch.End    = DateTime.UtcNow;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<BatchDetails> GetBatchDetailsAsync(string env, string runId, CancellationToken ct = default)
    {
        // Return deterministic static details for demo
        var details = new BatchDetails
        {
            RunId = runId ?? "RUN-UNKNOWN",
            BatchName = $"DemoBatch_{runId?.Split('-').LastOrDefault() ?? "X"}",
            Type = "FullLoad",
            Status = BatchStatus.Completed,
            Start = DateTime.UtcNow.AddMinutes(-42),
            End = DateTime.UtcNow.AddMinutes(-40),
            Metadata = new Dictionary<string, string>
            {
                ["Source"] = "s3://bucket/path",
                ["Target"] = "mongo://cluster/db/col",
                ["RecordsProcessed"] = "12,345",
                ["WorkerNode"] = "node-7",
                ["RequestId"] = Guid.NewGuid().ToString()
            }
        };

        // For some runIds simulate running
        if (runId?.Contains("RUN-") == true && runId.EndsWith("1"))
        {
            details.Status = BatchStatus.Running;
            details.End = null;
        }

        return Task.FromResult(details);
    }

    public Task<List<PerformanceEvent>> GetBatchEventsAsync(
        string env,
        string runId,
        DateTime from,
        CancellationToken ct = default)
    {
        if (!_eventsByRunId.TryGetValue(runId, out var allEvents))
        {
            return Task.FromResult(new List<PerformanceEvent>());
        }

        // Return only events with Start >= from timestamp
        var filtered = allEvents.Where(e => e.Start >= from).ToList();
        return Task.FromResult(filtered);
    }

    public Task<Topology> GetBatchTopologyAsync(string env, string runId, CancellationToken ct = default)
    {
        // For demo: compute topology from pre-generated events
        if (!_eventsByRunId.TryGetValue(runId, out var events))
        {
            return Task.FromResult(new Topology { TotalChunks = 0, TotalEvents = 0 });
        }

        // Convert to event store format (key-value by composite key)
        var eventStore = events.ToDictionary(e => e.CompositeKey, e => e);
        var topology = _topologyService.ComputeTopology(eventStore);

        return Task.FromResult(topology);
    }

    // ── Private ──────────────────────────────────────────────────────

    /// <summary>
    /// Generates a realistic chunk flow per batch:
    /// each chunk visits every (service, pipeline) hop in <see cref="ServicePipelines"/>
    /// order, except for "enrich-lookup" which is a fan-out branch some chunks
    /// take in addition to "enrich-main" (so Enricher has two incoming edges
    /// from Transformer and Loader sees a converged stream — a simple
    /// fan-out/fan-in shape to exercise the flow graph).
    /// A small fraction of chunks error out partway through and never finish
    /// their current pipeline (Finish stays null), and a small fraction of
    /// "in-flight" chunks on the last pipeline are left with Finish = null
    /// to simulate a running batch.
    /// </summary>
    private void GenerateMockEvents(Random rng)
    {
        foreach (var batch in _store.Take(10))
        {
            var events = new List<PerformanceEvent>();
            var batchStart = batch.Start;
            var chunksGenerated = rng.Next(20, 40);
            var isRunning = batch.Status == BatchStatus.Running;

            for (int chunkIdx = 0; chunkIdx < chunksGenerated; chunkIdx++)
            {
                var chunkId = $"CHK-{chunkIdx:D4}";
                var cursor = batchStart.AddSeconds(chunkIdx * 5 + rng.Next(0, 3));
                var chunkErrored = rng.Next(0, 100) < 8;
                var errorHop = chunkErrored ? rng.Next(0, ServicePipelines.Length) : -1;

                for (int hopIdx = 0; hopIdx < ServicePipelines.Length; hopIdx++)
                {
                    var (service, pipelines) = ServicePipelines[hopIdx];
                    var pipeline = pipelines[0];

                    AddEvent(events, chunkId, service, pipeline, cursor, rng, isLastHop: hopIdx == ServicePipelines.Length - 1,
                        isRunningBatch: isRunning, willError: chunkErrored && errorHop == hopIdx);

                    // Fan-out: Enricher has a secondary "enrich-lookup" pipeline
                    // that some chunks also pass through (in addition to enrich-main).
                    if (service == "Enricher" && pipelines.Length > 1 && rng.Next(0, 100) < 40)
                    {
                        var lookupCursor = cursor.AddSeconds(rng.Next(1, 4));
                        AddEvent(events, chunkId, service, pipelines[1], lookupCursor, rng, isLastHop: false,
                            isRunningBatch: isRunning, willError: false);
                    }

                    // Fan-out: Validator has a "validate-retry" pipeline that a
                    // small fraction of chunks visit before continuing.
                    if (service == "Validator" && pipelines.Length > 1 && rng.Next(0, 100) < 15)
                    {
                        var retryCursor = cursor.AddSeconds(rng.Next(1, 3));
                        AddEvent(events, chunkId, service, pipelines[1], retryCursor, rng, isLastHop: false,
                            isRunningBatch: isRunning, willError: false);
                    }

                    if (chunkErrored && errorHop == hopIdx)
                    {
                        // Chunk stops here — does not proceed to subsequent hops.
                        break;
                    }

                    cursor = cursor.AddSeconds(rng.Next(2, 6));
                }
            }

            _eventsByRunId[batch.RunId] = events.OrderBy(e => e.Start).ToList();
        }
    }

    private static void AddEvent(
        List<PerformanceEvent> events,
        string chunkId,
        string service,
        string pipeline,
        DateTime start,
        Random rng,
        bool isLastHop,
        bool isRunningBatch,
        bool willError)
    {
        var server = Servers[rng.Next(0, Servers.Length)];
        var processId = ProcessIds[rng.Next(0, ProcessIds.Length)];
        var durationSeconds = rng.Next(1, 5);

        DateTime? finish = start.AddSeconds(durationSeconds);
        string? error = null;

        if (willError)
        {
            error = "Validation failed: schema mismatch";
            // Errored chunks still record a finish time (failed, not hung).
        }
        else if (isRunningBatch && isLastHop && rng.Next(0, 100) < 25)
        {
            // Simulate some chunks still in progress on the final pipeline
            // of a running batch.
            finish = null;
        }

        events.Add(new PerformanceEvent
        {
            ChunkId = chunkId,
            Service = service,
            Pipeline = pipeline,
            Server = server,
            ProcessId = processId,
            Start = start,
            Finish = finish,
            Error = error,
            RecordCount = rng.Next(10, 500),
        });
    }
}
