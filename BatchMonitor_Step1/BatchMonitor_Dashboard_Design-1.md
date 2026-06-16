# Batch Monitoring Dashboard — Design Document

> Generated from design session — June 2026
> Covers: shell architecture, Batches tab, Batch Detail tab, API surface, implementation plan

---

## 1. Technology Stack

| Layer | Choice |
|---|---|
| Framework | .NET 8, Blazor Server |
| UI Components | MudBlazor >= 9 |
| Real-time | SignalR (shared connection per session) |
| Graph visualisation | D3.js (client-side, flow graph + slider) |
| Historical/query data | REST API |
| Theme | Dark + Light, user-switchable via Settings |

---

## 2. Shell Architecture

### Layout

```
+---sidebar---+------------------tab bar------------------+
|             | [Batches D1] [⬡ MyBatch U1 ×] [▼]         |
| [☰] Title   +--------------------------------------------+
|             |                                            |
| [🏠] Home   |   Active tab content area                 |
| [📋] Batches|   (or Home dashboard if no tab focused)   |
| [🖥️] Svc    |                                            |
| [📨] Kafka  |                                            |
| [🗄️] Mongo  |                                            |
| [🐛] Errors |                                            |
| [⚙️] Settings|                                           |
|             |                                            |
| ──────────  |                                            |
| [UAT1  ▼]   |                                            |
+-------------+--------------------------------------------+
```

### Sidebar

- **Role:** Launcher only — opens or focuses dashboard tabs
- **Toggle:** Burger icon (☰) top-left; title visible when expanded, hidden when collapsed
- **Collapsed state:** Icons only; tooltip on hover; environment selector collapses to a coloured rounded badge (e.g. `U1`)
- **State persistence:** Collapsed/expanded state and selected environment saved to localStorage

#### Sidebar Item States

| State | Visual |
|---|---|
| Default | Neutral muted colour. No tab open. |
| Open | Accent-tinted icon. Tab exists but not focused. |
| Active | Filled/highlighted icon. Tab currently focused. |

### Global Blazor Services

| Service | Scope | Responsibility |
|---|---|---|
| `TabService` | Scoped | Owns open tab list. Exposes OpenTab, CloseTab, FocusTab. |
| `EnvironmentSelectorService` | Scoped | Owns selected environment. Bidirectional sync between sidebar and Home. |
| `SignalRConnectionService` | Scoped | Single shared SignalR hub connection per session. Tabs subscribe/unsubscribe to groups. |

---

## 3. Theme

- User can switch between **Dark** and **Light** theme in Settings
- Preference persisted to localStorage
- Theme tokens:

| Token | Dark | Light |
|---|---|---|
| Background | `#0E1117` | `#F6F8FA` |
| Surface | `#161B22` | `#FFFFFF` |
| Elevated | `#21262D` | `#EAEEF2` |
| Border | `#30363D` | `#D0D7DE` |
| Muted text | `#8B949E` | `#57606A` |
| Primary text | `#E6EDF3` | `#1F2328` |
| Action accent | `#2F81F4` | `#2F81F4` |

#### Status Colours (both themes)

| Status | Colour | Note |
|---|---|---|
| Running | `#3FB950` (green, animated pulse) | Breathing keyframe animation |
| Completed | `#8B949E` (neutral) | |
| Failed | `#F85149` (red) | Red reserved for errors/failures only — never decorative |

#### Environment Badge Colours

| Tier | Colour |
|---|---|
| Development | Teal / Cyan |
| UAT | Amber / Yellow |
| Staging | Purple |
| Production | Deep Orange |
| Other | Blue Grey |

> **Red is reserved exclusively for errors, failures, and critical alerts.**

---

## 4. Tab System

### Tab Anatomy

```
[⬡ MyBatch  [U1]  ×]
   │          │    └─ close button
   │          └─ environment badge (rounded rectangle)
   └─ type icon + label
```

### Tab Icons (three distinct icons)

| Tab type | Icon |
|---|---|
| Batches dashboard (L1) | List/grid icon |
| Batch detail (L2) | Hexagon / batch-specific icon |
| Timeline comparison (L2) | Timeline/waveform icon |

