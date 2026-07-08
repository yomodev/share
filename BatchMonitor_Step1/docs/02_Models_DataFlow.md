# BatchMonitor — Models & Data Flow

## PerformanceEvent

The core domain object. Represents one chunk of work processed by one service.

```csharp
public class PerformanceEvent
{
    public string Name   { get; set; }   // Identifies the unit of work (e.g. "CHK-0001")
    public string Service   { get; set; }   // Service name (e.g. "Enricher")
    public string Pipeline  { get; set; }   // Pipeline within service (e.g. "enrich-main")
    public string Source    { get; set; }   // Class/processor name (e.g. "LookupEnricher")
    public string Server    { get; set; }   // Host that processed it
    public int ProcessId { get; set; }   // OS process ID
    public DateTime Start   { get; set; }
    public DateTime? Finish { get; set; }   // null = still in progress
    public string? Error    { get; set; }   // null = no error
    public int RecordCount  { get; set; }

    // Computed
    public string CompositeKey => $"{Name}:{Service}:{Pipeline}:{ProcessId}";
    public bool IsDone  => Finish.HasValue && Error == null;
    public bool IsError => Error != null;
    public DateTime Timestamp => Finish ?? Start;  // for upsert ordering
}
```

### Key field: `Source`

`Source` is the class name or pipe processor that handled the chunk. It's the **default Colour By** key in the Timeline tab. Example values: `CsvIngester`, `SchemaValidator`, `FieldMapper`, `LookupEnricher`, `MongoWriter`.

### Key field: `Name`

Same `Name` string can appear in **multiple services** (a chunk passes through the pipeline). In the Timeline tab, hovering any block highlights all blocks with the same `Name` across all batches/services.

---

## CompositeKey and Upsert

Events are upserted by `CompositeKey = "{Name}:{Service}:{Pipeline}:{ProcessId}"`. Last-write-wins by `Timestamp` (Finish > Start). This handles:
- Duplicate pushes from SignalR + polling overlap
- Updates when a chunk finishes (Start event arrives first, then Finish event)

---

## Topology Model

Used by the flow graph in BatchDetail.

```
Topology
├── Nodes: List<TopologyNode>
│   ├── Id, Label, InstanceCount
│   ├── HeaderState: PipelineState
│   └── Pipelines: List<PipelineRow>
│       ├── Name, Topic
│       ├── DoneCount, InProgressCount, ErrorCount
│       ├── Progress (0.0–1.0)
│       ├── State: PipelineState
│       ├── RecentThroughputScore
│       └── Instances: List<InstanceStats>
└── Edges: List<TopologyEdge>
    ├── Source, SourcePipeline → Target, TargetPipeline
    ├── DoneCount, WaitingEstimate
    └── State: PipelineState
```

### PipelineState (JSON: lowercase string)

```csharp
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PipelineState
{
    NotStarted, Completed, Idle, InProgress, Active, Errored
}
```

**State → colour mapping** (used in both graph and timeline):
- `Errored`    → `#F85149` (red)
- `Active`     → `#3FB950` (green, with pulse animation)
- `InProgress` → `#388BFD` (blue)
- `Idle`       → `#8B949E` (grey)
- `Completed`  → `#8B949E` (grey)
- `NotStarted` → `rgba(139,148,158,0.22)` (faint)

---

## Data Flow: BatchDetail Tab

```
User opens BatchDetail
    │
    ├─ BatchService.GetBatchDetailsAsync()  →  BatchDetails (metadata, status)
    │
    ├─ PerformanceEventService.StartAsync()
    │     ├─ HTTP: GetBatchEventsAsync() from batchStart  →  historical events
    │     ├─ SignalR: SubscribeToBatchAsync()  →  live push (running batches)
    │     └─ Poll loop (fallback, 30s when SignalR active)
    │
    ├─ DebouncedTopologyCompute() [400ms debounce]
    │     ├─ Filter events by _replayTimestamp (null = all)
    │     ├─ TopologyComputationService.ComputeTopology()
    │     └─ StateHasChanged() → D3FlowGraph.update(topology)
    │
    └─ D3FlowGraph (JS)
          ├─ dagre layout (LR or TB based on canvas aspect ratio)
          ├─ D3 nodes + edges render
          └─ D3Animation RAF loop (edge dash-flow)
```

