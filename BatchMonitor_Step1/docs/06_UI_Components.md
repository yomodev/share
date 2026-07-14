# NxtUI — UI Components & CSS

## Layout Structure

```
MainLayout (Layout/MainLayout.razor)
├── Sidebar (Layout/Sidebar.razor — collapsible, left)
│   └── SidebarNavItem × N (Shared/SidebarNavItem.razor)
├── Main content area
│   ├── TabBar (Layout/TabBar.razor — horizontal scroll, drag-to-reorder)
│   └── Tab panes (all rendered, hidden/shown via CSS — see TabContent.razor)
│       ├── Home.razor, Runs.razor, RunDetail.razor (× N), RunStatsPage.razor
│       ├── ServicesPage.razor, MemoryGraph.razor
│       ├── KafkaDashboard.razor, KafkaTopicInspector.razor, KafkaGroupsDashboard.razor
│       ├── MongoDashboard.razor, MongoCollectionInspector.razor
│       ├── ConfigPage.razor, EnvironmentPage.razor
│       ├── FileBrowser.razor, LogWorkspace.razor (LogViewer panes × N)
│       ├── TimelineTab.razor (× N), FilterHelp.razor, Settings.razor
└── ThemeToggle (in sidebar footer)
```

21 `TabType` variants map to these pages (see `01_Architecture.md`'s Multi-Tab
Architecture section for the full list) — the tab pane list above groups them by
subsystem rather than repeating the enum.

---

## TabBar

**File:** `Layout/TabBar.razor`

**Behaviour:**
- Horizontal scroll; no overflow dropdown.
- JS drag-and-drop (`app.js`'s `initTabDrag(containerEl, dotnetHelper)`, imported as
  an ES module and invoked via `_appModule.InvokeVoidAsync("initTabDrag", ...)`) —
  pure browser-side with a DOM drop indicator.
- One Blazor call on drop: `[JSInvokable] OnTabDropped(tabId, targetIndex)`.
- Mouse wheel scrolls horizontally (`initTabWheelScroll(containerEl)`, same module).
- Active tab auto-scrolls into view after focus changes (`scrollTabIntoView(tabId)`).
- `TabRenameDialog.razor` (also in `Layout/`) handles renaming a tab.

**Tab anatomy:**
- Icon (type-specific MudBlazor icon)
- Label (run name or tab type)
- Environment badge (`Shared/EnvironmentBadge.razor` — coloured pill)
- Close button (visible on hover/active)

**Cursor:** `default` on hover, `grabbing` only while actively dragging. No `grab`
cursor on hover (matches VS Code/Chrome tab behaviour).

---

## Runs Page

**File:** `Pages/Runs.razor` (table body factored into the shared
`Components/BmRunsTable.razor`, reused by Home's Recent Runs and Run Stats' table
view — see `09_Features.md`).

- `BmRunsTable` with pagination (`Components/BmPagination.razor`).
- Filter bar: `Components/FilterBox.razor` (shared filter-grammar text input — see
  `08_FilterSyntax.md`).
- Row click opens a `RunDetail` tab (`TabService.OpenOrFocus`).
- Status chips use `Shared/StatusIcon.razor`.

---

## RunDetail Page

**File:** `Pages/RunDetail.razor`

### Top bar

Status dot + label (colour from run status), run name, done/in-progress counts,
progress bar, and action buttons: Timeline (`[⏱]`, opens a Timeline tab), Cancel
(running runs only), expand/collapse the details strip.

**Status chip:** a separate dot element (background-colour) and label element
(colour), both driven by `bm-status-{status}` CSS classes — see the colour table
below (5 real statuses, not just running/completed/failed).

### Details strip

Collapsible key-value grid. Persistence via `localStorage`.

### Graph area

`Components/D3FlowGraph.razor`; receives the computed `Topology` and an
`IsVisible`-equivalent focus flag that pauses the edge-animation RAF loop when the
tab isn't active. Layout is computed by the custom `bm-flow-layout` engine (see
`04_FlowGraph.md`/`12_Custom_Layout_And_Nested_Runs.md`) — an ELK-based engine also
still exists in the JS as a comparison/fallback toggle, but isn't the default path.

### Slider (replay)

Bottom strip: history/live toggle, left/right timestamp labels, a `MudSlider`, and
an event count. See `02_Models_DataFlow.md`'s Replay mode section for the mechanics
behind it (`_replayTimestamp`, `SliderToDateTime`).

---

## CSS Architecture

### `app.css` — Global styles

Layout classes:
- `.bm-page` — full-height column, no self-scroll.
- `.bm-page-fill` — same + fills height end-to-end.
- `.bm-content-pane` — absolute+inset, multi-tab pane.
- `.bm-pane-hidden` — `display:none` for inactive tabs.

