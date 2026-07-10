# Logging DTSX / SSIS run progress to SQL

How a DTSX (SSIS) package can report its progress into a SQL table so the Run Detail
view can build topology, spans, errors and lag from it — using the generalized
`Actor → Message → Target` event model.

> This is a design resource for DB / SSIS authors. It describes the *shape* of the data
> we want; the C# side polls it through an `IEventSource` abstraction (resume from the
> last reported id/timestamp).

## Concept mapping: SSIS → event model

| Event model | SSIS / DTSX |
|---|---|
| **Service** (actor) | the package (a child package is its own Service) |
| **Server** | the SSIS host machine |
| **ProcessId** | OS PID, or the SSISDB `execution_id` |
| **Pipeline** | a **Data Flow Task** (a package with no data flow gets one *default* pipeline) |
| **Source** | the specific executable/component: `DFT Extract`, `EXEC usp_Transform`, `FST Archive` |
| **Target** | what it acted on: a table, a file path, an SP (or a topic) |
| **CorrelationId** (`Name`) | the batch/file/message id that flows across steps |
| **EventKind** | Start / Finish / Error / Produce / Consume / Connect / Disconnect / Info |

Key idea: a task's **Start/Finish** pair is a *span* (the dominant case — feeds the
Timeline and done/in-progress counts), and **Produce/Consume against a shared Target** is
what lets us draw an edge *without guessing from timestamps* — if `DFT Extract` produces to
table `stg.Customer` and `usp_Transform` consumes from `stg.Customer`, that shared Target
*is* the edge.

## The table

Append-only (never `UPDATE` — that keeps the resume cursor trivial and safe):

```sql
CREATE TABLE dbo.RunEventLog (
    LogId          BIGINT IDENTITY(1,1) PRIMARY KEY,  -- resume cursor: poll WHERE LogId > @lastId
    RunId          VARCHAR(64)   NOT NULL,            -- which run this belongs to
    EventId        CHAR(32)      NOT NULL,            -- unique per step-attempt (GUID 'N')
    CorrelationId  VARCHAR(128)  NULL,                -- the batch/file/message that flows across actors
    -- Actor
    Service        VARCHAR(128)  NOT NULL,            -- package name
    Server         VARCHAR(128)  NOT NULL,
    ProcessId      INT           NULL,                -- PID or SSISDB execution_id
    Pipeline       VARCHAR(128)  NULL,                -- data-flow name; NULL => default pipeline
    -- What happened
    EventKind      VARCHAR(24)   NOT NULL,            -- Start|Finish|Error|Produce|Consume|Connect|Disconnect|Info
    Source         VARCHAR(256)  NULL,                -- the task/component name
    Target         VARCHAR(512)  NULL,                -- table / file / SP it acted on
    EventTimeUtc   DATETIME2(3)  NOT NULL,
    -- Payload / metrics
    Status         VARCHAR(24)   NULL,                -- Success|Failed|Skipped (mainly on Finish)
    RecordCount    BIGINT        NULL,                -- rows in/out
    ErrorMessage   NVARCHAR(MAX) NULL,                -- on Error
    Info           NVARCHAR(1024) NULL,               -- free text / condition outcome
    Value          FLOAT         NULL,                -- optional numeric (duration ms, bytes...)
    LoggedAtUtc    DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_RunEventLog_Run ON dbo.RunEventLog (RunId, LogId);
```

Write rows from SSIS **event handlers** (`OnPreExecute` → Start, `OnPostExecute` → Finish,
`OnError` → Error) via a small `EXEC dbo.usp_LogRunEvent @...`, plus explicit calls inside
Script Tasks / Execute SQL Tasks for produce/consume/condition rows.

## Worked example — a "CustomerLoad" package

One package, two data flows, a truncate, an SP, a file archive, a branch. Batch id
`CUST-2026-07-11-01` flows through (that `CorrelationId` is on every row; omitted below for width):

