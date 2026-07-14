# NxtUI — Technical Decisions

> This is a historical decisions log (kept append-only, like an ADR journal) — entries
> describe the reasoning *at the time*, and some now use old names (`BatchMonitor.*`
> namespaces, `IBatchService`, `PerformanceEventService`/`TimelineBatch`) that were
> renamed to `NxtUI.*`/`IRunService`/`TimelineRun` since. Where a decision has been
> **factually superseded** (not just renamed) a note says so and points to the doc
> with current details — see `01_Architecture.md` and `02_Models_DataFlow.md` for the
> present-day picture.

## D1. Blazor Server only (no WASM)

**Decision:** Stay on Blazor Server throughout. Do not migrate to InteractiveAuto or WebAssembly.

**Rationale:**
- SignalR is already the circuit transport — adding a *second* SignalR hub for live events is trivial
- Server state means `IBatchService`, `PerformanceEventService` etc. can be scoped to the circuit without serialisation concerns
- JS interop in Blazor Server is synchronous from the browser's perspective — no JS→WASM bridge latency
- The app is internal tooling; bandwidth to the server is acceptable

**Consequence:** All JS rendering is fire-and-forget (`InvokeVoidAsync` without awaiting results in tight loops). D3 state lives in browser JS, not in Blazor components.

---

## D2. Persistent tab panes (CSS hide/show)

**Decision:** All tabs are rendered simultaneously. Inactive tabs are hidden with `display:none` via class `bm-pane-hidden`.

**Rationale:**
- D3 graphs and timeline instances maintain their SVG DOM state without re-initialisation
- SignalR subscriptions in `PerformanceEventService` and `TimelineBatch` continue accumulating events even when the tab is not visible
- No "cold start" delay when switching tabs

**Consequence:** `ShouldRender()` returns `IsFocused` to prevent re-renders for background tabs. A one-render allowance (`_allowNextRender`) is used when focus transitions occur to ensure layout is correct.

**Alternative considered:** Tab navigation with URL routing. Rejected because it destroys and recreates JS state (D3 graphs would re-initialise on every tab switch).

---

## D3. Multi-instance JS pattern for Timeline

**Decision:** `window.BatchMonitor.Timeline` is NOT a singleton. It maintains `_instances: Map<key, state>` where each key is a GUID generated per `TimelineTab` component instance.

**Problem solved:** Multiple open Timeline tabs were sharing JS state. The second tab's `init()` call overwrote the first tab's state, causing both to flash and render the same data.

**Implementation:**
```javascript
// Public API — all methods take key as first arg
function init(key, laneEl, bottomEl, options) {
    if (_instances.has(key)) disposeInstance(_instances.get(key));
    const s = createInstance(laneEl, bottomEl, options);
    _instances.set(key, s);
}
function update(key, payload) { const s = _instances.get(key); if (s) ... }
```

**Critical rule:** The state object `s` must be created BEFORE wiring any D3 event handlers (zoom, mousemove, etc.). All closures capture `s` by reference. Using `let s; ... s = createInstance()` with handlers defined after assignment avoids the temporal dead zone (TDZ) issue with `const`.

**Previous failed approach:** Automated regex-based refactoring of `_state → s` throughout the file. This left `s` referenced as a free variable in D3 callbacks registered on SVG elements (e.g. `.style('fill-opacity', d => s.hoveredName ...)`). Those callbacks fire later, outside any function scope that has `s`, causing `ReferenceError: s is not defined`.

---

## D4. Timeline frozen domain (x-axis never moves from new data)

**Decision:** The timeline's x-axis scale domain (`frozenDomainMax`) is set once on first data load and never changes from incoming events. Only user interaction (pan/zoom/range selector) changes the viewport.

**Problem solved:** When `globalMax` grew (new live events arriving), changing `xScale.domain([0, newMax])` shifted all existing block positions even though `xZoom` didn't change, because D3's rescaled scale uses the domain for positioning.

**Implementation:**
```javascript
if (s.frozenDomainMax === null) {
    // FIRST LOAD ONLY
    s.frozenDomainMax = newMax;
    s.xScale.domain([0, newMax]);
    s.xZoom = d3.zoomIdentity;
    applyZoom(s, d3.zoomIdentity);
}
// Subsequent updates: only s.globalMax grows. xScale and xZoom untouched.
s.globalMax = newMax;
```

