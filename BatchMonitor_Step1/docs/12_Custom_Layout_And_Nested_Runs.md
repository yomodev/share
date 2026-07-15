# 12 — Custom Layout Engine & Nested Runs (design)

**Status:** implemented and in production use — `bm-flow-layout` is the run-detail flow
graph's layout engine. On the `remove-elk-layout-engine` branch, ELK.js has additionally
been removed entirely (§9's "Retire ELK" step); on `main` it may still be present as a
dormant, unused dependency depending on when this doc is read relative to that merge.
**Supersedes for the flow graph:** the ELK (elkjs) usage described in [04_FlowGraph.md](04_FlowGraph.md).
**Builds on:** [11_Topology_Hints.md](11_Topology_Hints.md) (the hint schema is *augmented*, not replaced),
and the generalized event model in `NxtUI.Core/Events/` ([02_Models_DataFlow.md](02_Models_DataFlow.md)).

This doc covers two related pieces of work that share one architectural idea (the *recursive box*):

1. Replacing ELK with a small, custom, framework-agnostic layout library that we fully control.
2. Showing **nested runs** (a parent run that triggers child runs) in the Run Detail page.

---

## 1. Why replace ELK

ELK is an **automatic** layout engine: it makes its own placement decisions and actively
resists absolute/relative position constraints. Almost every item on the wishlist is the
*opposite* — telling the layout **where** things go:

- next service should appear left / right / above / below the current one
- a service can flow in its own direction (this chain goes vertical, that one horizontal)
- groups are hard macro-blocks that nothing may overlap
- a service can declare itself "external" relative to peers on the same target, with a hint
  about which side its incoming arrow should come from
- edges enter/leave a specific side (vertical vs horizontal), optionally snapping to a
  specific pipeline row, turning 90° smoothly
- declared services appear from t=0 so the layout is **stable** across the run

ELK fights all of this (we already hit the wall: `role`/`order` only nudge layer assignment,
they cannot say "put B to the left of A"). It earns its keep on exactly one thing we *don't*
need: world-class crossing minimization on large graphs. Our facts:

- **≤ 50 nodes** ever. Crossing quality is a non-issue at this size.
- **Cycles are only the trivial A↔B pair** (A→B and B→A). We do **not** need general
  cycle-breaking — the scary part of Sugiyama layout.
- We want **stability** (ELK re-solves from scratch every update and reshuffles) and lots of
  **manual control** (where ELK is weakest).

Conclusion: this is a good case for a custom engine. Switching to another automatic library
(dagre, etc.) buys nothing — they're all automatic-first and fight the same constraints.

### Non-goal
We are **not** building a general graph-drawing library. We're building the minimum layered
layout that serves *our* graphs (small, near-DAG, heavily hinted).

---

## 2. The core idea: recursive boxes

Three separate requirements turn out to be the same primitive:

- a **group** = a box, laid out internally, placed as a rigid unit in its parent
- a **"turn vertical here" sub-chain** = a box laid out *vertically*, embedded in a
  *horizontal* parent
- a **nested run** (expanded) = a box that is *another whole run's topology*

> **A box lays itself out internally and is then packed as a single rigid unit into its
> parent's layout.**

So the engine is **recursive**: `layout(graph)` where any node is either a *leaf* or a
*sub-graph with its own orientation*. Build this recursion in from day one even though the
first milestone only uses leaves — groups, per-service orientation, and nested runs then slot
in with no rewrite.

Default fast path: a single global flow direction. A "turn here" hint spawns a recursive
sub-flow rather than bending the global grid, so 99% of the graph stays on the cheap path and
we only pay recursion where a turn was explicitly requested.

---

## 3. Layout algorithm (per box)

Classic layered (Sugiyama-lite), specialized for our constraints:

1. **Cycle handling** — the only cycle is A↔B. Pick one direction as the *structural* edge
   (the other becomes a back-edge rendered as a curve routing around the pair). No general
   cycle-breaking pass.
2. **Layer assignment** — longest-path layering along the flow direction. `role` pins
   source→first layer, sink→last layer.
3. **Group constraint (hard)** — members of a group are forced into a contiguous run of
   layers *and* a contiguous cross-axis order, and their bounding rect is reserved so no
   non-member may be ordered into it. This is the "macro-block" guarantee.
4. **Ordering within layer** — median/barycenter heuristic, a few sweeps, to reduce
   crossings. Directional hints (soft) are injected here as ordering constraints.
5. **Dummy nodes** — an edge spanning more than one layer gets virtual waypoints in the
   intermediate layers so it routes cleanly around boxes instead of through them (standard
   Sugiyama trick; also what makes long-edge routing look good).
6. **Coordinate assignment** — position along the cross-axis, then stretch the whole box to
   the container's aspect ratio (the "fill the screen" behavior).
7. **Edge routing** — orthogonal polyline through the dummy-node waypoints, rounded corners.
   *We already have `roundedOrthPath` in `d3-graph.js` — it ports over directly.* Port side
   per endpoint controls vertical-vs-horizontal entry; snapping to a pipeline row sets the
   exact anchor point.

### Constraint precedence (decided)
1. **Groups — hard.**
2. **Roles — hard.**
3. **Directional hints — soft** (best-effort; crossing-minimization / feasibility may override).
4. **Child overrides parent** — when two services along a path both express a preference, the
   one nearer the *downstream* (child) service wins. You read hints outward from a node and
   the nearest wins, so conflicts resolve locally and predictably.
5. **`order` / `priority`** breaks remaining ties.
6. **Unsatisfiable soft hints are dropped and logged as a warning** (not silently ignored, not
   fatal).

---

## 4. The library

A **framework-agnostic** module — plain ES2020 JS (no TypeScript, no bundler — matching every
other file in this folder), no D3 / Blazor / MudBlazor / SVG dependency.

- **Input:** a graph description (nodes with size + hints, edges with endpoints + port/direction
  hints, group declarations, orientation). This is essentially the compiled topology blueprint
  plus measured node sizes.
- **Output:** pure geometry — positioned nodes (`x`, `y`, `w`, `h`), edge bend-point polylines,
  group rects. Nothing about how it's drawn.

Benefits: unit-testable in isolation without a browser (assert crossing counts, group
containment, constraint satisfaction, stability across updates), and it keeps the D3 component
as a thin renderer that just consumes geometry (same seam ELK sits behind today — `runLayout`
already returns a normalized `{ nodes, edges }`).

**Not a separate project.** It lives at `src/NxtUI.Web/wwwroot/js/bm-flow-layout/layout.js` — a
subfolder of the existing `wwwroot/js`, sharing that folder's `package.json`
(`batchmonitor-js`) and `vitest.config.js` rather than getting its own. This was a deliberate
call (not the original plan, which envisioned an actual separate project): there's no bundler
or multi-package JS setup anywhere else in this repo, and introducing one for a ~350-line,
single-consumer module isn't worth the extra moving parts (a build/copy step, a second
`package.json` to keep in sync, a slower edit-test loop). Revisit if `bm-flow-layout` ever
needs to be reused outside this app.

