# 15 — Backend Services Reference

Reference doc for `src/NxtUI.Web/Services/` singletons/hosted services that aren't
already covered elsewhere. `RunEventBroker`, `RunEventWatcher`,
`PerformanceEventService`, and `TimelineRun` are covered in
`02_Models_DataFlow.md`/`05_Timeline.md` — not repeated here.

## DI lifetime summary (`Program.cs`)

| Service | Lifetime |
|---|---|
| `HeartbeatMonitor` | Singleton + hosted service (one instance serves both) |
| `InfraHealthCache` | Singleton + hosted service |
| `ServiceMetricsMonitor` | Singleton + hosted service |
| `LogBrowserService` | Singleton |
| `LogViewerService` | Singleton |
| `EnvConfigService` | Singleton |
| `TabService` | Scoped (one per circuit) |
| `RunActionsService` | Scoped |
| `FocusGatedPoller` | **Not DI-registered** — constructed directly by pages that need it |
| `OperationTracker` | Singleton |
| `ErrorNotificationService` | Scoped |
| `ErrorNotificationHubFilter` | Scoped, SignalR hub filter, always active |
| `ThemeService` | Scoped |
| `DateTimeDisplayService` | Scoped |
| `BatchCatalogService` | Singleton |
| `TestLogGenerator` | Hosted service, **only if** `TestLogGenerator:Enabled` config flag is true |
| `ServerDiagnosticsMonitor` | Hosted service, **only if** `Diagnostics:Enabled` is true |
| `BlazorCircuitFilter` | Singleton hub filter, **only if** `Diagnostics:Enabled` is true |
| `WildcardFilter` | Static utility, no DI |
| `TabLoadingScope` | `readonly struct`, static factory, no DI |
| `IPushesOwnRunEvents` | Marker interface (see `02_Models_DataFlow.md`) |

---

## Environment / infra health monitoring

### HeartbeatMonitor / IHeartbeatMonitor

Server-side cache in front of `IHeartbeatService`/Mongo. Polls per-subscribed
environment on an interval, caches the resulting `ServiceStatus` list, and notifies
subscribers via `OnServicesUpdated(env)` — UI components never hit Mongo directly.

- **Subscriber-counted per env** (`Subscribe(env)` returns an `IDisposable`); polling
  only runs for environments with ≥1 active subscriber.
- First poll per env is a full fetch; later polls are **incremental**
  (`since: LastPollUtc`) — only asks for documents changed since the last successful
  poll.
- Liveness (`IsOnline`) is recomputed from wall-clock time on **every** poll for
  every cached entry, never trusted from fetch time — an incremental fetch only
  returns *changed* documents, so a service that silently stops heartbeating
  wouldn't otherwise flip offline.
- **Idle-release**: after the last subscriber leaves, the env's cache survives
  `IdleReleaseMinutes` before full eviction — a quick re-subscribe (tab switch back)
  avoids a cold refetch.
- Per-env `SemaphoreSlim` gate prevents overlapping polls for the same env (timer
  tick racing an immediate subscribe-triggered poll).

### InfraHealthCache

Polls Kafka cluster health and Mongo ping health independently (separate
configurable intervals, `InfraHealthSettings`) and caches results so consumers read
synchronously (`GetKafka`/`GetMongo`) with zero per-render network calls.

- Two fully independent polling loops — a slow Mongo cadence never blocks Kafka's.
- **Failure-streak escalation**: one failed poll never immediately reports Down. It
  takes `ConsecutiveFailuresBeforeDown` consecutive failures to escalate
  Degraded (steady) → DegradedEscalating (blinking, from the 2nd failure) → Down.
  Any success resets the streak.
- Mongo poll deliberately uses `GetClientAsync` (not the sync `GetClient`) — the
  *first* client build for a given settings fingerprint can itself take seconds
  (username probing); using the sync path would let that slow-but-working first
  connection blow past the configured timeout and get misreported as Down.
- `RequestRefresh(env)` triggers an immediate on-demand poll outside the timer
  cadence (e.g. on tab switch).

### ServiceMetricsMonitor / IServiceMetricsMonitor

