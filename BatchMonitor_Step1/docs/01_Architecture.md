# BatchMonitor — Architecture Overview

## Solution Structure

```
BatchMonitor.sln
├── BatchMonitor.Core/          Class library — domain models, interfaces, real services
└── BatchMonitor/               Blazor Server web app — UI, hubs, mock services, JS/CSS
```

### Why two projects?

`BatchMonitor.Core` was extracted so that:
- Domain models and `IBatchService` can be referenced without pulling in Blazor/SignalR
- `MongoBatchService` and topology computation are independently testable
- A future API worker or background service can reference Core without the web project

---

## Technology Stack

| Layer | Technology |
|-------|-----------|
| UI framework | Blazor Server (.NET 8) |
| Component library | MudBlazor 9.5 |
| Graph visualisation | D3 v7 + dagre 0.8.5 |
| Timeline visualisation | D3 v7 (custom, no dagre) |
| Real-time push | ASP.NET Core SignalR |
| Database (real) | MongoDB (MongoDB.Driver 2.27) |
| Database (mock) | In-memory Dictionary |
| SignalR client | Microsoft.AspNetCore.SignalR.Client 8.0 |

---

## Render Mode

**Blazor Server only** — no WebAssembly, no InteractiveAuto. All component state lives on the server. SignalR is the underlying transport for the Blazor circuit.

Key consequence: JS interop calls (`IJSRuntime.InvokeVoidAsync`) are asynchronous round-trips over the SignalR circuit. This affects how we handle D3 rendering — we never await D3 calls from tight loops.

---

## Multi-Tab Architecture

The app uses a **persistent multi-tab** pattern:
- `TabService` holds a list of `TabModel` items
- `MainLayout` renders **all** tabs simultaneously as `div.bm-content-pane` elements
- Inactive tabs are hidden via CSS `display:none` (class `bm-pane-hidden`)
- `ShouldRender()` returns `IsFocused` so inactive tabs don't re-render on every SignalR push

```
MainLayout
└── TabBar          (horizontal scroll, JS-side drag-and-drop)
└── for each tab:
    └── TabContent  (switch on TabType)
        ├── Home
        ├── Batches
        ├── BatchDetail    (graph + slider + SignalR)
        └── TimelineTab    (multi-batch timeline)
```

### Tab types

```csharp
public enum TabType { Home, Batches, BatchDetail, Timeline }
```

`TabModel.CreateTimeline(runId, env)` sets `EntityId = runId` which `TimelineTab` receives as `InitialRunId`.

---

## BatchMonitor.Core Contents

### Models

| File | Purpose |
|------|---------|
| `BatchSummary.cs` | List-view item: RunId, BatchName, Type, Status, Start, End |
| `BatchDetails.cs` | Detail view: adds Metadata dictionary, Duration computed |
| `PerformanceEvent.cs` | Processing event: ChunkId, Service, Pipeline, Source, Server, ProcessId, Start, Finish, Error, RecordCount |
| `Topology.cs` | Graph model: TopologyNode, PipelineRow, TopologyEdge, PipelineState enum |
| `EnvironmentInfo.cs` | Environment descriptor with badge colour |

### Services

| File | Purpose |
|------|---------|
| `IBatchService.cs` | Interface: GetBatches, Cancel, GetDetails, GetEvents, GetTopology |
| `MongoBatchService.cs` | Real MongoDB implementation |
| `TopologyComputationService.cs` | Pure function: event store → Topology graph |
| `PerformanceEventStore.cs` | In-memory upsert store keyed by CompositeKey |

### Configuration

| File | Purpose |
|------|---------|
| `MongoSettings.cs` | ConnectionString, DatabaseName, CollectionName |
| `AppSettings.cs` | Top-level settings wrapper |

---

## BatchMonitor (Web) — Key Services

| File | Scope | Purpose |
|------|-------|---------|
| `MockBatchService.cs` | Singleton | Generates deterministic fake data; simulates live push via `IHubContext<BatchEventsHub>` every 2s |
| `PerformanceEventService.cs` | Per-tab | Hybrid push+poll event accumulator |
| `SignalRConnectionService.cs` | Scoped (per circuit) | Owns one `HubConnection` to `/hubs/batch-events`; fans events to per-batch callbacks |
| `TabService.cs` | Scoped | Tab list CRUD + reorder |
| `ThemeService.cs` | Scoped | Dark/light theme toggle |
| `EnvironmentSelectorService.cs` | Scoped | Available environments list |
| `TimelineBatch.cs` | Per-tab instance | Per-batch event store + SignalR subscription for Timeline tab |

---

## Hubs

| Hub | Route | Purpose |
|-----|-------|---------|
| `BatchHub` | `/hubs/batch` | Legacy/general hub |
| `BatchEventsHub` | `/hubs/batch-events` | Live event streaming; groups named `{env}:{runId}` |

---

## Program.cs Registration Summary

```csharp
// Core
builder.Services.AddSingleton<IBatchService, MockBatchService>(sp =>
    new MockBatchService(sp.GetRequiredService<IHubContext<BatchEventsHub>>()));

// Web
builder.Services.AddScoped<SignalRConnectionService>();
builder.Services.AddScoped<TabService>();
builder.Services.AddScoped<ThemeService>();
builder.Services.AddScoped<EnvironmentSelectorService>();

// Hubs
app.MapHub<BatchHub>("/hubs/batch");
app.MapHub<BatchEventsHub>("/hubs/batch-events");
```

---

## Static Assets (`wwwroot/`)

```
css/
  app.css           Global styles, tab bar, status chips, layout
  d3-graph.css      Flow graph (BatchDetail) styles
  d3-timeline.css   Timeline tab styles
js/
  app.js            Tab drag-and-drop, wheel scroll
  d3-graph.js       D3 flow graph rendering
  d3-animation.js   Edge dash-flow animation state machine
  d3-timeline.js    D3 timeline rendering (multi-instance)
```

---

## Key Design Decisions (summary — see 03_Decisions.md for rationale)

1. **Blazor Server only** — no WASM complexity, server state simplifies SignalR
2. **Persistent tab panes** — CSS hide/show, not navigation, preserves JS state
3. **D3 for all visualisations** — not chart libraries; full control required
4. **Multi-instance JS pattern** — timeline uses `Map<key, state>` not a singleton
5. **Frozen domain** — timeline x-axis never moves when new data arrives
6. **BatchMonitor.Core split** — testability and future API reuse