### Tab Identity — Duplicate Prevention

```
TabId = TabType + EntityId + Environment

dashboard:batches:UAT1
detail:batch:RUN-2024-001:UAT1
detail:timeline:RUN-001+RUN-002:UAT1
dashboard:settings               ← no environment
```

Opening a sidebar item or clicking an entity link when that tab is already open → **focuses existing tab, never opens a duplicate.**

### Tab Overflow

When tabs exceed the tab bar width → **▼ dropdown** at right end lists all overflow tabs. Active tab is never hidden in overflow.

### Tab Lifecycle

- **On open:** Component mounted, REST fetches initial data, SignalR subscriptions established, `CancellationTokenSource` created
- **On focus (already open):** No re-fetch — renders from cached state immediately
- **On close:** CancellationToken cancelled, SignalR unsubscribed, component disposed, focus moves to nearest remaining tab or Home

---

## 5. Home Dashboard

- **Not a tab** — default canvas shown when no tab is focused
- Cannot be closed
- Contains its own environment selector (bidirectionally synced with sidebar via `EnvironmentSelectorService`)
- **Mini summary section** powered by the same batch list endpoint (see §7.1), showing last N batches with status icons
- Other panels: active batch count, error rate, average duration, chunks in flight, quick status indicators for Services / Kafka / MongoDB
- **Data:** SignalR for live counters; REST on load for historical summaries

---

## 6. Batches Dashboard Tab

### Layout

```
┌──────────────────────────────────────────────────────┐
│ [🔍 Filter bar — search RunId / RequestId / Name / Type / Status ] │
├──────────────────────────────────────────────────────┤
│ [●] BatchName   RunID        Type    Started    Duration  Status  │ Actions │
│ [✓] BatchName2  RunID        Type    Started    Duration  Status  │ Actions │
│ ...                                                    │
├──────────────────────────────────────────────────────┤
│ Rows per page: [25 ▼]   < 1 2 3 ... >   Showing 1–25 of 120 │
└──────────────────────────────────────────────────────┘
```

### Features

- **Default:** last 25 batches, ordered by start time descending
- **Pagination controls** with configurable rows per page (e.g. 10 / 25 / 50 / 100)
- **Status icon per row:** Running (green pulse ●), Completed (✓), Failed (✗)
- **Filter bar** (see §6.1)
- **Per-row actions** (icon buttons in same row):
  - 🔍 Open Detail tab
  - ⏱ Open Timeline tab
  - ■ Stop / Cancel batch (only enabled when Running)
- **Home mini-summary** reuses the same endpoint with a small `count` parameter

### 6.1 Filter

Filter is discussed in a dedicated next design step. Parameters will include:
- Free-text search across: RunId, RequestId, Name
- Status (multi-select: Running / Completed / Failed)
- Type (multi-select)
- The endpoint will accept an additional optional `filter` parameter

---

## 7. API Surface

All endpoints include `env` as a parameter (query string or route segment).

### 7.1 Batch List Endpoint

```
GET /api/{env}/batches?before={utcTimestamp}&count={n}&filter={...}
```

**Request parameters:**

| Parameter | Type | Description |
|---|---|---|
| `env` | string | Environment identifier |
| `before` | UTC datetime | Return batches that started before this time |
| `count` | int | Number of results to return (default 25) |
| `filter` | string (optional) | Free-text search: RunId / RequestId / Name |
| `status` | string[] (optional) | Filter by status values |
| `type` | string[] (optional) | Filter by batch type |

**Response per item:**

| Field | Type |
|---|---|
| `batchName` | string |
| `type` | string |
| `runId` | string |
| `status` | string (Running / Completed / Failed) |
| `start` | UTC datetime |
| `end` | UTC datetime? |

**Used by:** Batches dashboard (paginated), Home mini-summary (small count, no filter)

### 7.2 Cancel Batch Endpoint

```
POST /api/{env}/batches/{runId}/cancel
```

