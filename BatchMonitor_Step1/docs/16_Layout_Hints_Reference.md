# 16 — Layout Hints Reference

A single reference table for every topology-hint option: what object it lives on,
what values it accepts, what it actually does, and the gotchas that have come up in
practice. For the worked walkthrough (how to write a hint file from scratch) see
`13_Setting_Up_Nested_Runs_And_Topology.md`; for the layout *engine's* internals see
`12_Custom_Layout_And_Nested_Runs.md`. This doc is the "what does field X do and
where do I put it" lookup — it doesn't re-explain the algorithm.

All hints live in `config/topology/{runType}.json`, deserialized by
`src/NxtUI.Core/Models/TopologyHint.cs`. Every field below is optional; an unset
field means "no opinion, let the layout do its default thing."

---

## 1. Where each hint object sits

```
TopologyHintFile
├── RunType: string
├── Variants: [ TopologyVariant, ... ]   — tried in order, first VariantMatch wins
└── Default: TopologyVariant             — fallback when nothing matches

TopologyVariant
├── Match: VariantMatch?                 — discriminator (null on Default)
├── Layout: LayoutHint?                  — GRAPH-level preferences
├── Services: [ ServiceHint, ... ]       — one entry per declared/decorated service
├── Edges: [ EdgeHint, ... ]             — explicit service→service edges
├── Groups: [ GroupHint, ... ]           — per-group colour upgrade
├── ExpandChildrenByDefault: bool?       — nested-run behavior override
└── ChildRunBoxColor: string?            — nested-run box color override
```

- `TopologyVariant.Match` (`VariantMatch.AnyService`, a glob) picks which variant
  applies once a service matching it has been seen in the run. Multiple run
  "shapes" under one `runType` are handled by declaring several `Variants` and
  letting the first match lock it in; `Default` is the fallback.
- Everything below `Layout`/`Services[]`/`Edges[]`/`Groups[]` belongs to whichever
  variant matched (or `Default`).

---

## 2. Graph-level: `LayoutHint`

| Field | Values | Effect | Status |
|---|---|---|---|
| `direction` | `"horizontal"` \| `"vertical"` | Overall flow direction for this run's graph. Unset falls back to `RunsSettings:GraphDirection` (`"Horizontal"`/`"Vertical"`/`"Auto"` — Auto picks by the graph panel's own aspect ratio). | Live |
| `shape` | `"layered"` \| `"tree"` | Was ELK's `layered`/`mrtree` algorithm choice. | **Dead** — ELK removed; `bm-flow-layout` only ever does one layered algorithm. Schema-declared, not consumed. |
| `density` | `"compact"` \| `"normal"` \| `"airy"` | Scales node/edge spacing (×0.65 / ×1 / ×1.6). | **Dead** — was ELK-only, removed along with it. Schema-declared, not consumed by `bm-flow-layout`. |
| `straightenEdges` | `bool` | Was ELK's `NETWORK_SIMPLEX` vs `BRANDES_KOEPF` node-placement toggle. | **Dead** — same as above. |

Only `direction` is live. `shape`/`density`/`straightenEdges` remain in the schema
(no breaking JSON change) but do nothing on the current engine — see
`12_Custom_Layout_And_Nested_Runs.md` §9 for the ELK-removal history.

---

## 3. Per-service: `ServiceHint`