`globalMax` (the actual current data extent) is used for:
- Heatmap density proportions
- Range selector window position and size
- Right pan limit: `tx >= -(k * (globalMax/frozenDomainMax) * W - W)`

`frozenDomainMax` is used for:
- `xScale.domain` (what the scale maps to screen coordinates)
- Zoom clamp left boundary (tx ≤ 0)

**Reset view** fits to `[0, globalMax]`, not `frozenDomainMax`, so the user can see all data including events that arrived after the initial load:
```javascript
const k = gMax <= fMax ? 1 : fMax / gMax;
```

---

## D5. removeChild crash fix

**Problem:** `TypeError: Cannot read properties of null (reading 'removeChild')` when changing Group By or closing a tab.

**Root cause:** Blazor owns the `_canvasRef` div. When D3 called `svg.selectAll('*').remove()` or `svg.remove()` on elements inside that div, Blazor's subsequent DOM reconciliation tried to find nodes that no longer existed.

**Fix:** On dispose, remove only the SVG elements we appended — not by clearing the Blazor-owned container's children:
```javascript
function disposeInstance(s) {
    try { s.laneSvg.remove(); } catch {}  // removes the <svg> we appended
    try { s.botSvg.remove(); } catch {}
    try { s.tooltip.remove(); } catch {}
}
```

The Blazor container div remains untouched. When Blazor re-renders (e.g. after GroupBy change), the container is clean and `OnAfterRenderAsync` re-initialises D3.

---

## D6. Tab bar: horizontal scroll + JS-side drag

**Decision:** Replace the overflow-dropdown tab bar with horizontal scroll and JS-native drag-and-drop.

**Horizontal scroll:** `.bm-tabs-scroll` uses `overflow-x: auto; scrollbar-width: none`. Mouse wheel on the tab bar is intercepted by `initTabWheelScroll()` and translates `deltaY → scrollLeft`.

**Drag-and-drop:** Runs entirely in the browser using HTML5 drag events. A drop indicator (`div.bm-tab-drop-indicator`) is inserted into the DOM during drag to show insertion point. **One** Blazor call fires on drop: `OnTabDropped(tabId, targetIndex)` via `DotNetObjectReference`. No round-trips during the drag itself.

**Cursor:** `cursor: default` on `.bm-tab` (not `grab`). Only `cursor: grabbing` during `[draggable]:active`. This matches Chrome/VS Code behaviour — tabs don't show a grab cursor on hover.

---

## D7. SignalR hub event routing

> **Superseded.** Live event delivery no longer goes through a SignalR hub push at
> all — it's `RunEventBroker`, an in-process pub/sub keyed by `"{env}:{runId}"`
> (`RunHub` only handles group membership). See `02_Models_DataFlow.md`'s "Live
> Event Delivery" section for the current design. The routing problem this decision
> solved (multiple tabs sharing one connection, needing to know which run an event
> belongs to) is still solved the same way conceptually — by keying on `env:runId` —
> just without a SignalR round trip.

**Original decision:** The `"BatchEvent"` message carries three arguments: `(string env, string runId, PerformanceEvent event)`.

**Rationale:** `SignalRConnectionService` is scoped (one per Blazor circuit) and shared across all batch subscriptions in that circuit (multiple BatchDetail tabs + multiple Timeline tabs). Routing requires knowing which `(env, runId)` an incoming event belongs to. The event itself doesn't carry env/runId, so they must be sent as separate args.

**Alternative considered:** Separate HubConnections per batch tab. Rejected — SignalR connections are expensive; one per circuit is correct.

---

## D8. PerformanceEventService hybrid push+poll