Stops the batch identified by `runId` in the given environment. Returns updated status.

### 7.3 Batch Detail — Static Metadata

```
GET /api/{env}/batches/{runId}/details
```

Returns key/value pair metadata about the batch from a non-PerformanceTracker source (SQL Server join or similar). Exact fields TBD. Called once on tab open.

### 7.4 Batch Detail — Topology Snapshot

```
GET /api/{env}/batches/{runId}/topology
```

Returns a pre-computed graph snapshot for fast initial render:
- **Nodes:** service type, instance count, pipelines with message counts
- **Edges:** from service/pipeline → to service/pipeline, message count, estimated waiting count

Called once on tab open as a cold-start optimisation. May return empty/partial data if the batch has just started. After the client has accumulated raw events it owns the topology and no longer calls this endpoint.

### 7.5 Batch Detail — Incremental Events

```
GET /api/{env}/batches/{runId}/events?from={utcTimestamp}
```

Returns all PerformanceTracker documents for the batch with `Start >= from`. Used for:
- Full history load on tab open (`from` = batch start time)
- Incremental polling (`from` = last received event timestamp)
- Optionally replaced by SignalR push for running batches

**Response per event (lean fields only):**

| Field | Type | Notes |
|---|---|---|
| `chunkId` | string | Unique chunk identifier (TypePrefix + IncrementalNumber) |
| `service` | string | Service type name |
| `pipeline` | string | Pipeline name (unique per service) |
| `server` | string | Machine hostname |
| `processId` | int | OS process ID |
| `start` | UTC datetime | Always present |
| `finish` | UTC datetime? | Null if still in progress |
| `error` | string? | Set if this chunk failed |

> **Note:** `finish` may be null on first receipt and filled on a subsequent poll. Client must upsert by `(chunkId, service, pipeline, processId)` key.

---

## 8. Batch Detail Tab

### Tab Identity

```
TabId = detail:batch:{runId}:{env}
Label = batch name (from static details or batch list)
Icon  = batch-specific icon (distinct from Batches dashboard and Timeline icons)
```

### Page Structure

```
┌──────────────────────────────────────────────────────────────────────┐
│ GLOBAL TAB BAR                                                       │
├──────────────────────────────────────────────────────────────────────┤
│ TOP BAR (Blazor, ~40px)                                              │
│ ● Running  OrderBatch  ✓ 1,252  ⟳ 41  ~68% ████████░░  [⏱] [■]  [⌄]│
├──────────────────────────────────────────────────────────────────────┤
│ DETAILS STRIP — visible only when top bar expanded (~200px max)      │
│ RunId        abc-123-def       Type         FullLoad                 │
│ RequestId    req-789           StartTime    2026-06-15 14:22 UTC     │
│ Duration     00:14:22          Environment  UAT1                     │
│ ...          ...               ...          ...    (scrollable grid) │
├──────────────────────────────────────────────────────────────────────┤
│                                                                      │
│   D3 FLOW GRAPH (flex: 1, fills remaining height)                   │
│   zoom · pan · dash-flow edge animation                             │
│                                                                      │
│   [⤢ Fit]  (absolutely positioned button, top-right of canvas)      │
│                                                                      │
├──────────────────────────────────────────────────────────────────────┤
│ SLIDER (~48px)                                                       │
│ [14:22] ────────────────────●──────────────── [14:35]  [● LIVE]     │
└──────────────────────────────────────────────────────────────────────┘
```

### 8.1 Top Bar

A single fixed-height Blazor row (~40px) above the D3 canvas. Not floating — part of the layout flow.

#### Elements (left to right)

| Element | Detail |
|---|---|
| Status chip | Coloured dot + label (Running / Completed / Failed). Green pulse animation when Running. |
| Batch name | Plain text |
| Done count | `✓ N` |
| In-progress count | `⟳ N` |
| Inline progress bar + label | Compact bar, `~N% estimated`. Colour matches pipeline state (see §8.3). |
| Open Timeline button | `[⏱]` icon button — opens or focuses Timeline tab for this batch |
| Cancel button | `[■ Cancel]` — visible only when Running. Requires confirmation before firing. |
| Expand/collapse chevron | `[⌄]` / `[⌃]` — far right. Toggles the details strip. |

