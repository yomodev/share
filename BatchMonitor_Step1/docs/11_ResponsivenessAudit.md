# Responsiveness Audit — NxtUI (Blazor Server)

Scope: the three projects under `src/` (`NxtUI.Core`, `NxtUI.Protos`, `NxtUI.Web`), reviewed
for UI responsiveness: render bottlenecks, freeze/stall sources, JS-interop payload costs,
and background-work hygiene. Findings are ordered by expected user-facing impact, each with
concrete remediation steps.

**Overall verdict:** the architecture is fundamentally sound and clearly written by someone
who has already fought (and fixed) the classic Blazor Server problems — file I/O is
consistently pushed off the circuit thread (`Task.Run` in `LogViewerService`,
`LogBrowserService`, `ServiceMetricsMonitor`), polling is focus-gated (`FocusGatedPoller`,
`IsFocused` params, `ShouldRender` overrides), renders are throttled/coalesced in several
places, and heavy visuals (graph, timeline, log viewer, treemap) are delegated to JS/D3
instead of Blazor DOM diffing. The issues below are the remaining hot spots, most of them
concentrated in three places: the render cascade caused by mutable `TabModel` parameters,
whole-dataset JS-interop pushes in the Timeline, and the unvirtualized Kafka message table.

---

## 1. HIGH — `TabModel` parameter cascade re-runs every open tab on every notify

**Where:** `MainLayout.razor` (`TabService.OnChange += ...` → `StateHasChanged`),
`TabContent.razor`, every page taking `[Parameter] TabModel? Tab`.

**Problem.** `TabService.NotifyChanged()` is called extremely often — every
`Tab?.BeginLoading()` / `EndLoading()` pair on every page fires it (twice), and every tab
focus change fires it. Each notify → `MainLayout.StateHasChanged()` → re-render of the
`@foreach (var tab in TabService.Tabs)` loop. Because `Tab` is a **mutable class type**,
Blazor cannot prove the parameter is unchanged, so it **cannot skip** re-rendering
`TabContent` — and `TabContent` re-sets parameters on every open page component. Result:
`OnParametersSet(Async)` runs on *every open tab's page* every time *any* tab starts or
finishes loading.

Two concrete amplifiers:

- `RunDetail` (when focused) re-renders → `D3FlowGraph.OnParametersSetAsync` →
  `PushTopologyAsync()` **unconditionally** re-serializes the whole `Topology` to JS.
  A background tab's spinner toggling causes topology JSON pushes on the focused tab.
- `TimelineTab.OnParametersSetAsync` makes a `setVisible` JS interop round trip per notify.

**Remediation steps:**

1. In `D3FlowGraph.OnParametersSetAsync`, push topology only when it actually changed:
   keep `private Topology? _lastPushed;` and skip `PushTopologyAsync()` when
   `ReferenceEquals(Topology, _lastPushed)`. (RunDetail creates a new `Topology` instance
   per recompute, so reference equality is a correct and free change-detector.)
2. Same pattern in `TimelineTab.OnParametersSetAsync`: only call `setVisible` when
   `IsFocused` actually changed (track `_previousIsFocused` like RunDetail/ServicesPage
   already do).
3. Audit `NotifyChanged()` call volume: `TabLoadingScope`/`BeginLoading` could set a dirty
   flag and coalesce notifies through a ~50 ms debounce inside `TabService` instead of
   firing synchronously per call.
4. Longer-term (optional, bigger win): split the "tab strip changed" event (TabBar needs
   it) from the "loading spinner changed" event (only TabBar needs that too!) — the content
   panes in `MainLayout` only need re-render on open/close/focus changes, not on spinner
   ticks. Two events on `TabService` (`OnStripChanged`, `OnLayoutChanged`) subscribed
   separately by TabBar vs MainLayout would eliminate most content-pane cascades outright.

## 2. HIGH — Timeline pushes the entire event set over JS interop on every update

**Where:** `TimelineTab.razor` `BuildPayload()` / `PushDataAsync()`; `TimelineRun.cs`.