| LogId | Kind | Service | Pipeline | Source | Target | Status | Rows | Info |
|--|--|--|--|--|--|--|--|--|
| 1 | Connect | CustomerLoad | — | Package | — | | | package started |
| 2 | Start | CustomerLoad | — | `EXEC truncate stg` | `stg.Customer` | | | |
| 3 | Finish | CustomerLoad | — | `EXEC truncate stg` | `stg.Customer` | Success | 0 | |
| 4 | Start | CustomerLoad | DF Extract | `DFT Extract` | | | | |
| 5 | Consume | CustomerLoad | DF Extract | `Flat File Src` | `\\share\in\cust.csv` | | 50000 | file read |
| 6 | Produce | CustomerLoad | DF Extract | `OLE DB Dest` | `stg.Customer` | | 50000 | |
| 7 | Finish | CustomerLoad | DF Extract | `DFT Extract` | | Success | 50000 | |
| 8 | Info | CustomerLoad | — | `Precedence: rows>0` | | | | branch=Load taken |
| 9 | Start | CustomerLoad | — | `EXEC usp_Transform` | `usp_Transform` | | | |
| 10 | Finish | CustomerLoad | — | `EXEC usp_Transform` | `usp_Transform` | Success | 49985 | 15 rejected |
| 11 | Start | CustomerLoad | DF Load | `DFT Load` | | | | |
| 12 | Consume | CustomerLoad | DF Load | `OLE DB Src` | `stg.Customer` | | 49985 | |
| 13 | Error | CustomerLoad | DF Load | `OLE DB Dest` | `dbo.Customer` | Failed | | PK violation on CustomerId=8842 |
| 14 | Finish | CustomerLoad | DF Load | `DFT Load` | | Failed | 49984 | |
| 15 | Start | CustomerLoad | — | `FST Archive` | `\\share\archive\cust.csv` | | | |
| 16 | Finish | CustomerLoad | — | `FST Archive` | `\\share\archive\cust.csv` | Success | | |
| 17 | Disconnect | CustomerLoad | — | Package | — | Failed | | package ended |

## How the existing UI reads it

- **Nodes / pipeline rows:** group by `(Service, Pipeline)`. Rows 4–7 → a "DF Extract"
  pipeline; the SP/truncate/archive rows (Pipeline = NULL) → the default pipeline.
- **Edges (the upgrade over timestamp inference):** `DFT Extract` **produces** to
  `stg.Customer` (row 6) and `usp_Transform` / `DFT Load` **consume** from it (rows 9/12)
  → precise edges from the shared **Target**, and fan-out works (two consumers of one
  Target = two edges).
- **Spans / Timeline:** each Start/Finish pair (4+7, 9+10, 11+14…) is a bar. Because the
  table is append-only, pairing is "match Finish to the open Start with the same `EventId`"
  — the one place spans are reconstructed, and only here.
- **Errors dialog:** every `EventKind = 'Error'` row (13) is a row in the dialog
  (`Service · Server · Pipeline · Source · Target · ErrorMessage`).
- **Lag / "waiting":** produced-at-Target minus consumed-from-Target. (50000/49985 produced
  vs 49984 landed → stragglers are exact, not estimated.)
- **connect/disconnect** (rows 1, 17): drive node "ready/present" vs "done/errored" instead
  of inferring liveness from recent activity.

## Practical notes

1. **Resume cursor caveat.** Polling `WHERE LogId > @last` is perfect for a single
   sequential writer (one package). With concurrent writers, an `IDENTITY` value can commit
   *out of order* (row 20 visible before row 19 finishes committing), so a naive tail can
   skip a row. Cheap guard: only read rows older than a couple seconds —
   `AND LoggedAtUtc < DATEADD(second, -2, SYSUTCDATETIME())` — or track a `rowversion`.

