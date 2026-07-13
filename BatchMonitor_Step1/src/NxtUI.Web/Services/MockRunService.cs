using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NxtUI.Configuration;
using NxtUI.Core.Services;
using NxtUI.Core.Models;
using NxtUI.Core.Filtering;

namespace NxtUI.Web.Services;

public class MockRunService : IRunService, IPushesOwnRunEvents
{
    private static readonly string[] RunTypes = { "FullLoad", "DeltaSync", "Reconcile", "Archive" };
    private static readonly string[] RunEntities = { "Customers", "Orders", "Products", "Inventory", "Pricing", "Contracts", "Shipments", "Invoices" };

    // Hosts match App:Environments[].Servers in appsettings.json (union of all envs)
    private static readonly string[] Servers = {
        "dev1-srv-01", "dev1-srv-02",
        "dev2-srv-01",
        "uat1-srv-01", "uat1-srv-02",
        "uat2-srv-01",
        "stg1-srv-01", "stg1-srv-02",
        "prod-srv-01", "prod-srv-02", "prod-srv-03",
    };

    private static readonly string[] Services = {
        "Ingester", "Validator", "Normaliser", "Transformer",
        "Enricher", "Router", "Loader", "Auditor", "Notifier", "Archiver",
    };

    private static readonly string[] AllPipelines = {
        "csv-ingest",     "json-ingest",     "xml-ingest",      "binary-stream",
        "schema-check",   "rule-validate",   "format-check",    "retry-validate",
        "field-norm",     "date-norm",       "currency-norm",
        "field-map",      "record-merge",    "record-split",    "type-cast",
        "geo-enrich",     "ref-lookup",      "cache-fill",      "tag-enrich",
        "priority-route", "bulk-route",      "dlq-route",
        "mongo-write",    "elastic-write",   "sql-write",       "parquet-write",
        "audit-log",      "compliance-check",
        "email-notify",   "webhook-fire",
    };

    private static readonly string[] AllSources = {
        "CsvReader",         "JsonParser",        "XmlParser",         "BinaryDecoder",     "StreamConsumer",
        "SchemaValidator",   "RuleEngine",         "FormatChecker",     "RetryHandler",      "DeadLetterProcessor",
        "FieldNormaliser",   "DateConverter",      "CurrencyConverter", "UnitConverter",     "EncodingFixer",
        "FieldMapper",       "RecordMerger",       "RecordSplitter",    "TypeCaster",        "Deduplicator",
        "GeoLookup",         "RefLookup",          "CacheFiller",       "TagResolver",       "MetadataEnricher",
        "PriorityClassifier","BulkRouter",         "DlqRouter",         "LoadBalancer",      "PartitionSelector",
        "MongoWriter",       "ElasticWriter",      "SqlWriter",         "ParquetWriter",     "CsvExporter",
        "AuditLogger",       "ComplianceChecker",  "TraceEmitter",      "MetricRecorder",    "EventPublisher",
        "EmailSender",       "WebhookCaller",      "SlackNotifier",     "PushNotifier",      "SmsGateway",
        "ColdArchiver",      "Compressor",         "ChecksumVerifier",  "ManifestWriter",    "BackupWriter",
    };

    // Deterministic service → pipeline[] and pipeline → source[] assignments.
    private static readonly Dictionary<string, string[]> ServicePipelines = new();
    private static readonly Dictionary<string, string[]> PipelineSources = new();

    static MockRunService()
    {
        var rng = new Random(7);
        var pipes = AllPipelines.OrderBy(_ => rng.Next()).ToArray();
        int pi = 0;
        foreach (var svc in Services)
        {
            int n = rng.Next(1, 4);
            ServicePipelines[svc] = pipes.Skip(pi % pipes.Length).Take(n).ToArray();
            pi += n;
        }
        foreach (var pipe in AllPipelines)
        {
            int n = rng.Next(1, 5);
            PipelineSources[pipe] = AllSources.OrderBy(_ => rng.Next()).Take(n).ToArray();
        }
    }