**Status colours** (`.bm-status-{status}` — 5 real statuses, not 3):
```css
.bm-status-running     { color: #3FB950; }  /* green, pulsing icon animation */
.bm-status-completed   { color: #58A6FF; }  /* blue */
.bm-status-failed      { color: #F85149; }  /* red */
.bm-status-terminated  { color: #D29922; }  /* amber */
.bm-status-purged      { color: #8B949E; }  /* grey */
.bm-status-unknown     { color: #8B949E; }  /* grey */
```
Note these are a **different palette** from `PipelineState`'s node/pipeline colours
in `02_Models_DataFlow.md` — run status and pipeline-row state are separate concepts
with separate colour scales; don't assume "completed" looks the same in both places
(it's grey for a pipeline row, blue for a run).

### `d3-graph.css` — Run/pipeline flow graph

- `.bm-node:hover .bm-node-rect { filter: brightness(1.35) }` — hover glow (no transform).
- `.bm-node-hover-zoom { z-index: 1000; }` — keeps a zoomed-in hovered small node above
  everything else, including sibling child-run boxes (fixed this session — see
  git history around `d04e3ac`).
- `.bm-node-bg` — opaque rect matching page surface colour (set from JS via
  `getComputedStyle`).

### `d3-timeline.css` — Timeline

See `05_Timeline.md`'s CSS-relevant structural notes (canvas-wrap scroll, fixed
bottom panel height/z-index).

### `log-viewer.css`

Log Viewer pane styles — not covered by an earlier version of this doc at all;
added alongside the Log Viewer feature (`09_Features.md`'s Log Browser section).

---

## Shared Components (`src/NxtUI.Web/Shared/`, `src/NxtUI.Web/Components/`)

Split across two folders: `Shared/` for small presentational pieces, `Components/`
for larger composite/reusable UI (tables, dialogs, toolbars).

### `Shared/StatusIcon.razor`
Maps a run status to a coloured MudBlazor icon. Used in the Runs table and
Timeline's Add Run dialog.

### `Shared/SidebarNavItem.razor`
Nav item with icon, label, active state. Wraps MudBlazor `MudNavLink`.

### `Shared/EnvironmentBadge.razor`
Coloured pill showing the environment ID. Used in the tab bar and Runs filter.

### `Shared/TabContent.razor`
Switch statement rendering the appropriate page component based on `Tab.Type`.

### `Components/BmRunsTable.razor` / `Components/BmPagination.razor`
Shared run-list table + pagination, reused by Home, Runs, and Run Stats (table view).

### `Components/FilterBox.razor`
Shared filter-grammar text input with field-hint autocomplete — see
`08_FilterSyntax.md`.

### `Components/D3FlowGraph.razor`
Wraps `d3-graph.js`; used by `RunDetail.razor`.

### `Components/ToolbarRefresh.razor`
Shared refresh button (loading spinner state + tooltip) used across nearly every
data page's toolbar.

### `Components/BulkActionDialog.razor`
Shared bulk-select confirmation dialog, used by the Kafka/Mongo purge flows
(`09_Features.md`).

### Log Browser components
`Components/LogBrowserFileTree.razor`, `LogBrowserTreeNode.razor`,
`LogViewerToolbar.razor`, `LogViewport.razor`, `OpenLogFileDialog.razor` — the Log
Browser/Viewer's component breakdown (folder tree, per-pane toolbar, the scrollable
viewport itself, and the "open a local file" dialog).

### Kafka/Mongo dialogs
`Components/KafkaTopicInfoDialog.razor`, `KafkaConsumerGroupsDialog.razor`,
`MongoCollectionInfoDialog.razor` (in `Pages/`) — detail popups opened from their
respective dashboards.

