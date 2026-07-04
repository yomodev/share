# 09 — Features & Usage Guide

## Overview

This is a user-facing tour of features, grouped by page.

Every open page/dataset lives in a **tab** at the top of the window. Tabs
persist their state (scroll position, filters, live polling) even when you
switch away — switching back doesn't reload from scratch.

---

## Switching environment

The environment selector lives at the **bottom of the left sidebar** — a
colored badge (e.g. `DEV1`) that opens a menu of every configured
environment. Changing it here changes the environment for the currently
active view; Home and any open dashboard tabs pick up the new environment's
data immediately (existing tabs update in place, they don't get closed).

---

## Home

The landing page (no tab selected). Shows, top to bottom:

- **Kafka / Mongo health strip** (top right) — a row of colored dots, one per
  broker/node, plus an overall status label and last-checked time.
- **Memory** — a treemap of every service process across all hosts, one tile
  per process, colored on a blue → green → yellow → orange → red → deeppink
  gradient by RAM usage (grey = no sample yet, dim red = offline). Click a
  tile to jump to that process in the Memory Graph. A big spinner covers this
  section (plus Recent Runs below it) until both have loaded for the first
  time; after that, switching away and back doesn't re-show the spinner —
  the last known view stays up while a background refresh happens.
- **Recent Runs** — the 10 most recent runs, same columns as the Runs page.

Both the Memory map and Recent Runs stop polling while Home isn't the active
view (a different tab is focused), and resume the moment you come back.

---

## Runs

A filterable, sortable table of every AI run.

- **Filter box** — accepts the shared filter syntax (see `08_FilterSyntax.md`
  for the full grammar). Runs-specific fields: `runid`, `requestid`/`reqid`
  (integer, exact/range/comparison), `type`, `status`, `start`/`started`,
  `end`/`ended`/`finish`, `desc`/`description`. The filter always runs on
  the server — nothing is filtered client-side.
- **Default view**: `start:>=-10d` — the last 10 days. To see older runs,
  type an explicit `start:` filter with your own date range (e.g.
  `start:2024-01-01..2024-03-01`) — an explicit start-date filter bypasses
  the default lookback window entirely, so you're not silently capped to 10
  days.
- **Sorting** — click any column header (Run ID, Description, Type, Started,
  Status) to sort; click again to reverse. Sorting re-queries the server, it
  doesn't just reorder the current page. Duration isn't sortable (it's
  computed, not a real column).
- **Row limit** — capped by `Runs:MaxResults` in config (default 1000),
  regardless of what the filter/page size requests.
- Double-click a row (or use the row actions) to open its detail tab or its
  Timeline.

---

## Services

A live table/card view of every running service process, refreshed on a
polling loop.

- **Filter box** — default `updated:>-30m` (services with a heartbeat in the
  last 30 minutes). Fields: `servicename`/`svcname`, `hostname`/`host`,
  `ram`/`mem`/`memory`, `peak`, `updated`/`update`.
- **Card view / Table view** toggle (top left) — card view supports grouping
  by **Service** or **Host**.
- Refresh reloads with whatever filter is currently set.
- Double-click a row/card to open that process's Logs.

### Memory Graph

Opened from the Services page (memory icon) or by clicking a tile on Home's
treemap. A per-process RAM-over-time line chart with a brush-navigator strip
underneath for zooming into a time range.

- **Legend clicks** (each series = one process):
  - **Plain click** — solo that series (hide all others); click the same one
    again to bring everyone back.
  - **Ctrl+click** — toggle just that one series on/off without touching the
    others.
  - **Shift+click** — range-select from the last clicked series to this one,
    toggling all of them together.
- Mouse wheel zooms the main chart toward the cursor; drag the bottom strip
  to pick a specific time window.

---

## Kafka

Three related views, opened from the sidebar's Kafka entry:

- **Dashboard** — lists topics (with partition/lag info) and consumer
  groups side by side; a filter box narrows either list.
- **Topic Inspector** — live-tails a topic's messages (Start/Pause), with a
  filter box over the decoded fields and a JSON detail panel per message.
- **Consumer Groups** — select a group to see its per-topic lag breakdown.

---

## MongoDB

- **Dashboard** — pick a database, browse its collections with row counts
  and sizes; a filter box narrows the collection list.
- **Collection Inspector** — paginated, sortable, filterable document browser
  for a single collection; double-click a document to expand it.

---

## Timeline

Opened from a Run's row action, or built up manually via the **Add** button
in the toolbar. A horizontal swimlane visualization of a run's events.

- **Add Batch dialog** — pick an environment, then search by Run ID or
  Description to find runs to add; click a row to add that run's timeline
  alongside whatever's already loaded, so you can compare multiple runs
  side by side on the same time axis.
- **Grouping / coloring** controls in the toolbar change how lanes are
  organized (by service, pipeline, etc.) and what drives block color.
- Keyboard: `←`/`→` pan, `Ctrl+←`/`Ctrl+→` zoom toward the cursor,
  `Ctrl+↑`/`Ctrl+↓` change bar height, mouse wheel zooms the bottom
  selector, `Ctrl+click` a block copies its details to the clipboard.
- **Remove from view** menu (top right) lets you drop one run or clear all;
  **Export/Import CSV** saves/reloads the current set of loaded runs.

---

## Log Browser

Two related pieces: the **folder browser** (tab list entry "Logs") and the
**Log Viewer** (opened per file).

### Folder Browser

- **Left sidebar**: a tree of log folders, aggregated across every server
  configured for the environment — the same folder name on different hosts
  is merged into one node, so you don't have to browse host-by-host. Folders
  are listed alphabetically. A small filter box at the bottom of the tree
  filters the currently-loaded nodes (root level plus any branches you've
  already expanded) by name.
- **Toolbar (top row)**: an "Open file" button (pick a local file off your
  own machine to view), a search-bar toggle, an always-visible filter box
  that filters the **currently loaded file table** (fields: `server`/`host`,
  `file`/`filename`/`name`, `size`, `created`, `updated`/`modified`), a
  last-scanned timestamp, and Refresh (disabled while a search or refresh is
  already in flight).
- **Search bar** (toggle to show/hide, second toolbar row): a filename glob
  and a **content filter** side by side, plus a Start Search/Stop button.
  The content filter uses the same field grammar as the interactive Log
  Viewer (`ts`/`timestamp`, `level`, `machine`, `pid`, `thread`/`tid`,
  `msg`/`message`, `caller`) and is evaluated against each file using the
  same configurable line-format templates (`Logs:Formats` in config) the
  viewer itself uses — so a filter like `caller:OrderService` or `thread:5`
  matches consistently whether you're searching or just reading a file.
  Search recurses into subfolders and streams matching files back as it goes.
- Auto-refresh of the currently selected folder only runs while you're
  actively browsing it — selecting/clicking a folder starts it, opening the
  search bar pauses it (search results are a point-in-time snapshot, not a
  live view), and it stops entirely if the tab isn't focused.
- Double-click a file (or use its row actions) to open it in the Log Viewer.

### Log Viewer

- **Filename** in the header — click it to copy the file's path to the
  clipboard.
- **Filter box** narrows visible lines by the same field grammar as the
  folder browser's content search (`level`, `machine`, `pid`, `thread`,
  `message`, `caller`, `ts`).
- **Find** (`Ctrl+F`) does a plain/regex text search with match
  count + next/prev (`F3`/`Shift+F3`).
- **Bookmarks**: `F2`/`Shift+F2` jump to the next/previous bookmarked line.
- **Display**: `Ctrl+=`/`Ctrl+-` change font size, `Alt+Z` toggles word wrap.
- **Compare multiple logs side by side**: use **Duplicate right**
  (`Ctrl+Shift+→`) or **Duplicate down** (`Ctrl+Shift+↓`) to split the
  current pane and open another file next to/below it — any number of panes
  can be open in the same tab, each independently scrollable/filterable.
- **Live tail**: files outside your local machine keep tailing for new lines
  while their pane is focused.
- `Ctrl+Shift+C` copies a shareable link anchored to the line currently at
  the top of the viewport; `F5` refreshes; `Ctrl+O`/`Ctrl+W` open/close.
- A big spinner (plus the top progress bar) stays up through both the file
  fetch **and** the actual line-parsing pass — large files can take a
  moment to parse, and the spinner now reflects that instead of
  disappearing early.