**Problem.** Every debounced update (300 ms while events stream in) rebuilds and
JSON-serializes **all events of all loaded runs** through
`_tlModule.InvokeVoidAsync("update", ...)`. With the ~7.5k events seen in a normal run this
is already a multi-megabyte serialize + SignalR send every 300 ms on the circuit; with
multiple runs loaded it grows linearly and *never shrinks*. This is the most likely cause of
sluggishness while a Timeline tab is open on a live run.

**Aggravator (bug-level):** `TotalEventCount => _runs.Sum(r => r.Events.Count)` is evaluated
in the control-bar markup **on every render**, and `TimelineRun.Events` is
`lock { _events.Values.OrderBy(e => e.Start).ToList(); }` — a full **O(n log n) sort + list
copy of every event of every run, just to read `.Count`**, on the circuit thread, per render.

**Remediation steps:**

1. Immediate one-liner: add `public int EventCount { get { lock (_lock) return _events.Count; } }`
   to `TimelineRun` and use it in `TotalEventCount`. Removes the per-render sort/copy.
2. Cache the sorted list in `TimelineRun`: invalidate on upsert (`_sortedDirty = true`),
   re-sort lazily once per generation instead of per access. `BuildPayload` also calls
   `.Events` per run per push.
3. Incremental JS pushes: keep a `_lastPushedCount` (or per-run event generation counter)
   and add a JS `appendEvents(key, newEvents)` entry point; only fall back to the full
   `update` when events were *replaced* (filter/group changes, run removed). The D3 side
   already recomputes layout from its own retained data, so it only needs deltas.
4. If (3) is too invasive short-term: raise `RenderDebounceMs` from 300 ms to ~1000 ms for
   pushes where `TotalEventCount > 20k`; live-follow smoothness matters less than not
   saturating the circuit.

## 3. HIGH — Kafka Topic Inspector renders up to 3,000 real table rows, even when hidden

**Where:** `KafkaTopicInspector.razor` (`MaxBufferedMessages = 3_000`, `MudTable` over
`_filtered`, streaming loop with 150 ms render throttle).

**Problem.** Three compounding costs:

- **No virtualization**: 3,000 `MudTable` rows × 7 cells, each cell carrying its own
  `@ondblclick` handler → ~21k event handlers and enormous render trees. Every throttled
  `StateHasChanged` during streaming re-diffs this. This is the single largest DOM/diff
  load in the app.
- **No focus gating**: the page doesn't receive `IsFocused` (see `TabContent.razor`), so a
  hidden inspector tab keeps consuming, filtering, and re-rendering its 3k-row table every
  150 ms for as long as the stream runs.
- `_messages.Insert(0, msg)` is O(n) per message on a 3k list, and `ApplyClientFilter`
  re-filters the whole buffer per throttle tick (minor next to the render cost, but free to
  fix in passing).

**Remediation steps:**

1. Switch the message list to `<MudTable ... Virtualize="true">` (MudTable supports it for
   fixed-height tables, which this already is: `FixedHeader="true" Height="100%"`), or
   replace the table body with a `<Virtualize>` block. This alone cuts rendered rows from
   3,000 to ~30.
