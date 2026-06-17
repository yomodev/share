# BatchMonitor — Flow Graph (BatchDetail)

## Overview

The flow graph in `BatchDetail` shows the service pipeline topology for a batch run. Nodes represent services; edges represent chunk flow between services. The graph is rendered using D3 v7 + dagre for layout.

---

## Files

| File | Purpose |
|------|---------|
| `Components/D3FlowGraph.razor` | Blazor wrapper; passes `Topology` and `IsVisible` to JS |
| `wwwroot/js/d3-graph.js` | All D3 rendering logic |
| `wwwroot/js/d3-animation.js` | Edge dash-flow animation state machine |
| `wwwroot/css/d3-graph.css` | Graph styles |

---

## Blazor Interop

`D3FlowGraph.razor` exposes three JS methods via `window.BatchMonitor.D3FlowGraphInterop`:

```javascript
init(el, key)           // Initialise SVG in container element
update(key, topology)   // Push new topology data (debounced layout)
setVisible(key, visible)// Pause animation RAF when hidden
dispose(key)            // Clean up
fitToView(key)          // Programmatic fit-to-view
```

`key` is a string (typically the RunId) used to maintain multiple independent graph instances.

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

| State | Colour | Effect |
|-------|--------|--------|
| `errored` | `#F85149` | Red border + drop-shadow glow |
| `active` | `#3FB950` | Green border + drop-shadow + header pulse animation |
| `inprogress` | `#388BFD` | Blue border |
| `idle` / `completed` | `rgba(139,148,158,0.3)` | Dim grey |
| `notstarted` | `rgba(139,148,158,0.15)` | Very dim, reduced opacity |

---

## Edge Rendering

Edges use S-curve bezier paths with **forced horizontal entry and exit**:
```javascript
const dx = Math.max(Math.abs(t.x - s.x) * 0.45, 70);
return `M ${s.x} ${s.y} C ${s.x + dx} ${s.y}, ${t.x - dx} ${t.y}, ${t.x} ${t.y}`;
```

Control points are offset only in X, giving natural horizontal tangents at both ends regardless of angle.

**Port snapping:** Edge endpoints snap to the y-position of the specific pipeline row they connect. Source port exits from the right edge of the source node; target port enters from the left edge of the target node.

**Arrow marker:** `orient="auto"` (not `auto-start-reverse`). `fill="context-stroke"` makes the arrowhead inherit the path's stroke colour. **Critically:** `stroke` must be set as an SVG **attribute** (`path.attr('stroke', colour)`) not as a CSS property — `context-stroke` only resolves from SVG attributes, not CSS.

**Edge width:** `1 + log10(doneCount + 1) * 1.7` — logarithmic scaling, range 1–5px.

---

## Layout

**Auto-orient:** Layout direction (LR vs TB) is chosen based on canvas aspect ratio:
```javascript
function chooseDir(containerEl) {
    const r = containerEl.getBoundingClientRect();
    return r.width >= r.height ? 'LR' : 'TB';
}
```

**Dagre settings:**
```javascript
g.setGraph({
    rankdir: dir,
    nodesep: dir === 'LR' ? 60  : 80,
    ranksep: dir === 'LR' ? 160 : 100,
    marginx: 48, marginy: 48,
});
```

**Debounced layout:** `update()` sets `pendingTopology` and starts a 500ms debounce timer. `commitLayout()` runs dagre and re-renders. The first update bypasses debounce.

**User zoom:** `userZoomed` flag prevents auto-fit after the user has manually panned/zoomed. `fitToView()` resets this flag.

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

Each node has a `<clipPath id="bm-clip-{nodeId}">` in `<defs>` that clips the pipeline rows group. This prevents row content (progress bars, left border) from overflowing the rounded card corners.

The clip path rect dimensions match the node width/height and are updated whenever the topology changes.

---

## Hover Behaviour

**Node hover:** `filter: brightness(1.35)` on `.bm-node-rect` only — no CSS `transform: scale()`. Scale transforms on SVG `<g>` elements conflict with D3's `attr('transform', 'translate(x,y)')` and cause a "strobo" effect where the node jumps position on hover and flickers as D3 transitions and CSS transitions fight each other.

**Pipeline row hover:** Row text turns primary colour; row background gets a subtle blue tint.

---

## Theme Support

Node background uses `getComputedStyle(document.documentElement).getPropertyValue('--mud-palette-surface')` at render time. This makes the graph work in both dark and light MudBlazor themes. All other colours use either hardcoded dark-theme values (acceptable for internal tool) or reference `--mud-palette-*` CSS variables via SVG `fill` properties.
