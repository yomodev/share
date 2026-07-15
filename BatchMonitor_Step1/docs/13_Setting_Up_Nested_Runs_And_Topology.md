# 13 — Setting Up a Run With Children and Customizing Its Topology

A practical, hands-on companion to [11_Topology_Hints.md](11_Topology_Hints.md) (the hint
*schema* reference) and [12_Custom_Layout_And_Nested_Runs.md](12_Custom_Layout_And_Nested_Runs.md)
(the *design* doc for the custom layout engine and nested runs). Those two explain what every
field means and why the system is shaped the way it is. This doc is the other half: **how do I
actually go set one of these up**, using the real files and real run types that ship in this
repo, so you can see the effect of each step rather than just read about it.

If you only need a field-by-field reference, use doc 11. If you're debugging *why* the engine
laid something out a certain way, use doc 12. If you're trying to get a new run type on screen
with children and a custom shape for the first time, keep reading here.

---

## 1. The two things this doc covers, and how they relate

"Setting up a run with children" is really two independent features that happen to compose:

1. **Nested runs** — a parent run whose `RunDetails.Children` lists other runs it triggered.
   This is a **data-shape** concern: does your backend's `IRunService.GetRunDetailsAsync`
   actually populate `ParentRunId`/`Children`? If it doesn't, nothing below matters — the UI
   has nothing to draw.
2. **Topology customization** — an optional JSON file that tells the flow graph what a run
   *should* look like (which services, what layout, colors, groups) instead of purely
   inferring it from whatever events have arrived so far. This applies to **any** run, with
   or without children.

You can have nested runs with no topology hint at all (children just render as plain collapsed
blocks, services render however the runtime events imply). You can have a rich topology hint on
a run type that never has children. This doc walks through both, and then the combination,
using the actual demo data that ships in `MockRunService` so you can see it live without
writing any backend code first.

---

## 2. Prerequisite: which layout engine is active

The flow graph has (at the time of writing) two possible layout engines, chosen by
`Runs:GraphLayoutEngine` in `appsettings.json`: `"Elk"` (the original ELK.js-based engine) and
`"Custom"` (the in-house `bm-flow-layout` engine, see doc 12). **This repo currently ships with
it set to `"Custom"`** — that's the engine actually exercised in day-to-day development, the one
with orthogonal edge routing, direction/external hints, and everything else described below.
Everything in this doc assumes the Custom engine. If your `appsettings.json` has been changed
back to `"Elk"`, some of the newer hints (`direction`, `external`/`arriveFrom`) are silently
ignored — ELK never implemented them (see doc 12 §1 for why: ELK is fundamentally an *automatic*
layout engine and fights manual placement).

Check `src/NxtUI.Web/appsettings.json` → `"Runs": { "GraphLayoutEngine": "Custom" }` before
troubleshooting a hint that doesn't seem to be doing anything.

---

## 3. Walkthrough A — the shipped `NestedDemo` example (read this first)

