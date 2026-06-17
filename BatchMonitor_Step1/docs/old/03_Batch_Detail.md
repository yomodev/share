# Batch Monitor — Batch Detail Tab

> Part 3 of the design document series — June 2026

---

## 1. Tab Identity

```
TabId = detail:batch:{runId}:{env}
Label = batch name
Icon  = batch-specific icon (distinct from Batches dashboard and Timeline icons)
```

---

## 2. Page Structure

```
┌──────────────────────────────────────────────────────────────────────┐
│ GLOBAL TAB BAR                                                       │
├──────────────────────────────────────────────────────────────────────┤
│ TOP BAR (~40px)                                                      │
│ ● Running  OrderBatch  ✓ 1,252  ⟳ 41  ~68% ████████░░  [⏱] [■]  [⌄]│
├──────────────────────────────────────────────────────────────────────┤
│ DETAILS STRIP (expanded only, max ~200px, scrollable)               │
│ RunId        abc-123-def       Type         FullLoad                 │
│ RequestId    req-789           StartTime    2026-06-15 14:22 UTC     │
│ Duration     00:14:22          Environment  UAT1                     │
│ ...          ...               ...          ...                      │
├──────────────────────────────────────────────────────────────────────┤
│                                                                      │
│   D3 FLOW GRAPH (flex: 1)                                           │
│   [⤢ Fit]                                                           │
│                                                                      │
├──────────────────────────────────────────────────────────────────────┤
│ SLIDER (~48px)                                                       │
│ [14:22] ────────────────────●──────────────── [14:35]  [● LIVE]     │
└──────────────────────────────────────────────────────────────────────┘
```

---

## 3. Top Bar

Fixed height ~40px. Elements left to right:

| Element | Detail |
|---|---|
| Status chip | Coloured dot + label. Green pulse when Running. |
| Batch name | Plain text |
| Done count | `✓ N` |
| In-progress count | `⟳ N` |
| Inline progress bar | Compact, `~N% estimated`. Colour matches dominant pipeline state. |
| Open Timeline `[⏱]` | Opens or focuses Timeline tab for this batch |
| Cancel `[■]` | Visible only when Running. Confirm dialog before firing. |
| Chevron `[⌄/⌃]` | Far right. Toggles details strip. |

### Details Strip

- Revealed on chevron click; pushes D3 canvas down
- Max height ~200px, internal scroll
- Two-column KV grid (~30 items from `/details` endpoint)
- Static after load — `ShouldRender()` guard prevents re-render on live data
- Expand/collapse state persisted to localStorage
- D3 canvas listens to container resize and reflows on toggle

### Progress Bar Formula

```
progress ≈ count(finish not null) / count(all chunk × service × pipeline combinations)
```

Denominator: max hop count observed × total unique chunkIds seen (adjusts dynamically). Labelled `~N% estimated`.

---

## 4. D3 Flow Graph

### Overview

Rendered entirely in SVG/JS. Blazor pushes JSON snapshots via `IJSRuntime.InvokeVoidAsync`. D3 owns all rendering, animation, zoom, pan.

### Data Model

- **Node** = one service type
- **Pipeline row** = one pipeline within that service (unique name per service, one input topic)
- **Edge** = directed connection from a source pipeline row to a destination pipeline row

Topology inferred client-side: for each `chunkId`, sort events by `start`. The ordered `(service, pipeline)` sequence gives directed edges. Fan-out and fan-in both supported.

### Node Anatomy

```
┌────────────────────────────────────────┐
│  OrderProcessor            ×4      [↗] │  ← name · instances · open service details
├────────────────────────────────────────┤
│ ▐ order-ingest                         │  ← pipeline (coloured left border)
│   ████████████░░░░  ✓ 312   ⟳ 38      │  ← progress bar · counts
├────────────────────────────────────────┤
│ ▐ order-retry                          │
│   ██████████████░░  ✓ 298   ⟳ 4       │
└────────────────────────────────────────┘
```

Topic name not shown in node — appears only in hover tooltip.

### Pipeline Row States

| State | Colour | Condition |
|---|---|---|
| Not started | Dim surface, muted label | No events yet |
| In progress | `#388BFD` blue | Messages with finish null |
| Active | `#3FB950` green | Finish events within last 10s |
| Idle | `#8B949E` grey | No activity >10s, not completed |
| Completed | `#8B949E` + ✓ | All messages finished |
| Error | `#F85149` red | Any message has error set |

### Service Node Header State

Priority order (highest wins):

| Priority | Condition | Accent |
|---|---|---|
| 1 | Any pipeline errored | Red `#F85149` |
| 2 | Any pipeline active | Green `#3FB950` + pulse |
| 3 | Any pipeline in-progress | Blue `#388BFD` |
| 4 | All idle | Grey `#8B949E` |
| 5 | All completed | Grey + ✓ |
| 6 | Not started | Dim |

### Pipeline Row Hover Tooltip

JS-managed HTML div (not MudTooltip). Positioned relative to hovered row; dismisses on mouse leave.

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

Instances sorted by server name then PID. `[↗ Open Kafka]` opens Kafka Topic Inspector for that topic.

### Edge Design

Port-level connections: edges emerge from right of source pipeline row, arrive at left of destination pipeline row.