---

## 5. Stability

ELK's biggest miss for us. The custom engine:

- Memoizes node positions keyed by node id.
- Only re-runs a full solve on a **structural** change (a node/edge/group added or removed).
- On a pure data update (counts, state), positions are untouched — only decoration animates.
- Seeds each new ordering pass from the previous result, so even a structural change moves
  things minimally instead of reshuffling.

Declared-from-t=0 services already give a stable skeleton; memoization keeps it stable as
observed nodes fill in.

---

## 6. Augmented hint schema

Additive and backward-compatible with [11_Topology_Hints.md](11_Topology_Hints.md). New,
all optional:

- **orientation** (per node): `horizontal` | `vertical` — the flow direction *from* this node;
  spawns a recursive sub-flow when it differs from the parent's. **Implemented** — see
  `13_Setting_Up_Nested_Runs_And_Topology.md` §4.5b for the worked example (`AuditProcessor`/
  `AuditLog` in `RUN-DEMO-LAYOUT-HINTS`) and `computeOrientationClosures`/
  `buildOrientationSubGraph` in `wwwroot/js/d3-graph.js` for the implementation (the closure
  computation and subGraph construction live there, not in `bm-flow-layout` itself — the engine
  only provides the generic recursive `subGraph` primitive both this and nested-run/group boxes
  build on).
- **direction** (per node, relative to the *current* service, in flow terms):
  `left` | `right` | `above` | `below` — a soft placement preference for the node's successor.
  **Implemented.**
