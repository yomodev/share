# BatchMonitor — UI Components & CSS

## Layout Structure

```
MainLayout
├── Sidebar (collapsible, left)
│   └── SidebarNavItem × N
├── Main content area
│   ├── TabBar (horizontal scroll, drag-to-reorder)
│   └── Tab panes (all rendered, hidden/shown via CSS)
│       ├── Home.razor
│       ├── Batches.razor
│       ├── BatchDetail.razor (× N)
│       └── TimelineTab.razor (× N)
└── ThemeToggle (in sidebar footer)
```

---

## TabBar

**File:** `Layout/TabBar.razor`

**Behaviour:**
- Horizontal scroll; no overflow dropdown
- JS drag-and-drop (`BatchMonitor.initTabDrag`) — pure browser-side with DOM indicator
- One Blazor call on drop: `[JSInvokable] OnTabDropped(tabId, targetIndex)`
- Mouse wheel scrolls horizontally (`BatchMonitor.initTabWheelScroll`)
- Active tab auto-scrolls into view after focus changes

**Tab anatomy:**
- Icon (type-specific MudBlazor icon)
- Label (batch name or tab type)
- Environment badge (coloured pill, `font-size: 0.56rem`)
- Close button (visible on hover/active)

**Cursor:** `default` on hover, `grabbing` only while actively dragging. No `grab` cursor on hover (matches VS Code/Chrome tab behaviour).

---

## Batches Page

**File:** `Pages/Batches.razor`

- MudDataGrid with virtual scrolling
- Filter bar: text search + status chips + type filter
- Pagination: configurable rows-per-page (10/25/50/100)
- Row click opens BatchDetail tab (`TabService.OpenOrFocus`)
- Status chips use `StatusIcon.razor` shared component

**Rows-per-page select:** `width: 68px; font-size: 0.72rem` (set via `Style=` and CSS `.bm-rows-select .mud-input`). MudBlazor ignores inline font-size on the component itself; must target the inner `.mud-input` class via CSS.

---

## BatchDetail Page

**File:** `Pages/BatchDetail.razor`

### Top bar
```
● Running  DeltaSync_Orders  ✓123  ⟳45  ░░░░░░░░░░░ ~67%  [⏱] [■] [∨]
```

Components:
- Status dot + label (colour from `BatchStatus`)
- Batch name
- Done count (✓), In-progress count (⟳)
- Progress bar (~% estimated)
- Timeline button `[⏱]` → opens Timeline tab
- Cancel button `[■]` (running only)
- Expand/collapse details `[∨]`

**Status chip:** Separate dot element (background-color) and label element (color). Both derive colour from `bm-status-{status}` CSS classes.

### Details strip
Collapsible 2-column key-value grid. Persistence via `localStorage` (`bm_batch_details_strip_expanded`).

### Graph area
`D3FlowGraph` component; receives `Topology` and `IsVisible` params. `IsVisible` pauses the animation RAF loop when the tab is not active.

### Slider (replay)

Bottom strip with:
- History icon `[↺]` (enter replay) / Live icon `[●]` (return to live)
- Left timestamp label
- MudSlider (Min=0, Max=1000, Step=1)
- Right timestamp label
- Event count

**Replay mode:** REPLAY chip (amber, pulsing) in the top bar shows current replay time.

---

## CSS Architecture

### `app.css` — Global styles

**Key CSS custom properties** (defined on `:root` or MudBlazor vars):
```css
--bm-font-mono       /* JetBrains Mono → Fira Code → monospace fallback */
--bm-radius-sm       /* 4px */
--bm-radius-lg       /* 10px */
--bm-transition      /* 150ms ease */
```

**Status colours** (via CSS classes):
```css
.bm-status-running   /* green pulse dot */
.bm-status-completed /* grey */
.bm-status-failed    /* red */
.bm-status-unknown   /* dim */
```

**Layout classes:**
- `.bm-page` — full-height column, no self-scroll
- `.bm-page-fill` — same + fills height end-to-end
- `.bm-content-pane` — absolute+inset, multi-tab pane
- `.bm-pane-hidden` — `display:none` for inactive tabs