Applies to one entry in `Services[]`. `Name` may be a literal (pre-renders a
greyed skeleton node from t=0) or a glob (`*`/`?`, only decorates a matching
runtime node — no pre-render, since there's no concrete identity to draw yet).

| Field | Values | Applies to | Effect |
|---|---|---|---|
| `role` | `"source"` \| `"sink"` \| `"middle"` | any node | Hard-pins the node to the first/last layer. `middle` (or unset) has no pin — normal layer assignment by longest-path from sources. |
| `group` | string (cluster label) | any node | Nodes sharing a `group` value are kept **contiguous** — same run of layers *and* cross-axis order — with a bounding band drawn behind them (see §4). Any shared tag gets a cosmetic band automatically; no separate `Groups[]` entry needed unless you want a real coloured/bordered box (§4). |
| `color` | hex string | any node | Header accent override. The node's *state* colour (green/blue/red/grey) still shows via the border/glow — `color` only changes the flat accent bar, it doesn't hide status. |
| `order` | int (lower first) | any node, typically ones sharing a `group` or converging on the same target | Tie-break for ordering within a layer, after every hard/soft constraint has already settled everything else. |
| `pin` | bool | any node | *Reserved* — no effect in the current engine (proper pinning would need persisted positions across relayouts, not built yet). |
| `direction` | `"left"` \| `"right"` \| `"above"` \| `"below"` | any node | Soft placement preference for **this node's successor(s)** — "whatever comes after me, put it below/above/etc. me." A successor's own placement preference (not yet exposed in the JSON schema — JS-engine-only `placement`/`placeSuccessor`, see `12` §6) always wins if both are set ("child overrides parent"). Only `above`/`below` are meaningful in a horizontal flow, only `left`/`right` in a vertical one. |
| `orientation` | `"horizontal"` \| `"vertical"` | any node, meant for a genuine side-branch's *first* node | When different from the graph's own flow direction, this node becomes the root of a recursive sub-flow: itself + its exclusive downstream closure (forward-reachable nodes that never rejoin the main graph) are laid out internally in the new direction and packed as one rigid box. See `13` §4.5b for a worked example (`AuditProcessor`/`AuditLog`). Equal to the graph's own direction, or unset, is a no-op. |
| `external` | bool | a node that's one of several peers converging on the same downstream target | Pulls the node to the extreme edge of its own layer's cross-axis ordering instead of letting it interleave with its siblings by the normal median heuristic. Meaningless without `arriveFrom` also set (dropped with a warning). |
| `arriveFrom` | `"left"` \| `"right"` \| `"above"` \| `"below"` | only meaningful with `external: true` | Which extreme of the layer to pin the `external` node to. **Common misconception**: this does *not* change which side of the node's own card the incoming edge visually connects to — edges always enter/exit at the flow-direction sides (west/east in a horizontal flow). It only affects the node's own position; the edge's *path* sweeps toward that side as it approaches (since the node sits there), but the connection point itself stays on the flow-axis side. Only `above`/`below` valid in horizontal, only `left`/`right` in vertical (invalid side dropped with a warning). |
| `collapsed` | bool | any node | *Reserved* — no effect currently. |
| `publishes` / `subscribes` | `string[]` (topic/target names) | any node | Used to *derive* edges: two services sharing a publish/subscribe target get an implied edge between them, an alternative to declaring `Edges[]` explicitly. |

### Can `arriveFrom` be used on more than one node, or on a "normal" mainline node?

Yes, nothing in the schema restricts it — but it's designed for a node that's
genuinely one of several peers converging on the same target and needs visual
separation from that cluster (the `Sidecar` case: it and `Checker` both feed off
`Intake`, and `Sidecar` needs to read as "the side one"). Setting it on a node
that has no such cluster to separate from (e.g. a single straight-through
mainline node like `Checker`, which just flows onward to `Splitter`/
`AuditProcessor`) is harmless but won't visibly do anything meaningful — there's
nothing for it to pull away *from*.

---

## 4. Group colouring: `GroupHint`

| Field | Values | Effect |
|---|---|---|
| `name` | string, matches a `ServiceHint.group` value | Which group this colour applies to. |
| `color` | hex string | Upgrades that group from the default cosmetic band (soft, theme-coloured, drawn for *any* shared `group` tag with no declaration needed) to a real bordered box in this colour — border is a more saturated/opaque shade of the same colour used for the translucent fill. There's no separate on/off flag: declaring a colour here *is* what turns the box on. |

A group not listed in `Groups[]` still renders — just as the plain cosmetic band,
not a coloured box.

**Group boxes and neighbors — two things to know if you're debugging an
overlap:**
1. A group's box is a simple bounding rectangle over its members' final laid-out
   positions. Nothing about the box shape is aware of non-member nodes or edges
   that might geometrically fall inside it purely by coincidence (adjacent-layer
   centering, a long edge routed through that layer, etc.).
2. Two defensive passes exist specifically for this: `pushExternalNodesClearOfGroups`
   (an `external` node landing inside an unrelated group's box gets pushed clear)
   and `pushChainPointsClearOfGroups` (an edge's routed segments — including bend
   points synthesized during orthogonalization, not just raw waypoints — get
   pushed clear the same way, keeping every segment axis-aligned). Both use an
   8px clearance margin (`GROUP_CLEAR_GAP` in `bm-flow-layout/layout.js`) past the
   group's own 14px render padding. If you see a *node* or *edge* still touching
   a group box that it's not a member of, that's a real bug in one of these two
   passes (or a case neither currently covers) — not expected behavior.

---

## 5. Explicit edges: `EdgeHint`

| Field | Values | Effect |
|---|---|---|
| `from` | service name (literal or glob) | Edge source. |
| `to` | service name (literal or glob) | Edge target. |

An alternative to `publishes`/`subscribes`-derived edges, for when two services
are related but don't actually share a topic/target name in a way the derivation
can pick up (or when you want an edge to exist even before either service has
published/subscribed anything observable).

---

## 6. Per-pipeline edge ports (no dedicated JSON field yet)

Multiple real edges between the same two services (different `sourcePipeline`/
`targetPipeline` pairs) automatically render as separate ports on each node's
card rather than all converging on the node's centre — this is inferred from
the observed/declared pipeline rows themselves, not a hint you set. A per-edge
`snapToPipeline` override (forcing a specific edge to connect to a specific row
regardless of inference) is designed but **not implemented** — see `12` §6's
"port hints" gap.

---

## 7. Nested-run overrides (not layout, but same file)

| Field | Values | Effect |
|---|---|---|
| `expandChildrenByDefault` | bool? | Per-run-type override of `RunsSettings:ExpandChildRunsByDefault`. Null inherits the app-wide setting. |
| `childRunBoxColor` | CSS color string? | Per-run-type override of `RunsSettings:ChildRunBoxColor` for this run type's own child-run boxes/cards. Null inherits the app-wide setting. |

See `12_Custom_Layout_And_Nested_Runs.md` §7.4.

---

## 8. Removed / never-implemented fields (don't use these)

| Field | Object | Status |
|---|---|---|
| `pinX` / `pinY` | `ServiceHint` | **Removed.** Was an ELK-only explicit-position hint; `bm-flow-layout` never consumed it, and it's gone from the schema entirely along with ELK. |
| `shape`, `density`, `straightenEdges` | `LayoutHint` | Schema-declared, **inert** — see §2. |
| `pin` | `ServiceHint` | Schema-declared, **inert** — no persisted-position mechanism exists yet. |
| `collapsed` | `ServiceHint` | Schema-declared, **inert**. |
| `placement`/`placeSuccessor` (a node's own placement preference relative to a predecessor) | — | Exists in the JS engine (`bm-flow-layout`) but **not exposed in the C# JSON schema yet** — only the predecessor's own `direction` hint is settable today. See `12` §6. |
| `snapToPipeline` (per-edge port override) | — | Designed, **not implemented**. See §6 above. |
