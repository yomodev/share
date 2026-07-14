# NxtUI — Models & Data Flow

## PerformanceEvent

The core domain object (`src/NxtUI.Core/Models/PerformanceEvent.cs`). Represents one
chunk of work processed by one service/pipeline.

```csharp
public class PerformanceEvent
{
    public string Name       { get; set; }   // Identifies the unit of work (e.g. "CHK-0001")
    public string Service    { get; set; }   // Service name (e.g. "Enricher")
    public string Pipeline   { get; set; }   // Pipeline within service (e.g. "enrich-main")
    public string Source     { get; set; }   // Class/processor name (e.g. "LookupEnricher")
    public string Server     { get; set; }   // Host that processed it
    public int ProcessId     { get; set; }   // OS process ID
    public DateTime Start    { get; set; }
    public DateTime? Finish  { get; set; }   // null = still in progress
    public string? Error     { get; set; }   // null/empty = no error
    public int RecordCount   { get; set; }
    public string? Info      { get; set; }   // free-text extra detail, shown in the UI
    public double? Value     { get; set; }   // optional numeric metric attached to the event
    public DateTime LastUpdate { get; set; }

    // Computed
    public string CompositeKey => $"{Name}:{Service}:{Pipeline}:{ProcessId}";
    public bool IsDone  => Finish.HasValue;
    public bool IsError => !string.IsNullOrEmpty(Error);
    public DateTime Timestamp => Start;   // for upsert ordering
}
```

### Key field: `Source`

