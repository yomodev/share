# Batch Monitor — Timeline Comparison Tab

> Part 4 of the design document series — June 2026

---

## 1. Tab Identity

```
TabId = detail:timeline:{runId}:{env}     (single batch entry point)
TabId = detail:timeline:multi:{env}       (multi-batch)
Label = "Timeline"
Icon  = timeline/waveform icon
```

Entry points:
- `[⏱]` button on Batch Detail top bar
- Per-row action on Batches dashboard
- `[+ Add]` within the tab itself

---

## 2. Page Structure

```
┌─────────────────────────────────────────────────────────────────┐
│ CONTROL BAR (Blazor)                                            │
│ [🔍 filter]  [Group by ▼]  [Colour by ▼]  ──  [+ Add]  [🗑 ▼] │
│ [⬇ CSV]  [⬆ Import]  [⊞ Stack]                                 │
├─────────────────────────────────────────────────────────────────┤
│  ● LIVE  OrderBatch  abc-123  [UAT1]  ──────────────────────── │  ← thin batch header
│  [message blocks — groups separated by thin dashed lines]      │
│                                                                 │
│  TICK SCALE:  ──┬──────┬──────┬──────┬──────┬──────           │  ← per batch
│                 0s    10s    20s    30s    40s                  │
│                                                                 │
│  OtherBatch  def-456  [PRD]  ───────────────────────────────── │
│  [message blocks]                                              │
│                                                                 │
│  TICK SCALE:  ──┬──────┬──────┬──────┬──────┬──────           │
│                 0s    10s    20s    30s    40s                  │
├─────────────────────────────────────────────────────────────────┤
│ GLOBAL RANGE SELECTOR  [◄■■■■■■■■■■■►]  [↺ Reset view]         │  ← above heatmap
│ GLOBAL HEATMAP  ░░▒▒▓▓████▓▓▒░░░░▒▒▓████▓▒░░░░                │  ← combined density
└─────────────────────────────────────────────────────────────────┘
```

Outer container scrolls vertically. Horizontal navigation via zoom/pan only.

All rendering is SVG/D3 client-side. Blazor manages data loading and the control bar only.

---

## 3. Control Bar

```
[🔍 filter text]  [Group by ▼]  [Colour by ▼]  ──  [+ Add]  [🗑 ▼]  [⬇ CSV]  [⬆ Import]  [⊞ Stack]
```

### Group By

Configurable list of `{ label, keyFn(event) => string }`. Adding a grouping = one list entry, no structural change.

| Option | Row identity |
|---|---|
| Service + Pipeline (default) | `service / pipeline` |
| PID + Service + Pipeline | `server:pid / service / pipeline` |
| Service only | `service` |
| Pipeline only | `pipeline` |
| PID only | `server:pid` |

### Colour By

Deterministic hash of key string → curated palette index. Same name always maps to same colour.

| Option | Colour basis |
|---|---|
| Source name (default) | `source` field |
| Pipeline | Pipeline name |
| Service | Service name |
| Status | State-based colours from flow graph palette |

### Remove Batch Menu `[🗑 ▼]`

Dropdown listing all loaded batches by name + RunId. Click to remove. "Remove all" at bottom with confirmation. On remove: batch section fades out (~200ms), range/heatmap recompute, layout reflows.

---

## 4. Global Range Selector and Heatmap

Fixed composite control at the top of the SVG area, above all batch sections. Two layered elements:

**Global range selector (top):**
- D3 brush spanning union of all batch time ranges
- Drag handles to zoom; drag body to pan; double-click to reset
- Bidirectionally linked to main viewport zoom/pan state
- `[↺ Reset view]` button resets to full range

**Global heatmap (below selector):**
- Combined message density across all batches
- X = full union time range (always — acts as minimap)
- Palette: `#0E1117` → `#388BFD` → `#3FB950` → `#E6EDF3`
- Recomputed on data change only, not on zoom/pan

---

## 5. Per-Batch Section

### Batch Header

Single line ~28px, no background fill:

```
● LIVE  OrderBatch  abc-123-def  [UAT1]  ──────────────────────────
```

| Element | Detail |
|---|---|
| `● LIVE` | Green pulse. Visible only while receiving live data. |
| Batch name | Plain text, primary colour |
| RunId | Muted, smaller font |
| Env badge | Tier-colour rounded rectangle |
| Separator | 1px border-colour line below header |

### Background Grid

| Line | Colour | Style |
|---|---|---|
| Major tick vertical | `#30363D` | Solid 1px |
| Minor tick vertical | `#21262D` | Solid 1px |
| Group separator horizontal | `#30363D` | Dashed `4 4`, 1px |

Grid renders below message blocks in SVG z-order.

### Lane Layout (no lane labels)