#### Details Strip (expanded state)

- Revealed below the top bar on chevron click; pushes the D3 canvas down
- Fixed max height ~200px with internal vertical scroll if KV pairs overflow
- Two-column key/value grid (~30 items from `/details` endpoint)
- KV data is static after load — no re-render on live data updates
- Only the live elements in the collapsed bar (status chip, counts, progress bar) update on data push; `ShouldRender()` guards prevent the KV grid from re-rendering
- Expand/collapse state persisted to localStorage per tab type
- D3 canvas listens to container resize event and reflows layout on expand/collapse

---

### 8.2 D3 Flow Graph

#### Overview

A directed acyclic graph (DAG) rendered entirely in SVG/JS. Blazor never touches the SVG — it only pushes data snapshots via `IJSRuntime.InvokeVoidAsync`. D3 owns all rendering, animation, zoom, and pan.

#### Data model

- **Node** = one service type
- **Pipeline row** = one pipeline within that service (unique name per service, one input topic)
- **Edge** = directed connection from a specific source pipeline row to a specific destination pipeline row

Edges are inferred client-side: for each `chunkId`, sort events by `start` time. The ordered sequence of `(service, pipeline)` pairs gives the directed edges. A pipeline can fan out (multiple outgoing edges) and fan in (multiple incoming edges).

#### Node anatomy

```
┌────────────────────────────────────────┐
│  OrderProcessor            ×4      [↗] │  ← service name · instance count · open service details
├────────────────────────────────────────┤
│ ▐ order-ingest                         │  ← pipeline name (coloured left border = state)
│   ████████████░░░░  ✓ 312   ⟳ 38      │  ← progress bar · done count · in-progress count
├────────────────────────────────────────┤
│ ▐ order-retry                          │
│   ██████████████░░  ✓ 298   ⟳ 4       │
└────────────────────────────────────────┘
```

- **Service header:** service name, instance count (`×N`), open-service-details icon button `[↗]`
- **Pipeline rows:** one per pipeline. Left coloured border reflects pipeline state. Progress bar and counts per row.
- **Topic name:** not shown in the node — appears only in the pipeline row hover tooltip
- **Progress bar:** always shown. Denominator = total seen (done + in-progress) as proxy, prefixed with `~`. Revisit once schema is confirmed.

#### Pipeline row states and colours

| State | Left border / bar colour | Condition |
|---|---|---|
| Not started | Dim surface, muted label | No events yet |
| In progress | `#388BFD` blue | Messages with finish null present |
| Active | `#3FB950` green | Finish events arriving within last 10s |
| Idle | `#8B949E` cool grey | No activity for >10s, not completed |
| Completed | `#8B949E` + ✓ icon | All messages finished |
| Error | `#F85149` red | Any message has error field set |

#### Service node header state

Derived from the worst/most active pipeline, evaluated in priority order:

| Priority | Condition | Header accent |
|---|---|---|
| 1 (highest) | Any pipeline errored | Red `#F85149` |
| 2 | Any pipeline active (green) | Green `#3FB950` + pulse |
| 3 | Any pipeline in-progress (blue) | Blue `#388BFD` |
| 4 | All pipelines idle | Grey `#8B949E` |
| 5 | All pipelines completed | Grey + ✓ |
| 6 | Not started | Dim, no accent |

#### Pipeline row hover tooltip (JS-managed HTML, not MudTooltip)

```
┌──────────────────────────────────────────────────┐
│ order-ingest                                     │
│ Topic: order-events-uat1        [↗ Open Kafka]   │
├──────────────────────────────────────────────────┤
│ server01  PID 4821   ✓ 78   ⟳ 10               │
│ server01  PID 4822   ✓ 81   ⟳ 9                │
│ server02  PID 5103   ✓ 76   ⟳ 11               │
│ server02  PID 5104   ✓ 77   ⟳ 8                │
└──────────────────────────────────────────────────┘
```

