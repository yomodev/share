# Topology hints (per-run-type blueprints)

An **optional** JSON file per run type that seeds the Run Detail flow graph: which services
a run is expected to involve, how they connect, and how to lay them out. It's purely
**advisory** — the runtime event stream still drives state/counts and can add anything the
file didn't mention. A run type with no file behaves exactly as before (pure runtime
inference).

## Why

- **Stable layout from t=0.** Declared services render immediately as greyed `NotStarted`
  nodes; runtime events only flip their state and fill counts — no nodes popping in and
  forcing an ELK re-layout mid-run.
- **Intended shape is visible even before (or without) data** — including expected edges.
- **Author-controlled layout** per run type (vertical/tree, source/sink roles, groups).

## Location & lookup

```
config/topology/{runType}.json      // runType lowercased; e.g. run Type "CustomerETL" → customeretl.json
```

`{runType}` is the run's `Type`, lowercased. Missing or invalid file ⇒ no blueprint (logged,
not fatal). Files are parsed once and cached for the process lifetime. The flow shape doesn't
vary by environment, so this is **not** env-scoped (unlike `config/{env}.json`). JSON may
contain `//` comments and trailing commas.

## File shape

```json
{
  "runType": "CustomerETL",
  "variants": [
    {
      "match": { "anyService": "OrderLoader*" },
      "layout": { "direction": "vertical", "shape": "tree", "density": "compact" },
      "services": [
        { "name": "Extract*",  "role": "source", "group": "Ingest",  "publishes": ["stg.Customer"] },
        { "name": "Transform", "role": "middle", "group": "Process", "subscribes": ["stg.Customer"], "publishes": ["dbo.Customer"] },
        { "name": "Load",      "role": "sink",   "group": "Persist", "subscribes": ["dbo.Customer"], "color": "#A371F7" }
      ],
      "edges": []
    }
  ],
  "default": { "layout": { "direction": "horizontal", "shape": "layered" }, "services": [] }
}
```

## Variant selection

A run type can have several `variants` (e.g. the same type runs a different shape depending
on which entrypoint fired). Selection:

1. Try each variant **in file order**; the first whose `match.anyService` glob matches **any
   service seen so far** in the run wins, and is then **locked** (never switches, even as more
   services appear).
2. If none match, `default` is used (and re-evaluated on later events until a variant locks).
3. If nothing matches and there's no `default` → no blueprint.

`anyService` is a glob (`*` = any, `?` = one), case-insensitive.

## Services & edges

Each `services` entry declares a service and optional node hints.

- **`name`** — a service name or glob.
  - A **literal** name (no `*`/`?`) **pre-renders** as a skeleton node from t=0 (greyed
    `NotStarted`).
  - A **glob** name only **decorates** matching runtime nodes — it has no concrete identity to
    draw before the service appears.
- **Edge derivation** — an edge is declared when a producer's `publishes` token equals a
  consumer's `subscribes` token (**exact string, case-insensitive**). Two globs form a declared
  edge only if the tokens are string-equal; otherwise the edge is left to runtime, where globs
  resolve against concrete targets. Declared edges only draw between endpoints that resolve to
  concrete nodes (so a `Extract*` → `Transform` edge waits for a real `Extract…` to appear).
- **`edges`** — an explicit `[{ "from": "...", "to": "..." }]` shorthand for authors who don't
  model targets. Normalized into the same declared-edge set.

If a real service matches **two** `services` entries, the **first in declaration order** wins.

## Layout vocabulary → ELK

Graph-level (`layout`):

| Hint | Values | Effect |
|---|---|---|
| `direction` | `horizontal` / `vertical` | ELK `RIGHT` / `DOWN`. Omit ⇒ auto by panel aspect ratio |
| `shape` | `layered` / `tree` | ELK `layered` (fans back in) / `mrtree` (branching hierarchy) |
| `density` | `compact` / `normal` / `airy` | scales node/edge spacing (×0.65 / ×1 / ×1.6) |
| `straightenEdges` | `true` / `false` | node placement `NETWORK_SIMPLEX` vs `BRANDES_KOEPF` |

Per-node (`services[]`):

| Hint | Meaning | Effect |
|---|---|---|
| `role` | `source` / `sink` / `middle` | pins to first / last layer (**layered only**; ignored by `tree`) |
| `group` | cluster label | same-group nodes share a labelled backdrop band |
| `color` | hex | header accent tint (state still shown via border) |
| `order` | int (lower first) | in-layer placement bias (ELK priority — approximate) |
| `pin` | bool | *reserved* in v1 (proper pinning needs persisted positions) |
| `pinX` / `pinY` | number | explicit position hint (**ELK only** — `RunsSettings.GraphLayoutEngine = "Elk"`, the default). Best-effort: ELK still avoids overlaps and keeps edges sensible, so a pinned node can shift somewhat from the exact value. Set either/both; an unset axis follows ELK's normal automatic layout. Switches the whole graph into ELK's interactive layering/crossing-minimization strategies whenever at least one node sets either — expect to iterate on the value by looking at the rendered result. |
| `collapsed` | bool | *reserved* in v1 |

## Reconciliation (runtime vs blueprint)

| Situation | Result |
|---|---|
| Declared **and** observed | normal node, decorated with its hints |
| Declared, **never** observed (literal) | greyed skeleton node (`NotStarted`), 50% opacity |
| Observed, **not** declared | added with a **dashed border** ("off-plan") |
| Declared edge, not yet flowing | faint dashed edge |

Runtime always wins on state/counts; the blueprint only adds structure and styling.

## Implementation pointers

- Model + glob: `src/NxtUI.Core/Models/TopologyHint.cs`
- Loader (cached, per run type): `src/NxtUI.Core/Services/TopologyHintLoader.cs`
- Variant select + compile: `src/NxtUI.Core/Models/TopologyBlueprint.cs`
- Merge into the computed graph: `TopologyComputationService.ApplyBlueprint`
- Selection/locking + wiring: `src/NxtUI.Web/Pages/RunDetail.razor` (`ResolveBlueprint`)
- Layout/decoration → ELK + rendering: `src/NxtUI.Web/wwwroot/js/d3-graph.js`
