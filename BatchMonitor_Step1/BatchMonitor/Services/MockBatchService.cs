using BatchMonitor.Hubs;
using BatchMonitor.Models;
using Microsoft.AspNetCore.SignalR;

namespace BatchMonitor.Services;

/// <summary>
/// Mock implementation of <see cref="IBatchService"/>.
/// Generates deterministic fake data and — for "Running" batches — simulates
/// a live stream of <see cref="PerformanceEvent"/> objects pushed via
/// <see cref="BatchEventsHub"/> so the SignalR live-push path can be tested
/// without a real backend (Step 9).
/// </summary>
public class MockBatchService : IBatchService
{
    private static readonly string[] Types    = { "FullLoad", "DeltaSync", "Reconcile", "Archive" };
    private static readonly string[] Entities = { "Customers", "Orders", "Products", "Inventory", "Pricing", "Contracts", "Shipments", "Invoices" };

    private static readonly (string Service, string[] Pipelines)[] ServicePipelines =
    {
        ("Ingester",    new[] { "ingest-main" }),
        ("Validator",   new[] { "validate-main", "validate-retry" }),
        ("Transformer", new[] { "transform-main" }),
        ("Enricher",    new[] { "enrich-main", "enrich-lookup" }),
        ("Loader",      new[] { "load-main" }),
    };

    private static readonly string[] Servers    = { "server01", "server02" };
    private static readonly string[] ProcessIds = { "4821", "4822", "5103", "5104" };

    // Source class names per (service, pipeline) — simulates PerformanceTracker.Source field.
    private static readonly Dictionary<(string, string), string[]> SourceNames = new()
    {
        { ("Ingester",    "ingest-main")     , new[] { "CsvIngester", "JsonIngester", "XmlIngester" } },
        { ("Validator",   "validate-main")   , new[] { "SchemaValidator", "BusinessRuleValidator" } },
        { ("Validator",   "validate-retry")  , new[] { "RetryValidator" } },
        { ("Transformer", "transform-main")  , new[] { "FieldMapper", "Normaliser", "Deduplicator" } },
        { ("Enricher",    "enrich-main")     , new[] { "LookupEnricher", "GeoEnricher" } },
        { ("Enricher",    "enrich-lookup")   , new[] { "ReferenceLookup", "CacheEnricher" } },
        { ("Loader",      "load-main")       , new[] { "MongoWriter", "ElasticWriter" } },
    };

    private static string PickSource(string service, string pipeline, Random rng)
    {
        if (SourceNames.TryGetValue((service, pipeline), out var names))
            return names[rng.Next(0, names.Length)];
        return service;
    }

    private readonly List<BatchSummary> _store;
    private readonly Dictionary<string, List<PerformanceEvent>> _eventsByRunId = new();
    private readonly TopologyComputationService _topologyService = new();
    private readonly IHubContext<BatchEventsHub>? _hubContext;

    // Background push simulation for running batches.
    private readonly CancellationTokenSource _bgCts = new();