- Positioned relative to the hovered pipeline row; dismisses on mouse leave
- Instances sorted by server name then PID
- `[↗ Open Kafka]` opens the Kafka topic tab for that topic
- Tooltip is a positioned `<div>` managed entirely in JS — zero Blazor re-render cost

#### Edge design

Edges connect a **source pipeline row** to a **destination pipeline row** at port level (emerge from right side of source row, arrive at left side of destination row).

| Property | Behaviour |
|---|---|
| Thickness | Log-scaled with message count (min 1px, max 6px) |
| Colour | Inherits source pipeline row state colour |
| Label | `✓ N  ⟳ N` — done and waiting counts. Waiting = ~N (tilde = derived) |
| Animation | Dash flow: animated dashes travel in flow direction, speed ∝ throughput |
| On activity spike | Edge brightens briefly (~300ms) |
| High throughput | Dash gap shrinks toward zero; slight thickness increase |

#### Queue depth / waiting estimate

For edge A/pipelineX → B/pipelineY:
`waiting ≈ count of chunkIds where A/pipelineX has finish set but B/pipelineY has no event yet`
Labelled `~N waiting` on the edge.

#### Layout

- **Algorithm:** Hierarchical DAG (dagre or d3-dag), left-to-right. Force-directed avoided for layout stability.
- **Nodes are variable height** depending on pipeline count; dagre treats each service block as a single node with multiple ports.
- **Debounced recompute:** 400–600ms. Timer resets on each new event batch. Layout committed only when updates pause.
- **On recompute:** existing nodes animate to new positions (~300ms ease); new nodes fade + scale in; edges redraw after node transitions complete.
- **Early state:** single known node fills majority of canvas; graph grows outward as topology is discovered.

#### Zoom, pan, and cursor

- D3 zoom behaviour on SVG container. Mouse wheel zooms, drag pans when zoomed.
- Container has `overflow: auto` scrollbars as fallback for very large graphs.
- **Cursor rules (managed entirely in JS):**

| State | Cursor |
|---|---|
| Default (scale = 1, not panning) | `default` |
| Zoomed in (scale > 1) | `grab` |
| Actively dragging | `grabbing` |
| Hovering node or pipeline row | `pointer` |
| Hovering edge | `pointer` |

- **Fit to view button:** `[⤢]` absolutely positioned over canvas (top-right). Resets zoom/pan to show full graph. HTML button, not SVG.

#### Cold-start states

| Condition | Graph display |
|---|---|
| Zero events yet | Empty canvas, "Waiting for first events…" + subtle pulse |
| Partial topology (batch just started) | Shows known nodes/edges; grows as data arrives |
| Complete batch | Full graph from topology snapshot, enriched with raw events for slider |

---

### 8.3 Global Progress Bar

Shown inline in the top bar:

```
progress ≈ count(finish is not null across all chunk × service × pipeline combinations)
          / count(all expected chunk × service × pipeline combinations)
```

- Denominator: maximum hop count observed × total unique chunkIds seen (adjusts dynamically)
- Labelled `~N% estimated`
- Bar colour matches the dominant pipeline state (green if active, blue if in-progress, grey if idle/completed)
- Updates in real time as new events arrive; implemented as a shallow Blazor component with value-changed guard

---

### 8.4 Slider

```
[batch start time] ────────────●─────────── [end time / last update]
```

- **Range:** batch start (left) → completion time if finished, or last received event time if running
- **Right edge** updates automatically as new data arrives in Live mode
- **Pure client-side:** scrubbing triggers no network request

#### Slider Modes

| Mode | Condition | Behaviour |
|---|---|---|
| **LIVE** | Slider at right edge | Graph animates, receives new data, counts increment |
| **REPLAY** | Slider anywhere else | Graph frozen at time T; new data accumulates silently |

- `● LIVE` badge (green pulse) near slider in Live mode
- `REPLAY` label (grey) when scrubbing to a past time
- Snapping slider to right edge returns to Live mode

#### Graph state at time T (Replay mode)

