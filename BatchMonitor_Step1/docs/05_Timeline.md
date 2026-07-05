# BatchMonitor — Timeline Tab

## Overview

The Timeline tab visualises PerformanceEvents across one or more batch runs on a shared time axis. Multiple batches can be loaded and compared side by side.

---

## Files

| File | Purpose |
|------|---------|
| `Pages/TimelineTab.razor` | Blazor component: control bar, canvas div, dialog, import/export |
| `Services/TimelineBatch.cs` | Per-batch event store + SignalR subscription |
| `wwwroot/js/d3-timeline.js` | All D3 rendering (multi-instance) |
| `wwwroot/css/d3-timeline.css` | Timeline styles |

---

## Two-SVG Architecture

The timeline uses **two separate SVG elements**:

```
.bm-tl-canvas-wrap (overflow-y: auto — vertical scroll)
└── .bm-tl-canvas
    └── laneSvg (height = content height, can be taller than viewport)
        ├── gridL      (global vertical tick lines)
        ├── cursorL    (magenta cursor line — behind blocks)
        ├── blockL     (message blocks per section)
        └── headerL    (batch name rows — on top)

.bm-tl-bottom-panel (fixed height, never scrolls)
└── botSvg
    ├── tickL      (global tick scale — HH:MM:SS labels)
    ├── heatL      (heatmap — density vertical lines)
    ├── selL       (range selector brush overlay)
    └── curBotL    (cursor time label pill)
```

The bottom panel is fixed regardless of lane scroll position.

---

## Instance Isolation

Each `TimelineTab` component generates a unique `_instanceKey = Guid.NewGuid().ToString("N")`. All JS calls pass this key:

```csharp
await JS.InvokeVoidAsync("BatchMonitor.Timeline.init",    _instanceKey, _canvasRef, _bottomRef, GetOptions());
await JS.InvokeVoidAsync("BatchMonitor.Timeline.update",  _instanceKey, payload);
await JS.InvokeVoidAsync("BatchMonitor.Timeline.dispose", _instanceKey);
```

JS maintains `_instances: Map<key, state>`. Each instance has completely independent zoom, data, and DOM elements.

---

## X-Axis (Time)

### Coordinate system
- All events use **relative milliseconds** from their batch's `BatchStart`
- `t=0` = batch start; `t=60000` = 1 minute into the batch
- Multiple batches are **all aligned to t=0** (not absolute wall clock)
- This allows comparing "how did this batch perform vs last time" at the same relative position

### Frozen domain
`frozenDomainMax` is set on first data load to the maximum finish time across all loaded batches. It **never changes** from incoming data updates. `xScale.domain([0, frozenDomainMax])` is set once and stays fixed.

`globalMax` tracks the true current maximum (including new live events). It grows as new events arrive. Only the heatmap and range selector use `globalMax`.

### Zoom/pan limits
- Left: `tx ≤ 0` (t=0 never goes right of the left screen edge → no negative time)
- Right: `tx ≥ -(k * (globalMax/frozenDomainMax) * W - W)` (can always pan to see `globalMax`)
- Min zoom: `scaleExtent([1, 5000])` — can't zoom out past "show all"

### Reset view
Fits to `[0, globalMax]` (not `frozenDomainMax`) so all current data is visible:
```javascript
const k = gMax <= fMax ? 1 : fMax / gMax;
applyZoom(s, d3.zoomIdentity.scale(k));
```

---

## Y-Axis (Row Layout)

### Normal mode

Per batch section:
```
HEADER_H (26px)  — batch name row
├── Group row 1 (height = ROW_PAD*2 + subrows.length * (SUBROW_H + BLOCK_GAP))
├── Group row 2
└── ...
SECTION_GAP (6px)
```

**Group key** is determined by the GroupBy setting:
| Option | Key |
|--------|-----|
| Service / Pipeline | `"Enricher / enrich-main"` |
| PID / Service / Pipeline | `"server01:4821 / Enricher / enrich-main"` |
| Service | `"Enricher"` |
| Pipeline | `"enrich-main"` |
| PID | `"server01:4821"` |

**Sub-row packing** (greedy interval, first-fit from top):
1. Sort events by `startMs`
2. For each event, find the first sub-row where `e.startMs >= row.last.xEndPack`
3. `xEndPack` = `finishMs` if known, else `startMs + 5000ms` (estimate only)
4. Rendered block uses actual `finishMs` (or extends to viewport right edge for in-progress)

**No cap** on sub-row count. Row height expands as needed.

**Sub-row height adjustment:** `↑/↓` keys change `s.subrowHOverride` (min 1px). When vertical scrollbar is active, `↑/↓` scroll instead (Ctrl+↑/↓ to adjust height).

### Stack mode

One row per distinct `name`. Events within a chunk are placed **consecutively by duration** (not wall-clock position):
```javascript
let cursor = 0;
for (const e of evts) {
    const dur = e.finishMs - e.startMs;
    subrow.push({ xStart: cursor, xEnd: cursor + dur });
    cursor += dur;
}
```

Use case: compare pipeline stage durations for each chunk, independent of when processing started.