The largest service in this set. Per-environment memory metrics come from two
merged sources: a live Kafka topic tail (primary) and a one-time disk-log backfill
(covering the gap between a process's start time and the earliest sample still
within Kafka's low retention window). If an env's Kafka topic has no data, it falls
back entirely to continuous file polling for that env.

- Subscriber-counted per env with the same idle-release pattern as
  `HeartbeatMonitor`; also keeps a `HeartbeatMonitor.Subscribe(env)` alive for the
  duration since it depends on that service list.
- `MetricsSettings.Source` can force `FileSystem`-only or `Kafka`-only instead of
  the default `Both`.
- File-poll path: cheap `FileInfo.LastWriteTimeUtc` check before ever reading; only
  reads (via offset-based `IncrementalFileReader`) when mtime changed. Bounded to
  `MaxParallel = 48` concurrent file reads across all envs via a `SemaphoreSlim`
  I/O gate.
- Kafka path: seeks per-service using `OffsetsForTimes` from the oldest known
  service's `CreatedDateTime` (cheap broker-side resolution, not a scan). Waits up
  to `HeartbeatWaitTimeout` (10s) for the heartbeat cache to warm before falling
  back to "seek from latest," to avoid silently missing early messages.
- **Coalesced-rerun**: if a new trigger arrives while a poll for that env is already
  in flight, it's not dropped or queued — the in-flight poll notices and reruns
  once more when done.
- `UpsertSample` does a **sorted insert**, not a blind append — the same entry can
  be written by the file-poller, Kafka-live, and disk-backfill paths concurrently,
  and a blind append could land a sample out of order (showed up as a backward
  "triangular hole" in the sparkline UI before this fix).
- Disk backfill for a service runs at most once, covering only
  `[processStartUtc, kafkaEarliestUtc)` — gated off entirely once Kafka is
  confirmed live for the env; it is not the ongoing file-tail poller.

---

## Log services

### LogBrowserService (internals — UI-facing behavior in `09_Features.md`)

Folder/file discovery across the log root and streaming content-filtered search
across multiple servers, using the same line-format grammar as the interactive Log
Viewer.

- No caching of its own — listings are read live off disk on every call.
- `SearchAsync` streams results via a **bounded** `Channel<LogFileEntry>` (capacity
  256, `FullMode = Wait`) — a fast recursive multi-server scan can't outrun a slow
  UI consumer and buffer unboundedly.
- Recursive directory walk runs per-server in parallel producers feeding the shared
  channel; producer exceptions are explicitly observed after the channel drains, not
  swallowed by `Task.WhenAll` completing before the reader catches up.
- Content filtering buffers multi-line log entries by detecting a
  timestamp-prefixed line as the start of a new entry, then evaluates each complete
  entry against a `FilterNode` via `FilterEvaluator` (the C# filter grammar — see
  `08_FilterSyntax.md`).
- `CompileFormat` is `LogBrowserService`'s own from-scratch regex-template compiler
  for `Logs:Formats` — internal + `[InternalsVisibleTo]` specifically so a
  format-grammar contract test can validate parity against the JS-side
  `log-viewer-parser.js` implementation.
- Falls back to a legacy fixed pipe-delimited 6/7-field format if no `Logs:Formats`
  config is present.

### LogViewerService / ILogViewerService

Small, focused I/O wrapper for reading log file content in the interactive viewer
(full read + incremental tail read). No caching, no state.

- Both read methods are explicitly wrapped in `Task.Run` — a `FileStream` without
  `FileOptions.Asynchronous` silently falls back to synchronous blocking I/O, and
  these are often slow network-share files, so this must never run on the Blazor
  circuit's UI thread.
- `ReadDeltaAsync` delegates to the same offset-based `IncrementalFileReader` used
  by `ServiceMetricsMonitor`.
- Opens with `FileShare.ReadWrite | FileShare.Delete` so it never locks a log file
  being actively written/rotated by the producing service.

---

## Config, tabs, and UI infrastructure

### EnvConfigService / IEnvConfigService

Loads/saves the per-environment config file used by `Pages/ConfigPage.razor` (see
`09_Features.md`'s Config section).

- `GetSourcePath` resolves against the *first* server for the environment; editing
  is only enabled when the `EditingEnabled` config flag is true **and** a source
  path resolves.
- `SaveAsync`: before overwriting the source file, copies it to a timestamped
  `.bak` file — save then only proceeds if backup + write to the source of truth
  both succeed. If the source save fails, mirror saves are **not** attempted at all.
- `SaveMode.AllServers` mirrors the save to every other server's resolved config
  path, independently try/caught per target so one failing mirror doesn't affect
  the others.
- No in-memory caching — every call touches disk directly.

### TabService

Owns the list of open tabs for a circuit — open/focus/close/reorder/rename — and
raises `OnChange` on every mutation.

- `OpenOrFocus`: no duplicate tabs by `Id`; if already open and a new navigation
  hint is supplied, it's propagated onto the existing tab rather than creating a
  second tab.
- `Close`: focus moves to the tab immediately left of the closed one, falling back
  to the first remaining tab, or Home if none remain.
- `Reorder`: removes then re-inserts at a clamped target index, measured *after*
  removal.
- `NotifyChanged()` is a public escape hatch for callers that mutate a `TabModel`'s
  own fields directly (e.g. `IsLoading`) without going through a `TabService`
  method — used by `TabLoadingScope`.

### RunActionsService

Single funnel for cancelling a run instead of every page calling
`IRunService.CancelRunAsync` directly — a documented stand-in for a real external
orchestrator call.

- POSTs to `api/runs/cancel-simulate` first; only on success does it call through to
  `IRunService.CancelRunAsync` to update local run state.
- Returns `(bool Success, string Message)`; never throws — all exceptions are caught
  and surfaced as a failed result.

### FocusGatedPoller

Extracts the repeated `PeriodicTimer`/`CancellationTokenSource` start-stop
boilerplate that Home and the Log Browser pages had each hand-rolled — "poll on an
interval, but only while visible/focused." Deliberately does **not** own the
definition of "visible"; that stays in the caller's `onTick` callback, this class
only owns timer lifecycle.

- `Start()`/`Stop()`/`SetActive(bool)` are idempotent.
- `onTick` exceptions are caught per-tick and routed to an optional `onError`
  callback rather than killing the loop; `OperationCanceledException` still
  propagates to end the loop cleanly on `Stop()`/dispose.

### OperationTracker

Lightweight in-flight-operation registry. Any service calls `Track(description)`
when starting meaningful work; `ServerDiagnosticsMonitor` reports whatever's still
registered when it detects timer lag, to correlate the stall with what was
executing.

- `Track` returns an `IDisposable` handle whose `Dispose()` removes the entry —
  callers wrap operations in a `using`.
- Used by `InfraHealthCache` (per Kafka/Mongo poll) and `HeartbeatMonitor`
  (per env poll).

### ErrorNotificationService / ErrorNotificationHubFilter

Single funnel for surfacing server-side errors as toasts + a history list in
Settings.

- `Report(message, exception, source)`: every call is logged (NLog), appended to a
  bounded in-memory history (`MaxHistory = 200`, oldest evicted first), and raises
  `OnError` for reactive toast display.
- `Report` is frequently called from inside a `catch` block, so each `OnError`
  subscriber is invoked with exceptions swallowed per-handler — a throwing
  subscriber must never escape and crash the catch block reporting the original
  failure.
- `ErrorNotificationHubFilter` (a SignalR hub filter, always active regardless of
  `Diagnostics:Enabled`) recovers specifically from unhandled exceptions thrown
  while dispatching a UI event handler (`DispatchBrowserEvent`), which Blazor
  Server would otherwise treat as fatal to the whole circuit — reports via
  `ErrorNotificationService` and swallows it to keep the circuit alive.

### ThemeService

Tracks dark/light/system theme mode; persists via the `bm-theme` cookie; holds the
shared `MudTheme` palette definitions as a static member.

- Seeds mode from the `bm-theme` cookie; if mode is `System`, also seeds a
  last-known OS-dark guess from a `bm-theme-system` cookie to minimize
  flash-of-wrong-theme before JS reports the real OS preference.
- `SetSystemDark(bool)` is the JS-interop entry point; only fires `OnChange` if the
  effective displayed theme would actually change.

### DateTimeDisplayService

Tracks the user's UTC-vs-local display preference (`bm-tz` cookie) plus a dev-mode
flag (`bm-dev` cookie); centralizes UTC→display conversion.

- `ToDisplay(dt)`: if `UseUtc`, forces the DateTimeKind to Utc; otherwise converts
  to local time only if the input is already tagged `DateTimeKind.Utc` (assumes
  non-UTC-tagged input is already local).
- `DevMode`/`UseUtc` setters raise `OnChange` only on actual value change.

### BatchCatalogService (the "Batches" `TabType` — distinct from Run/Pipeline)

Loads a static batch-job catalog from `wwwroot/data/batch-catalog.json` and
triggers jobs by POSTing a resolved JSON body to each batch's configured HTTP
endpoint — explicitly a stand-in for a real backend, not part of the mocked
Run/Pipeline domain (`IRunService`/`MockRunService`).

- Catalog cached in-memory after first load (never invalidated for the process's
  lifetime), guarded by a double-checked-lock `SemaphoreSlim(1,1)`.
- `TriggerAsync` merges user-supplied param values with built-in placeholders
  (`id`, `env`, `today`, `yesterday`, `month`, `year`), substitutes `{key}` tokens
  into the endpoint URL, and builds a typed JSON body per parameter.
- On HTTP failure/unreachable endpoint, **catches the exception and simulates a
  success** with a `[MOCK] Endpoint unreachable — simulated success` message —
  deliberately, so the demo UI never dead-ends on a real backend that doesn't exist
  yet.

---

## Dev-only diagnostics (conditionally registered)

### TestLogGenerator

Synthetic data generator for local/demo environments — writes both `Metrics.log`
files (feeding `ServiceMetricsMonitor`'s file-poll path) and realistic `App.log`
files (feeding `LogBrowserService`/Log Viewer) under today's date, so the app always
has something to show without a real backend. Only registered when
`TestLogGenerator:Enabled` is true.

- Seeding phase runs once at startup on a background thread: for each service from
  `IHeartbeatService`, backfills a full day of metrics lines and several days of
  app log lines, with random skips to simulate realistic gaps.
- After seeding, ticks on `WriteIntervalSeconds` cadence, appending new lines to
  simulate live/bursty activity — weighted log-level distribution (60% INFO / 20%
  DEBUG / 12% WARN / 8% ERROR), ERROR lines include a synthetic multi-line stack
  trace.
- File writes are best-effort — all exceptions swallowed, a write failure here must
  never crash the generator loop.

### ServerDiagnosticsMonitor

Debugging aid — periodically samples server health (timer lag, CPU%, working set,
thread counts, thread-pool saturation) and logs it; on detecting lag, also dumps
`OperationTracker.ActiveOperations` to correlate the stall with what was executing.
Only registered when `Diagnostics:Enabled` is true.

- Samples every 10s; "lag" is how much the actual `Task.Delay` overshot the
  requested interval — a proxy for thread-pool/event-loop pressure.
- Log level scales with lag severity: Debug (<100ms), Information (100–500ms),
  Warning (>500ms).

### BlazorCircuitFilter

Wraps every Blazor circuit hub message (render, event dispatch, JS interop) to log
slow operations and unhandled exceptions — independent of
`ErrorNotificationHubFilter` (which only targets `DispatchBrowserEvent` and is
always-on). Only registered when `Diagnostics:Enabled` is true.

- Threshold-based severity: Warning ≥200ms, Error ≥1000ms (logged with a "possible
  deadlock or thread starvation" hint). Exceptions are always logged as Error
  regardless of duration, then **rethrown** — unlike `ErrorNotificationHubFilter`,
  this filter doesn't swallow.

---

## Small utilities

### WildcardFilter

Static one-liner: converts a user-typed filter string into a "contains"-style
case-insensitive regex, with `*`/`?` as real wildcards
(`Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".")`). Deliberately
**not** anchored with `^...$` — plain text behaves as substring search, `*`/`?`
remain available for callers who want to anchor/constrain further.

### TabLoadingScope

Collapses the `BeginLoading()`/`NotifyChanged()`/try-finally/`EndLoading()`/
`NotifyChanged()` pattern duplicated across every dashboard page's load method into
one `using` statement. A `readonly struct` (no heap allocation per use); `_tab` is
nullable and safe to no-op when there's no tab context (e.g. Home).