    public MockBatchService(IHubContext<BatchEventsHub>? hubContext = null)
    {
        _hubContext = hubContext;

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
                End       = status != BatchStatus.Running ? start.AddSeconds(rng.Next(60, 1800)) : null,
            };
        }).OrderByDescending(b => b.Start).ToList();

        GenerateMockEvents(rng);

        // Start background push simulation for running batches.
        if (_hubContext is not null)
        {
            _ = SimulateLivePushAsync(_bgCts.Token);
        }
    }

    // ── IBatchService ─────────────────────────────────────────────────────

    public Task<List<BatchSummary>> GetBatchesAsync(
        string env, DateTime before, int count,
        BatchFilter? filter = null, CancellationToken ct = default)
    {
        var query = _store.Where(b => b.Start < before);
        if (filter is not null && !filter.IsEmpty)
        {
            if (!string.IsNullOrWhiteSpace(filter.SearchText))
            {
                var text = filter.SearchText.Trim();
                query = query.Where(b =>
                    b.RunId.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    b.BatchName.Contains(text, StringComparison.OrdinalIgnoreCase));
            }
            if (filter.Statuses?.Count > 0) query = query.Where(b => filter.Statuses.Contains(b.Status));
            if (filter.Types?.Count > 0)    query = query.Where(b => filter.Types.Contains(b.Type, StringComparer.OrdinalIgnoreCase));
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
        var summary = _store.FirstOrDefault(b => b.RunId == runId);
        var details = new BatchDetails
        {
            RunId     = runId ?? "RUN-UNKNOWN",
            BatchName = summary?.BatchName ?? $"DemoBatch_{runId?.Split('-').LastOrDefault() ?? "X"}",
            Type      = summary?.Type ?? "FullLoad",
            Status    = summary?.Status ?? BatchStatus.Completed,
            Start     = summary?.Start ?? DateTime.UtcNow.AddMinutes(-42),
            End       = summary?.End,
            Metadata  = new Dictionary<string, string>
            {
                ["Source"]           = "s3://bucket/path",
                ["Target"]           = "mongo://cluster/db/col",
                ["RecordsProcessed"] = "12,345",
                ["WorkerNode"]       = "node-7",
                ["RequestId"]        = Guid.NewGuid().ToString(),
            },
        };
        return Task.FromResult(details);
    }

    public Task<List<PerformanceEvent>> GetBatchEventsAsync(
        string env, string runId, DateTime from, CancellationToken ct = default)
    {
        if (!_eventsByRunId.TryGetValue(runId, out var all))
            return Task.FromResult(new List<PerformanceEvent>());

        return Task.FromResult(all.Where(e => e.Start >= from).ToList());
    }

    public Task<Topology> GetBatchTopologyAsync(string env, string runId, CancellationToken ct = default)
    {
        if (!_eventsByRunId.TryGetValue(runId, out var events))
            return Task.FromResult(new Topology());

        var store = events.ToDictionary(e => e.CompositeKey, e => e);
        return Task.FromResult(_topologyService.ComputeTopology(store));
    }

    // ── Live push simulation (Step 9) ────────────────────────────────────

    /// <summary>
    /// For each "Running" batch in the store, periodically generates new
    /// PerformanceEvents and pushes them to connected clients via
    /// <see cref="BatchEventsHub"/>, simulating what a real backend would do.
    /// </summary>
    private async Task SimulateLivePushAsync(CancellationToken ct)
    {
        var rng = new Random();
        int chunkCounter = 1000; // start above pre-generated range

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);

                foreach (var batch in _store.Where(b => b.Status == BatchStatus.Running))
                {
                    var env = "DEV1"; // mock environment
                    var group = BatchEventsHub.GroupName(env, batch.RunId);

                    // Generate 1-3 new events per tick per running batch.
                    var newEvents = GenerateLiveEvents(batch.RunId, ref chunkCounter, rng);
                    foreach (var evt in newEvents)
                    {
                        // Persist into local store so HTTP polling also returns them.
                        if (!_eventsByRunId.ContainsKey(batch.RunId))
                            _eventsByRunId[batch.RunId] = new List<PerformanceEvent>();
                        _eventsByRunId[batch.RunId].Add(evt);

                        // Push to SignalR group with env+runId so client-side routing works.
                        await _hubContext!.Clients.Group(group)
                            .SendAsync("BatchEvent", env, batch.RunId, evt, ct);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[MockBatchService] Live push error: {ex.Message}");
            }
        }
    }

    private List<PerformanceEvent> GenerateLiveEvents(string runId, ref int counter, Random rng)
    {
        var events = new List<PerformanceEvent>();
        var count  = rng.Next(1, 4);

        for (int i = 0; i < count; i++)
        {
            var hopIdx  = rng.Next(0, ServicePipelines.Length);
            var (service, pipelines) = ServicePipelines[hopIdx];
            var pipeline = pipelines[rng.Next(0, pipelines.Length)];
            var start    = DateTime.UtcNow.AddSeconds(-rng.Next(0, 3));

            events.Add(new PerformanceEvent
            {
                ChunkId     = $"LIVE-{counter++:D5}",
                Service     = service,
                Pipeline    = pipeline,
                Source      = PickSource(service, pipeline, rng),
                Server      = Servers[rng.Next(0, Servers.Length)],
                ProcessId   = ProcessIds[rng.Next(0, ProcessIds.Length)],
                Start       = start,
                Finish      = rng.Next(0, 100) < 80 ? start.AddSeconds(rng.Next(1, 4)) : null,
                RecordCount = rng.Next(10, 200),
            });
        }
        return events;
    }

    // ── Mock data generation ──────────────────────────────────────────────

    private void GenerateMockEvents(Random rng)
    {
        foreach (var batch in _store.Take(10))
        {
            var events  = new List<PerformanceEvent>();
            var isRunning = batch.Status == BatchStatus.Running;

            for (int ci = 0; ci < rng.Next(20, 40); ci++)
            {
                var cursor      = batch.Start.AddSeconds(ci * 5 + rng.Next(0, 3));
                var willError   = rng.Next(0, 100) < 8;
                var errorHop    = willError ? rng.Next(0, ServicePipelines.Length) : -1;

                for (int hi = 0; hi < ServicePipelines.Length; hi++)
                {
                    var (svc, pipes) = ServicePipelines[hi];
                    AddEvent(events, $"CHK-{ci:D4}", svc, pipes[0], cursor, rng,
                        isLastHop: hi == ServicePipelines.Length - 1,
                        isRunning: isRunning,
                        willError: willError && errorHop == hi);

                    if (svc == "Enricher" && pipes.Length > 1 && rng.Next(0, 100) < 40)
                        AddEvent(events, $"CHK-{ci:D4}", svc, pipes[1], cursor.AddSeconds(rng.Next(1, 4)), rng, false, isRunning, false);
                    if (svc == "Validator" && pipes.Length > 1 && rng.Next(0, 100) < 15)
                        AddEvent(events, $"CHK-{ci:D4}", svc, pipes[1], cursor.AddSeconds(rng.Next(1, 3)), rng, false, isRunning, false);

                    if (willError && errorHop == hi) break;
                    cursor = cursor.AddSeconds(rng.Next(2, 6));
                }
            }

            _eventsByRunId[batch.RunId] = events.OrderBy(e => e.Start).ToList();
        }
    }

    private static void AddEvent(List<PerformanceEvent> events, string chunkId, string service,
        string pipeline, DateTime start, Random rng, bool isLastHop, bool isRunning, bool willError)
    {
        DateTime? finish = start.AddSeconds(rng.Next(1, 5));
        if (isRunning && isLastHop && rng.Next(0, 100) < 25) finish = null;

        events.Add(new PerformanceEvent
        {
            ChunkId     = chunkId,
            Service     = service,
            Pipeline    = pipeline,
            Source      = PickSource(service, pipeline, rng),
            Server      = Servers[rng.Next(0, Servers.Length)],
            ProcessId   = ProcessIds[rng.Next(0, ProcessIds.Length)],
            Start       = start,
            Finish      = finish,
            Error       = willError ? "Validation failed: schema mismatch" : null,
            RecordCount = rng.Next(10, 500),
        });
    }
}