- **placement** (per node): `left` | `right` | `above` | `below` — the mirror image of
  `direction`: this node's own preference relative to whichever predecessor points at it.
  Wins over a predecessor's `direction` for the same edge. **Implemented** (`ServiceHint.
  Placement`/`TopologyNode.Placement`, wired through `TopologyComputationService.Decorate`
  and threaded into `bm-flow-layout`'s existing `placement` field — the engine already
  supported this hint, it just had no C# schema field to reach it until now).
- **external** (per node, boolean) + **arriveFrom** (`left`|`right`|`above`|`below`): "place me
  outside the cluster of peers on the same target; my incoming arrow should come from this
  side," which the engine uses to pick which side of the shared target to place the node.
  **Implemented** — see the pushExternalNodesClearOfGroups note in doc 13 §4.5b for a known
  interaction with hard group boxes worth knowing about when debugging this hint.
- **port hints** (per edge or per endpoint): `enter`/`leave` side (vertical vs horizontal) and
  `snapToPipeline` (must connect to a specific pipeline row → forces a 90° turn if needed).
  Per-pipeline port routing (`LayoutEdge.variants`) is **implemented**; `snapToPipeline` as a
  distinct per-edge override is **not yet built**.

Reminder from doc 11 that still holds: hint *names* match against the **stripped** service
label (env-agnostic), not the raw id — see `ApplyBlueprint` in `TopologyComputationService`.

---

## 7. Nested runs

### 7.1 Relationship model (as it exists in real life)
A web service creates a parent `runId`, then calls a few endpoints that each start a **child**
run, passing the parent `runId` as a parameter. The parent is notified when a child
**completes or errors**. That's the entire linkage: children know their parent; the parent
holds a lightweight, growing record of its children.

- Depth: support up to **5 levels** (children can themselves be parents).
- **Children appear during the run** — the orchestrator spawns them over time. So "no children
  yet" is never "leaf forever."
- Trigger is **run-level**, not tied to a specific parent service/pipeline — so child blocks
  float as run-level units rather than hanging off a pipeline edge.
- The parent may be a **pure orchestrator today** (no services of its own), but the design
  treats "has services" and "has children" uniformly, so a parent that later gains its own
  services needs no special-casing.

### 7.2 Data contract
Extend `RunDetails`; add a small child-summary DTO. **Do not** reuse `PerformanceEvent` for
anything here, and note the general event type is already `NxtUI.Core.Events.RunEvent` (see
§7.5).

```
RunDetails (existing) gains:
  ParentRunId : string?        // on the fetched run ITSELF, for walking UP to build
                               // breadcrumbs on a deep-link; null at the root. NOT repeated
                               // on nested children (their parent is implicit from position).
  Children    : RunNode[]      // child summaries "so far" — GROWS during the run.

RunNode (new — summary only, never full detail):
  RunId       : string
  Description : string
  Status      : RunStatus                 // NotStarted | Running | Completed | Error | …
  Start       : DateTime?
  End         : DateTime?
  DoneCount   : int?           // best-effort live progress; null when not cheaply known
  TotalCount  : int?