- Filter accumulated events to those where `start <= T`
- Events where `finish > T` (or finish null at T) are considered in-progress at T
- Node counts, edge counts, and waiting estimates all reflect the T-filtered dataset
- **Topology:** current (most up-to-date) topology reused for layout stability — only counts and states rewind

---

### 8.5 Data Loading Strategy

| Step | Action |
|---|---|
| Tab open | Call `/details` (static metadata, once) |
| Tab open | Call `/topology` (graph snapshot for fast render, once) |
| Tab open | Call `/events?from={batchStart}` (full history load) |
| Client catches up | Client owns topology from this point — no further topology calls |
| Polling / SignalR | Call `/events?from={lastEventTimestamp}` incrementally |
| New service/pipeline discovered | New node/row fades in; debounced layout recompute triggered |
| Slider scrub | Client-side filter on in-memory event store — no network call |

---

## 9. Performance Architecture

### Rendering boundary

D3 and all animation live entirely in JavaScript/SVG. Blazor never re-renders the graph.

| Layer | Owner | Responsibility |
|---|---|---|
| Data fetching, state, tab lifecycle | Blazor | Polls/receives events, maintains in-memory store |
| Top bar, details strip, slider | Blazor/MudBlazor | Standard components, guarded with `ShouldRender()` |
| SVG canvas, nodes, edges, animation | D3 / JS | Full ownership; updated via `IJSRuntime.InvokeVoidAsync` snapshot push |
| Hover tooltips inside canvas | JS-managed HTML div | No MudTooltip — zero Blazor re-render cost |

Blazor pushes a plain JSON snapshot to D3 on each data update. D3 diffs and updates the SVG. No Blazor involvement in animation frames.

### Multi-tab performance

| Concern | Mitigation |
|---|---|
| Unfocused tab consuming render budget | `ShouldRender() => isFocused` on tab components |
| Unfocused canvas consuming GPU | `display: none` on D3 canvas when tab not focused |
| Polling loops stacking | Unfocused tabs throttle to 10s cadence; restore fast cadence on focus |
| Completed batch continuing to poll | Polling stops on batch completion event |
| SignalR overhead | One shared connection per session; tabs subscribe/unsubscribe to groups |
| KV grid re-rendering on live data | Static after load; `ShouldRender()` guard prevents unnecessary re-renders |

### Render mode

Blazor Server is retained. The JS interop boundary (not render mode) is the key performance isolation. `InteractiveAuto` / Wasm is not used — the Wasm download cost and mode-switch disruption outweigh the benefit given that the hot path is already fully client-side via D3.

---

## 10. Timeline Comparison Tab

- Opens from: Batches dashboard (multi-select + Compare) or top bar `[⏱]` button on Batch Detail
- **Tab icon:** distinct timeline/waveform icon
- Multiple batches overlaid; each gets a distinct colour
- Time axis is **relative** (offset from batch start) by default
- Absolute time shown on hover via tooltip
- Slider affects all loaded batches simultaneously
- Additional batches can be added from within the tab
- **Data:** Progressive REST per batch (`/events`); optional SignalR if a batch is still running
- Pipe-level detail is in scope for this tab — deferred to dedicated design session

---

## 11. Implementation Plan

The following steps are designed to be executed sequentially by a coding agent. Each step is self-contained and produces a testable increment.

---

### Step 1 — Shell, Theme, Sidebar, Tab Bar

**Goal:** Render the application shell with no real data.

- Blazor Server project scaffolding with MudBlazor >= 9
- Implement `TabService`, `EnvironmentSelectorService`, `SignalRConnectionService` (stub)
- Sidebar: icons, labels, burger toggle, collapsed/expanded state, localStorage persistence
- Environment selector in sidebar: dropdown, tier-based badge colour, collapsed badge
- Tab bar: open/close/focus tabs, environment badge per tab, overflow ▼ dropdown
- Home canvas: placeholder content, environment selector synced with sidebar
- Dark / Light theme toggle wired to Settings stub, persisted to localStorage
- No real API calls — all data hardcoded or mocked