Before writing anything yourself, run the app (`dotnet run` from `src/NxtUI.Web`, or use your
IDE's usual launch) and open **Home → Recent Runs → `RUN-DEMO-ROOT`** ("NestedDemo — root").
This run and its family (`RUN-DEMO-CHILD-A`, `RUN-DEMO-CHILD-B`, `RUN-DEMO-GRANDCHILD-A1`) are
synthetic data built specifically to exercise every piece described in this doc, three levels
deep. Two files make it happen:

- **`src/NxtUI.Web/Services/MockRunService.cs`**, method `GenerateDemoNestedRuns()` — creates
  the four `RUN-DEMO-*` runs and wires their parent/child relationships via a static map:
  ```csharp
  private static readonly Dictionary<string, string[]> DemoChildren = new()
  {
      ["RUN-DEMO-ROOT"]    = new[] { "RUN-DEMO-CHILD-A", "RUN-DEMO-CHILD-B" },
      ["RUN-DEMO-CHILD-A"] = new[] { "RUN-DEMO-GRANDCHILD-A1" },
  };
  ```
  `RUN-DEMO-CHILD-B` and `RUN-DEMO-GRANDCHILD-A1` have no entry, so they're leaves. This is the
  entire "backend" side of nested runs for the demo — `GetRunDetailsAsync` (in the same file)
  looks a run's id up in this map and populates `RunDetails.Children` with `RunNode` summaries
  for whatever it finds. **This is the pattern your real backend needs to replicate** — see §5.

- **`src/NxtUI.Web/config/topology/nesteddemo.json`** — the topology hint file for run `Type`
  `"NestedDemo"` (all four demo runs share that type). It declares a 4-service pipeline
  (`Ingester → Validator → Enricher → Loader`), two groups (`Stage1`, `Stage2`, only `Stage1`
  given a real color so you can see the "cosmetic band vs. real box" distinction from doc 11
  live), and `"expandChildrenByDefault": true` so `RUN-DEMO-ROOT`'s children start expanded
  without you needing to click anything.

Open `RUN-DEMO-ROOT` and you should see: the root's own 4-service pipeline, `Child A` already
expanded in place showing its *own* nested pipeline plus its own already-expanded grandchild
(`Grandchild A.1`), and `Child B` sitting alongside as a collapsed card (because it wasn't
listed in `RUN-DEMO-ROOT`'s children with an override, and expand-by-default only applies one
level — see doc 12 §7.4's "deliberately not recursive" note, also documented on
`RunsSettings.ExpandChildRunsByDefault`). Click `Child B`'s card to expand it manually and
confirm in-place expand works interactively too.

This one worked example demonstrates: nested runs three levels deep, a topology hint file,
groups with mixed cosmetic/real-box styling, and both automatic and manual in-place expand.
Everything below is "how do I build my own version of this."

---

## 4. Walkthrough B — writing a topology hint file from scratch

Say you have a run `Type` called `"CustomerETL"` and you want its flow graph to render a
specific shape instead of whatever the event stream happens to imply.

### 4.1 Create the file

```
src/NxtUI.Web/config/topology/customeretl.json
```

The filename is the run type **lowercased** — `TopologyHintLoader` looks it up that way, so
`"CustomerETL"` → `customeretl.json`. Get the case wrong and you'll get silent "no blueprint"
behavior (logged, not fatal — check the app's log output for a loader warning if a hint you
just added doesn't seem to apply at all).

### 4.2 Start minimal, then layer on hints

The absolute minimum useful file just declares the services and how they connect:

```json
{
  "runType": "CustomerETL",
  "default": {
    "services": [
      { "name": "Extract",   "role": "source", "publishes": ["stg.Customer"] },
      { "name": "Transform", "role": "middle", "subscribes": ["stg.Customer"], "publishes": ["dbo.Customer"] },
      { "name": "Load",      "role": "sink",   "subscribes": ["dbo.Customer"] }
    ]
  }
}
```

Save this, open (or refresh) a run of type `CustomerETL`, and you should immediately see three
greyed skeleton nodes (`Extract`/`Transform`/`Load`) even before any events have arrived — this
is the "stable layout from t=0" benefit from doc 11 §Why. As events come in, the matching nodes
light up; if the real service names in your events don't literally match `"Extract"` etc.,
they'll show up as *additional*, dashed-border "off-plan" nodes instead of decorating your
declared ones — see doc 11's Reconciliation table. This is usually the first thing to check when
a hint "isn't working": **hint names match the runtime service's *stripped* label** (filler
words like "Pipeline" removed per `Ui:LabelStripWords`), not necessarily the raw service string
in your event data. Compare what actually renders as "off-plan" against what you declared.

### 4.3 Add layout preferences

```json
{
  "runType": "CustomerETL",
  "default": {
    "layout": { "direction": "vertical", "shape": "layered", "density": "compact" },
    "services": [ /* ... as above ... */ ]
  }
}
```

`direction: vertical` flips the whole graph top-to-bottom instead of left-to-right.
`density: compact` tightens node/edge spacing (useful once you have >10 nodes and screen space
gets tight). See doc 11's "Layout vocabulary" table for the full list — `shape: tree` in
particular is worth knowing about if your pipeline branches heavily rather than being a mostly
linear chain.

### 4.4 Add groups

Group related services visually:

```json
"services": [
  { "name": "Extract",   "role": "source", "group": "Ingest",  "publishes": ["stg.Customer"] },
  { "name": "Transform", "role": "middle", "group": "Process", "subscribes": ["stg.Customer"], "publishes": ["dbo.Customer"] },
  { "name": "Load",      "role": "sink",   "group": "Process", "subscribes": ["dbo.Customer"] }
]
```

At this point `Transform`/`Load` get a plain dashed cosmetic band around them (free, no extra
config) because they share a `group` name. To upgrade that to a real bordered/tinted box (like
`Stage1` in the `NestedDemo` example), add a top-level `groups` array to the variant:

```json
"groups": [
  { "name": "Process", "color": "#3FB950" }
]
```

The distinction matters: a plain cosmetic band costs nothing to add (just repeat a `group`
string) and is a nice default; a real colored box is a deliberate visual choice you make per
group, per variant, precisely so declaring *every* group with a color everywhere isn't required
busywork.

### 4.5 Add directional/external hints (Custom engine only, needs §2's check)

If the default layered ordering doesn't put a node where you want it relative to its neighbor:

```json
{ "name": "Auditor", "role": "middle", "direction": "below" }
```

This is a *soft* hint: `Auditor` is saying "whatever comes after me, put it below me" (in a
horizontal flow; `above`/`below` only make sense for a horizontal flow's cross axis — see doc
11's table and doc 12 §6). If a node instead wants to say something about *its own* placement
relative to whoever points at it, use `placement` instead — distinct from `direction`, and easy
to conflate since they're mirror images of the same relationship (predecessor's `direction`
about its successor vs. a node's own `placement` about itself, with the node's own hint always
winning — "child overrides parent," doc 12 §3):

```json
{ "name": "Auditor", "role": "middle", "placement": "above" }
```

Here `Auditor` is saying "wherever my predecessor wants to put me, I want to be above it
instead" — this wins even if the predecessor also declared a `direction` hint pointing
elsewhere.

`external`/`arriveFrom` solves a different problem: several services all point at the same
downstream target, and you want one of them visually separated from the rest — "outside the
cluster." Set `external: true` plus `arriveFrom: "below"` (or `above`/`left`/`right` depending
on flow direction) on that node; the engine pins it to the extreme edge of its layer rather than
letting it get sorted in among its siblings by the normal ordering heuristic.