### Vertical scroll

The `.bm-tl-canvas-wrap` has `overflow-y: auto`. Lane SVG height = `max(viewH, totalH + 16)`. If content is taller than the viewport, a scrollbar appears.

---

## Colour System

**Colour By** options map events to colours:
| Option | Colour key |
|--------|-----------|
| Source (default) | `e.source` (class name) |
| Pipeline | `e.pipeline` |
| Service | `e.service` |
| Status | `e.status` → fixed colours |

**Status colours:**
- `done` → `#3FB950` (green)
- `inprogress` → `#388BFD` (blue)
- `error` → `#F85149` (red)

**Palette colours** (Source/Pipeline/Service): deterministic hash of the key string → index into a 19-colour curated palette. Same key always gets the same colour across sessions.

---

## Filtering

The `Filter…` text box filters events by substring match across: `name`, `source`, `pipeline`, `service`, `processId`, `server`. Non-matching events are excluded from the layout (but still count in heatmap).

---

## Cursor

The magenta cursor line spans the full height of the lane SVG. It auto-hides after 2 seconds of mouse stillness.

The cursor label in the bottom panel shows:
- Relative time: `HH:MM:SS.mmm`
- Absolute time in parentheses if the hovered batch has a known `batchStartEpochMs`
- Which batch is "hovered" is determined by the y-position of the mouse in the lane area

---

## Heatmap

The heatmap shows event density across the full `globalMax` range using vertical lines:
- Neutral background (`#161620`)
- Lines coloured `#388BFD` with opacity `0.12 + 0.88 * (count / maxCount)`
- 500 buckets across the usable width (PAD left/right)

---

## Range Selector

`d3.brushX()` overlay on top of the heatmap. Built once per instance; only `.move` is called to sync position. The brush extent maps `[PAD, W-PAD] → [0, globalMax]`.

Dragging the brush calls `applyBrushSelection()` which computes the new `xZoom` and calls `applyZoom()` (which temporarily removes then re-adds the zoom handler to avoid re-entrancy).

---

## Block Highlighting (name)

Hovering any block highlights all blocks with the same `name` across all batch sections:
```javascript
s.blockL.selectAll('rect.bm-tl-block')
    .style('fill-opacity', b => b.e.name === s.hoveredName ? 1 : 0.12);
```

The same chunk ID can appear in multiple batches (if multiple runs processed the same chunk) and in multiple services within a batch (as the chunk travels through the pipeline).

---

## Tooltip

Shows on block hover:
- Name + status badge
- Source, Pipeline, Service, PID, Server
- Start (relative + absolute)
- Finish (relative + absolute, or `…` if in-progress)
- Duration
- Error message (if any)

**Ctrl+click** copies all fields to clipboard.

Relative times use `HH:MM:SS.mmm` precision. Absolute times use `HH:MM:SS.mmm` (local time).

---

## CSV Export / Import

### Export
Uses `window.showSaveFilePicker()` (Chrome/Edge save dialog) with blob-download fallback. Filename: `timeline_YYYY-MM-DD-HH-MM-SS.csv`.

Columns: `RunId, BatchName, Name, Source, Pipeline, Service, PID, Server, StartRelMs, FinishRelMs, DurationMs, Status, Error`

### Import
Triggered by hidden `<InputFile>` element clicked programmatically. Parsed by `ImportCsvAsync()` which:
1. Reads header row to find column indices (order-independent)
2. Groups rows by `RunId`
3. Upserts into existing `TimelineBatch` or creates a new CSV-only batch via `TimelineBatch.CreateFromCsv()`

CSV batches have no live subscription and no polling. They are identified by the absence of an `IBatchService` reference.

---

## Add Batch Dialog

- MudBlazor `MudDataGrid` with search, env selector, paginated results
- Single-click selects/deselects a row
- Double-click (detected via `MouseEventArgs.Detail == 2`) immediately loads the batch
- `CloseButton = true` in `DialogOptions` adds an × button to the dialog header
- Already-loaded batches show an info alert; loading them again refreshes/merges events

---

## Control Bar

| Button | Icon | Action |
|--------|------|--------|
| Filter | Search | Filters events by text |
| Group by | Select | Recomputes lane layout |
| Colour by | Select | Changes block colours |
| Stack view | ViewColumn | Toggles duration-stacking mode |
| Reset view | ZoomOutMap | Fits all data in viewport |
| Export | Save | Saves CSV (with save dialog on Chrome/Edge) |
| Import | FolderOpen | Opens file picker for CSV import |
| Remove | LayersClear | Dropdown to remove individual or all batches |
| Add | Add | Opens Add Batch dialog |

---

## Known Limitations / Pending Work

- Group row labels (lane key names on the left side) — not yet implemented
- Legend for colour key — not yet implemented
- Batch title header does not scroll with vertical pan (intentional — it re-renders with the content)
- Stack view x-axis represents accumulated duration, not wall clock — ticks show relative time which may be confusing when mixed with normal batches
- Import does not reconstruct absolute `batchStartEpochMs` from relative ms (absolute times in tooltip will not show for imported batches)