### `d3-graph.css` — Flow graph

Key rules:
- `.bm-node:hover .bm-node-rect { filter: brightness(1.35) }` — hover glow (NO transform)
- `.bm-tl-sel-layer .overlay { fill: transparent !important }` — brush overlay transparency
- `.bm-node-bg` — opaque rect matching page surface colour (set from JS via `getComputedStyle`)

### `d3-timeline.css` — Timeline

Key structural rules:
```css
.bm-tl-canvas-wrap {
    overflow-y: auto;   /* vertical scroll for tall content */
    overflow-x: hidden;
    scrollbar-width: thin;
}
.bm-tl-bottom-panel {
    flex-shrink: 0;
    height: 52px;       /* TICK_H + BOT_PAD_T + HEAT_H + BOT_PAD_B */
    position: relative;
    z-index: 10;        /* above lane content */
}
.bm-tl-sel-layer .overlay { fill: transparent !important; }
```

---

## Shared Components

### `StatusIcon.razor`
Maps `BatchStatus` to a coloured MudBlazor icon. Used in Batches grid and Timeline Add dialog.

### `SidebarNavItem.razor`
Nav item with icon, label, active state. Wraps MudBlazor `MudNavLink`.

### `EnvironmentBadge.razor`
Coloured pill showing environment ID. Used in tab bar and Batches filter.

### `TabContent.razor`
Switch statement that renders the appropriate page component based on `Tab.Type`. Passes `IsFocused="@Tab.IsActive"` to all content components.

---

## `_Imports.razor`

Global usings for all Razor files:
```razor
@using MudBlazor
@using BatchMonitor
@using BatchMonitor.Configuration
@using BatchMonitor.Models
@using BatchMonitor.Services
@using BatchMonitor.Shared
```

---

## `_Host.cshtml` — Script and CSS order

```html
<!-- CSS -->
<link href="css/app.css" rel="stylesheet" />
<link href="css/d3-graph.css" rel="stylesheet" />
<link href="css/d3-timeline.css" rel="stylesheet" />

<!-- JS (order matters — dependencies first) -->
<script src="https://d3js.org/d3.v7.min.js"></script>
<script src="https://cdn.jsdelivr.net/npm/dagre@0.8.5/dist/dagre.min.js"></script>
<script src="js/d3-graph.js"></script>
<script src="js/d3-animation.js"></script>
<script src="js/d3-timeline.js"></script>
<script src="js/app.js"></script>
```

`d3-animation.js` must load before `d3-graph.js` because the graph RAF loop calls `window.BatchMonitor.D3Animation.tick()`.

---

## MudBlazor Version Notes (v9.5)

Known API changes from earlier versions:
- `MudDialog`: use `@bind-Visible` (not `@bind-IsVisible`)
- `MudDialog`: `CloseButton` goes in `DialogOptions` object
- `MudDataGrid`: no `RowDoubleClick` parameter — use `RowClick` + `MouseEventArgs.Detail == 2`
- `MudSelect` font size: set via CSS targeting `.mud-input`, not inline `Style=`

---

## `app.js` — Tab utilities

**`BatchMonitor.initTabDrag(containerEl, dotnetHelper)`**
Wires HTML5 drag events on the tab strip. Shows a 2px drop indicator div during drag. On `drop`, calls `dotnetHelper.invokeMethodAsync('OnTabDropped', tabId, targetIndex)`.

**`BatchMonitor.initTabWheelScroll(containerEl)`**
Prevents default on wheel events and translates `deltaY → scrollLeft`. `{ passive: false }` required to call `preventDefault()`.

**`BatchMonitor.scrollTabIntoView(tabId)`**
Smooth-scrolls the active tab into the visible strip area.

**`BatchMonitor.getLocalStorage(key)` / `setLocalStorage(key, value)`**
Wrapped in try/catch for environments where `localStorage` may throw.
