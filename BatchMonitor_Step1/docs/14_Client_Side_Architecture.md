# 14 — Client-Side Architecture: JS Modules and How to Test Them

BatchMonitor is a Blazor **Server** app — components run on the server, and the DOM the user
sees is a live SignalR-synced rendering of server state. Most UI is plain Blazor/MudBlazor and
never touches JavaScript at all. But several features are either too visually specialized (the
D3-based flow graph and timeline), too performance-sensitive to round-trip through the SignalR
circuit for every frame (animation, virtual-scrolled log viewing), or need direct browser APIs
Blazor doesn't expose (the File System Access API) — those live in
`src/NxtUI.Web/wwwroot/js/*.js` as plain ES modules, invoked from Razor components via Blazor's
JS interop (`IJSRuntime.InvokeAsync`/`InvokeVoidAsync`, `[JSInvokable]` callbacks back into C#).

This doc is the map of that JS layer: what each module does, how it's wired to its Razor
component, which ones have real unit test coverage and which are manual-verification-only, and
how to actually run/extend the tests. Read this before making a non-trivial change to any file
under `wwwroot/js/` — several of these modules encode non-obvious invariants (documented inline,
and summarized here) that are easy to accidentally break.

---

## 1. The house style: no bundler, no framework

Every file in `wwwroot/js/` is a **plain ES2020 module**, loaded directly by the browser — no
webpack/vite/esbuild, no TypeScript, no npm-installed UI framework. Two consequences:

- **D3 and CodeMirror are loaded globally via `<script>` CDN tags** (see `_Host.cshtml` /
  wherever the layout's `<head>` lives) rather than `npm install`ed and bundled. Files that use
  D3 (`d3-graph.js`, `d3-timeline.js`, `run-stats-chart.js`, the memory charts, the home
  treemap) reference the `d3` global directly — no `import * as d3`. `text-editor.js` is the one
  exception: CodeMirror 6 ships as many small interdependent npm packages, so it's pulled from
  **esm.sh** (a CDN built specifically for resolving npm-style ESM dependency graphs at request
  time) instead — see §7 for why the exact CDN and query params matter there.
- **Cache-busting is manual, via a `?v=NN` query string** on the dynamic `import()` call in the
  owning Razor component (e.g. `D3FlowGraph.razor`'s `JS.InvokeAsync<IJSObjectReference>("import",
  "./js/d3-graph.js?v=53")`). Bump the number every time you edit the JS file. **This is easy to
  forget** — if a JS change doesn't seem to take effect after a rebuild, check whether the
  version string was bumped and whether the Razor file itself was recompiled (a `.razor`-embedded
  string change needs an actual rebuild + circuit restart to take effect; a version-string bump
  with no rebuild does nothing). One file, `d3-graph.js`, `import`s a sibling module
  (`bm-flow-layout/layout.js`) **without** its own version query — that inner import is cached by
  the browser separately from the outer one; if you edit `layout.js` and a fresh navigation still
  seems to run the old code, a hard-refresh (bypassing cache) rules that out first.

There's a **single shared** `package.json` (`batchmonitor-js`) and `vitest.config.js` at the
`wwwroot/js/` root — not one per module, not a separate project per file. This was a deliberate
choice (see doc 12 §4's note about `bm-flow-layout` specifically) to avoid multi-package
overhead for a folder with no build step; every module's tests run through the same `vitest`
invocation.

---

## 2. Module map

| File | Owns | Razor component(s) | Unit-tested? |
|---|---|---|---|
| `d3-graph.js` | Run Detail flow graph: layout orchestration, rendering, hover popovers, animation loop | `D3FlowGraph.razor` | No (DOM-heavy; see §3) |
| `bm-flow-layout/layout.js` | Pure-geometry custom layout engine consumed by `d3-graph.js` | *(none directly — consumed by d3-graph.js)* | **Yes** — `layout.test.js` |
| `d3-animation.js` | Edge dash-flow animation math (throughput → speed/gap/glow) | *(consumed by d3-graph.js)* | No, but pure functions — easy to add tests |
| `d3-timeline.js` | Timeline tab: per-instance Gantt-style event blocks, search/filter, hover popups | `Timeline.razor` (or equivalent) | No (DOM-heavy) |
| `filter.js` | Shared filter-syntax parser/evaluator (AST mirrors `FilterParser.cs`) | Used by Timeline, Log Viewer, Runs/Services filter boxes | **Yes** — `filter.test.js` |
| `log-viewer-parser.js` | Pure log-line parsing (legacy fixed-format + configurable `{placeholder}` formats) | *(consumed by log-viewer.js)* | **Yes** — `log-viewer-parser.test.js`, plus `log-format-grammar.contract.test.js` |
| `log-viewer.js` | Log Viewer: virtual scroll, search, bookmarks, multi-viewport-per-document | `LogViewer.razor` | No (DOM-heavy; parsing logic it depends on is tested separately) |
| `log-file-access.js` | Browser-native (File System Access API) log file reads, Chromium-only | Log Browser page | No (needs a real browser + user gesture; not mockable meaningfully) |
| `run-stats-chart.js` | Run Stats grouped-bar duration histogram | `RunStatsPage.razor` | No (DOM-heavy) |
| `d3-home-treemap.js` | Home page memory treemap | `Home.razor` | No (DOM-heavy) |
| `d3-memory-stacked.js` | Memory stacked-area chart | Services/memory detail views | No (DOM-heavy) |
| `d3-memory-sunburst.js` | Memory per-service line chart + brush navigator (despite the filename, the sunburst it was named for was removed — see the file's own header comment) | Services/memory detail views | No (DOM-heavy) |
| `text-editor.js` | CodeMirror 6 wrapper for the Config page's JSON/text editor | `Config` page | No (DOM-heavy, third-party editor) |
| `json-viewer.js` | Collapsible JSON tree viewer (Kafka message payloads, etc.) | Kafka message viewer, elsewhere | No (small, DOM-only, low-risk) |
| `purge-stream.js` | Streams NDJSON progress from `/api/purge-simulate` to Blazor for a live progress bar | `PurgeDialog` | No (network-streaming glue, thin) |
| `workspace.js` | Drag-resize for `WorkspacePanel` split handles | `WorkspacePanel.razor` | No (pure DOM interaction) |
| `app.js` | Small cross-cutting utilities (currently: suppress double/triple-click text selection in data tables; also hosts tab drag/scroll helpers — see `initTabDrag`/`scrollTabIntoView` used by `TabBar.razor`) | Global (`App.razor`/layout) + `TabBar.razor` | No (DOM glue) |

**Rule of thumb for "is this testable":** the `vitest.config.js` environment is `'node'` — **no
jsdom, no real DOM**. Modules that only manipulate the DOM (query/create/mutate elements,
attach D3 selections) cannot be meaningfully unit tested in this setup at all; they're verified
manually in the browser (see the Verification workflow used throughout this repo's development —
open the app, interact, check the console/network tab). Modules whose core logic is **pure**
(string/object in, value out, no DOM) — `filter.js`, `log-viewer-parser.js`,
`bm-flow-layout/layout.js` — get real `vitest` coverage, because that's exactly what a
Node-environment test runner can exercise. If you're adding a new module and want it testable,
the actionable takeaway is: **separate the pure logic from the DOM-touching parts** into its own
file/function, the way `log-viewer.js` (DOM) depends on `log-viewer-parser.js` (pure) rather
than being one monolith.

---

## 3. Deep dive: `d3-graph.js` — the Run Detail flow graph

The largest and most architecturally involved module (~2000 lines). It owns everything the
Run Detail page's flow graph does: turning a `Topology` (see `NxtUI.Core.Models.Topology`) into
positioned nodes/edges, drawing them, animating them, and handling hover/click interaction —
including nested child-run boxes (docs/12 §7.4) and two swappable layout *engines* underneath a
single rendering pipeline.

### 3.1 The handle pattern

`D3FlowGraph.razor` calls `init(containerEl, key, dotNetRef, portConstraints, edgeStyle,
defaultDirection, layoutEngine)`, which creates and stores a **handle** object keyed by `key` in
a module-level `_handles` Map. Every subsequent call from Razor (`update`, `setVisible`,
`fitToView`, `dispose`) looks the handle back up by that same key. This is the standard pattern
for "one JS-side instance per Blazor component instance" without a class/closure per component —
Blazor's own object references aren't stable enough across circuit reconnects to key off
directly, so a caller-chosen string key (`GetHandleKey()` in the `.razor` file, typically derived
from the component's own hash code) is used instead. The handle carries every piece of mutable
state for one graph: the SVG root, layered `<g>` groups (`gLayer`/`cbLayer`/`eLayer`/`nLayer` —
groups, child-run boxes, edges, nodes, each its own layer so paint order is correct regardless of
data-update order), the zoom behavior, the current/previous laid-out graph for diffing, the
animation RAF handle, and configuration echoed from `RunsSettings` (port constraints, edge style,
direction, which layout engine).

### 3.2 The layout seam: two engines, one render pipeline

`RunsSettings.GraphLayoutEngine` picks `"Elk"` (the ELK.js-based engine, `runLayoutElk`) or
`"Custom"` (the in-house `bm-flow-layout` engine, `runLayoutCustom`, wrapping the `layout()`
function from `bm-flow-layout/layout.js` — see §4). **Both must return the exact same shape**:
`{ nodes, edges, width, height }`, node positions/sizes and edge waypoint polylines in the same
coordinate space. Everything downstream — `renderNodes`, `renderEdges`, `renderGroups`,
`renderChildRunBoxes`, the animation loop, `fitToView` — is **shared** and knows nothing about
which engine produced the geometry it's drawing. This is the seam described in doc 12 §9 ("Stage
0"): it's what let the Custom engine be built and compared side-by-side with ELK without
touching a single line of rendering code, and (at the time of writing) is also what a future
"remove ELK" branch collapses down to a single call, deleting `runLayoutElk` and the seam itself
while leaving rendering completely untouched.

Practical implication for anyone debugging a rendering bug: **first determine whether the bug is
in layout (wrong position/size) or rendering (right position, wrong pixels)** by inspecting
`handle.currentGraph` in the browser console — if node coordinates already look wrong there, the
bug is upstream in whichever engine is active; if coordinates look right but the SVG doesn't
match, the bug is in the render functions.

### 3.3 Child-run boxes: satellite positioning, not layered positioning

Expanded and collapsed child-run items (docs/12 §7.4) are deliberately **not** fed into the
layered/structural layout solve alongside real pipeline nodes — they have no structural edges (a
child run isn't attached to a specific service/pipeline), so including them corrupted layer
assignment and caused visible overlaps (a real bug fixed earlier in this project's history — see
the git log for "bm-flow-layout: fix group/box overlap bug"). Instead, `buildLevelForLayout`
collects them into a separate `boxSpecs` list, and `packSatelliteBoxes`/`mergeSatelliteBoxes`
position them in their own row/column *outside* the main layout, merging the result into a
recomputed bounding box afterward. `flattenChildRunBoxes` then walks the resolved positions
recursively (an expanded box's own content can itself contain further-nested boxes/cards, to
whatever depth the user has drilled into), and `renderChildRunBoxes` is the **single** renderer
for every child-run item at every nesting depth — collapsed card or expanded box, top-level child
or grandchild. (An earlier version had a separate `renderChildRuns`/`crLayer` path that only ever
handled the top level's own immediate children; it was deleted once the current unified approach
landed, precisely because a grandchild's own collapsed card never rendered under the old split
design.)

### 3.4 Animation loop

`d3-animation.js` computes dash-flow parameters (speed, gap, brightness) per edge from recent
throughput; `d3-graph.js`'s own RAF loop (`renderAnimationFrame`) applies them directly via raw
DOM mutation (`el.__data__`/`setAttribute`, not `d3.select(...).attr(...)`) rather than
re-entering D3's selection machinery every frame — this was a deliberate CPU optimization (see
git history: "Investigate high CPU usage on RunDetail page") since a full D3 selection re-bind
60 times a second for every edge on a busy graph was measurably expensive. The loop pauses
entirely when the owning tab isn't visible (`setVisible(false)`, driven by `IsVisible` on
`D3FlowGraph.razor` — see its own doc comment about "unfocused canvas consuming GPU").

### 3.5 The "structurally stable" fast path

`commitLayout` (the function that takes freshly computed geometry and applies it to the DOM)
has two branches: a full re-layout, and a cheaper "structurally stable" path taken when a new
`Topology` snapshot has the exact same **structural key** (same node ids, same edges, same
groups — computed once and compared) as the previous one. On a live-updating run, most ticks
only change counts/state/colors, not shape — the stable path skips the expensive parts (full
clear-and-rebuild of child-run box clip-paths, full re-layout) and only updates what actually
changed (`childRunBoxesSignature` — status/color plus a collapsed card's own live counts — decides
whether even the boxes need touching). This is the single biggest lever for keeping a live
Run Detail page's CPU usage low; if you're adding a new per-tick concern, ask whether it needs
the full-relayout branch or can be folded into the stable-path signature check instead.

---

## 4. Deep dive: `bm-flow-layout/layout.js` — the custom layout engine

A small, framework-agnostic (no D3/DOM/Blazor dependency at all — pure function, unit-testable
in Node) layered graph layout engine, purpose-built for this app's actual needs rather than
being a general graph-drawing library. Full design rationale is in doc 12 §1–§6; this section is
the "how the code is actually organized" summary for anyone about to modify it.

**Pipeline, in order** (see the `layout()` function, which is the single exported entry point):

1. **Recursive `subGraph` resolution** (bottom-up) — any node declaring a `subGraph` gets its own
   sub-layout computed *first*, sized, and the result cached; only then does the parent's own
   layout run, treating that node as a leaf with a synthesized width/height. This is what powers
   groups-as-boxes, per-node orientation changes, and nested-run expansion — all three are the
   same primitive (doc 12 §2).
2. **Cycle splitting** (`splitBackEdges`) — only the trivial A↔B pair is supported; the
   first-seen direction becomes structural, the reverse becomes a back-edge routed separately as
   a bowed curve.
3. **Layer assignment** (`assignLayers`) — longest-path layering via Kahn's algorithm topological
   order.
4. **Role pinning** (`pinRoles`) — `source`/`sink` nodes forced to layer 0 / last layer, but
   *only* if doing so doesn't contradict the DAG's own edges (otherwise dropped with a warning).
5. **Dummy-node chain construction** (`buildDummyChains`) — every edge spanning >1 layer gets a
   waypoint node in each intermediate layer, so routing has explicit points to pass through
   rather than cutting through intervening layers.
6. **Ordering within layers** (`orderLayers`) — a few passes of median/barycenter heuristic
   ordering, layered with (in this priority): `external`/`arriveFrom` absolute pins (highest —
   `EXTERNAL_BIAS`) → directional `placement`/`placeSuccessor` soft hints (`HINT_BIAS`) → plain
   neighbor-median. `clusterByGroup` runs after every pass to keep group members contiguous
   (hard constraint, always wins over ordering).
7. **Coordinate assignment** (`assignCoordinates`) — positions along the flow axis (grid-aligned
   per layer, sized to the widest node in that layer) and the cross axis (each layer centered
   independently around 0 — meaning **coordinates are routinely negative**; this bit a
   `subGraph` translation bug once, see the code comment near the bounding-box computation in
   `layout()` about why min/max must both be tracked, not just the positive extent).
8. **Edge point generation** (`chainToPoints`/`orthogonalizeChain`) — real orthogonal routing:
   endpoints clip to the actual node border (not center), and an elbow is inserted between any
   two consecutive waypoints whose cross-axis coordinate differs, so every rendered segment is
   pure horizontal or pure vertical (required for the shared `roundedOrthPath` renderer in
   `d3-graph.js` to draw correct rounded corners).

**Stability** (doc 12 §5): `opts.seedOrder` (a node-id → previous-index map) biases the initial
ordering toward the previous layout's result, so a structural change moves things minimally
instead of reshuffling the whole graph. `d3-graph.js` is responsible for building and passing
this map across relayouts — the engine itself is a pure function with no memory between calls.

### 4.1 How to test it

```
cd src/NxtUI.Web/wwwroot/js/bm-flow-layout
npx vitest run          # once, all tests
npx vitest              # watch mode
```

(If `npx`/`npm` aren't on `PATH` in your shell — this has been observed in at least one dev
environment used on this project — install Node.js properly or invoke `vitest` via its full path
in `node_modules/.bin/`. The test file, `layout.test.js`, is plain enough to also sanity-check by
hand: write a throwaway `.mjs` script that `import`s `layout` from `layout.js`, builds a small
`{ nodes, edges }` input, calls `layout(input)`, and asserts on `result.nodes`/`result.edges`
directly with `node script.mjs` — no test runner needed for a quick one-off check. This is
exactly how several of the engine changes described in this repo's commit history were verified
when `npx` wasn't available.)

`layout.test.js` is organized by feature area (`describe` blocks): layer assignment, back-edge
handling, dummy-node routing, crossing reduction, stability, vertical direction, role hard-pin,
groups, directional soft hints, and `external`/`arriveFrom`. **When adding a new hint or
constraint, add a new `describe` block following this pattern** — most existing tests build a
small hand-crafted graph (3-5 nodes) and assert a specific geometric relationship (e.g. "B's y is
greater than C's y"), not exact pixel values (exact positions are an implementation detail that
would make tests brittle; relative relationships are the actual contract).

---

## 5. Deep dive: `filter.js` — the shared filter-syntax engine

Parses and evaluates the same filter-box syntax used across Runs, Services, Timeline, and the
Log Viewer (see [08_FilterSyntax.md](08_FilterSyntax.md) for the syntax itself). Exports
`parse(input, searchableFields, aliases?) → FilterNode | null`, `evaluate(node, obj) → boolean`,
and `serialize(node) → string`. The AST shape is **mirrored on the C# side**
(`MongoFilterBuilder`/`FilterParser.cs`) — client-side filtering (Timeline, Log Viewer, anything
operating on data already in the browser) and server-side filtering (Mongo queries) both parse
the *same* input text into structurally equivalent trees, so the filter syntax behaves
identically regardless of which side ends up evaluating it. **If you change the grammar here,
the C# parser needs the matching change** — there's no single source of truth shared at the code
level (JS and C# each have their own parser), only at the design/contract level, so it's on the
implementer to keep them in sync. `filter.test.js` covers this file directly; there's no
cross-language contract test that runs both parsers against the same input and diffs the
result — worth knowing as a gap if you're touching the grammar.

---

## 6. Deep dive: Log Viewer — `log-viewer-parser.js` + `log-viewer.js`

Split deliberately along the "pure logic vs. DOM" line described in §2:

- **`log-viewer-parser.js`** — no DOM at all. Parses raw log text into structured entries, in
  two modes: legacy fixed-position pipe-delimited (`timestamp|level|host|pid|thread|message|
  caller`), or configurable via `Logs:Formats` template strings (`{timestamp}`, `{level}`, etc.,
  plus `{*}` for "anything, non-capturing" and `{$}` for end-of-line) with auto-detection of
  which configured format matches the first few lines. `log-format-grammar.contract.test.js`
  exists specifically to pin down that grammar's edge cases (what `{*}` does and doesn't match,
  multi-line `BEGIN`/`END` blocks) as a contract independent of the line-parsing tests in
  `log-viewer-parser.test.js` — read both files together if you're touching format-string
  parsing, since the split exists to separate "does the grammar mean what we think it means"
  from "does a specific real log line parse correctly."
- **`log-viewer.js`** — everything DOM: virtual scrolling (only rendering visible rows for
  multi-hundred-thousand-line files), search/highlight, bookmarks. Architecturally split into
  `_docs` (one `Doc` per loaded file — shared entries/bookmarks) and `_vp` (one `Vp` per DOM
  container/viewport — its own scroll position and search state), so the same file can be shown
  in two viewports (e.g. split-pane) without loading/parsing it twice; `destroy()` ref-counts
  viewports so a `Doc` is only unloaded once its last viewport closes.

`log-file-access.js` is a related but separate concern: opt-in, Chromium-only, browser-native
file reads via the File System Access API, so a double-click can open a log file directly from
the browser's own OS-level folder access instead of round-tripping the content through the
server/SignalR circuit. Every entry point resolves to `null` on any failure (unsupported
browser, user declined the permission prompt, path not found under the previously-granted
folder), and callers are expected to fall back to the existing server-read path — this file
should never be the sole path to reading a file.

---

## 7. `text-editor.js` — the CodeMirror ESM CDN gotcha

Worth calling out on its own because it's a real footgun documented in the file's own header
comment: CodeMirror 6 is split across many small npm packages that import each other internally
(`@codemirror/state`, `@codemirror/lang-json`, `@lezer/highlight`, etc.). Since this app has no
bundler, each package is fetched individually from **esm.sh**, a CDN that resolves npm-style
dependency graphs at request time — critically using esm.sh's `?deps=` query parameter to pin
every package to the *same* resolved copy of shared dependencies. **Do not** switch to a
different CDN (e.g. jsDelivr's `+esm` endpoint) or mix CDNs for these imports: if two packages
end up resolving `@codemirror/state` to two different bundled copies, CodeMirror's extension
system (which uses `instanceof` checks internally to validate extensions) throws
`"Unrecognized extension value ... multiple instances of @codemirror/state are loaded"`, or
silently fails to apply syntax highlighting. If you ever need to add a CodeMirror
language/extension package, read the existing import block in `text-editor.js` first and follow
its exact `?deps=` pinning pattern rather than adding a bare import.

---

## 8. Everything else, briefly

- **`d3-timeline.js`** — the Timeline tab's Gantt-style per-instance event rendering, search, and
  hover popups. Imports `filter.js` for its own search box. Like `d3-graph.js`, uses a
  per-instance-key state map (no module-level mutable state) so multiple Timeline tabs stay
  isolated from each other.
- **`run-stats-chart.js`**, **`d3-home-treemap.js`**, **`d3-memory-stacked.js`**,
  **`d3-memory-sunburst.js`** — self-contained D3 chart renderers, each exposed as a
  `window.<name>` object (not an ES module export) consumed directly by their owning Razor
  component's JS interop calls. `d3-memory-sunburst.js`'s current content is actually a line
  chart with a brush navigator — the sunburst it was originally named for was removed when
  superseded, and the file was never renamed; don't be misled by the filename.
- **`json-viewer.js`** — a small collapsible JSON tree (`window.bmJsonViewer.render(el, json)`),
  used for viewing Kafka message payloads and similar structured data.
- **`purge-stream.js`** — reads the NDJSON (newline-delimited JSON) streaming response from
  `/api/purge-simulate` line-by-line as it arrives, forwarding each line to Blazor immediately
  rather than waiting for the full response — this is what lets `PurgeDialog` show live progress
  instead of a single end-of-job result.
- **`workspace.js`** — drag-resize logic for `WorkspacePanel`'s split handles; panes use
  `flex-grow` (not `flex-basis` percentages) specifically so the 4px handle itself is
  automatically excluded from the distributed space rather than needing manual accounting.
- **`app.js`** — cross-cutting utilities not big enough to deserve their own file: currently,
  suppressing the browser's native double/triple-click text-selection behavior inside data
  tables (so `@ondblclick` row-action handlers don't also select cell text), plus tab
  drag-reorder (`initTabDrag`) and scroll-into-view (`scrollTabIntoView`) helpers used by
  `TabBar.razor`.

---

## 9. Adding a new JS module: checklist

1. Plain ES module (`export function ...`), or `window.<name> = (() => {...})()` if it needs to
   be a Blazor-callable global without per-instance state — follow whichever pattern the
   *closest analogous existing file* uses (a stateless chart renderer → `window.*` IIFE pattern;
   anything with per-instance/per-key state → the handle-map pattern from `d3-graph.js`/
   `d3-timeline.js`).
2. If it uses D3 or another CDN-loaded global, do **not** `import` it — reference the global
   directly, matching every other chart module.
3. Separate pure logic from DOM manipulation into different functions/files wherever feasible —
   that's the difference between "testable with `vitest`" and "manual-browser-verification
   only," and it's worth the split even for a module that's mostly DOM work, if there's any
   parsing/computation piece that can stand alone (see `log-viewer.js`/`log-viewer-parser.js`).
4. Add a `?v=1` (or next available number) query string on whatever Razor component's
   `JS.InvokeAsync<IJSObjectReference>("import", "./js/your-module.js?v=1")` call loads it, and
   remember to bump that number on every future edit.
5. If it has pure-logic pieces, add a `your-module.test.js` alongside it — `vitest` picks up any
   `*.test.js` file under `wwwroot/js/` automatically via the shared root `vitest.config.js`, no
   per-file registration needed.
6. Verify manually in the browser regardless of test coverage — there is no automated
   browser/screenshot test in this repo for any of these modules; the verification workflow used
   throughout this project's development is: open the app, interact with the feature, check the
   browser console for errors and the Network tab for anything unexpected, and (for anything
   touching the flow graph specifically) inspect `handle.currentGraph` directly via the console
   to separate a layout bug from a rendering bug (§3.2).

---

## 10. See also

- [12_Custom_Layout_And_Nested_Runs.md](12_Custom_Layout_And_Nested_Runs.md) — design rationale
  for `bm-flow-layout` and nested-run rendering (§3's material in narrative/design form).
- [13_Setting_Up_Nested_Runs_And_Topology.md](13_Setting_Up_Nested_Runs_And_Topology.md) —
  practical walkthrough for configuring what `d3-graph.js` renders via topology hint JSON,
  without touching this code at all.
- [05_Timeline.md](05_Timeline.md) — Timeline tab design (complements §8's brief mention of
  `d3-timeline.js`).
- [08_FilterSyntax.md](08_FilterSyntax.md) — the filter syntax `filter.js` (§5) and its C#
  mirror both implement.