Per group (Group By setting):
- Messages packed into sub-rows to avoid time overlap
- Sub-rows expand lane height dynamically
- All sub-rows visually contained within the lane boundary
- Minimum lane height: 24px (1 sub-row)

### Per-Batch Tick Scale

At the bottom of each batch section:

```
──┬────────┬────────┬────────┬────────┬──────
  0s      10s      20s      30s      40s
```

- Major ticks labelled; minor ticks unlabelled
- Tick density adapts to zoom level (D3 time scale auto-tick)
- All batches share the same horizontal zoom/pan state

---

## 6. Message Blocks

| Property | Value |
|---|---|
| X position | `start` mapped to relative time axis |
| Width | `finish - start`. Minimum 2px. |
| Height | Sub-row height minus 2px padding |
| Corner radius | `rx=2` |
| Fill | Determined by Colour By |
| Border | 1px slightly darker shade of fill |
| In-progress | Right edge dashed; fill slightly desaturated; extends to current time |

### Viewport Culling

Only blocks whose time range intersects the visible window are in the DOM. Blocks outside are removed from SVG. Level-of-detail: blocks narrower than 2px at current zoom merge into a density bar.

### Hover Behaviour

On hover:
1. All blocks sharing the same Colour By key: `2px solid` bright border at full colour brightness
2. All other blocks dim to 40% opacity
3. Rich tooltip appears

On mouse leave: all blocks fade back to normal opacity (~150ms ease).

### Rich Tooltip

JS-managed HTML div. Width adjusts dynamically to content — no fixed or minimum width. 12px horizontal padding. Positioned above block; flips below near top edge.

```
┌────────────────────────────────────────────────┐
│ msg-0042-abc                          ✓ Done   │
├────────────────────────────────────────────────┤
│ Source      ParsePipe                          │
│ Pipeline    order-ingest                       │
│ Service     OrderProcessor                     │
│ PID         4821                               │
│ Server      server01                           │
│ Start       +00:00:14.220  (14:24:14.220 UTC)  │
│ End         +00:00:15.840  (14:24:15.840 UTC)  │
│ Duration    1,620ms                            │
├────────────────────────────────────────────────┤
│ Ctrl+click to copy              Copied ✓       │
└────────────────────────────────────────────────┘
```

Status chip top-right: `✓ Done` / `⟳ In Progress` / `✗ Error`.

Absolute times in parentheses after relative time.

**Ctrl+click:** copies formatted text to clipboard. Footer shows "Copied ✓" for ~1s then reverts. Footer always visible.

---

## 7. Cursor Line and Time Tooltip

- Thin magenta line (`#FF00FF`, 1px), full canvas height, SVG overlay layer
- Follows mouse X while over any batch lane
- **Time tooltip:** small floating label just above the heatmap strip of each intersected batch

Format:
```
+00:01:42.350  (14:24:42.350 UTC)
```

- Relative time + absolute time in parentheses
- Each batch shows its own label (absolute times differ per batch)
- Visible while cursor moving; fades after ~1.5s of stillness
- Between batches: labels persist at last position until cursor re-enters a lane

---

## 8. Zoom, Pan, Keyboard Shortcuts

Horizontal only. Shared across all batches.

| Action | Behaviour |
|---|---|
| Mouse wheel | Zoom in/out, centred on cursor X |
| Click + drag on lanes | Pan |
| `+` / `=` | Zoom in |
| `-` | Zoom out |
| `←` / `→` | Pan 10% |
| `Shift+←/→` | Pan 50% |
| `Home` | Jump to time 0 |
| `End` | Jump to last event |
| `Ctrl+0` | Reset to full range |
| Range selector drag | Pan |
| Range selector handle | Zoom |
| Range selector double-click | Reset |

| State | Cursor |
|---|---|
| Hovering lanes | `crosshair` |
| Dragging | `grabbing` |
| Range selector handle | `ew-resize` |

---

## 9. Time Axis Formulas

### Normal (Timeline) View

```
timelineWidth = max(finish) - min(start)
```

Axis extends with ~300ms D3 transition when new data arrives beyond current right edge. All blocks animate to new positions.

### Stack View

```
timelineWidth = max over all name-groups of sum(finish - start) for that group
```

Widest group by total accumulated processing time determines the axis. Extends with animation when any group's sum grows.

Both views: axis extension always animates, never snaps.

---

## 10. Stack View

Toggled by `[⊞ Stack]`. Ignores absolute time positioning:

- Messages grouped by Colour By key
- Within each group: blocks placed sequentially in start-time order, no gaps
- Block width proportional to duration
- X axis = accumulated processing time (not wall clock)
- Duration axis shared across all batches: `max(group.sum(finish-start))` across all batches
- Per-batch tick scale at bottom shows cumulative duration
- Group separator lines, block hover, tooltip all unchanged
- Global range selector and heatmap still present (density = duration distribution)
- Cursor line and time tooltip show cumulative duration offset
- Group By and text filter still apply; zoom/pan works on duration axis