| Property | Behaviour |
|---|---|
| Thickness | Log-scaled with message count (min 1px, max 6px) |
| Colour | Inherits source pipeline row state colour |
| Label | `✓ N  ⟳ ~N waiting` |
| Animation | Dash flow: dashes travel in flow direction, speed ∝ throughput |
| Activity spike | Edge brightens ~300ms |
| High throughput | Dash gap shrinks toward zero, slight thickness increase |

Waiting estimate: `count of chunkIds where source has finish set but destination has no event yet`.

### Layout

- Algorithm: dagre hierarchical DAG, left-to-right
- Debounced recompute: 400–600ms after last event batch
- Existing nodes animate to new positions (~300ms ease)
- New nodes fade + scale in
- Cold-start: "Waiting for first events…" with subtle pulse

### Zoom, Pan, Cursor

| State | Cursor |
|---|---|
| Default (scale = 1) | `default` |
| Zoomed in | `grab` |
| Dragging | `grabbing` |
| Hovering node / pipeline / edge | `pointer` |

Fit-to-view `[⤢]` button: absolutely positioned HTML button top-right of canvas.

---

## 5. Slider

```
[batch start] ──────────────●──────────── [end / last update]  [● LIVE]
```

| Mode | Condition | Behaviour |
|---|---|---|
| LIVE | Slider at right edge | Graph animates, receives new data |
| REPLAY | Slider elsewhere | Graph frozen at time T; new data accumulates silently |

Snap to right edge → returns to LIVE. Right edge auto-updates as new events arrive.

**Graph state at time T:** filter events to `start <= T`. Events where `finish > T` or finish null at T are in-progress. Topology layout reused for stability — only counts and states rewind.

---

## 6. API Endpoints

### 6.1 Batch Detail — Static Metadata

```
GET /api/{env}/batches/{runId}/details
```

Returns ~30 KV pairs from SQL Server. Called once on tab open. Fields TBD (schema-dependent).

### 6.2 Topology Snapshot

```
GET /api/{env}/batches/{runId}/topology
```

Pre-computed graph snapshot for fast initial render. Nodes with pipelines, edges at pipeline-row level. Called once; client owns topology after that.

### 6.3 Incremental Events

```
GET /api/{env}/batches/{runId}/events?from={utcTimestamp}
```

| Field | Type | Notes |
|---|---|---|
| `messageId` | string | Unique — primary key for upsert |
| `chunkId` | string | TypePrefix + IncrementalNumber |
| `source` | string | Pipe/stage name (from MongoDB `source` property) |
| `service` | string | Service type name |
| `pipeline` | string | Pipeline name (unique per service) |
| `server` | string | Hostname |
| `processId` | int | OS PID |
| `start` | UTC datetime | Always present |
| `finish` | UTC datetime? | Null if in progress |
| `error` | string? | Set if failed |

Upsert by `messageId` — last version wins. Also used by Timeline tab.

---

## 7. Data Loading Strategy

| Step | Action |
|---|---|
| Tab open | GET `/details` — once |
| Tab open | GET `/topology` — once |
| Tab open | GET `/events?from={batchStart}` — full history |
| Catch-up complete | Client owns topology; no further topology calls |
| Running batch | Poll `/events?from={lastTs}` or receive via SignalR push |
| Slider scrub | Client-side filter on in-memory store — no network call |

---

## 8. Implementation Steps

### Step 4 — Batch Detail Shell + Top Bar

- Implement GET `/details`
- Tab opens with correct TabId, label, icon
- Top bar: all elements, chevron expand/collapse, details strip KV grid
- Graph area: "Loading…" placeholder
- Slider: rendered but empty

### Step 5 — Event Loading + In-Memory Store

- Implement GET `/events`
- Client event store: dictionary keyed by `messageId`, upsert
- Full history load on open; incremental poll; unfocused throttle (10s)
- CancellationToken wired to tab close

### Step 6 — D3 Flow Graph (static)

- Implement GET `/topology`
- Topology inference from event store
- D3 dagre layout, service nodes, pipeline rows, edges
- Status colours, debounced recompute, fade-in animations
- Fit-to-view, zoom/pan, cursor rules
- `ShouldRender()` + `display: none` for unfocused tab

### Step 7 — Edge Animation + Tooltips

- Dash-flow animation, activity spike brightening
- Node header pulse on active state
- Pipeline row hover tooltip (JS div)
- `[↗]` stubs for Service Details and Kafka Topic tabs

### Step 8 — Slider + Replay Mode

- Progress bar (`~N% estimated`)
- Slider time range, LIVE/REPLAY modes
- Client-side time filter for Replay
- New data accumulates silently in Replay

### Step 9 — SignalR Live Push

- Subscribe to `{runId}:{env}` group on tab open for Running batches
- Merge pushed events into store (upsert by messageId)
- Completion event: update status, freeze slider right edge
- Tab close: unsubscribe

---

## 9. Open Questions

| Topic | Status |
|---|---|
| `/details` endpoint exact fields (~30 KV pairs) | TBD — schema-dependent |
| Progress bar denominator refinement | TBD — revisit once schema confirmed |
| Minimap for large topologies (>10 nodes) | Deferred |
| Error state visualisation in graph | Deferred |
| Exact debounce threshold | Tune during Step 6 |
