using BatchMonitor.Models;

namespace BatchMonitor.Services;

/// <summary>
/// Mock implementation of <see cref="IBatchService"/>.
/// Generates deterministic fake data so the UI works without a real backend.
/// </summary>
public class MockBatchService : IBatchService
{
    private static readonly string[] Types = { "FullLoad", "DeltaSync", "Reconcile", "Archive" };
    private static readonly string[] Entities = { "Customers", "Orders", "Products", "Inventory", "Pricing", "Contracts", "Shipments", "Invoices" };
    private static readonly string[] Services = { "Ingester", "Validator", "Transformer", "Enricher", "Loader" };
    private static readonly string[] ProcessIds = { "proc-1", "proc-2", "proc-3", "proc-4" };

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

        Console.WriteLine($"[MockBatchService] Generated {_store.Count} batches");
        Console.WriteLine($"[MockBatchService] Generated events for {_eventsByRunId.Count} batches");
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

        Console.WriteLine($"[MockBatchService.GetBatchDetailsAsync] RunId={runId}, Events available: {_eventsByRunId.ContainsKey(runId ?? "")}");

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
            Console.WriteLine($"[MockBatchService.GetBatchEventsAsync] No events found for {runId}");
            return Task.FromResult(new List<PerformanceEvent>());
        }

        var filtered = allEvents.Where(e => e.Timestamp >= from).ToList();
        Console.WriteLine($"[MockBatchService.GetBatchEventsAsync] RunId={runId}, from={from:O}, returned {filtered.Count} events (total: {allEvents.Count})");
        return Task.FromResult(filtered);
    }

    public Task<Topology> GetBatchTopologyAsync(string env, string runId, CancellationToken ct = default)
    {
        if (!_eventsByRunId.TryGetValue(runId, out var events))
        {
            Console.WriteLine($"[MockBatchService.GetBatchTopologyAsync] No events for {runId}");
            return Task.FromResult(new Topology { TotalChunks = 0, TotalEvents = 0 });
        }

        var eventStore = events.ToDictionary(e => e.CompositeKey, e => e);
        var topology = _topologyService.ComputeTopology(eventStore);

        Console.WriteLine($"[MockBatchService.GetBatchTopologyAsync] RunId={runId}, Topology: {topology.Nodes.Count} nodes, {topology.Edges.Count} edges, {topology.TotalChunks} chunks");

        return Task.FromResult(topology);
    }

    // ── Private ──────────────────────────────────────────────────────

    private void GenerateMockEvents(Random rng)
    {
        // Generate events for the first 10 batches (those that will be visible)
        foreach (var batch in _store.Take(10))
        {
            var events = new List<PerformanceEvent>();
            var eventCount = rng.Next(50, 200);  // More events for better graph
            var batchStart = batch.Start;
            var chunksGenerated = rng.Next(15, 40);

            Console.WriteLine($"[MockBatchService] Generating {eventCount} events ({chunksGenerated} chunks) for batch {batch.RunId}");

            // Generate chunks that flow through the service pipeline
            for (int chunkIdx = 0; chunkIdx < chunksGenerated; chunkIdx++)
            {
                var chunkId = $"chunk-{chunkIdx:D4}";
                var chunkStartTime = batchStart.AddSeconds(chunkIdx * 2 + rng.Next(0, 2));

                // Each chunk flows through each service in sequence
                for (int serviceIdx = 0; serviceIdx < Services.Length; serviceIdx++)
                {
                    var service = Services[serviceIdx];
                    var processId = ProcessIds[rng.Next(0, ProcessIds.Length)];
                    var timestamp = chunkStartTime.AddSeconds(serviceIdx * 1 + rng.Next(0, 1));

                    events.Add(new PerformanceEvent
                    {
                        ChunkId = chunkId,
                        Service = service,
                        ProcessId = processId,
                        Timestamp = timestamp,
                        DurationMs = rng.Next(50, 1000),
                        Status = rng.Next(0, 100) < 85 ? "Success" : (rng.Next(0, 100) < 50 ? "Failed" : "Skipped"),
                        Message = rng.Next(0, 100) < 15 ? "Validation failed" : null,
                        RecordCount = rng.Next(10, 500),
                        MemoryMb = rng.Next(50, 300),
                        CpuPercent = rng.Next(20, 85)
                    });
                }
            }

            _eventsByRunId[batch.RunId] = events.OrderBy(e => e.Timestamp).ToList();
        }
    }
}