    private static string PickService(Random rng) => Services[rng.Next(Services.Length)];
    private static string PickPipeline(string svc, Random rng)
    {
        var p = ServicePipelines.GetValueOrDefault(svc, AllPipelines);
        return p[rng.Next(p.Length)];
    }
    private static string PickSource(string pipe, Random rng)
    {
        var s = PipelineSources.GetValueOrDefault(pipe, AllSources);
        return s[rng.Next(s.Length)];
    }
    private static string PickServer(Random rng) => Servers[rng.Next(Servers.Length)];
    private static int PickPid(Random rng) => rng.Next(1000, 65000);

    // ── State ─────────────────────────────────────────────────────────────

    private readonly List<RunSummary> _store;
    private readonly Dictionary<string, List<PerformanceEvent>> _eventsByRunId = new();
    private readonly TopologyComputationService _topologyService = new();
    private readonly RunEventBroker? _eventBroker;
    private readonly RunsSettings _runsSettings;
    private readonly ILogger<MockRunService> _log;
    private readonly CancellationTokenSource _bgCts = new();

    // Live pool: runId -> chunkId -> pending (svc, pipeline, src, server, pid) tuples not yet
    // fired. Scoped per run (not a single flat dict across every Running batch) — it used to
    // be flat, which meant firing pending hops for one batch's iteration could dequeue and
    // emit events that were enqueued moments earlier for a DIFFERENT batch in the same tick,
    // attributing them to the wrong run entirely. Each tuple represents an independent
    // service instance that will process this chunk.
    private readonly Dictionary<string, Dictionary<string, List<(string Svc, string Pipeline, string Src, string Server, int Pid)>>>
        _livePool = new();

    // ── Construction ──────────────────────────────────────────────────────

    public MockRunService(RunEventBroker? eventBroker = null, RunsSettings? runsSettings = null, ILogger<MockRunService>? logger = null)
    {
        _eventBroker = eventBroker;
        _runsSettings = runsSettings ?? new RunsSettings();
        _log = logger ?? NullLogger<MockRunService>.Instance;
        var rng = new Random(42);

        _store = Enumerable.Range(1, 200).Select(i =>
        {
            var type = RunTypes[i % RunTypes.Length];
            var entity = RunEntities[i % RunEntities.Length];
            var start = DateTime.UtcNow.AddMinutes(-(i * 7 + rng.Next(0, 5)));
            var status = i switch
            {
                1 or 2 => RunStatus.Running,
                3 or 7 => RunStatus.Failed,
                _ => RunStatus.Completed,
            };
            return new RunSummary
            {
                RunId = $"RUN-{DateTime.UtcNow:yyyyMMdd}-{i:D3}",
                Description = $"{type}_{entity}",
                Type = type,
                Status = status,
                Start = start,
                End = status != RunStatus.Running ? start.AddSeconds(rng.Next(60, 1800)) : null,
            };
        }).OrderByDescending(b => b.Start).ToList();

        GenerateMockEvents(rng);
        GenerateDemoNestedRuns();

        if (_eventBroker is not null)
            _ = SimulateLivePushAsync(_bgCts.Token);
    }

    // ── IRunService ─────────────────────────────────────────────────────

    public Task<List<RunSummary>> GetRunsAsync(
        string env, DateTime before, int count,
        RunFilter? filter = null, CancellationToken ct = default)
    {
        var query = _store.Where(b => b.Start < before);
        if (filter is not null && !filter.IsEmpty)
        {
            if (filter.FilterAst is not null)
                query = query.Where(b => FilterEvaluator.Evaluate(filter.FilterAst, b));

            if (!string.IsNullOrWhiteSpace(filter.SearchText))
            {
                var text = filter.SearchText.Trim();
                query = query.Where(b =>
                    b.RunId.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    b.Description.Contains(text, StringComparison.OrdinalIgnoreCase));
            }
            if (filter.Statuses?.Count > 0) query = query.Where(b => filter.Statuses.Contains(b.Status));
            if (filter.Types?.Count > 0) query = query.Where(b => filter.Types.Contains(b.Type, StringComparer.OrdinalIgnoreCase));
        }

        query = SortByField(query, filter?.SortField, filter?.SortDescending ?? true);

        var effectiveCount = Math.Min(count > 0 ? count : _runsSettings.PageSize, _runsSettings.MaxResults);
        return Task.FromResult(query.Take(effectiveCount).ToList());
    }