```

Deliberately **absent**:
- `parentRunId` on `RunNode` — implicit from tree position.
- `hasChildren` — children appear dynamically, so a static flag is a moving target *and*
  redundant. Every run is *potentially* a parent; a child block is always drillable, and
  drilling shows whatever exists at that instant.

### 7.3 Fetch model (hybrid: summary-at-parent, detail-on-demand)
- `GetRunDetailsAsync(env, runId)` returns the run's own detail **plus** its *immediate*
  children as `RunNode` summaries. The backend's "is this a parent?" branch simply populates
  `Children`.
- Optional `childDepth` parameter lets the frontend request N levels in one call for
  "expand all"; default 1 keeps payloads tiny and bounded at every level (important at depth 5).
- The parent view **polls and diffs `Children[]`**: a newly-appeared child animates in; a
  status change updates its block. Live progress bar only if `DoneCount`/`TotalCount` are
  present, else an indeterminate "running" state that snaps to Completed/Error on the
  notification (matches "parent only notified on completion/error").

Payloads stay small (a summary is ~100–200 bytes; even 100 descendants ≈ 20 KB — and we
raised the SignalR receive limit to 10 MB anyway).

### 7.4 UX — a drill hierarchy (both "all together" and "one by one")
- **Overview:** the current run's flow (services, maybe none) **plus** its children as
  **collapsed blocks** — status color + mini progress bar/counts, no internal services. A pure
  orchestrator's overview is just the child cluster (optionally under a synthetic root showing
  roll-up progress).
- **Drill-in (click a child):** expands **in place** — the child's sub-flow renders inside the
  now-grown block, the parent dims/frames around it. Free, because the engine is recursive:
  "expand in place" == "render that box's sub-layout." (Fetches that child's detail on demand.)
- **Expand-all toggle:** the "see everything" mode — every child inline; best-effort, gets busy,
  explicit choice. Auto-expand is capped (e.g. ≤ 2 levels) to avoid a fetch storm; deeper needs
  a click.
- **Breadcrumbs:** a **second line in the Run Detail control bar**, shown only once you've
  drilled in (depth > 0), so it costs no vertical space normally. Each crumb is clickable to
  jump back to that level; the last (current) crumb shows that run's status/progress. This
  strip also hosts the expand-all/collapse toggle, keeping navigation controls in one place
  instead of floating over the canvas.
- **Not** used: opening a child in a whole separate page — it throws away context and in-place
  expansion is strictly better.

### 7.5 Naming: `RunEvent` already exists — lean into it
The instinct to distinguish a general `RunEvent` from the specific `PerformanceEvent` is
already realized: **`NxtUI.Core.Events.RunEvent`** is the generalized `Actor → Message → Target`
event; `PerformanceEvent` is one specific shape of it; `PerformanceEventBridge` maps between
them. So we **do not** create a new `RunEvent` DTO — that name is taken by exactly the concept
intended. For nested runs the only new DTO is `RunNode` (§7.2). Longer term, the topology can
be computed from `RunEvent` directly instead of via the `PerformanceEvent` bridge, but that's
independent of this work.

---

## 8. Zoom-on-hover (independent — can ship anytime, even on `main`)
A viewport/transform effect: hovering a node that renders small scales it up (and/or its
detail) so it's readable, without changing layout. Completely orthogonal to the layout engine —
listed here only so it isn't forgotten. Do **not** couple it to this experiment.

---

## 9. Build plan (staged, with a cheap bail-out)

Each stage is a checkpoint; **Stage 1 is the go/no-go gate** — if the automatic quality isn't
there on real runs, we stop having spent ~2 days, not a rewrite.

- **Stage 0 — seam.** Extract layout behind a clean interface so ELK and the custom engine are
  swappable. `runLayout` already returns a normalized `{ nodes, edges }`; formalize that as the
  contract. No behavior change; ELK still active.
- **Stage 1 — base engine (GATE).** New `bm-flow-layout` module (§4): leaves only, single
  global direction, layer assignment + median ordering + dummy-node routing + coordinate assignment.
  Render it side-by-side with ELK on real runs; compare. Unit tests for crossing counts and
  determinism. **Decide here whether to continue.**
- **Stage 2 — hints & groups.** Roles→pinning, groups→hard macro-blocks, directional (soft)
  ordering constraints, external/arriveFrom, per-edge port/orientation, recursive sub-flows for
  orientation changes. Warnings on unsatisfiable soft hints.
- **Stage 3 — stability.** Position memoization; relayout only on structural change; seed from
  previous.
- **Stage 4 — nested runs.** `RunNode` + `RunDetails.ParentRunId`/`Children`; `childDepth` fetch;
  overview collapsed blocks with live diffing; in-place drill; breadcrumb bar; expand-all.
  (Needs the backend `getRunDetails` change — a parallel track.)
- **Independent — zoom-on-hover.** Anytime.

Retire ELK (remove elkjs, delete the ELK branch of the seam) only after Stages 1–3 are trusted
on real data. **Done** on the `remove-elk-layout-engine` branch: the elkjs CDN `<script>` tag,
`runLayoutElk` and its ELK-only helpers (`portId`, `layerConstraint`, `densityScale`,
`portYFromTop`), `handle.elk`/`handle.layoutEngine`, `RunsSettings.GraphLayoutEngine`/
`GraphPortConstraints`, and `ServiceHint`/`TopologyNode.PinX`/`PinY` are all removed.
`portXEvenSpread` was kept — despite being written for the ELK path originally, the
custom engine's own per-pipeline ports (`portOffset`, §6 "port hints") now reuse it too.

---

## 10. Open decisions
- **Backend child progress:** can the store cheaply produce `DoneCount`/`TotalCount` per run for
  the summary, or is status-only acceptable until completion? (Drives whether collapsed blocks
  animate a real bar.)
- **Synthetic root** for a pure-orchestrator overview: show one, or just a bare child cluster?
- **`left/right/above/below` semantics** are relative to the *current* service in *flow* terms
  — confirm this reads intuitively once a sub-flow flips orientation (a "below" inside a
  vertical sub-flow is a different screen direction than in a horizontal parent).