### Replay mode (Step 8)

`_replayTimestamp` (nullable DateTime):
- `null` → LIVE mode, slider at rightmost position
- set → REPLAY mode, topology computed filtering `event.Start <= _replayTimestamp`

Slider uses 0–1000 integer steps (not Unix ms) to avoid browser validation tooltip. `SliderToDateTime(value)` converts linearly within `[_sliderMin, _sliderMax]`.

---

## Data Flow: Timeline Tab

```
User opens Timeline (from BatchDetail [⏱] button)
    │
    ├─ InitialRunId set → LoadBatchAsync(env, runId)
    │
    └─ TimelineBatch.LoadAsync()
          ├─ GetBatchDetailsAsync()  →  BatchName, BatchStart, IsLive
          ├─ GetBatchEventsAsync()   →  historical events (upsert by Name)
          └─ SignalRConnectionService.SubscribeToBatchAsync() (if IsLive)
                └─ OnBatchEvent() → UpsertEvent() → DebouncedPushAsync()

DebouncedPushAsync() [300ms debounce]
    └─ BuildPayload()
          ├─ groupBy, colourBy, filter, stackView
          └─ batches[]:
                ├─ runId, batchName, isLive, batchStartEpochMs
                └─ events[]: name, source, pipeline, service,
                             processId, server, startMs, finishMs,
                             status, error
    └─ JS: BatchMonitor.Timeline.update(key, payload)
```

### TimelineBatch

One instance per loaded batch in a Timeline tab. Manages:
- Event store (`Dictionary<string, PerformanceEvent>` keyed by Name)
- SignalR subscription (live batches only)
- 3-hour live timeout (per spec)
- CSV-imported batches use `CreateFromCsv()` — no live subscription

---

## SignalR Event Push Flow

```
MockBatchService background timer (2s)
    └─ GenerateLiveEvents()
          └─ IHubContext<BatchEventsHub>.Clients
               .Group("{env}:{runId}")
               .SendAsync("BatchEvent", env, runId, PerformanceEvent)

Client (SignalRConnectionService)
    └─ HubConnection.On<string,string,PerformanceEvent>("BatchEvent")
          └─ Route to handlers keyed by "{env}:{runId}"
                ├─ PerformanceEventService.OnSignalREvent()  (BatchDetail)
                └─ TimelineBatch.OnBatchEvent()              (Timeline)
```

**Three arguments** on `"BatchEvent"`: env + runId + event. The env+runId allow client-side routing to the correct batch handler. This was necessary because the HubConnection is shared across all batch subscriptions in a circuit.

---

## MockBatchService Data Generation

### Source names by service/pipeline

```csharp
("Ingester",    "ingest-main")  → ["CsvIngester", "JsonIngester", "XmlIngester"]
("Validator",   "validate-main")→ ["SchemaValidator", "BusinessRuleValidator"]
("Transformer", "transform-main")→["FieldMapper", "Normaliser", "Deduplicator"]
("Enricher",    "enrich-main") → ["LookupEnricher", "GeoEnricher"]
("Enricher",    "enrich-lookup")→ ["ReferenceLookup", "CacheEnricher"]
("Loader",      "load-main")   → ["MongoWriter", "ElasticWriter"]
```

### Service pipeline chain

```
Ingester → Validator → Transformer → Enricher → Loader
```

Enricher may also process via `enrich-lookup` (40% chance). Validator retries via `validate-retry` (15% chance). Some chunks error at a random hop.

### Live push simulation

`SimulateLivePushAsync()` runs as a background Task for all `BatchStatus.Running` batches. Every 2 seconds it generates 1–3 new events and pushes them via `IHubContext<BatchEventsHub>`. Events are also persisted in-memory so HTTP polling also returns them.