---

## 11. Batch Loading and Subscription

### Entry Points

| Entry | Behaviour |
|---|---|
| `[⏱]` on Batch Detail | Opens Timeline with that batch pre-loaded |
| Batches dashboard row action | Opens Timeline with that batch pre-loaded |
| `[+ Add]` in tab | Opens Add Batch dialog |
| `[⬆ Import]` | CSV file picker |

### Subscription Lifecycle

Each running batch from the API gets an independent SignalR subscription. No dependency on Batch Detail tab.

| Condition | State |
|---|---|
| API-loaded Running batch | Subscribe to `{runId}:{env}` SignalR group |
| Batch Detail also open for same batch | Both subscribe independently |
| Batch Detail closed | Timeline subscription unaffected |
| Completion event received | Unsubscribe, remove LIVE badge, freeze |
| Batch age > 3h, no completion | Stop, show timeout warning on header |
| CSV-loaded batch | No subscription |
| Timeline tab closed | All subscriptions cancelled via CancellationToken |

### Message Identity and Upsert

Upsert by `messageId` — last version wins. No duplicates. Incoming messages update existing records or add new ones.

---

## 12. Add Batch Dialog

```
┌─────────────────────────────────────────────────────────────┐
│  Add Batch                                               [×] │
├─────────────────────────────────────────────────────────────┤
│  Env [UAT1 ▼]   [🔍 search RunId / Name ················]   │
├─────────────────────────────────────────────────────────────┤
│  RunId          Name           Status     Start      Dur     │
│  ─────────────────────────────────────────────────────────  │
│  abc-123-def    OrderBatch     ● Running  14:22       —      │
│  abc-122-aaa    OrderBatch     ✓ Done     13:10   00:48      │
│  def-456-bcd    InvoiceBatch   ✓ Done     12:05   01:12      │
├─────────────────────────────────────────────────────────────┤
│  < 1  2  3 >                      Rows: [25 ▼]  1–25 of 87  │
├─────────────────────────────────────────────────────────────┤
│  Already loaded: abc-123-def (UAT1) — will update messages  │
├─────────────────────────────────────────────────────────────┤
│                                [Cancel]  [Load Batch]        │
└─────────────────────────────────────────────────────────────┘
```

- Env selector and filter on the same line
- Both env change and filter edit trigger same endpoint call — no separate triggers:
  `GET /api/{env}/batches?filter={text}&count={n}&before={cursor}`
- Env change resets pagination to page 1 and clears row selection
- Single-row selection; click again to deselect
- Already-loaded notice: info strip if RunId+env already in timeline; Load still enabled (triggers refresh/merge)
- Cross-environment: any env selectable — enables cross-environment batch comparison
- Time alignment: each batch always starts from relative time 0
- Load Batch: disabled until row selected; closes dialog, begins loading

---

## 13. CSV Export / Import

### Export

Browser-side download. All messages across all batches:

```
RunId,BatchName,MessageId,ChunkId,Source,Pipeline,Service,PID,Server,StartAbs,FinishAbs,StartRel,FinishRel,DurationMs,Status,Error
```

Includes absolute and relative timestamps. Exports raw data regardless of current view/filter.

### Import

- File picker → parse → validate columns
- Upsert by `MessageId` — replace if already loaded
- Unknown RunId → create new batch section
- Known RunId → merge/replace messages by MessageId
- Schema mismatch → inline error listing unexpected/missing columns

---

## 14. Implementation Steps

### Step 10 — Timeline Core Render

- Tab opens from Batches dashboard or Batch Detail `[⏱]`
- Control bar: all controls
- SVG canvas: message blocks, sub-row packing, group separator lines
- Background grid: major/minor tick vertical lines
- Per-batch tick scale; adapts to zoom
- Global range selector above global heatmap
- Time axis formula: `max(finish) - min(start)`; extends with animation
- Viewport culling and level-of-detail

### Step 11 — Interactions + Multi-Batch

- Block hover: highlight same-name, dim others, rich tooltip, Ctrl+click copy
- Cursor line: magenta, full height
- Time tooltip: per-batch above heatmap, relative + absolute, fades after stillness
- Add Batch dialog, Remove batch menu
- Multi-batch rendering, shared zoom/pan
- Live subscriptions per running batch; 3h timeout

### Step 12 — Stack View + CSV

- Stack view: sequential blocks, duration axis, shared scale across batches
- Axis extends with animation on new data
- Toggle between views preserves data
- CSV export/import with messageId upsert
- `[↺ Reset view]` button

---

## 15. Open Questions

| Topic | Status |
|---|---|
| Add Batch dialog — additional entry modes (paste data) | Deferred |
| Stack view block ordering within group | By start time (confirmed default) |