2. **Alternative: read SSISDB directly** — see the full write-up below. It's the
   "I can't touch the packages" fallback; the custom `RunEventLog` above is cleaner and lets
   any source (DTSX, a C# service, a log file) speak the same shape. Both sit behind the
   same `IEventSource`.

## Option 2: reading SSISDB directly (no custom table, no package edits)

When packages run from the SSIS catalog (project-deployment model), SSIS already logs
execution telemetry into `SSISDB` catalog views automatically — no `RunEventLog`, no
package changes. The useful views:

| View | One row per | Gives you |
|---|---|---|
| `catalog.executions` | package execution | run lifecycle: `execution_id`, `package_name`, `server_name`, `status`, `start_time`, `end_time` |
| `catalog.event_messages` | logged event | **the tail source**: `event_message_id` (monotonic cursor), `operation_id` (= execution_id), `message_time`, `message_type` (OnPreExecute/OnPostExecute/OnError/OnInformation…), `message_source_name` (the task/component), `execution_path` (the executable tree), `message` |
| `catalog.executable_statistics` | executable | clean **spans**: `execution_path`, `start_time`, `end_time`, `execution_duration`, `execution_result` |
| `catalog.execution_data_statistics` | component→component edge in a data flow | `source_component_name`, `destination_component_name`, `rows_sent` — real **row counts** |
| `catalog.execution_component_phases` | component phase | per-phase timings (PreExecute, ProcessInput…) |

### Mapping to the event model

- **Service** = `package_name`, **Server** = `executions.server_name`, **ProcessId** = `execution_id`.
- **Pipeline / Source** = parsed from `execution_path` (e.g. `\CustomerLoad\DFT Extract\OLE DB Dest` → pipeline `DFT Extract`, source `OLE DB Dest`).
- **EventKind** from `message_type`: OnPreExecute→Start, OnPostExecute→Finish, OnError→Error, OnInformation→Info.
- **Spans** come straight from `executable_statistics`; **row counts** from `execution_data_statistics`.
- **Tailing**: `event_message_id` is monotonic — resume with `WHERE operation_id = @exec AND event_message_id > @lastId`. Same cursor pattern as the custom table.

### Is it a good idea?

Genuinely good for the "get packages on the map with zero effort" 80%: at the default
*Basic* logging level you get run lifecycle, per-task Start/Finish/Error, and clean spans —
lighting up the flow-graph nodes/rows/states, the Timeline, and the errors dialog with no
package changes at all.

But it's weak exactly where the custom event model is strongest, and the ceiling should be
known before committing:

1. **No business correlation id.** SSISDB tracks the *executable hierarchy within one
   execution*, not a batch/file/message flowing **across** packages. The cross-service
   edges the new model was designed for — "package A produced this batch, package B
   consumed it" — SSISDB can't express, because it has no shared business key. Only
   parent→child structure inside a single package run.
2. **Target is component-shaped, not resource-shaped.** You get `"OLE DB Destination"`, not
   `dbo.Customer` or `\\share\in\cust.csv`. So the "two actors share a Target ⇒ edge"
   inference — the core upgrade — has nothing real to match on. Recovering the actual
   table/file means parsing connection managers or SQL text, which is fragile.
3. **Row counts (⇒ lag) require *Verbose* logging.** That's a real perf/volume cost most
   shops don't run in prod. Without it, no produce/consume counts, no lag.
4. **Operational friction:** coding against SSIS internals (message_type codes,
   execution_path parsing, version quirks); one SSISDB per SSIS host (so the multi-source
   merge already anticipated for `IEventSource` applies here too, per catalog); querying a
   catalog under retention pressure that's competing with the live engine.

### Recommendation: layer both

Don't treat it as either/or:

- **SSISDB-direct** as a fallback `IEventSource` → every catalog package appears
  immediately with spans + errors, no edits.
- For the packages where cross-service topology and lag actually matter, add the *one
  thing SSISDB can't infer*: a single `OnInformation` line (or Script Task call) that emits
  the **business CorrelationId + the real Target** as a produce/consume event. Everything
  else — timing, task status, errors — comes free from SSISDB.

This minimizes package edits to essentially one "emit batch id + target" call, while the
rich timing comes from the catalog. Both feed the same `IEventSource`, so a package can
start SSISDB-only and get "upgraded" later just by adding that one log line.