**Acceptance:** Shell renders, tabs open/close/focus correctly, theme switches, sidebar collapses.

---

### Step 2 — Batches Dashboard Tab (read-only, no filter)

**Goal:** Render the Batches tab with live data from the batch list endpoint.

- Implement `GET /api/{env}/batches` endpoint (env, before, count)
- Batches tab component: table with columns (status icon, name, runId, type, start, duration, status chip)
- Status icons with correct colours; Running status has CSS pulse animation
- Pagination controls: rows-per-page selector (10/25/50/100), page navigation
- Default load: last 25 batches
- Per-row action icons: Open Detail (navigates — stub tab for now), Open Timeline (stub), Cancel (POST cancel endpoint, confirm dialog, disabled if not Running)
- Implement `POST /api/{env}/batches/{runId}/cancel` endpoint
- Home mini-summary panel using same endpoint (count=5, no pagination)

**Acceptance:** Batches list renders with real data, pagination works, cancel fires correctly.

---

### Step 3 — Batches Dashboard Filter

**Goal:** Add the filter bar to the Batches tab.

- Filter bar UI: free-text input (searches RunId / RequestId / Name), status multi-select, type multi-select
- Extend `GET /api/{env}/batches` with optional `filter`, `status[]`, `type[]` parameters
- Filter state reflected in URL query params (deep-linkable)
- Filter resets pagination to page 1
- Filter bar also available in Home mini-summary (condensed version)

**Acceptance:** Filtering narrows results correctly across all filter dimensions.

---

### Step 4 — Batch Detail Tab Shell + Top Bar

**Goal:** Open a Batch Detail tab with the top bar and static metadata — no graph yet.

- Implement `GET /api/{env}/batches/{runId}/details` endpoint
- Batch Detail tab opens with correct TabId, label (batch name), and batch-specific icon
- Top bar: status chip, batch name, done count, in-progress count, inline progress bar, Open Timeline button `[⏱]`, Cancel button `[■]` (confirm dialog, disabled if not Running), expand/collapse chevron `[⌄/⌃]`
- Details strip: expands below top bar on chevron click, two-column KV grid (~30 items), scrollable, max height ~200px
- Details strip collapse state persisted to localStorage
- Graph area: empty canvas placeholder with "Loading…" state
- Bottom slider: rendered but disabled/empty

**Acceptance:** Tab opens, top bar renders correctly, details strip expands/collapses, static metadata displays.

---

### Step 5 — Incremental Event Loading + In-Memory Store

**Goal:** Client accumulates raw events and maintains an in-memory event log.

- Implement `GET /api/{env}/batches/{runId}/events?from=` endpoint (returns lean PerformanceTracker fields including `pipeline`)
- Client event store: in-memory dictionary keyed by `(chunkId, service, pipeline, processId)`, upsert on each poll
- On tab open: call with `from = batchStart`, load full history
- Polling loop: call with `from = lastEventTimestamp`, merge new/updated events
- Track last event timestamp from received data
- Unfocused tabs throttle poll cadence to 10s; restore on focus
- No graph rendering yet — verify store populates correctly (console log or raw count display)
- CancellationToken wired to tab close → stops polling

**Acceptance:** Event store populates and updates incrementally; tab close stops polling; unfocused throttle works.

---

### Step 6 — Topology Inference + D3 Flow Graph (static render)

**Goal:** Infer graph topology from event store and render with D3 (no animation yet).

- Implement `GET /api/{env}/batches/{runId}/topology` endpoint (returns nodes with pipelines, edges at pipeline-row level)
- Client topology computation: from event store, infer service nodes, pipeline rows, and pipeline-to-pipeline edges
- D3 dagre layout: left-to-right hierarchical, variable node height per pipeline count, port-level edge routing
- Render service nodes (header + pipeline rows with progress bar, done/in-progress counts)
- Render directed edges with label (`✓ N  ⟳ N`)
- Status colours applied to pipeline rows and node headers per §8.2
- Debounced layout recompute (400–600ms)
- New nodes fade + scale in; existing nodes animate to new positions (~300ms)
- Fit-to-view button; zoom + pan with correct cursor rules (default / grab / grabbing / pointer)
- Cold-start state: "Waiting for first events…"
- `ShouldRender()` guard on unfocused tab; `display: none` on canvas when tab not focused

