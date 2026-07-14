# NxtUI — Architecture Overview

## Solution Structure

```
NxtUI.sln
├── src/NxtUI.Core/       Class library — domain models, real service implementations
│                         (Mongo/Kafka/SQL), filtering, topology computation
├── src/NxtUI.Protos/     Dynamic protobuf schema handling — parses .proto files at
│                         runtime (protobuf-net.Reflection), no generated C# classes
├── src/NxtUI.Web/        Blazor Server web app — UI, hubs, mock services, JS/CSS
└── tests/NxtUI.Tests/    xUnit v3 test suite covering Core, Protos, and Web
```

### Why split Core/Protos/Web?

- Domain models and `IRunService`/`IKafkaService`/`IMongoService` can be referenced
  without pulling in Blazor/SignalR.
- The real Mongo/Kafka/SQL implementations (see below) are independently testable.
- Dynamic proto decoding (topic message schemas discovered/loaded at runtime, not
  compiled in) is isolated in its own project since it pulls in protobuf-net.Reflection
  and has its own test-only dependencies (Google.Protobuf/Grpc.Tools, used only to
  generate fixture messages for the decoder's own tests).
- A future API worker or background service can reference Core without the web project.

---

## Technology Stack

| Layer | Technology |
|-------|-----------|
| UI framework | Blazor Server (.NET 8) |
| Component library | MudBlazor 9.6 |
| Run/pipeline graph visualisation | D3 v7 + custom `bm-flow-layout` layered-layout engine (own Sugiyama-lite implementation; dagre and ELK are both gone from the render path — see `04_FlowGraph.md`/`12_Custom_Layout_And_Nested_Runs.md`) |
| Timeline visualisation | D3 v7 (custom, no layout library) |
| Memory/treemap visualisations | D3 v7 (`d3-home-treemap.js`, `d3-memory-stacked.js`, `d3-memory-sunburst.js`) |
| Real-time push | ASP.NET Core SignalR |
| Kafka client | Confluent.Kafka 2.14 |
| MongoDB client | MongoDB.Driver 3.9 |
| SQL client | Microsoft.Data.SqlClient 7.0 |
| JSONPath queries (Kafka/Mongo message inspectors) | JsonPath.Net 2.1 |
| Logging | NLog.Web.AspNetCore |
| JS testing | Vitest (`wwwroot/js` is a proper tested subproject — `package.json`, `vitest.config.js`, `*.test.js` files alongside the modules they cover) |

---

## Render Mode

**Blazor Server only** — no WebAssembly, no InteractiveAuto. All component state lives
on the server. SignalR is the underlying transport for the Blazor circuit.

Key consequence: JS interop calls (`IJSRuntime.InvokeVoidAsync`) are asynchronous
round-trips over the SignalR circuit. This affects how D3 rendering is driven — never
await D3 calls from tight loops, and JS modules are imported lazily per-component
(`IJSObjectReference`) rather than loaded globally, so a circuit reconnect/tab close
cleanly disposes just that component's JS state.

---

## Multi-Tab Architecture

The app uses a **persistent multi-tab** pattern, same idea as a browser's own tab bar
but implemented entirely inside one Blazor circuit:
- `TabService` (scoped) holds the list of `TabModel` items for the circuit.
- `MainLayout` renders **all** open tabs simultaneously as separate content panes.
- Inactive tabs are hidden via CSS rather than being torn down, so JS-side state
  (D3 render state, CodeMirror editor instances, live SignalR subscriptions, scroll
  position) survives switching away and back.
- Inactive tabs skip re-rendering on incoming pushes (`ShouldRender()` gated on
  whether the tab is the focused one), so a busy background tab doesn't cost render
  time until you switch to it.

### Tab types (`TabModel.TabType`, `src/NxtUI.Web/Models/TabModel.cs`)

```csharp
public enum TabType
{
    Runs, Services, Pipelines, Kafka, MongoDB, Config, Batches, FileBrowser,
    Settings, RunDetail, Timeline, ServiceDetail, KafkaMessages, KafkaGroups,
    MongoDetail, LogViewer, LogWorkspace, FilterHelp, MemoryGraph, Environment,
    RunStats,
}
```

One tab type per distinct page/view described in `09_Features.md` — e.g. `RunDetail`
carries the run's graph view, `Timeline` the multi-run swimlane view, `LogViewer` an
individual open log file (there can be many of these open at once, one tab each).

---

## NxtUI.Core Contents

### Models (`src/NxtUI.Core/Models/`)

Domain is **runs**, not "batches" — a "batch" naming vestige survives only in
`IBatchCatalogService`/`TabType.Batches`, an unrelated catalog concept, not the core
run/pipeline monitoring model. Key models: `RunSummary`/`RunDetails` (list/detail run
views), `PerformanceEvent` (a single pipeline processing event), `Topology`/
`TopologyNode`/`PipelineRow`/`TopologyEdge` (the run's graph model — see
`02_Models_DataFlow.md`), `TopologyHint` (per-run layout customization — see
`11_Topology_Hints.md`), `EnvironmentInfo`.

### Services (`src/NxtUI.Core/Services/`)

| Interface | Mock (default, always registered) | Real implementations (exist, commented out in `Program.cs` pending live infra) |
|---|---|---|
| `IRunService` | `MockRunService` | `MongoRunService`, `SqlRunService` |
| `IKafkaService`/`IKafkaMonitor`/`IKafkaAdmin` | `MockKafkaService` | (real Kafka-backed implementation via Confluent.Kafka) |
| `IMongoService`/`IMongoReader`/`IMongoAdmin` | `MockMongoService` | (real Mongo-backed implementation via MongoDB.Driver) |
| `IHeartbeatService` | `MockHeartbeatService` | — |
| `IEnvironmentService` | `MockEnvironmentService` | — |

Every environment in this deployment currently runs on mocks; swapping to a real
backend is a `Program.cs` DI registration change, not a rewrite — the real
implementations already exist in Core and are exercised by the test suite.

Also in Core: `TopologyComputationService` (pure function: event store → `Topology`
graph), `IMessageRegistry`/`TopicDeserializerPipeline` (Kafka message decoding,
including the dynamic-proto path via NxtUI.Protos), filtering (`08_FilterSyntax.md`'s
grammar parser/evaluator).

---

## NxtUI.Web — Key Services (`src/NxtUI.Web/Services/`)

| Lifetime | Services |
|---|---|
| Singleton | `EnvironmentConfigLoader`, `TopologyHintLoader`, `KafkaConnectionFactory`, `MongoConnectionFactory`, `OperationTracker`, `RunEventBroker`, `HeartbeatMonitor`, `ILogPathDiscoveryService`, `ILogBrowserService`, `ILogViewerService`, `IEnvConfigService`, `InfraHealthCache`, `IBatchCatalogService`, `ServiceMetricsMonitor` |
| Scoped (per circuit) | `TabService`, `EnvironmentSelectorService`, `ThemeService`, `DateTimeDisplayService`, `ErrorNotificationService`, `RunActionsService` |
| Hosted (background) | `RunEventWatcher`, `HeartbeatMonitor`, `InfraHealthCache`, `ServiceMetricsMonitor`, and conditionally `TestLogGenerator`/`ServerDiagnosticsMonitor` (dev/diagnostics only) |

---

## Hubs

| Hub | Route | Purpose |
|-----|-------|---------|
| `RunHub` | `/hubs/run` | Live run/pipeline event push to circuits watching a given run. |

`ErrorNotificationHubFilter` and (conditionally) `BlazorCircuitFilter` are SignalR hub
filters (cross-cutting error reporting / circuit diagnostics), not separate hubs.

---

## Program.cs Registration Summary

```csharp
// Core — mock implementations, real ones exist and are swap-in registrations
builder.Services.AddSingleton<IRunService, MockRunService>();
builder.Services.AddSingleton<IKafkaService, MockKafkaService>();
builder.Services.AddSingleton<IMongoService, MockMongoService>();
builder.Services.AddSingleton<IHeartbeatService, MockHeartbeatService>();
builder.Services.AddSingleton<IEnvironmentService, MockEnvironmentService>();

// Web — scoped, one instance per Blazor circuit
builder.Services.AddScoped<TabService>();
builder.Services.AddScoped<ThemeService>();
builder.Services.AddScoped<EnvironmentSelectorService>();

// Hosted background services
builder.Services.AddHostedService<RunEventWatcher>();
builder.Services.AddHostedService<HeartbeatMonitor>();
builder.Services.AddHostedService<InfraHealthCache>();
builder.Services.AddHostedService<ServiceMetricsMonitor>();

// Hub
app.MapHub<RunHub>("/hubs/run");
```

---

## Static Assets (`src/NxtUI.Web/wwwroot/`)

```
css/
  app.css               Global styles, tab bar, status chips, layout
  d3-graph.css          Run/pipeline flow graph styles
  d3-timeline.css       Timeline tab styles
  log-viewer.css        Log Viewer styles
js/
  app.js                Tab drag-and-drop, wheel scroll, shared interop helpers
  bm-flow-layout/        Custom layered-layout engine (own package, own tests —
                          see 12_Custom_Layout_And_Nested_Runs.md)
  d3-graph.js            Run/pipeline flow graph rendering (drives bm-flow-layout)
  d3-animation.js        Edge dash-flow animation state machine
  d3-timeline.js         Multi-instance D3 timeline rendering
  d3-home-treemap.js     Home page memory treemap
  d3-memory-stacked.js   Memory Graph stacked/line chart
  d3-memory-sunburst.js  Memory Graph sunburst view
  filter.js              Shared filter-grammar parser/evaluator (08_FilterSyntax.md)
  json-viewer.js         Expandable JSON detail panel (Kafka/Mongo inspectors)
  log-file-access.js     Chromium local-file-access mode for the Log Viewer
  log-viewer.js          Log Viewer rendering/interaction
  log-viewer-parser.js   Log line format grammar (compile/parse), shared with C#
                          via a contract test — see the log-format-grammar files
  purge-stream.js        Streaming progress UI for Kafka/Mongo bulk purge
  run-stats-chart.js     Run Stats page graph view
  text-editor.js         CodeMirror wrapper used by the Config page editor
  workspace.js           Tab/window management glue
  package.json, vitest.config.js, node_modules/   JS is a proper tested subproject;
                          run `node node_modules/vitest/vitest.mjs run` from
                          wwwroot/js to execute the JS suite directly if the
                          node_modules/.bin wrapper isn't runnable in your shell
```

---

## Key Design Decisions (summary — see `03_Decisions.md` for rationale)

1. **Blazor Server only** — no WASM complexity, server state simplifies SignalR.
2. **Persistent tab panes** — CSS hide/show, not navigation, preserves JS/editor/live-
   subscription state across tab switches.
3. **D3 for all visualisations** — not chart libraries; full control required.
4. **Custom layered-layout engine (`bm-flow-layout`)** replaced the earlier
   dagre/ELK-based run graph layout — purpose-built support for role pinning, groups,
   directional/orientation hints, nested run boxes, and per-pipeline edge ports that
   general-purpose layout libraries don't offer out of the box.
5. **Multi-instance JS pattern** — timeline/graph/memory JS modules key their state by
   a per-tab identifier (`Map<key, state>`), not a singleton, so multiple tabs of the
   same view type don't collide.
6. **Mock-first services with real implementations ready** — every `I*Service` has a
   working mock backing the whole app's demo/dev experience, with the real
   Mongo/Kafka/SQL-backed implementation already written in Core and one `Program.cs`
   line away from being live.
7. **Core/Protos/Web split** — testability, future API reuse, and isolating the
   dynamic-proto-decoding dependency footprint from the rest of Core.
