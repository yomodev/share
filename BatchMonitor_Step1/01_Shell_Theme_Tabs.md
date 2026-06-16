# Batch Monitor — Shell, Theme, and Tab System

> Part 1 of the design document series — June 2026

---

## 1. Technology Stack

| Layer | Choice |
|---|---|
| Framework | .NET 8, Blazor Server |
| UI Components | MudBlazor >= 9 |
| Real-time | SignalR (shared connection per session) |
| Graph / Timeline visualisation | D3.js (client-side, SVG) |
| Historical/query data | REST API |
| Theme | Dark + Light, user-switchable via Settings |

---

## 2. Shell Architecture

### Layout

```
+---sidebar---+------------------tab bar------------------+
|             | [Batches D1] [⬡ MyBatch U1 ×] [▼]         |
| [☰] Title   +--------------------------------------------+
|             |                                            |
| [🏠] Home   |   Active tab content area                 |
| [📋] Batches|   (or Home dashboard if no tab focused)   |
| [🖥️] Svc    |                                            |
| [📨] Kafka  |                                            |
| [🗄️] Mongo  |                                            |
| [🐛] Errors |                                            |
| [⚙️] Settings|                                           |
|             |                                            |
| ──────────  |                                            |
| [UAT1  ▼]   |                                            |
+-------------+--------------------------------------------+
```

### Sidebar

- **Role:** Launcher only — opens or focuses dashboard tabs
- **Toggle:** Burger icon (☰) top-left; title visible when expanded, hidden when collapsed
- **Collapsed state:** Icons only; tooltip on hover; environment selector collapses to a coloured rounded badge (e.g. `U1`)
- **State persistence:** Collapsed/expanded state and selected environment saved to localStorage

#### Sidebar Item States

| State | Visual |
|---|---|
| Default | Neutral muted colour. No tab open. |
| Open | Accent-tinted icon. Tab exists but not focused. |
| Active | Filled/highlighted icon. Tab currently focused. |

### Global Blazor Services

| Service | Scope | Responsibility |
|---|---|---|
| `TabService` | Scoped | Owns open tab list. Exposes OpenTab, CloseTab, FocusTab. |
| `EnvironmentSelectorService` | Scoped | Owns selected environment. Bidirectional sync between sidebar and Home. |
| `SignalRConnectionService` | Scoped | Single shared SignalR hub connection per session. Tabs subscribe/unsubscribe to groups. |

---

## 3. Theme

- User can switch between **Dark** and **Light** theme in Settings
- Preference persisted to localStorage
- Theme tokens:

| Token | Dark | Light |
|---|---|---|
| Background | `#0E1117` | `#F6F8FA` |
| Surface | `#161B22` | `#FFFFFF` |
| Elevated | `#21262D` | `#EAEEF2` |
| Border | `#30363D` | `#D0D7DE` |
| Muted text | `#8B949E` | `#57606A` |
| Primary text | `#E6EDF3` | `#1F2328` |
| Action accent | `#2F81F4` | `#2F81F4` |

#### Status Colours (both themes)

| Status | Colour | Note |
|---|---|---|
| Running | `#3FB950` (green, animated pulse) | Breathing keyframe animation |
| Completed | `#8B949E` (neutral) | |
| Failed | `#F85149` (red) | Red reserved for errors/failures only — never decorative |

#### Environment Badge Colours

| Tier | Colour |
|---|---|
| Development | Teal / Cyan |
| UAT | Amber / Yellow |
| Staging | Purple |
| Production | Deep Orange |
| Other | Blue Grey |

> **Red is reserved exclusively for errors, failures, and critical alerts.**

---

## 4. Tab System

### Tab Anatomy

```
[⬡ MyBatch  [U1]  ×]
   │          │    └─ close button
   │          └─ environment badge (rounded rectangle)
   └─ type icon + label
```

### Tab Icons

| Tab type | Icon |
|---|---|
| Batches dashboard | List/grid icon |
| Batch detail | Hexagon / batch-specific icon |
| Timeline comparison | Timeline/waveform icon |
| Topic inspector | Message/envelope icon |
| Consumer groups | Group/people icon |

### Tab Identity — Duplicate Prevention

```
TabId = TabType + EntityId + Environment

dashboard:batches:UAT1
detail:batch:RUN-2024-001:UAT1
detail:timeline:RUN-001+RUN-002:UAT1
dashboard:kafka:UAT1
inspector:topic:your-prefix.orders:UAT1
dashboard:settings               ← no environment
```

Opening a sidebar item or clicking an entity link when that tab is already open → **focuses existing tab, never opens a duplicate.**

### Tab Overflow

When tabs exceed the tab bar width → **▼ dropdown** at right end lists all overflow tabs. Active tab is never hidden in overflow.

### Tab Lifecycle

- **On open:** Component mounted, REST fetches initial data, SignalR subscriptions established, `CancellationTokenSource` created
- **On focus (already open):** No re-fetch — renders from cached state immediately
- **On close:** CancellationToken cancelled, SignalR unsubscribed, component disposed, focus moves to nearest remaining tab or Home

---

## 5. Home Dashboard

- **Not a tab** — default canvas shown when no tab is focused
- Cannot be closed
- Contains its own environment selector (bidirectionally synced with sidebar via `EnvironmentSelectorService`)
- Panels:
  - Batch summary (last N batches, status icons, active count, error rate, avg duration)
  - Kafka health widget (see Kafka design doc)
  - Services status indicators
  - MongoDB status indicators
- **Data:** SignalR for live counters; REST on load for historical summaries

---

## 6. Performance Architecture

### Rendering boundary

D3 and all animation live entirely in JavaScript/SVG. Blazor never re-renders the graph or timeline canvas.

| Layer | Owner | Responsibility |
|---|---|---|
| Data fetching, state, tab lifecycle | Blazor | Polls/receives events, maintains in-memory store |
| Top bars, strips, controls | Blazor/MudBlazor | Standard components, guarded with `ShouldRender()` |
| SVG canvas, nodes, edges, animation | D3 / JS | Full ownership; updated via `IJSRuntime.InvokeVoidAsync` |
| Hover tooltips inside canvas | JS-managed HTML div | No MudTooltip — zero Blazor re-render cost |

### Multi-tab performance

| Concern | Mitigation |
|---|---|
| Unfocused tab consuming render budget | `ShouldRender() => isFocused` on tab components |
| Unfocused canvas consuming GPU | `display: none` on D3 canvas when tab not focused |
| Polling loops stacking | Unfocused tabs throttle to 10s cadence; restore fast cadence on focus |
| Completed batch continuing to poll | Polling stops on batch completion event |
| SignalR overhead | One shared connection per session; tabs subscribe/unsubscribe to groups |
| Large data grids | `MudDataGrid` with `Virtualize=true` throughout — only visible rows in DOM |

### Render mode

Blazor Server is retained. `InteractiveAuto` / Wasm is not used — the JS interop boundary is the key performance isolation, not render mode.