**Acceptance:** Flow graph renders correctly from real data; layout stable under updates; cursor behaviour correct.

---

### Step 7 — Edge Animation + Hover Tooltips

**Goal:** Add dash-flow animation to edges and JS-managed tooltips to pipeline rows.

- Dash-flow animation on edges: dashes animate in flow direction, speed proportional to throughput
- On activity spike: edge brightens briefly (~300ms)
- High throughput: dash gap shrinks, edge thickens slightly
- Node header pulse animation when any pipeline is active (green state)
- Pipeline row hover tooltip (JS HTML div, not MudTooltip): pipeline name, topic name + Open Kafka link, instance list (server / PID / done / in-progress), sorted by server then PID
- Service node `[↗]` button opens Service Details tab (stub for now)
- Kafka `[↗]` link in tooltip opens Kafka topic tab (stub for now)

**Acceptance:** Animation scales gracefully under high message volume; tooltips display correctly; no Blazor re-render triggered by hover.

---

### Step 8 — Slider + Replay Mode

**Goal:** Wire the bottom slider to the event store for time-travel.

- Top bar progress bar computes and displays `~N% estimated` from event store
- Slider renders with correct time range (batch start → last event time / completion time)
- Slider right edge updates as new events arrive in Live mode
- LIVE / REPLAY mode toggle with badge indicator near slider
- Replay mode: graph re-renders at time T using client-side filter on event store (topology reused, counts rewind)
- Snapping to right edge returns to Live mode
- New data accumulates silently in Replay mode; does not affect graph until Live is restored

**Acceptance:** Slider scrubs correctly; LIVE/REPLAY modes behave as specified; no network calls on scrub.

---

### Step 9 — SignalR Live Push for Running Batches

**Goal:** Replace polling with SignalR push for currently running batches.

- Extend `SignalRConnectionService` to support batch event groups
- On tab open for a Running batch: subscribe to SignalR group `{runId}:{env}`
- Server pushes new PerformanceTracker events to subscribed clients
- Client merges pushed events into existing event store (same upsert logic as polling)
- On batch completion: server sends completion event; client updates status, stops live mode, sets slider max to completion time
- Tab close: unsubscribe from SignalR group

**Acceptance:** Running batch updates in real time via push; no polling overhead for running batches.

---

### Step 10 — Timeline Comparison Tab

**Goal:** Implement the Timeline Comparison tab.

- Tab opens from Batches dashboard multi-select or Batch Detail top bar `[⏱]` button
- Correct tab icon (timeline/waveform)
- Multiple batches overlaid, each with a distinct colour
- Relative time axis (offset from each batch's start)
- Absolute time on hover tooltip
- Shared slider affecting all loaded batches simultaneously
- Add batch button within the tab
- SignalR subscription per running batch in the comparison set
- Pipe-level detail design: covered in separate design session before implementation

**Acceptance:** Timeline renders and compares multiple batches; relative time axis works correctly.

---

## 12. Open Questions / Deferred Decisions

| Topic | Status |
|---|---|
| Filter bar detailed UX (filter chips, clear all, URL state) | Deferred to next design session |
| Exact fields returned by `/details` endpoint (~30 KV pairs) | TBD — depends on SQL Server schema |
| Progress bar denominator refinement | TBD — revisit once schema confirmed |
| Minimap for large topologies (>10 nodes) | Deferred — add if needed |
| SignalR vs polling decision point (which batches get push vs poll) | Deferred to Step 9 |
| Error state visualisation in the flow graph (failed chunks, error nodes) | Deferred |
| Timeline Comparison tab detailed design (including pipe-level detail) | Deferred to dedicated design session |
| Exact debounce threshold (400–600ms range) | Tune during Step 6 |
| Services dashboard design | Deferred |
| Kafka dashboard design | Deferred |
| MongoDB dashboard design | Deferred |
