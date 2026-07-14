# NxtUI — Flow Graph (Run Detail)

## Overview

The flow graph in `RunDetail` shows the service pipeline topology for a run. Nodes
represent services; edges represent chunk flow between services (and, for nested
runs, whole child-run boxes — see `12_Custom_Layout_And_Nested_Runs.md`). The graph
is rendered using D3 v7, with node/edge **positions** computed by a pluggable layout
engine — the custom `bm-flow-layout` (default) or ELK (`elkjs`, still present as a
comparison/fallback toggle, `handle.layoutEngine`). **Dagre is gone entirely** — this
doc previously described a dagre-based layout that no longer exists.

This doc covers the D3/SVG **rendering** layer (what happens once node/edge
positions are known) — for how those positions are actually computed (role pins,
groups, direction/orientation hints, nested-run recursive boxes, per-pipeline edge
ports), see `12_Custom_Layout_And_Nested_Runs.md`, which is the authoritative,
much more detailed doc for the layout engine itself.

---

## Files

| File | Purpose |
|------|---------|
| `Components/D3FlowGraph.razor` | Blazor wrapper; passes `Topology` and visibility to JS |
| `wwwroot/js/d3-graph.js` | All D3 rendering logic, drives `bm-flow-layout`/ELK for positions |
| `wwwroot/js/bm-flow-layout/` | The custom layout engine itself (own package, own tests) |
| `wwwroot/js/d3-animation.js` | Edge dash-flow animation state machine |
| `wwwroot/css/d3-graph.css` | Graph styles |

---

## Blazor Interop

`d3-graph.js` is imported lazily as an ES module per `D3FlowGraph` instance
(`IJSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/d3-graph.js?v=NN")`) —
there is no `window.BatchMonitor.D3FlowGraphInterop` global anymore. Exported
functions, called directly on the imported module reference:

```javascript
init(el, key, dotNetRef, portConstraints, edgeStyle, defaultDirection, layoutEngine)
update(key, topo)          // Push new topology data (debounced layout)
setVisible(key, visible)   // Pause animation RAF when hidden
dispose(key)               // Clean up
fitToView(key)             // Programmatic fit-to-view (resets zoom first)
```

`key` is a string (typically the RunId) used to maintain multiple independent graph
instances (multiple `RunDetail` tabs, or split panes). `init` now takes several
config args driven by `RunsSettings` — `portConstraints`, `edgeStyle`,
`defaultDirection`, `layoutEngine` — so the engine, edge-drawing style, and default
flow direction are all configurable per deployment, not hardcoded.

---

## Node Anatomy

```
┌─────────────────────────────┐
│ ████ ServiceName       ×8   │  ← header accent bar (4px, state colour)
│                             │     + label + instance badge
├─────────────────────────────┤  ← header bottom border
│ ░ pipeline-name   ✓15 ⟳0  │  ← pipeline row (3px left border, progress bar, counts)
├─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─│
│ ░ pipeline-two    ✓34 ⟳0  │
└─────────────────────────────┘
```

- **Opaque background rect** (`bm-node-bg`): fills node with `--mud-palette-surface` colour so edges don't show through
- **Border rect** (`bm-node-rect`): transparent fill, coloured stroke based on `headerState`
- **Header accent bar**: 4px wide rect on left edge, coloured by state
- **Instance badge**: blue circle with `×N` count top-right of header
- **Pipeline rows**: each row has a 3px left border, divider line, name text, progress track/fill, done/in-progress counts

### State → border colour

Same mapping as `PipelineState`'s colours (`02_Models_DataFlow.md`):

| State | Colour | Effect |
|-------|--------|--------|
| `errored` | `#F85149` | Red border + drop-shadow glow |
| `active` | `#3FB950` | Green border + drop-shadow + header pulse animation |
| `inprogress` | `#388BFD` | Blue border |
| `idle` / `completed` | `rgba(139,148,158,0.3)` | Dim grey |
| `notstarted` | `rgba(139,148,158,0.15)` | Very dim, reduced opacity |

---

## Edge Rendering

Edges are no longer simple S-curve beziers between two node centres — the layout
engine (bm-flow-layout or ELK) returns a full **waypoint polyline** per edge
(start point, zero or more bend points for orthogonal routing around other nodes,
end point), and `d3-graph.js` draws through those waypoints one of two ways
(`RunsSettings:GraphEdgeStyle`, `handle.edgeStyle`):

- **`roundedOrthPath`** — straight orthogonal segments with rounded corners at each
  bend (`Q` quadratic curve commands at each waypoint transition).
- **`curvedPath`** — a smooth Catmull-Rom spline through the same waypoints
  (`d3.line().curve(d3.curveCatmullRom.alpha(0.5))`) for a looser, organic look.

Both apply to the *same* underlying waypoints — the choice is purely cosmetic, not a
different routing algorithm. See `12_Custom_Layout_And_Nested_Runs.md` for how the
waypoints themselves are chosen (orthogonal channel routing between node "blocks" so
edges never cross a node).

**Port snapping / per-pipeline ports:** edge endpoints snap to the y-position (or
x-position, direction-dependent) of the specific pipeline row they connect, and
`bm-flow-layout`'s `LayoutEdge.variants` mechanism can route multiple real edges
(different `sourcePipeline`/`targetPipeline` pairs between the same node pair)
through one shared structural chain while still rendering each from/to its own
distinct port — see `12_Custom_Layout_And_Nested_Runs.md` §6 "port hints".

