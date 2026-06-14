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
- **Nodes:** service type, instance count, total messages processed so far
- **Edges:** from → to, message count, estimated pending count

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
| `server` | string | Machine hostname |
| `processId` | int | OS process ID |
| `start` | UTC datetime | Always present |
| `finish` | UTC datetime? | Null if still in progress |
| `error` | string? | Set if this chunk failed |

> **Note:** `finish` may be null on first receipt and filled on a subsequent poll for the same `chunkId` + `service` pair. Client must upsert by `(chunkId, service, processId)` key.

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
┌──────────────────────────────────────────────────────────────┐
│ GLOBAL TAB BAR: [Batches D1] [⬡ MyBatch U1 ×] [⏱ Timeline ×] [▼] │
├──────────────────────────────────────────────────────────────┤
│                                                              │
│   D3 FLOW GRAPH (fills full content area)                   │
│   zoom + pan (D3 zoom behaviour)                            │
│   live edge animation                                        │
│                                                              │
│   ┌─────────────────────────────┐  (floating, top area)     │
│   │ SUB-HEADER PANEL            │  translucent, collapsible │
│   │ [● Running] MyBatch         │  backdrop-filter: blur    │
│   │ RunID: abc-123              │  collapsed by default,    │
│   │ Type: FullLoad  14:22 UTC   │  expands on hover         │
│   │ Duration: 00:12:34          │  [■ Cancel] [⏱ Timeline]  │
│   └─────────────────────────────┘                           │
│                                    ┌──────────┐             │
│                                    │ [─]Details│  floating  │
│                                    │ ──────────│  right     │
│                                    │ k: v      │  translucent│
│                                    │ k: v      │  backdrop  │
│                                    │ ...       │  blur      │
│                                    └──────────┘             │
├──────────────────────────────────────────────────────────────┤
│ ░░░░░░░░░░░░░░░░░░░░░░░░░░░░  ~67% estimated   [● LIVE]     │
│ [14:22] ────────────────────●──────────────── [14:35]       │
└──────────────────────────────────────────────────────────────┘
```

### 8.1 Floating Panels

Both panels are **floating above the graph**, translucent with backdrop blur (`backdrop-filter: blur(8px)`), and do not consume layout space from the D3 canvas.

#### Sub-header Panel (top-left area)
- **Collapsed default:** shows a compact single line — status chip + batch name only
- **Expanded on hover:** full detail — RunId, Type, start time, live duration ticker, Cancel button, Open Timeline button
- Cancel button only visible/enabled when status is Running
- Panel state (collapsed/expanded) NOT persisted — resets to collapsed on tab open

#### Details Panel (right side)
- **Collapsed default:** narrow vertical strip showing "Details" label rotated 90°
- **Expanded on click or hover:** full key/value list with vertical scrollbar if long
- `backdrop-filter: blur(8px)` + semi-transparent surface colour
- When expanded, graph pan/zoom centre shifts left slightly
- Collapses automatically when user starts interacting with the graph
- Collapse/expand state persisted to localStorage per tab type

### 8.2 D3 Flow Graph

#### What it shows

A directed acyclic graph (DAG) where:
- **Nodes** = service types involved in the batch
- **Edges** = message handoff between service types (inferred from chunk journey)
- **Edge labels / thickness** = message count and estimated pending

#### Topology inference (client-side)

For each unique `chunkId`, sort all events by `start` time. The ordered sequence of `service` values gives the directed edges. Collapse across all chunks to get the full graph topology. This is computed incrementally as events accumulate.

- **Source service** = service that first appears for a given `chunkId` with no prior service in the chain
- **Downstream service** = service that receives the same `chunkId` after an upstream service's `finish`
- **Chunk name format:** TypePrefix + IncrementalNumber — prefix has no mapping to service type

#### Node design

Each node displays:
- Service type name
- Instance count (e.g. `×4`)
- Total messages processed (aggregate count)
- **Hover tooltip:** per-instance breakdown (hostname / PID + count)
- Node brightness / saturation reflects recent throughput — dim when idle, lit when active

#### Edge animation

- **Low throughput (< threshold):** individual particle dots travel along the edge
- **High throughput:** edge brightness / thickness pulses proportionally to throughput in the last N seconds — avoids particle blizzard at scale
- Threshold is adaptive — automatically switches modes based on observed message rate

#### Queue depth estimation

For edge A → B: pending ≈ count of `chunkId` values where A has `finish` set but B has no event yet (or B's `start` is null). Labelled as `~N pending` (tilde signals it is derived/approximate).

#### Layout

- **Algorithm:** Hierarchical DAG layout (dagre or d3-dag) — left-to-right or top-to-bottom. Avoids force-directed to ensure layout stability
- **Stability:** Layout recompute is **debounced (400–600ms)**. New updates arriving within the debounce window reset the timer. Only when updates stop flowing is a new layout committed
- **On recompute:**
  - Existing nodes animate smoothly to new positions (D3 transition ~300ms ease)
  - New nodes fade + scale in at their computed position
  - Edges redraw after node transitions complete
- **Early state:** When only one service is known, that node occupies the majority of the canvas. Graph grows outward as topology is discovered
- **Zoom + pan:** D3 zoom behaviour. Mouse wheel zoom, drag to pan. "Fit to view" button resets to show full graph

### 8.3 Global Progress Bar

Displayed above the slider:

```
progress ≈ count(finish is not null across all chunk × service pairs)
          / count(all expected chunk × service pairs)