### 4.5b Add an `orientation` (continue the flow in a different direction)

A different problem again: a genuine **side-branch** — one node and everything downstream of
it that never rejoins the main pipeline — reads better laid out top-to-bottom (or left-to-right)
instead of continuing the main flow's own direction. Set `orientation` on the *first* node of
that branch to a direction different from the run's own `layout.direction`:

```json
{ "name": "AuditProcessor", "role": "middle", "orientation": "vertical",
  "subscribes": ["audit-processor-topic"], "publishes": ["audit-log-topic"] },
{ "name": "AuditLog", "role": "sink", "subscribes": ["audit-log-topic"] }
```

In a horizontal (default) run, this makes `AuditProcessor` the root of a small recursive
sub-flow laid out **vertically**: it and its exclusive downstream closure (`AuditLog` here —
computed automatically by forward-reachability from `AuditProcessor`, then trimmed of anything
that has an edge coming in from *outside* that closure, i.e. a rejoin point) are packed as one
rigid box in the parent layout, the same recursive-box primitive used for groups and nested-run
child boxes. You only declare `orientation` on the turn node itself — its downstream members
don't need any hint at all, they're picked up automatically as long as they're a genuine
dead-end and don't loop back into the main graph.

This is the newest of the layout hints — see it working in the shipped
`RUN-DEMO-LAYOUT-HINTS`/`layouthintsdemo.json` example (§3's sibling demo, described in that
file's own header comment): `AuditProcessor`/`AuditLog` hang off `Checker` as a vertical
side-branch while the rest of the run flows horizontally.

**A subtlety worth knowing if you're debugging this hint**: the sub-flow's own recursive box is
just another node as far as its *parent* layer's rendering is concerned — including group
bounding boxes. A hard `group` box in one layer is drawn as a simple rectangle over its
members' laid-out positions, and the layered layout centers each layer's cross-axis extent
independently, so a tall member in one layer can end up geometrically overlapping an unrelated
node (including an `orientation` sub-flow's box, or an `external` node) in the very next layer
purely by coincidence — this bit an early version of the `Sidecar`/`Ingest` group interaction in
this same demo file, fixed by pushing `external` nodes clear of any group box they'd otherwise
land inside (`pushExternalNodesClearOfGroups` in `bm-flow-layout/layout.js`). If you add a new
hint combination and see one box swallow another that clearly isn't a member, this is the class
of bug to suspect first.

### 4.6 Verify with the JS unit tests, not just the browser

Every hint described above (`role`, `group`+`groups` coloring, `direction`, `external`/
`arriveFrom`) has dedicated unit test coverage in
`src/NxtUI.Web/wwwroot/js/bm-flow-layout/layout.test.js` that runs against the layout *engine*
directly (no browser, no Blazor, pure input-graph-in/positions-out). If a hint isn't behaving
the way you expect, it's often faster to write a throwaway 10-line test there reproducing your
exact node/edge shape than to iterate by refreshing the browser — see §7 for how to run it.

---

## 5. Walkthrough C — wiring up nested runs for real (not the mock)

The demo's "backend" (`MockRunService.GenerateDemoNestedRuns` + its `DemoChildren` map) is
deliberately trivial — a real implementation needs to populate the same two `RunDetails` fields
from your actual data store:

```csharp
public class RunDetails
{
    // ... existing fields ...
    public string? ParentRunId { get; set; }   // this run's OWN parent, or null at the root
    public List<RunNode> Children { get; set; } = new();  // this run's children "so far"
}
```

`RunNode` is a **summary only** — never a full nested `RunDetails`:

```csharp
public class RunNode
{
    public string RunId { get; set; }
    public string Description { get; set; }
    public RunStatus Status { get; set; }
    public DateTime? Start { get; set; }
    public DateTime? End { get; set; }
    public int? DoneCount { get; set; }   // best-effort live progress; null if not cheaply known
    public int? TotalCount { get; set; }
}
```

Steps to wire a real `IRunService` implementation (see `MongoRunService`/`SqlRunService` for
where this lives — at the time of writing, both have `GetRunDetailsAsync` stubbed to return
static demo data, same as `MockRunService`, so there's no working non-mock reference
implementation to copy from yet; you're the first):

1. **Decide how parent/child linkage is actually recorded in your data store.** Doc 12 §7.1
   describes the assumed real-world shape: an orchestrator creates a parent `runId`, then calls
   endpoints that each start a child run passing the parent's `runId` along — so linkage is
   whatever your orchestrator already writes down (a column, a correlation field in your event
   stream, etc.). This app doesn't prescribe a schema for that; it only prescribes the shape
   `GetRunDetailsAsync` must hand back.
2. **Implement `ParentRunId`** — a single lookup ("what's my own parent, if any") used only for
   building breadcrumbs when someone deep-links directly to a child run.
3. **Implement `Children`** — look up whatever runs record *this* run as their parent. Return
   `RunNode` summaries, not full details (keep this cheap — it's called on every poll while the
   run is active, see doc 12 §7.3).
4. **Respect `childDepth`.** `GetRunDetailsAsync(env, runId, ct, childDepth)` takes an int
   (default 1). At depth 1, populate only *immediate* children. Deeper values let the frontend
   fetch multiple levels in one round trip for "expand all" — implement this only if you plan to
   support that toggle; depth 1 alone is enough for the default click-to-expand flow described
   in doc 12 §7.4.
5. **Children can appear over time.** A run with zero children today may have some tomorrow —
   never cache "this run has no children" as a permanent fact. `RunDetail.razor` already polls
   and diffs `Children[]` on a timer (`RunChildRunsRefreshLoopAsync`) while a run is `Running`;
   your implementation just needs to return the current truth on each call.

Once `Children`/`ParentRunId` are real, everything from §3–4 applies unchanged — the topology
hint file, groups, directional hints, all work identically whether the child data came from
`MockRunService` or your real store, because the UI only ever sees the `RunDetails`/`RunNode`
shape, never your storage internals.

### 5.1 Configuring how children behave once they exist

Three `RunsSettings` (in `appsettings.json` under `"Runs"`) control the UX, all overridable
per-run-type via the topology hint file (the hint always wins when it sets a value):

| Setting | Hint override | Effect |
|---|---|---|
| `ChildRunExpandMode` ("InPlace" default \| "NewTab") | *(app-wide only)* | Clicking a collapsed child card expands it inline as a box, or opens it in a new tab (the older, simpler behavior). |
| `ExpandChildRunsByDefault` (bool, default `false`) | `TopologyVariant.ExpandChildrenByDefault` | Whether a run's *immediate* children start expanded when first discovered. Not recursive — see §3's `Child B` example. |
| `ChildRunBoxColor` (nullable CSS color) | `TopologyVariant.ChildRunBoxColor` | Fixed accent color for every child-run box/card of this run, overriding the default "color derived from that child's own status" behavior — makes nested runs visually distinct from same-colored top-level services at a glance. |

---

## 6. Common problems and how to actually check them

- **"My hint file changes nothing."** Check the run's `Type` maps to the exact lowercased
  filename (§4.1). Check `Runs:GraphLayoutEngine` if you're using a Custom-only hint (§2). Check
  the app's startup/request log for a `TopologyHintLoader` parse warning (malformed JSON is
  logged, not thrown).
- **"My variant never matches."** `match.anyService` is checked against services *seen so far in
  the run* — if your match glob targets a service that only appears late in the pipeline, the
  `default` variant (if any) is what renders until then, then re-evaluates on each new event
  until a non-default variant locks in (doc 11 "Variant selection"). If two variants could both
  match, the **first in file order** wins.
- **"A node I declared renders as off-plan/dashed instead of decorated."** Almost always a name
  mismatch against the *stripped* label — see §4.2. Log the actual `Service` string from your
  `PerformanceEvent` stream and compare byte-for-byte (case-insensitive) against your hint's
  `name`.
- **"My `direction`/`external` hint has no visible effect."** These are Custom-engine-only (§2).
  Also: `direction`/`placement`/`arriveFrom` only apply along the flow's *cross* axis —
  `above`/`below` in a horizontal flow, `left`/`right` in a vertical one. The orthogonal pair is
  silently dropped with a warning (visible in the browser console, prefixed
  `bm-flow-layout:`) — check there first.
- **"A grandchild's collapsed card doesn't show up when I expand its parent."** This was a real
  bug fixed in this codebase's history (see the commit "Fix nested collapsed child-run cards not
  rendering below top level") — if you're on a build that predates it, update; every child-run
  item (collapsed card or expanded box) at *every* nesting depth should render via one unified
  code path (`renderChildRunBoxes` in `d3-graph.js`), not just a run's own immediate children.

---

## 7. Testing your changes

- **JSON hint files**: no compile step — save and refresh the browser tab (or navigate to the
  run again). A malformed file logs a warning and behaves as "no blueprint," never crashes the
  page.
- **`bm-flow-layout` engine changes/experiments**: `cd src/NxtUI.Web/wwwroot/js/bm-flow-layout &&
  npx vitest run` runs the full unit suite (pure Node, no browser). This is the fastest
  iteration loop for anything about *positioning* (layer assignment, ordering, hints, groups) —
  see doc 14 (client-side JS architecture) for the full testing story, including how to write a
  new test.
- **Backend `IRunService` changes**: `dotnet test tests/NxtUI.Tests/NxtUI.Tests.csproj` — add
  coverage for your new `Children`/`ParentRunId` logic alongside the existing
  `TopologyComputationService` tests.
- **End-to-end**: run the app locally, open a run of your new type, and use the browser's
  console (`bm-flow-layout:` warnings) plus doc 12 §7.4's expand/collapse interactions to verify
  visually. There is currently no automated end-to-end/screenshot test for the flow graph — this
  is a manual verification step every time.

---

## 8. See also

- [11_Topology_Hints.md](11_Topology_Hints.md) — full hint schema reference.
- [12_Custom_Layout_And_Nested_Runs.md](12_Custom_Layout_And_Nested_Runs.md) — design rationale,
  the recursive-box model, nested-run data contract in full, and the staged build history.
- [14_Client_Side_Architecture.md](14_Client_Side_Architecture.md) — the JS module map (d3-graph.js,
  bm-flow-layout, animation, charts) and how to test each one, if you're changing rendering
  behavior rather than just configuring it via hints.