    // Mirrors SqlRunService's allow-listed sort columns; unrecognized/null falls back to Start desc.
    private static IEnumerable<RunSummary> SortByField(IEnumerable<RunSummary> query, string? field, bool descending)
    {
        Func<RunSummary, IComparable> keySelector = field switch
        {
            "RunId" => b => b.RunId,
            "Type" => b => b.Type,
            "Status" => b => b.Status,
            "Description" => b => b.Description,
            "EndTime" => b => b.End ?? DateTime.MinValue,
            _ => b => b.Start,
        };
        return descending ? query.OrderByDescending(keySelector) : query.OrderBy(keySelector);
    }

    public Task<bool> CancelRunAsync(string env, string runId, CancellationToken ct = default)
    {
        var batch = _store.FirstOrDefault(b => b.RunId == runId);
        if (batch is not null && batch.Status == RunStatus.Running)
        {
            batch.Status = RunStatus.Failed;
            batch.End = DateTime.UtcNow;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<RunDetails> GetRunDetailsAsync(string env, string runId, CancellationToken ct = default, int childDepth = 1)
    {
        var summary = _store.FirstOrDefault(b => b.RunId == runId);
        var details = new RunDetails
        {
            RunId = runId ?? "RUN-UNKNOWN",
            Description = summary?.Description ?? $"DemoRun_{runId?.Split('-').LastOrDefault() ?? "X"}",
            Type = summary?.Type ?? "FullLoad",
            Status = summary?.Status ?? RunStatus.Completed,
            Start = summary?.Start ?? DateTime.UtcNow.AddMinutes(-42),
            End = summary?.End,
            Metadata = new Dictionary<string, string>
            {
                ["Source"] = "s3://bucket/path",
                ["Target"] = "mongo://cluster/db/col",
                ["RecordsProcessed"] = "12,345",
                ["WorkerNode"] = "node-7",
                ["RequestId"] = Guid.NewGuid().ToString(),
                ["InitiatedBy"] = "User",
                ["Priority"] = "High",
                ["Notes"] = "This is a mock run for demonstration purposes.",
                ["Tags"] = "demo, test, mock",
                ["Environment"] = env,
                ["PipelineVersion"] = "v1.2.3",
                ["ErrorDetails"] = summary?.Status == RunStatus.Failed ? "Simulated failure for testing." : string.Empty,
            },
            Children = childDepth >= 1 ? BuildMockChildren(runId) : new List<RunNode>(),
        };
        return Task.FromResult(details);
    }

    /// <summary>
    /// Demo-only nested-run simulation (docs/12_Custom_Layout_And_Nested_Runs.md §7): every
    /// 4th run (deterministic on RunId hash, so the same run always looks the same across
    /// calls — no real orchestrator exists in the mock) gets 2 synthesized children, picked
    /// from other entries already in `_store` so they're independently drillable via the same
    /// GetRunDetailsAsync. DoneCount/TotalCount are left null (RunSummary carries no counts) —
    /// matches the design's "optional, status is enough" decision. childDepth > 1 (fetching
    /// grandchildren in the same call) is NOT implemented here yet; RunNode is intentionally
    /// a flat summary with no nested Children of its own (see RunNode's own doc comment), so
    /// a real deeper fetch means the caller drilling in and calling this again per level.
    /// </summary>
    private List<RunNode> BuildMockChildren(string runId)
    {
        if (string.IsNullOrEmpty(runId)) return new List<RunNode>();

        // The deliberately-crafted demo family (GenerateDemoNestedRuns) always wins over the
        // generic hash-based simulation below — it needs an EXACT, stable hierarchy 3 levels
        // deep, not a pseudo-random pick from the unrelated 200-run pool.
        if (DemoChildren.TryGetValue(runId, out var demoChildIds))
        {
            return demoChildIds
                .Select(id => _store.FirstOrDefault(b => b.RunId == id))
                .Where(child => child is not null)
                .Select(child => new RunNode
                {
                    RunId = child!.RunId,
                    Description = child.Description,
                    Status = child.Status,
                    Start = child.Start,
                    End = child.End,
                })
                .ToList();
        }

        // A demo run not itself a DemoChildren key (a leaf, e.g. RUN-DEMO-CHILD-B) must never
        // fall through to the generic simulation below — its RunId hash could coincidentally
        // pass the "% 4 == 0" check and pick up phantom children from the unrelated 200-run
        // pool, which is exactly the bug this guard caught in testing.
        if (runId.StartsWith("RUN-DEMO-", StringComparison.Ordinal)) return new List<RunNode>();

        if (_store.Count < 3) return new List<RunNode>();
        if (Math.Abs(runId.GetHashCode()) % 4 != 0) return new List<RunNode>();

        var selfIndex = _store.FindIndex(b => b.RunId == runId);
        var baseIndex = selfIndex >= 0 ? selfIndex : Math.Abs(runId.GetHashCode()) % _store.Count;

        return Enumerable.Range(1, 2)
            .Select(offset => _store[(baseIndex + offset) % _store.Count])
            .Where(child => child.RunId != runId)
            .Select(child => new RunNode
            {
                RunId = child.RunId,
                Description = child.Description,
                Status = child.Status,
                Start = child.Start,
                End = child.End,
            })
            .ToList();
    }

    public Task<List<PerformanceEvent>> GetRunEventsAsync(
        string env, string runId, DateTime from, CancellationToken ct = default)
    {
        if (!_eventsByRunId.TryGetValue(runId, out var all))
            return Task.FromResult(new List<PerformanceEvent>());
        return Task.FromResult(all.Where(e => e.Start >= from).ToList());
    }

    public Task<Topology> GetRunTopologyAsync(string env, string runId, CancellationToken ct = default)
    {
        if (!_eventsByRunId.TryGetValue(runId, out var events))
            return Task.FromResult(new Topology());
        var store = events.ToDictionary(e => e.CompositeKey, e => e);
        return Task.FromResult(_topologyService.ComputeTopology(store));
    }

    // ── Static mock data ──────────────────────────────────────────────────

    private void GenerateMockEvents(Random rng)
    {
        foreach (var batch in _store.Take(10))
        {
            var events = new List<PerformanceEvent>();
            var duration = (batch.End ?? batch.Start.AddMinutes(30)) - batch.Start;
            // Was rng.Next(200, 400) * 3-10 hops (avg ~2100 events/run) — cut to ~1/3 so
            // Runs/Timeline aren't pushing huge payloads for a mock backend.
            int chunks = rng.Next(65, 135);

            for (int ci = 0; ci < chunks; ci++)
            {
                var chunkId = $"CHK-{ci:D4}";
                // Each chunk is independently processed by 3-10 different
                // (service, pipeline, source, server, pid) combinations.
                // Events are NOT sequential — each fires at its own time.
                int hops = rng.Next(3, 11);
                for (int h = 0; h < hops; h++)
                {
                    var svc = PickService(rng);
                    var pipeline = PickPipeline(svc, rng);
                    var src = PickSource(pipeline, rng);
                    var server = PickServer(rng);
                    var pid = PickPid(rng);

                    var startOffset = TimeSpan.FromSeconds(rng.Next(0, Math.Max(1, (int)duration.TotalSeconds)));
                    var start = batch.Start + startOffset;
                    var dur = rng.Next(1, 45);
                    var isInProg = batch.Status == RunStatus.Running && rng.Next(0, 100) < 8;
                    var isError = !isInProg && rng.Next(0, 100) < 4;

                    events.Add(new PerformanceEvent
                    {
                        Name = chunkId,
                        Service = svc,
                        Pipeline = pipeline,
                        Source = src,
                        Server = server,
                        ProcessId = pid,
                        Start = start,
                        Finish = isInProg ? null : start.AddSeconds(dur),
                        Error = isError ? "Processing error: schema mismatch" : null,
                        RecordCount = rng.Next(10, 500),
                        LastUpdate = isInProg ? start : start.AddSeconds(dur),
                    });
                }
            }

            _eventsByRunId[batch.RunId] = events.OrderBy(e => e.Start).ToList();
        }
    }

    // ── Deliberately-crafted nested-run demo ────────────────────────────────
    // A small, clean 4-service linear pipeline (Ingester -> Validator -> Enricher -> Loader,
    // every chunk visiting all four in that fixed order) nested exactly 3 levels deep, so the
    // hierarchy and edges are predictable and easy to read — unlike the generic pool above,
    // which picks random services per hop and is only meant to look like "a lot of activity,"
    // not to be a legible example. Each run is capped well under 1000 events. See
    // docs/12_Custom_Layout_And_Nested_Runs.md §7 and config/topology/nesteddemo.json, which
    // demonstrates the "groups" colour/border schema against these same 4 services.
    private static readonly string[] DemoServices = { "Ingester", "Validator", "Enricher", "Loader" };
    private const int DemoChunkCount = 180; // * 4 services = 720 events/run, comfortably < 1000

    private static readonly Dictionary<string, string[]> DemoChildren = new()
    {
        ["RUN-DEMO-ROOT"]    = new[] { "RUN-DEMO-CHILD-A", "RUN-DEMO-CHILD-B" },
        ["RUN-DEMO-CHILD-A"] = new[] { "RUN-DEMO-GRANDCHILD-A1" },
    };

    private void GenerateDemoNestedRuns()
    {
        var now = DateTime.UtcNow;
        // Status variety is deliberate: Running (root) exercises the live child-polling loop
        // (RunDetail.razor's RunChildRunsRefreshLoopAsync); Completed/Failed/Running across
        // the rest show every child-run-block colour in one demo.
        AddDemoRun("RUN-DEMO-ROOT",          "NestedDemo — root",              RunStatus.Running,   now.AddMinutes(-20), null,                seed: 1001);
        AddDemoRun("RUN-DEMO-CHILD-A",       "NestedDemo — child A",           RunStatus.Completed, now.AddMinutes(-18), now.AddMinutes(-9),  seed: 1002);
        AddDemoRun("RUN-DEMO-CHILD-B",       "NestedDemo — child B",           RunStatus.Failed,    now.AddMinutes(-17), now.AddMinutes(-12), seed: 1003);
        AddDemoRun("RUN-DEMO-GRANDCHILD-A1", "NestedDemo — grandchild A.1",    RunStatus.Running,   now.AddMinutes(-10), null,                seed: 1004);
    }

    private void AddDemoRun(string runId, string description, RunStatus status, DateTime start, DateTime? end, int seed)
    {
        _store.Insert(0, new RunSummary
        {
            RunId = runId,
            Description = description,
            Type = "NestedDemo",
            Status = status,
            Start = start,
            End = end,
        });
        _eventsByRunId[runId] = GenerateDemoEvents(start, end, status, seed);
    }

    private static List<PerformanceEvent> GenerateDemoEvents(DateTime start, DateTime? end, RunStatus status, int seed)
    {
        var rng = new Random(seed); // per-run seed so the demo is stable across restarts
        var events = new List<PerformanceEvent>();
        var duration = (end ?? start.AddMinutes(9)) - start;

        for (int ci = 0; ci < DemoChunkCount; ci++)
        {
            var chunkId = $"CHK-{ci:D4}";
            var t = start + TimeSpan.FromSeconds(rng.Next(0, Math.Max(1, (int)duration.TotalSeconds)));

            for (int h = 0; h < DemoServices.Length; h++)
            {
                var svc = DemoServices[h];
                var pipeline = PickPipeline(svc, rng);
                var src = PickSource(pipeline, rng);
                var server = PickServer(rng);
                var pid = PickPid(rng);
                var dur = rng.Next(1, 20);
                var isLastHop = h == DemoServices.Length - 1;
                var isInProg = status == RunStatus.Running && isLastHop && rng.Next(0, 100) < 15;
                var isError = status == RunStatus.Failed && isLastHop && rng.Next(0, 100) < 30;

                events.Add(new PerformanceEvent
                {
                    Name = chunkId,
                    Service = svc,
                    Pipeline = pipeline,
                    Source = src,
                    Server = server,
                    ProcessId = pid,
                    Start = t,
                    Finish = isInProg ? null : t.AddSeconds(dur),
                    Error = isError ? "Processing error: schema mismatch" : null,
                    RecordCount = rng.Next(10, 500),
                    LastUpdate = isInProg ? t : t.AddSeconds(dur),
                });

                t = t.AddSeconds(dur + rng.Next(1, 5)); // next hop starts after this one finishes
            }
        }

        return events.OrderBy(e => e.Start).ToList();
    }

    // ── Live push simulation ──────────────────────────────────────────────

    private async Task SimulateLivePushAsync(CancellationToken ct)
    {
        var rng = new Random();
        int chunkCounter = 10000;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);

                foreach (var batch in _store.Where(b => b.Status == RunStatus.Running))
                {
                    var env = "DEV1";
                    // Demo runs (docs/12_Custom_Layout_And_Nested_Runs.md §7) stay on their
                    // own clean 4-service chain — they used to draw from the generic pool
                    // like every other run, which meant a long-running demo session slowly
                    // filled with unrelated services and got messier over time.
                    var servicePool = batch.RunId.StartsWith("RUN-DEMO-", StringComparison.Ordinal)
                        ? DemoServices : Services;

                    foreach (var evt in GenerateLiveEvents(batch.RunId, servicePool, ref chunkCounter, rng))
                    {
                        if (!_eventsByRunId.ContainsKey(batch.RunId))
                            _eventsByRunId[batch.RunId] = new();
                        _eventsByRunId[batch.RunId].Add(evt);

                        _eventBroker?.Publish(env, batch.RunId, evt);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Live event simulation loop failed"); }
        }
    }

    private List<PerformanceEvent> GenerateLiveEvents(string runId, string[] servicePool, ref int counter, Random rng)
    {
        var events = new List<PerformanceEvent>();
        var pool = _livePool.TryGetValue(runId, out var existing) ? existing : (_livePool[runId] = new());

        // The demo pool (docs/12 §7 — Ingester -> Validator -> Enricher -> Loader) is a
        // deliberately clean, fixed-order linear chain (its own topology hint declares
        // Ingester as "source" and Loader as "sink" with no other wiring). Every OTHER hop
        // in a chunk here used to pick a service at random regardless of pool, which for the
        // demo pool could put Loader before Ingester (or any other out-of-order pairing) in
        // the same chunk — TopologyComputationService derives edges purely from consecutive
        // per-chunk hop order, so that produced a genuine cycle into "source"/out of "sink",
        // logged every relayout as bm-flow-layout's role-hint-ignored warning. The generic
        // pool has no declared order, so it keeps picking randomly.
        bool isDemoPool = ReferenceEquals(servicePool, DemoServices);

        // Spawn 1-2 new chunks, each assigned 4-12 independent processing hops
        // across different service/pipeline/source/server/pid combinations.
        int newChunks = rng.Next(1, 3);
        for (int i = 0; i < newChunks; i++)
        {
            var chunkId = $"LIVE-{counter++:D5}";
            int hops = rng.Next(4, 13);
            var pending = new List<(string, string, string, string, int)>(hops);
            for (int h = 0; h < hops; h++)
            {
                var svc = isDemoPool ? servicePool[h % servicePool.Length] : servicePool[rng.Next(servicePool.Length)];
                var pipe = PickPipeline(svc, rng);
                pending.Add((svc, pipe, PickSource(pipe, rng), PickServer(rng), PickPid(rng)));
            }
            pool[chunkId] = pending;
        }

        // Each tick, each in-flight chunk independently reports 0-3 of its
        // pending hops as new immutable events (fixed start + finish).
        var done = new List<string>();
        foreach (var (chunkId, hops) in pool)
        {
            int toFire = Math.Min(hops.Count, rng.Next(0, 4));
            for (int f = 0; f < toFire; f++)
            {
                var (svc, pipeline, src, server, pid) = hops[0];
                hops.RemoveAt(0);

                var start = DateTime.UtcNow.AddSeconds(-rng.Next(1, 12));
                // The demo pool (docs/12 §7) is documented as a deliberately clean, predictable
                // example — random errors here contradicted that (a service could turn red mid-
                // demo for no reason a viewer could see, since GenerateDemoEvents' own error
                // injection is correctly conditioned on RunStatus.Failed but this live-tail path
                // wasn't). The generic pool keeps its unconditional error rate — some background
                // noise is realistic there.
                var isError = !isDemoPool && rng.Next(0, 100) < 5;
                var inProg = !isError && rng.Next(0, 100) < 8;

                events.Add(new PerformanceEvent
                {
                    Name = chunkId,
                    Service = svc,
                    Pipeline = pipeline,
                    Source = src,
                    Server = server,
                    ProcessId = pid,
                    Start = start,
                    Finish = inProg ? null : start.AddSeconds(rng.Next(1, 20)),
                    Error = isError ? "Live processing error" : null,
                    RecordCount = rng.Next(10, 500),
                    LastUpdate = DateTime.UtcNow,
                });
            }
            if (hops.Count == 0) done.Add(chunkId);
        }
        foreach (var id in done) pool.Remove(id);

        return events;
    }
}