```

- Denominator uses maximum hop count observed × total unique chunkIds seen (adjusts dynamically)
- Labelled `~N% estimated` with qualifier that softens early-run inaccuracy
- Updates in real time as new events arrive

### 8.4 Slider

```
[batch start time] ────────────●─────────── [end time / last update]
```

- **Range:** batch start (left, 0) → completion time if finished, or last received event time if still running
- **Right edge updates** automatically as new data arrives when in Live mode
- **Pure client-side:** scrubbing does not trigger any network request

#### Slider Modes

| Mode | Condition | Behaviour |
|---|---|---|
| **LIVE** | Slider at right edge | Graph animates, receives new data, counts increment |
| **REPLAY** | Slider anywhere else | Graph frozen at time T, animation paused, new data accumulates silently in background |

- A **`● LIVE`** badge (green pulse) appears near the slider when in Live mode
- A **`REPLAY`** label (grey) appears when scrubbing to a past time
- Snapping slider back to right edge returns to Live mode

#### Graph state at time T (Replay mode)

- Filter accumulated events to those where `start <= T`
- Events where `finish > T` (or finish is null at T) are considered in-progress at T
- Node counts, edge counts, and pending estimates all reflect the T-filtered dataset
- **Topology:** current (most up-to-date) topology is reused even in Replay mode for layout stability — only the counts and states rewind, not the node/edge set itself

### 8.5 Data Loading Strategy

| Step | Action |
|---|---|
| Tab open | Call `/details` (static metadata, once) |
| Tab open | Call `/topology` (graph snapshot for fast render, once) |
| Tab open | Call `/events?from={batchStart}` (full history load) |
| Client catches up | Client owns topology from this point — no more topology endpoint calls |
| Polling / SignalR | Call `/events?from={lastEventTimestamp}` incrementally |
| New service discovered | New node fades in, debounced layout recompute triggered |
| Slider scrub | Client-side filter on in-memory event log — no network call |

#### Cold-start states

| Condition | Graph display |
|---|---|
| Zero events yet | Empty canvas with "Waiting for first events…" + subtle pulse |
| Partial topology (batch just started) | Shows known nodes/edges, grows as data arrives |
| Complete batch | Full graph from topology snapshot, enriched with raw events for slider |

---

## 9. Timeline Comparison Tab

- Opens from: Batches dashboard (multi-select + Compare) or Batch Detail sub-header button
- **Tab icon:** distinct timeline/waveform icon
- Multiple batches overlaid; each gets a distinct colour
- Time axis is **relative** (offset from batch start) by default
- Absolute time shown on hover via tooltip
- Slider affects all loaded batches simultaneously
- Additional batches can be added from within the tab
- **Data:** Progressive REST per batch (`/events`); optional SignalR if a batch is still running

---

## 10. Implementation Plan

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

### Step 4 — Batch Detail Tab Shell + Static Data

**Goal:** Open a Batch Detail tab with static metadata and floating panels — no graph yet.

- Implement `GET /api/{env}/batches/{runId}/details` endpoint
- Batch Detail tab opens with correct TabId, label (batch name), and batch-specific icon
- Sub-header floating panel: compact collapsed state, expands on hover, shows RunId / Type / start / duration ticker / status chip / Cancel / Open Timeline buttons
- Details floating panel: right-side collapsible, backdrop blur, vertical scroll, key/value list from `/details`
- Both panels styled with translucent surface + `backdrop-filter: blur(8px)`
- Details panel collapse state persisted to localStorage
- Graph area: empty canvas placeholder with "Loading…" state
- Bottom slider: rendered but disabled/empty

**Acceptance:** Tab opens, both panels work, static metadata displays correctly.

---

### Step 5 — Incremental Event Loading + In-Memory Store

**Goal:** Client accumulates raw events and maintains an in-memory event log.

- Implement `GET /api/{env}/batches/{runId}/events?from=` endpoint (returns lean PerformanceTracker fields)
- Client event store: in-memory dictionary keyed by `(chunkId, service, processId)`, upsert on each poll
- On tab open: call with `from = batchStart`, load full history
- Polling loop: call with `from = lastEventTimestamp`, merge new/updated events
- Track last event timestamp from received data
- No graph rendering yet — just verify store is populated correctly (can log to console or show raw count)
- CancellationToken wired to tab close → stops polling

**Acceptance:** Event store populates and updates incrementally; tab close stops polling cleanly.

---

### Step 6 — Topology Inference + D3 Flow Graph (static render)

**Goal:** Infer graph topology from event store and render with D3 (no animation yet).

- Implement `GET /api/{env}/batches/{runId}/topology` endpoint
- Client topology computation: from event store, infer nodes (service types, instance counts, processed counts) and edges (from→to, message count, pending estimate)
- D3 dagre layout: left-to-right hierarchical layout
- Render nodes (label, instance count, processed count) and directed edges (label with count)
- Debounced layout recompute (400–600ms)
- New nodes fade+scale in; existing nodes animate to new positions on recompute
- "Fit to view" button; zoom + pan via D3 zoom behaviour
- Cold-start state: "Waiting for first events…" when event store is empty

**Acceptance:** Flow graph renders correctly from real data; layout is stable under updates.

---

### Step 7 — Edge Animation + Node Throughput Indicators

**Goal:** Add live visual feedback to the flow graph.

- Running status pulse on nodes (brightness/saturation reflects recent throughput)
- Edge animation: particle dots at low throughput, edge pulse/thickness at high throughput
- Throughput threshold auto-adapts based on observed message rate
- "~N pending" label per edge derived from queue depth estimate
- Hover tooltips on nodes: per-instance breakdown (hostname / PID / count)

**Acceptance:** Graph feels alive; animation scales gracefully under high message volume.

---

### Step 8 — Slider + Replay Mode

**Goal:** Wire the bottom slider to the event store for time-travel.

- Progress bar: compute and display `~N% estimated` from event store
- Slider renders with correct time range (batch start → last event time / completion time)
- Slider right edge updates as new events arrive
- LIVE / REPLAY mode toggle: badge indicator near slider
- Replay mode: graph re-renders at time T using client-side filter on event store (topology reused, counts rewind)
- Snapping to right edge returns to Live mode
- New data continues accumulating silently in Replay mode; does not affect the graph until slider returns to Live

**Acceptance:** Slider scrubs correctly; LIVE/REPLAY modes behave as specified; no network calls on scrub.

---

### Step 9 — SignalR Live Push for Running Batches

**Goal:** Replace polling with SignalR push for currently running batches.

- Extend `SignalRConnectionService` to support batch event groups
- On tab open for a Running batch: subscribe to SignalR group for `{runId}:{env}`
- Server pushes new PerformanceTracker events to subscribed clients
- Client merges pushed events into existing event store (same upsert logic as polling)
- On batch completion: server sends completion event, client updates status, disables live mode, sets slider max to completion time
- Tab close: unsubscribe from SignalR group

**Acceptance:** Running batch updates in real time via push; no polling overhead.

---

### Step 10 — Timeline Comparison Tab

**Goal:** Implement the Timeline Comparison tab.

- Tab opens from Batches dashboard multi-select or Batch Detail sub-header
- Correct tab icon (timeline/waveform)
- Multiple batches overlaid, each with a distinct colour
- Relative time axis (offset from each batch's start)
- Absolute time on hover tooltip
- Shared slider affecting all loaded batches simultaneously
- Add batch button within the tab
- SignalR subscription per running batch in the comparison set

**Acceptance:** Timeline renders and compares multiple batches; relative time axis works correctly.

---

## 11. Open Questions / Deferred Decisions

| Topic | Status |
|---|---|
| Filter bar detailed UX (filter chips, clear all, URL state) | Deferred to next design session |
| Exact fields returned by `/details` endpoint | TBD — depends on SQL Server schema |
| Minimap for large topologies (>10 nodes) | Deferred — add if needed |
| SignalR vs polling decision point (which batches get push vs poll) | Deferred to Step 9 |
| Error state handling in the flow graph (failed chunks, error nodes) | Deferred |
| Timeline Comparison tab detailed design | Partially designed — full design deferred |
| Per-instance tooltip design detail | Deferred to Step 7 |
| Exact debounce threshold (400–600ms range) | Tune during Step 6 |