**Arrow marker:** `orient="auto"` (not `auto-start-reverse`). `fill="context-stroke"`
makes the arrowhead inherit the path's stroke colour. **Critically:** `stroke` must
be set as an SVG **attribute** (`path.attr('stroke', colour)`) not as a CSS
property — `context-stroke` only resolves from SVG attributes, not CSS.

**Edge width:** logarithmic scaling by `doneCount`, range 1–5px (same idea as
before; check `d3-graph.js` for the exact current constant if precision matters).

---

## Layout

**Direction:** an explicit topology-hint `layout.direction` (`"horizontal"` /
`"vertical"`) always wins; otherwise falls back to `handle.defaultDirection`
(`RunsSettings:GraphDirection` — `"RIGHT"`/`"DOWN"` fixed, or `"AUTO"` for the old
aspect-ratio auto-pick):
```javascript
function chooseDir(handle, layout) {
    const d = layout?.direction ? String(layout.direction).toLowerCase() : '';
    if (d === 'horizontal') return 'RIGHT';
    if (d === 'vertical')   return 'DOWN';
    if (handle.defaultDirection === 'AUTO') {
        const r = handle.containerEl.getBoundingClientRect();
        return r.width >= r.height ? 'RIGHT' : 'DOWN';
    }
    return handle.defaultDirection;
}
```

**Density:** a `layout.density` hint (`"compact"`/`"airy"`) scales node/edge
spacing via a multiplier (`densityScale()`) — schema-declared and read by the JS,
but currently only meaningfully wired for the ELK comparison path; see the
"snapToPipeline/density/straightenEdges" note in this repo's session history if
you're extending it for the custom engine (as of this writing these are inert on the
`bm-flow-layout` path).

**Engine choice:** `handle.layoutEngine` (`'custom'` default, `'elk'` alternative)
picks between `runLayoutElk(handle, topo)` and the custom engine
(`bm-flow-layout/layout.js`) — a comparison-only toggle; the custom engine is what
supports role pins, groups, direction/orientation hints, external/arriveFrom
placement, nested-run recursive boxes, and per-pipeline ports (`12_Custom_...md`).
ELK doesn't support all of those.

**Debounced layout:** `update()` sets a pending topology and debounces the actual
layout recompute; the first update bypasses the debounce so the initial render isn't
delayed.

**User zoom:** a `userZoomed` flag prevents auto-fit after the user has manually
panned/zoomed. `fitToView()` resets this flag and computes the real bounding box of
every node **and child-run box** (bm-flow-layout coordinates are routinely negative,
not anchored at origin — a bug fixed this session, see git history around `d04e3ac`)
rather than assuming content starts at `(0,0)`.

---

## Popover (Pipeline Detail)

Click a pipeline row to open a sticky card popover showing:
- Pipeline name + state badge
- Progress bar + percentage
- Topic name + "↗ Kafka" button stub
- Instance list (server, PID, done/in-progress counts per instance)

Popover behaviour:
- Opens on click; closes on ×, canvas click, or clicking same row again
- Stays open while mouse is over node or popover (hover auto-hide with 220ms delay)
- Positioned right of node by default; falls back to left if near right edge

---

## Animation (`d3-animation.js`)

The edge dash-flow animation uses a requestAnimationFrame loop:

**State per edge:** `sampleEdge(state, edgeId, doneCount, now)` maintains a sliding average of throughput and generates:
- `dashArray` / `dashOffset`: animated dashes flowing along the edge
- `thicknessBoost`: temporary width increase on activity spike
- `brighten`: glow intensity (0–1)

**When paused:** `setVisible(key, false)` halts the RAF loop. `lastFrameTime` is reset to null so the next frame doesn't get a huge `dt`.

---

## SVG Clip Paths

Each node has a `<clipPath id="bm-clip-{nodeId}">` in `<defs>` that clips the
pipeline rows group, preventing row content (progress bars, left border) from
overflowing the rounded card corners. Nested-run child-card header accent bands get
their own clip via `registerChildCardClip` (`id="bm-clip-child-run-<runId>"`).

The clip path rect dimensions match the node width/height and are updated whenever
the topology changes.

---

## Hover Behaviour

**Node hover:** `filter: brightness(1.35)` on `.bm-node-rect` only — no CSS `transform: scale()`. Scale transforms on SVG `<g>` elements conflict with D3's `attr('transform', 'translate(x,y)')` and cause a "strobo" effect where the node jumps position on hover and flickers as D3 transitions and CSS transitions fight each other.

**Hover-zoom** (small/scaled-down nodes get temporarily enlarged on hover) uses
`.bm-node-hover-zoom { z-index: 1000 }` so a zoomed node renders above sibling
child-run boxes too (fixed this session — see `d04e3ac`), and is skipped for
already-expanded child-run boxes (task #56).

**Pipeline row hover:** Row text turns primary colour; row background gets a subtle blue tint.

---

## Theme Support

Node background uses `getComputedStyle(document.documentElement).getPropertyValue('--mud-palette-surface')` at render time. This makes the graph work in both dark and light MudBlazor themes. All other colours use either hardcoded dark-theme values (acceptable for internal tool) or reference `--mud-palette-*` CSS variables via SVG `fill` properties.