2. Move the seven per-cell `@ondblclick` handlers to a single `@ondblclick` on the row
   (`RowTemplate`'s cells all do the same thing) — one handler per row instead of seven.
3. Pass `IsFocused="@Tab.IsActive"` from `TabContent` and gate the render throttle: while
   unfocused, keep consuming into `_messages` but skip `ApplyClientFilter` +
   `StateHasChanged` entirely; run both once when focus returns (same pattern
   RunDetail/ServicesPage use).
4. Replace `Insert(0, ...)` with appending and rendering in reverse order (or a deque), so
   buffer maintenance is O(1).

## 4. MEDIUM — Log viewer transfers whole files as a single interop payload

**Where:** `LogViewerService.ReadAllAsync` → `LogViewer.RenderViewerAsync` →
`_viewport.RenderAsync(_rawContent!, ...)`.

**Problem.** The entire file is read into one C# string and crosses JS interop in a single
call. The read itself is correctly off-thread, and client-side rendering is virtualized
(`LogViewport`), but a large network-share log (hundreds of MB) means: a giant LOH string,
a second full-size serialization buffer, and one enormous SignalR frame that stalls the
circuit's send pipe (all other UI updates queue behind it). This is a realistic freeze/OOM
vector — the one place the "don't block the circuit" discipline still has a gap.

**Remediation steps:**

1. Chunk the transfer: send the text in ~1–4 MB segments via repeated
   `viewport.AppendChunkAsync(...)` calls, letting other circuit messages interleave. The
   JS parser already supports incremental input (tail mode uses `ReadDeltaAsync` +
   append), so the plumbing exists — initial load can reuse the same append path.
2. Add a size guard: above a configurable threshold (e.g. `Logs:MaxFullLoadMb`, default
   50 MB), load only the tail N MB with a "load more" affordance, instead of the whole file.
3. Optional stretch: serve file content over a plain HTTP endpoint (like `/api/treemap`
   already does for the treemap, bypassing SignalR) and have the JS viewport `fetch()` it —
   zero circuit involvement for the bulk bytes.

## 5. MEDIUM — Per-circuit SignalR loopback connection for run events

**Where:** `SignalRConnectionService` (`nav.ToAbsoluteUri("/hubs/run-events")`),
`MockRunService` → `IHubContext<RunEventsHub>`.

**Problem.** Each Blazor circuit opens a *client-style* `HubConnection` back to its own
server process. Every run event is: serialized by `IHubContext` → sent through the HTTP/
websocket stack → received by the same process → deserialized → dispatched. That's double
serialization and a loopback network hop per event per circuit, plus one extra websocket
per user session to babysit (reconnect logic, etc.). Events are also delivered
**one message per event** with no batching, so a burst of N events = N loopback messages =
N handler invocations (each of which triggers the RunDetail debounce machinery).

**Remediation steps:**

1. Replace the loopback with an in-process event bus: a singleton
   `RunEventBroker` with `Subscribe(env, runId, handler)` and `Publish(...)`;
   `MockRunService`/real backends publish to it directly, and `SignalRConnectionService`
   becomes a thin adapter (or disappears). Keep the hub only if *external* processes need
   to publish run events over the network.
2. Batch delivery: whether via broker or hub, deliver `IReadOnlyList<PerformanceEvent>`
   per tick (e.g. accumulate 100 ms) instead of single events. Consumers
   (`PerformanceEventService.OnSignalREvent`, `TimelineRun.OnRunEvent`) already upsert
   idempotently, so batching is transparent — and it collapses N debounce-reset cycles
   into one.

## 6. MEDIUM — RunDetail topology recompute: full snapshot + full recompute per debounce

**Where:** `RunDetail.DebouncedTopologyCompute`, `PerformanceEventStore.Snapshot`,
`TopologyComputationService.ComputeTopology`.

**Problem.** Every 400 ms debounce window: full dictionary copy (`Snapshot`), then
`ComputeTopology` does ~10 LINQ passes over all events (multiple `GroupBy`/`ToDictionary`/
`Count` scans). It runs on the thread pool (good — the UI won't freeze), but on long runs
(100k+ events) each recompute is significant CPU + allocation, repeating every 400 ms for
as long as events stream. `PerformanceEventStore.LastEventTimestamp` also does a full
`MaxBy` scan per poll.

Also, `DebouncedTopologyCompute` allocates a new `CancellationTokenSource` + `Task.Delay`
continuation **per incoming event** (SignalR push calls it per event).

**Remediation steps:**

1. Cheap wins first: cache `LastEventTimestamp` in the store (update on upsert);
   single-pass the four `Count()`/`Max()` scans in `ComputeTopology` phase 1/2 where trivial.
2. Make the debounce allocation-free: instead of cancel+new CTS per event, keep one
   periodic "recompute if dirty" loop per RunDetail (e.g. `PeriodicTimer(400ms)` checking a
   `volatile bool _dirty` set by the event callback). Same latency, zero per-event churn.
   (With batching from finding 5, this matters less.)
3. Only if profiling still shows pain on very large runs: incremental topology — maintain
   per-(service,pipeline) counters updated per upsert instead of recomputing from scratch.
   Substantial rework; defer until real backends show real volumes.

## 7. LOW-MEDIUM — File browser tree-filter eager load is fully serial

**Where:** `FileBrowser.EnsureTreeLoadedAsync`.

**Problem.** Typing the first character in the tree filter triggers loading **3 levels of
every folder**, one `GetSubfoldersAsync` at a time (`foreach` + `await` per node). Each call
is a network-share directory listing; a root with 30 folders × 20 children means hundreds of
sequential round trips — the filter can appear dead for minutes on slow shares (the UI
doesn't freeze, but the feature does).

**Remediation steps:**

1. Parallelize per level with a bounded degree (e.g. `Parallel.ForEachAsync` /
   `Task.WhenAll` batched ~8 at a time — mirror `LogBrowserService`'s `_ioGate` discipline).
2. Filter what's loaded immediately and refine as more levels arrive (progressive results),
   instead of waiting for the full prefetch before the filter reflects anything.
3. Consider capping per-folder child expansion during prefetch (e.g. skip folders with
   hundreds of children, marking them "filter can't see inside").

## 8. LOW — Assorted smaller items

- **`KafkaTopicInspector` payload preview per row:** `TruncateValue(context.JsonPayload)`
  runs per row per render; with virtualization (finding 3) this becomes irrelevant —
  otherwise memoize.
- **`Home` health strip:** fine as-is (event-driven, visibility-gated). No action.
- **`ServicesPage`/`MemoryGraph`/`LogViewer`/`FileBrowser`:** correctly focus-gated;
  keep the pattern for new pages — and add `IsFocused` to the pages that *don't* have it
  yet but hold live data: `KafkaTopicInspector` (finding 3) is the one that matters.
- **`MudTable` non-virtualized elsewhere:** Runs/dialog tables are paged (≤200 rows) —
  acceptable. The 5,000-cap search results table in FileBrowser (`MaxSearchResults`) can
  reach 5k rows; consider `Virtualize="true"` there too if large searches are common.
- **`Topology` classes as JS-interop DTOs:** serialization uses reflection-based
  `System.Text.Json` per push. If profiling shows serialization cost, a
  `JsonSerializerContext` (source-generated) for `Topology`/`TimelineTab` payloads is a
  drop-in win.
- **Diagnostics**: `ServerDiagnosticsMonitor` + `DiagnosticsMonitor` (client long-task/FPS
  reporting) already exist and are exactly the right tooling to verify these fixes — use
  the `client-diag | long-task blocked=...ms` log lines as the before/after metric.

---

## Suggested execution order

| Step | Finding | Effort | User-visible payoff |
|------|---------|--------|---------------------|
| 1 | #2 quick wins (EventCount, cached sort) | ~1 h | Timeline tab stops taxing every render |
| 2 | #1 steps 1–2 (change-gated topology/setVisible push) | ~1 h | Focused tab stops re-pushing on unrelated spinners |
| 3 | #3 (virtualize Kafka table + focus gate) | ~½ day | Biggest DOM/diff load removed |
| 4 | #5 step 2 (event batching) then #6 step 2 (dirty-flag debounce) | ~½ day | Live runs scale to high event rates |
| 5 | #2 step 3 (incremental timeline pushes) | 1–2 days | Timeline scales past ~50k events |
| 6 | #4 (chunked log transfer + size guard) | 1 day | Huge logs stop being a freeze/OOM risk |
| 7 | #1 step 4 (split TabService events), #5 step 1 (in-proc bus) | 1–2 days | Structural cleanup, lower baseline load |
| 8 | #7 (parallel tree prefetch) | ~½ day | Tree filter usable on slow shares |

Steps 1–3 are low-risk and independently shippable; measure with the existing diagnostics
monitor between each.