`Source` is the class name or pipe processor that handled the chunk. It's the
**default Colour By** key in the Timeline tab. Values vary per generated demo run
(mock data draws from a randomized service/source-name pool — see "MockRunService
data generation" below) — don't rely on any specific literal `Source` value existing
across every run.

### Key field: `Name`

Same `Name` string can appear in **multiple services** (a chunk passes through the
pipeline). In the Timeline tab, hovering any block highlights all blocks with the
same `Name` across all runs/services.

---

## CompositeKey and Upsert

Events are upserted by `CompositeKey = "{Name}:{Service}:{Pipeline}:{ProcessId}"`.
This handles:
- Duplicate pushes from live-push + polling overlap.
- Updates when a chunk finishes (a Start-only event arrives first, then a Finish
  event with the same key replaces it).

---

## Topology Model (`src/NxtUI.Core/Models/Topology.cs`)

Used by the flow graph in Run Detail. Considerably richer than a plain node/edge
graph — it also carries nested-run and layout-hint data:

```
Topology
├── Nodes: List<TopologyNode>
│   ├── Id, Label, InstanceCount
│   ├── HeaderState: PipelineState
│   ├── Pipelines: List<PipelineRow>
│   │   ├── Name, DisplayName, Topic
│   │   ├── DoneCount, InProgressCount, ErrorCount
│   │   ├── Progress (0.0–1.0)
│   │   ├── State: PipelineState
│   │   ├── RecentThroughputScore
│   │   └── Instances: List<InstanceStats>
│   └── Layout-hint decoration (see 11_Topology_Hints.md):
│       Role, Group, Color, Order, Pin, PinX, PinY, Direction, External,
│       ArriveFrom, Orientation, IsDeclared, IsObserved
├── Edges: List<TopologyEdge>
│   ├── Source, SourcePipeline → Target, TargetPipeline
│   ├── DoneCount, WaitingEstimate
│   ├── State: PipelineState
│   └── IsDeclared, IsObserved
├── ChildRuns: List<RunNode>              — nested child-run summaries (see 12_/13_)
├── ExpandedChildren: Dictionary<string, Topology> — recursively-computed sub-topology
│                                            per expanded child run
├── Layout: LayoutHint?, GroupColors, ChildRunBoxColor, HasBlueprint
└── Rollups: TotalChunks, TotalEvents, TotalDone, TotalInProgress, EstimatedProgress
```

`IsDeclared`/`IsObserved` on nodes and edges track whether an element came from a
topology hint's blueprint (declared) versus was inferred purely from observed events
(observed) — a node/edge can be either, both, or (briefly, before its first event)
declared-only.

### PipelineState (JSON: lowercase string)

```csharp
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PipelineState
{
    NotStarted = 0,
    Completed  = 1,
    Idle       = 2,
    InProgress = 3,
    Active     = 4,
    Errored    = 5,
}
```

Numeric values double as priority order (higher wins) when rolling many pipeline
rows' states up into one node's `HeaderState`.

**State → colour mapping** (`wwwroot/js/d3-graph.js` / `css/d3-graph.css`, used in
both the flow graph and the Timeline):
- `Errored`    → `#F85149` (red)
- `Active`     → `#3FB950` (green, with pulse animation)
- `InProgress` → `#388BFD` (blue)
- `Idle`       → `#8B949E` (grey)
- `Completed`  → `#8B949E` (grey)
- `NotStarted` → faint/dimmed (theme divider colour)

---

## Data Flow: Run Detail Tab

```
User opens RunDetail (src/NxtUI.Web/Pages/RunDetail.razor)
    │
    ├─ RunService.GetRunDetailsAsync(env, runId)  →  RunDetails (metadata, status)
    │
    ├─ StartEventServiceAsync()
    │     ├─ PerformanceEventService: HTTP-loads full event history
    │     └─ RunEventBroker.SubscribeToRunAsync(env, runId, handler)
    │           — in-process pub/sub, NOT a SignalR client call (see below)
    │
    ├─ RunTopologyLoopAsync() [PeriodicTimer, 400ms tick]
    │     ├─ only recomputes when DebouncedTopologyCompute() flagged _topologyDirty
    │     ├─ filters events by _replayTimestamp (null = all, i.e. live)
    │     ├─ TopologyComputationService.ComputeTopology(events, labelFormatter, blueprint)
    │     └─ StateHasChanged() → <D3FlowGraph Topology="@_topology" .../>
    │
    └─ D3FlowGraph (Components/D3FlowGraph.razor) → wwwroot/js/d3-graph.js
          ├─ bm-flow-layout engine lays out nodes/edges (see 12_Custom_Layout_And_Nested_Runs.md)
          ├─ D3 nodes + edges render, including nested/expanded child-run boxes
          └─ d3-animation.js RAF loop (edge dash-flow)
```

### Replay mode

`_replayTimestamp` (nullable `DateTime`):
- `null` → LIVE mode, slider at rightmost position.
- set → REPLAY mode; `RecomputeTopologyAsync()` filters events to
  `Start <= _replayTimestamp`.

Slider uses integer steps (`0..SliderSteps`, not raw timestamps, to avoid a browser
validation tooltip). `SliderToDateTime(value)` clamps the fraction to `[0,1]` and maps
linearly across `[_sliderMin, _sliderMax]`, where the min/max bounds are derived from
every loaded event's Start/Finish **including expanded children's events** — opening
a child-run box extends the replay range to cover it (task #50). `_isPlaying` drives
an auto-advance loop through replay time when enabled.

---

## Data Flow: Timeline Tab

```
User opens Timeline (from RunDetail's [⏱] button, or the sidebar, or the "Add" dialog)
    │
    ├─ InitialRunId set (if opened from a run) → adds that run via the same path as "Add"
    │
    ├─ TimelineTab's Add dialog: search/filter runs by env → constructs a TimelineRun,
    │  appended to _runs — this is how every run (including the initial one) is loaded
    │
    └─ TimelineRun.LoadAsync() (src/NxtUI.Web/Services/TimelineRun.cs)
          ├─ GetRunDetailsAsync()  →  run name, start time, status
          ├─ GetRunEventsAsync()   →  historical events (upsert by CompositeKey)
          └─ StartLiveSubscriptionAsync() — only if Status == Running
                └─ RunEventBroker.SubscribeToRunAsync(env, runId, OnEvent)
                      └─ OnEvent() → UpsertEvent() → DebouncedPushAsync()

DebouncedPushAsync() [debounced]
    └─ BuildPayload(): groupBy, colourBy, filter, stackView, and per-run event arrays
    └─ JS: Timeline.update(key, payload)  (wwwroot/js/d3-timeline.js)
```

### TimelineRun

One instance per loaded run in a Timeline tab (the renamed `TimelineBatch`). Manages:
- Event store keyed by `CompositeKey`.
- Live subscription via `RunEventBroker`, only while the run's status is `Running`.
- `StatusWatchLoopAsync` re-polls run status every 30s and stops the live
  subscription either when status leaves `Running` or after a **3-hour live
  timeout** (`LiveTimeout = TimeSpan.FromHours(3)`) — a run that's realistically
  never going to update again stops costing a subscription.
- CSV import/export: `ExportCsv()` (JS-driven download); importing parses a CSV
  client-side and builds a run via `TimelineRun.CreateFromCsv(runId, env, events)` —
  no live subscription, since there's no real run behind it to watch.

---

## Live Event Delivery: RunEventBroker (not a SignalR push)

The `RunHub` (`/hubs/run`) SignalR hub only handles group membership
(`SubscribeToRun`/`UnsubscribeFromRun`) — **nothing in the app currently pushes data
through it**. Actual live delivery is entirely in-process, via `RunEventBroker`:

```
RunEventBroker (singleton)
  Dictionary<"{env}:{runId}", List<Func<PerformanceEvent, Task>>>
  Publish(env, runId, evt) → fans out synchronously to every subscribed handler
  fires RunWatchStarted / RunWatchStopped on 0→1 / 1→0 subscriber transitions

Two ways an event reaches the broker:
  1. MockRunService implements IPushesOwnRunEvents and calls
     broker.Publish(...) directly from its own simulated-live-run timer.
  2. RunEventWatcher (IHostedService) listens for RunWatchStarted/Stopped and, for
     any IRunService implementation that does NOT self-push (e.g. a real SQL/Mongo-
     backed service), starts a shared 3-second poll loop per watched run and calls
     broker.Publish(...) itself with whatever new events polling turns up.

Subscribers (both go through the same broker, not a hub):
  ├─ PerformanceEventService.OnEvent()  (RunDetail's event accumulator)
  └─ TimelineRun.OnEvent()              (Timeline tab)
```

This design means swapping `IRunService` from the mock to a real Mongo/SQL-backed
implementation (see `01_Architecture.md`) doesn't require any change to how the UI
receives live updates — `RunEventWatcher`'s poll-and-publish path already covers any
`IRunService` that isn't self-pushing.

---

## MockRunService Data Generation

`MockRunService` (`src/NxtUI.Web/Services/MockRunService.cs`) generates two kinds of
demo topology:

- A small, fixed 4-stage chain — `Ingester → Validator → Enricher → Loader` — used
  for a handful of clean, easy-to-read demo runs.
- Larger randomized topologies drawn from a bigger service-name pool
  (`Ingester, Validator, Normaliser, Transformer, Enricher, Router, Loader, Auditor,
  Notifier, Archiver`, ...) paired with a `Source`-class pool (`SchemaValidator,
  RuleEngine, FormatChecker, RetryHandler, DeadLetterProcessor, GeoLookup, RefLookup,
  CacheFiller, TagResolver, MetadataEnricher`, ...) to build varied, less predictable
  topologies for exercising the layout engine and filters against realistic variety.

Don't treat any single fixed chain (including the 4-stage one above) as canonical —
most generated runs draw randomly from the larger pools. Separately, the dedicated
`RUN-DEMO-LAYOUT-HINTS`/`layouthintsdemo.json` and `RUN-DEMO-ROOT`/`nesteddemo.json`
topologies (see `13_Setting_Up_Nested_Runs_And_Topology.md`) are hand-authored, not
randomized — those are the ones to read when you want a specific, reproducible
example of a hint/feature rather than general variety.