### `Components/DiagnosticsMonitor.razor`
Client-side perf metrics reporter (see `ServerDiagnosticsMonitor`/`client-diag` log
lines in `01_Architecture.md`'s hosted-service list).

### `Components/AsyncData.razor`
Generic loading/error/data wrapper pattern used across pages that fetch data
asynchronously.

---

## `_Imports.razor`

Global usings for all Razor files (`src/NxtUI.Web/_Imports.razor`):
```razor
@using MudBlazor
@using NxtUI
@using NxtUI.Web.Components
@using NxtUI.Core
@using NxtUI.Core.Models
@using NxtUI.Configuration
@using NxtUI.Web.Layout
@using NxtUI.Web.Pages
@using NxtUI.Web.Models
@using NxtUI.Core.Services
@using NxtUI.Core.Services.Mock
@using NxtUI.Web.Services
@using NxtUI.Shared
```

---

## `_Host.cshtml` — Script and CSS order

```html
<!-- CSS -->
<link href="_content/MudBlazor/MudBlazor.min.css" rel="stylesheet" />
<link href="css/app.css?v=NN" rel="stylesheet" />
<link href="css/d3-graph.css?v=NN" rel="stylesheet" />
<link href="css/d3-timeline.css?v=NN" rel="stylesheet" />
<link href="css/log-viewer.css?v=NN" rel="stylesheet" />

<!-- Classic <script> tags: D3, ELK (comparison-engine dependency), and a handful of
     JS files not yet migrated to ES modules -->
<script src="https://d3js.org/d3.v7.min.js"></script>
<script src="https://cdn.jsdelivr.net/npm/elkjs@0.9.3/lib/elk.bundled.js"></script>
<script src="js/d3-memory-sunburst.js?v=NN"></script>
<script src="js/d3-memory-stacked.js?v=NN"></script>
<script src="js/d3-home-treemap.js?v=NN"></script>
<script src="js/json-viewer.js?v=NN"></script>
<script src="js/log-file-access.js?v=NN"></script>

<!-- ES modules: imported by IJSObjectReference from each owning component, or
     preloaded here as a perf hint -->
<script type="module" src="js/log-viewer.js?v=NN"></script>
<script src="js/workspace.js"></script>
<link rel="modulepreload" href="js/log-viewer-parser.js?v=NN" />
<link rel="modulepreload" href="js/filter.js?v=NN" />
<link rel="modulepreload" href="js/app.js?v=NN" />
<link rel="modulepreload" href="js/d3-timeline.js?v=NN" />
```

Every JS asset carries a `?v=NN` cache-busting query string that must be bumped
whenever that file changes — see `feedback_...` conventions established across this
project's commit history (bump the version, don't rely on browser/CDN caching to
pick up an edit). `d3-graph.js` itself is imported lazily per-`D3FlowGraph` instance
(`IJSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/d3-graph.js?v=NN")`),
not loaded as a classic script tag — its own `?v=` bump lives in
`Components/D3FlowGraph.razor`, not `_Host.cshtml`.

---

## MudBlazor Version Notes (v9.6)

Known API quirks worth remembering:
- `MudDialog`: use `@bind-Visible` (not `@bind-IsVisible`).
- `MudDialog`: `CloseButton` goes in the `DialogOptions` object.
- `MudTable`: no row-level double-click parameter, but `@ondblclick` works fine
  bound directly on each `<MudTd>` (see `Components/BmRunsTable.razor`,
  `Pages/FileBrowser.razor`, `Pages/KafkaDashboard.razor` for the pattern used
  throughout this app) — no JS workaround needed for `MudTable`.
- `MudDataGrid` (used by Timeline's Add Run dialog) genuinely has no double-click
  parameter at all — detect via `MouseEventArgs.Detail == 2` on `RowClick` instead.
- `MudSelect` font size: set via CSS targeting `.mud-input`, not inline `Style=`.
- `MudSelect`/`MudTable` render as **two nested elements sharing a similar class**
  (e.g. `.mud-select` on both an outer wrapper and an inner input container) in some
  versions — a `Class=` prop only reaches the inner one; use `:has()` in CSS to
  target the outer wrapper when needed (see `project_mudselect_flex_grow_override`
  in this repo's working notes for a concrete case).

---

## `app.js` — Shared JS utilities (ES module)

Exported functions (imported per-component as `IJSObjectReference`, not a global
namespace):

- `getLocalStorage(key)` / `setLocalStorage(key, value)` — wrapped in try/catch for
  environments where `localStorage` may throw.
- `saveTheme(value)`, `saveTz(value)`, `saveDevMode(enabled)` — settings persistence.
- `watchSystemTheme(dotnetRef)` — subscribes to the OS-level dark/light media query
  and calls back into .NET on change.
- `focusAfterFrame(wrapper)` — focuses an element after the next animation frame
  (needed when Blazor's own render hasn't committed the DOM yet).
- `clickInputInside(wrapper)` — programmatically clicks a hidden `<input>` (used for
  the Config page's word-wrap toggle target and CSV/file import triggers elsewhere).
- `initTabDrag(containerEl, dotnetHelper)` / `initTabWheelScroll(containerEl)` /
  `scrollTabIntoView(tabId)` — TabBar interop, described above.

A handful of legacy globals still hang off `window` rather than being ES exports
(`window.bmInspectorResizer`, `window.logBrowserResizer`, `window.bmClientDiag`) —
resizer/diagnostics helpers that predate the ES-module migration and haven't been
converted; there is no longer a `window.BatchMonitor.*` namespace anywhere in the
app.