> **Terminology updated, decision still current.** "SignalR push" below is now
> `RunEventBroker` push (see D7's note and `02_Models_DataFlow.md`) — the
> hybrid push+poll shape and rationale are otherwise unchanged as of this writing.
> Current intervals: focused-poll 3s, unfocused-poll 15s, push-active fallback 30s
> (`FocusedPollIntervalMs`/`UnfocusedPollIntervalMs`/`PushFallbackPollMs` in
> `PerformanceEventService.cs`).

**Decision:** For running runs, subscribe to broker push AND run a slow fallback poll (30s interval when push is active). For completed runs (or if push subscription fails), rely on the regular polling loop.

**Rationale:**
- The in-process broker has no persistence — a circuit that starts polling
  mid-run and a push subscription that silently stops both need polling as a
  safety net.
- Completed runs don't get push events from a service that's no longer running.
- Focus-aware: unfocused tabs poll slower (15s) than focused ones (3s active poll
  baseline, relaxed to 30s once push is confirmed active).

**Poll vs push priority:** broker-pushed events upsert into the event store
immediately. Poll results are also upserted. Because upsert uses `CompositeKey`
with last-write-wins, duplicate delivery from both paths is idempotent.

---

## D9. Block overlap: sub-row greedy packing

**Decision:** Events in the same group row are packed into sub-rows using greedy interval packing (first-fit from top). No cap on number of sub-rows. No LOD (level-of-detail) density bars.

**Algorithm:**
```javascript
for (const e of sortedByStart) {
    const xePack = e.finishMs ?? (e.startMs + AVG_DURATION_MS);  // 5s estimate for in-progress
    placed = false;
    for (const row of subrows) {
        if (e.startMs >= row[last].xEndPack) { row.push(e); placed = true; break; }
    }
    if (!placed) subrows.push([e]);
}
```

**Why `xEndPack` not `xEnd`:** In-progress events have no finish time. Using `frozenDomainMax` as `xEnd` for packing would make every row look "full" and push all subsequent events into new sub-rows. A 5s estimate (`AVG_DURATION_MS`) allows reasonable packing. The rendered block still extends to the right edge of the viewport.

**Sub-row height adjustment:** `↑/↓` arrow keys adjust `s.subrowHOverride` (min 1px, default `SUBROW_H=12`). When the vertical scrollbar is visible, `↑/↓` scroll instead (Ctrl overrides to height adjustment).

---

## D10. Stack view layout

**Decision:** Stack view shows one row per distinct `name`. Events within a chunk are placed consecutively by duration (not wall-clock position).

**Use case:** Compare how long each stage (service) took for each chunk, independent of when it started.

**Implementation:**
```javascript
// Each name → one row with events laid out left-to-right by duration
let cursor = 0;
for (const e of evtsSortedByStart) {
    const dur = e.finishMs - e.startMs;
    subrow.push({ xStart: cursor, xEnd: cursor + dur });
    cursor += dur;
}
```

The stack-mode blocks use `item.xStart`/`item.xEnd` directly (not scaled through `xS`) because they represent accumulated durations, not timestamps.

---

## D11. Core namespace strategy

> **Reversed.** The current Core library (`NxtUI.Core`) uses fully-qualified
> namespaces matching its folder structure — `NxtUI.Core.Models`,
> `NxtUI.Core.Services`, etc. — with no `RootNamespace` override, the opposite of
> this decision. `_Imports.razor` lists every namespace explicitly instead (see
> `06_UI_Components.md`). Left here for the historical rationale in case the
> flat-namespace tradeoff comes up again.

**Original decision:** Keep namespaces as `BatchMonitor.Models`, `BatchMonitor.Services`, `BatchMonitor.Configuration` in the Core library (not `BatchMonitor.Core.Models` etc.).

**Rationale:** Zero `@using` changes required in all `.razor` files. The assembly is named `BatchMonitor.Core` but the root namespace is `BatchMonitor` (set via `<RootNamespace>BatchMonitor</RootNamespace>` in the csproj).

---

## D12. Range selector implementation

**Decision:** Use `d3.brushX()` for the range selector (viewport window over heatmap).

**History:** Multiple attempts were made:
1. Custom drag-based selector — hard to maintain consistent state with zoom
2. `d3.brushX` rebuilt on every `renderBottom()` call — brush reset mid-drag, causing jumps
3. **Current:** `d3.brushX` built once (`buildSelector(s)`), only its `.move` position synced (`syncSelector(s)`). The brush `extent` is updated on resize. This prevents the drag interference.

**Brush overlay transparency:** `.bm-tl-sel-layer .overlay { fill: transparent !important }` makes the brush overlay transparent so the heatmap is visible through it.

**Heatmap behind selector:** The layer order in the bottom SVG is: tickL → heatL → selL → curBotL. The selector `g` is on top of the heatmap `g`, and the brush selection rect is semi-transparent (`rgba(56,139,253,0.18)`).
